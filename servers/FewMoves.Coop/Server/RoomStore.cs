using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;

namespace FewMoves.Coop.Server
{
    public sealed class RoomStore
    {
        private readonly object _gate = new object();
        private readonly ServerOptions _options;
        private readonly CoopRoomDefinition _template;
        private readonly string _templateFingerprint;
        private readonly string _statePath;
        private readonly Dictionary<string, RoomRuntime> _runtimes = new Dictionary<string, RoomRuntime>(StringComparer.Ordinal);
        private PersistedServerState _persisted;
        private byte[] _serverSecret;

        public RoomStore(ServerOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            _options = options;
            _options.Validate();
            _template = LoadTemplate(_options.RoomPath);
            _templateFingerprint = CoopRules.RoomFingerprint(_template);
            _statePath = Path.Combine(_options.StateRoot, "rooms.v1.json");
            EnsurePrivateStateRoot();
            LoadOrCreate();
        }

        public string StateFilePath { get { return _statePath; } }

        public StoreResult Create(CreateRoomRequest request, DateTimeOffset now)
        {
            if (request == null || !IsOpaqueSecret(request.CreateRequestId)) return Failure(400, "create_request_id_invalid", null);
            string requestHash = Hash("create", request.CreateRequestId);

            lock (_gate)
            {
                PersistedRoom prior = FindCircleRecoveryLocked(requestHash);
                if (prior != null)
                {
                    RoomRuntime recovered = _runtimes[prior.RoomId];
                    if (IsExpired(prior, now)) return Failure(410, "room_expired", null);
                    string retryToken = DeriveToken("circle", request.CreateRequestId);
                    if (!ConstantEquals(Hash("token", retryToken), prior.Circle.TokenHash)) throw new ServerStateCorruptException("Circle retry token mismatch.");
                    Touch(recovered, CoopActor.Circle, now);
                    return new StoreResult
                    {
                        Ok = true,
                        StatusCode = 200,
                        RoomId = prior.RoomId,
                        InviteCode = DeriveToken("invite", request.CreateRequestId),
                        Seat = CoopActor.Circle,
                        SeatToken = retryToken,
                        Snapshot = SnapshotLocked(recovered, CoopActor.Circle, now)
                    };
                }

                if (LiveRoomCountLocked(now) >= _options.MaxLiveRooms) return Failure(429, "room_limit_reached", null);
                if (_persisted.Rooms.Count >= _options.MaxStoredRooms) return Failure(429, "room_record_limit_reached", null);

                StoreRollback backup = CaptureRollbackLocked();
                string roomId = Guid.NewGuid().ToString("N");
                string inviteCode = DeriveToken("invite", request.CreateRequestId);
                string circleToken = DeriveToken("circle", request.CreateRequestId);
                CoopSession session = CoopSession.Create(_template);
                var record = new PersistedRoom
                {
                    RoomId = roomId,
                    InviteCodeHash = Hash("invite", inviteCode),
                    Circle = new PersistedSeat { TokenHash = Hash("token", circleToken), RecoveryRequestHash = requestHash },
                    Diamond = null,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    ExpiresAtUtc = now.Add(_options.RoomTtl),
                    RulesVersion = _template.RulesVersion,
                    ContentVersion = _template.ContentVersion,
                    RoomFingerprint = _templateFingerprint,
                    Envelope = CoopSaveCodec.Capture(session, roomId)
                };
                AddAudit(record, now, "room_created", CoopActor.Circle, "created");
                _persisted.Rooms.Add(record);
                var runtime = new RoomRuntime { Record = record, Session = session, CircleLastSeen = now };
                _runtimes.Add(roomId, runtime);
                if (!PersistLocked())
                {
                    RestoreRollbackLocked(backup);
                    return Failure(503, "persistence_failed", null);
                }
                return new StoreResult
                {
                    Ok = true,
                    StatusCode = 201,
                    RoomId = roomId,
                    InviteCode = inviteCode,
                    Seat = CoopActor.Circle,
                    SeatToken = circleToken,
                    Snapshot = SnapshotLocked(runtime, CoopActor.Circle, now)
                };
            }
        }

        public StoreResult Join(JoinRoomRequest request, DateTimeOffset now)
        {
            if (request == null || !IsOpaqueSecret(request.InviteCode) || !IsOpaqueSecret(request.JoinRequestId)) return Failure(400, "join_request_invalid", null);
            string inviteHash = Hash("invite", request.InviteCode);
            string requestHash = Hash("join", request.JoinRequestId);

            lock (_gate)
            {
                PersistedRoom retry = FindDiamondRecoveryLocked(requestHash);
                if (retry != null)
                {
                    if (!ConstantEquals(retry.InviteCodeHash, inviteHash)) return Failure(409, "join_request_payload_conflict", null);
                    RoomRuntime recovered = _runtimes[retry.RoomId];
                    if (IsExpired(retry, now)) return Failure(410, "room_expired", null);
                    string retryToken = DeriveToken("diamond", request.JoinRequestId);
                    if (!ConstantEquals(Hash("token", retryToken), retry.Diamond.TokenHash)) throw new ServerStateCorruptException("Diamond retry token mismatch.");
                    Touch(recovered, CoopActor.Diamond, now);
                    return new StoreResult
                    {
                        Ok = true,
                        StatusCode = 200,
                        RoomId = retry.RoomId,
                        Seat = CoopActor.Diamond,
                        SeatToken = retryToken,
                        Snapshot = SnapshotLocked(recovered, CoopActor.Diamond, now)
                    };
                }

                PersistedRoom record = FindInviteLocked(inviteHash);
                if (record == null) return Failure(404, "invite_not_found", null);
                RoomRuntime runtime = _runtimes[record.RoomId];
                if (IsExpired(record, now)) return Failure(410, "room_expired", null);
                if (record.Diamond != null) return Failure(409, "room_full", null);

                StoreRollback backup = CaptureRollbackLocked();
                string diamondToken = DeriveToken("diamond", request.JoinRequestId);
                record.Diamond = new PersistedSeat { TokenHash = Hash("token", diamondToken), RecoveryRequestHash = requestHash };
                record.UpdatedAtUtc = now;
                Touch(runtime, CoopActor.Diamond, now);
                AddAudit(record, now, "seat_joined", CoopActor.Diamond, "joined");
                if (!PersistLocked())
                {
                    RestoreRollbackLocked(backup);
                    return Failure(503, "persistence_failed", null);
                }
                return new StoreResult
                {
                    Ok = true,
                    StatusCode = 201,
                    RoomId = record.RoomId,
                    Seat = CoopActor.Diamond,
                    SeatToken = diamondToken,
                    Snapshot = SnapshotLocked(runtime, CoopActor.Diamond, now)
                };
            }
        }

        public StoreResult GetState(string roomId, string bearer, DateTimeOffset now)
        {
            lock (_gate)
            {
                RoomRuntime runtime;
                CoopActor seat;
                StoreResult denied;
                if (!TryAuthorizeLocked(roomId, bearer, now, out runtime, out seat, out denied)) return denied;
                Touch(runtime, seat, now);
                return new StoreResult { Ok = true, StatusCode = 200, RoomId = roomId, Seat = seat, Snapshot = SnapshotLocked(runtime, seat, now) };
            }
        }

        public StoreResult Heartbeat(string roomId, string bearer, DateTimeOffset now)
        {
            return GetState(roomId, bearer, now);
        }

        public StoreResult Dispatch(string roomId, string bearer, CommandRequest request, DateTimeOffset now)
        {
            if (request == null || !IsCommandId(request.CommandId)) return Failure(400, "command_id_invalid", null);
            string payloadHash = Hash("command", CanonicalPayload(request));
            lock (_gate)
            {
                RoomRuntime runtime;
                CoopActor seat;
                StoreResult denied;
                if (!TryAuthorizeLocked(roomId, bearer, now, out runtime, out seat, out denied)) return denied;

                StoreRollback backup = CaptureRollbackLocked();
                Touch(runtime, seat, now);
                PersistedCommandLedgerEntry existing = FindCommand(runtime.Record, request.CommandId);
                if (existing != null)
                {
                    if (!ConstantEquals(existing.PayloadHash, payloadHash)) return CommandFailure(409, "command_id_payload_conflict", runtime, seat, now);
                    return new StoreResult
                    {
                        Ok = true,
                        StatusCode = existing.StatusCode,
                        RoomId = roomId,
                        Seat = seat,
                        Accepted = existing.Accepted,
                        Idempotent = true,
                        Reason = existing.Reason,
                        Events = CloneEvents(existing.Events),
                        Snapshot = SnapshotLocked(runtime, seat, now)
                    };
                }

                AvailabilityView availability = AvailabilityLocked(runtime, seat, now);
                if (availability.Status == RoomAvailabilityStatus.Expired) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 410, "room_expired", now);
                if (availability.Status == RoomAvailabilityStatus.WaitingForPeer) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 409, "room_waiting_for_peer", now);
                if (availability.Status == RoomAvailabilityStatus.PausedOffline) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 409, "room_paused_offline", now);
                if (!TryConsumeCommand(runtime, seat, now)) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 429, "command_rate_limited", now);
                if (runtime.Record.CommandLedger.Count >= _options.MaxCommandLedgerRecords) return CommandFailure(429, "command_ledger_limit_reached", runtime, seat, now);

                CoopCommandKind kind;
                if (!TryCommandKind(request.Kind, out kind)) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 400, "command_kind_invalid", now);
                if (kind == CoopCommandKind.Pass) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 422, "pass_not_supported", now);
                if (kind == CoopCommandKind.Slide && (!request.HasDirection || !TryDirection(request.Direction, out _))) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 400, request.HasDirection ? "direction_invalid" : "direction_missing", now);
                if ((kind == CoopCommandKind.RequestUndo || kind == CoopCommandKind.RequestRestart || kind == CoopCommandKind.ResolveUndo || kind == CoopCommandKind.ResolveRestart)
                    && (!request.HasRequestId || !IsCommandId(request.RequestId))) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 400, "request_id_invalid", now);
                if ((kind == CoopCommandKind.ResolveUndo || kind == CoopCommandKind.ResolveRestart) && !request.HasApprove)
                    return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 400, "approve_missing", now);
                if (kind == CoopCommandKind.Express)
                {
                    CoopExpression expression;
                    if (!request.HasExpression || !TryExpression(request.Expression, out expression)) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 400, request.HasExpression ? "expression_invalid" : "expression_missing", now);
                    if (!TryConsumeExpression(runtime, seat, now)) return PersistExternalRejectionLocked(backup, runtime, seat, request.CommandId, payloadHash, 429, "expression_rate_limited", now);
                }

                CoopCommand command = ToCoreCommand(request, seat, kind);
                CoopDispatchResult dispatch;
                try
                {
                    dispatch = runtime.Session.Dispatch(command);
                    runtime.Record.Envelope = CoopSaveCodec.Capture(runtime.Session, runtime.Record.RoomId);
                }
                catch (Exception)
                {
                    RestoreRollbackLocked(backup);
                    return CommandFailure(503, "persistence_failed", _runtimes[roomId], seat, now);
                }

                var entry = new PersistedCommandLedgerEntry
                {
                    CommandId = request.CommandId,
                    PayloadHash = payloadHash,
                    Accepted = dispatch.Accepted,
                    StatusCode = dispatch.Accepted ? 200 : 409,
                    Reason = dispatch.Reason,
                    Events = LimitEvents(dispatch.Events),
                    CreatedAtUtc = now
                };
                runtime.Record.CommandLedger.Add(entry);
                if (kind == CoopCommandKind.Express && dispatch.Accepted && !dispatch.Idempotent)
                {
                    runtime.Record.ExpressionSequence = checked(runtime.Record.ExpressionSequence + 1);
                    runtime.Record.ExpressionEvents.Add(new ExpressionEventView
                    {
                        Sequence = runtime.Record.ExpressionSequence,
                        Sender = seat,
                        Expression = (CoopExpression)request.Expression,
                        CreatedAtUtc = now
                    });
                    TrimOldest(runtime.Record.ExpressionEvents, _options.MaxExpressionEvents);
                }
                runtime.Record.UpdatedAtUtc = now;
                AddAudit(runtime.Record, now, dispatch.Accepted ? "command_accepted" : "command_rejected", seat, dispatch.Reason);
                if (!PersistLocked())
                {
                    RestoreRollbackLocked(backup);
                    return CommandFailure(503, "persistence_failed", _runtimes[roomId], seat, now);
                }

                return new StoreResult
                {
                    Ok = true,
                    StatusCode = entry.StatusCode,
                    RoomId = roomId,
                    Seat = seat,
                    Accepted = dispatch.Accepted,
                    Idempotent = false,
                    Reason = dispatch.Reason,
                    Events = LimitEvents(dispatch.Events),
                    Snapshot = SnapshotLocked(runtime, seat, now)
                };
            }
        }

        private StoreResult PersistExternalRejectionLocked(StoreRollback backup, RoomRuntime runtime, CoopActor seat, string commandId, string payloadHash, int statusCode, string code, DateTimeOffset now)
        {
            if (runtime.Record.CommandLedger.Count >= _options.MaxCommandLedgerRecords) return CommandFailure(429, "command_ledger_limit_reached", runtime, seat, now);
            runtime.Record.CommandLedger.Add(new PersistedCommandLedgerEntry
            {
                CommandId = commandId,
                PayloadHash = payloadHash,
                Accepted = false,
                StatusCode = statusCode,
                Reason = code,
                Events = new CoopEvent[0],
                CreatedAtUtc = now
            });
            runtime.Record.UpdatedAtUtc = now;
            AddAudit(runtime.Record, now, "command_rejected", seat, code);
            if (!PersistLocked())
            {
                string roomId = runtime.Record.RoomId;
                RestoreRollbackLocked(backup);
                return CommandFailure(503, "persistence_failed", _runtimes[roomId], seat, now);
            }
            return CommandFailure(statusCode, code, runtime, seat, now);
        }

        private StoreResult CommandFailure(int statusCode, string code, RoomRuntime runtime, CoopActor seat, DateTimeOffset now)
        {
            return new StoreResult
            {
                Ok = false,
                StatusCode = statusCode,
                ErrorCode = code,
                RoomId = runtime.Record.RoomId,
                Seat = seat,
                Snapshot = SnapshotLocked(runtime, seat, now)
            };
        }

        private StoreResult Failure(int statusCode, string code, StateSnapshot snapshot)
        {
            return new StoreResult { Ok = false, StatusCode = statusCode, ErrorCode = code, Snapshot = snapshot };
        }

        private bool TryAuthorizeLocked(string roomId, string bearer, DateTimeOffset now, out RoomRuntime runtime, out CoopActor seat, out StoreResult denied)
        {
            runtime = null;
            seat = CoopActor.Circle;
            denied = null;
            if (!IsRoomId(roomId))
            {
                denied = Failure(404, "room_not_found", null);
                return false;
            }
            if (!_runtimes.TryGetValue(roomId, out runtime))
            {
                denied = Failure(404, "room_not_found", null);
                return false;
            }
            if (IsExpired(runtime.Record, now))
            {
                denied = Failure(410, "room_expired", null);
                return false;
            }
            if (!IsBearerToken(bearer))
            {
                denied = Failure(401, "seat_token_invalid", null);
                return false;
            }
            string tokenHash = Hash("token", bearer);
            if (runtime.Record.Circle != null && ConstantEquals(tokenHash, runtime.Record.Circle.TokenHash))
            {
                seat = CoopActor.Circle;
                return true;
            }
            if (runtime.Record.Diamond != null && ConstantEquals(tokenHash, runtime.Record.Diamond.TokenHash))
            {
                seat = CoopActor.Diamond;
                return true;
            }
            denied = IsKnownSeatTokenLocked(tokenHash) ? Failure(403, "room_access_denied", null) : Failure(401, "seat_token_invalid", null);
            return false;
        }

        private bool IsKnownSeatTokenLocked(string tokenHash)
        {
            for (int index = 0; index < _persisted.Rooms.Count; index++)
            {
                PersistedRoom room = _persisted.Rooms[index];
                if (room.Circle != null && ConstantEquals(tokenHash, room.Circle.TokenHash)) return true;
                if (room.Diamond != null && ConstantEquals(tokenHash, room.Diamond.TokenHash)) return true;
            }
            return false;
        }

        private StateSnapshot SnapshotLocked(RoomRuntime runtime, CoopActor seat, DateTimeOffset now)
        {
            PersistedRoom record = runtime.Record;
            return new StateSnapshot
            {
                Seat = seat,
                Room = new PublicRoomView
                {
                    RoomId = record.RoomId,
                    RulesVersion = record.RulesVersion,
                    ContentVersion = record.ContentVersion,
                    RoomFingerprint = record.RoomFingerprint,
                    ExpiresAtUtc = record.ExpiresAtUtc
                },
                State = runtime.Session.State,
                Availability = AvailabilityLocked(runtime, seat, now),
                Expressions = new ExpressionStreamView
                {
                    ExpressionSequence = record.ExpressionSequence,
                    Events = CloneExpressions(record.ExpressionEvents)
                }
            };
        }

        private AvailabilityView AvailabilityLocked(RoomRuntime runtime, CoopActor caller, DateTimeOffset now)
        {
            bool circleConnected = IsConnected(runtime.CircleLastSeen, now);
            bool diamondConnected = IsConnected(runtime.DiamondLastSeen, now);
            RoomAvailabilityStatus status;
            if (IsExpired(runtime.Record, now)) status = RoomAvailabilityStatus.Expired;
            else if (runtime.Record.Diamond == null) status = RoomAvailabilityStatus.WaitingForPeer;
            else if (circleConnected && diamondConnected) status = RoomAvailabilityStatus.Active;
            else status = RoomAvailabilityStatus.PausedOffline;
            DateTimeOffset? callerSeen = caller == CoopActor.Circle ? runtime.CircleLastSeen : runtime.DiamondLastSeen;
            return new AvailabilityView
            {
                Status = status,
                CircleConnected = circleConnected,
                DiamondConnected = diamondConnected,
                HeartbeatExpiresAtUtc = callerSeen.HasValue ? callerSeen.Value.Add(_options.HeartbeatTimeout) : (DateTimeOffset?)null
            };
        }

        private bool IsConnected(DateTimeOffset? seen, DateTimeOffset now)
        {
            return seen.HasValue && now - seen.Value <= _options.HeartbeatTimeout;
        }

        private static bool IsExpired(PersistedRoom record, DateTimeOffset now)
        {
            return now >= record.ExpiresAtUtc;
        }

        private static void Touch(RoomRuntime runtime, CoopActor seat, DateTimeOffset now)
        {
            if (seat == CoopActor.Circle) runtime.CircleLastSeen = now;
            else runtime.DiamondLastSeen = now;
        }

        private bool TryConsumeCommand(RoomRuntime runtime, CoopActor seat, DateTimeOffset now)
        {
            Queue<DateTimeOffset> values = seat == CoopActor.Circle ? runtime.CircleCommands : runtime.DiamondCommands;
            Prune(values, now - _options.CommandWindow);
            if (values.Count >= _options.MaxCommandsPerSeatPerWindow) return false;
            values.Enqueue(now);
            return true;
        }

        private bool TryConsumeExpression(RoomRuntime runtime, CoopActor seat, DateTimeOffset now)
        {
            DateTimeOffset? previous = seat == CoopActor.Circle ? runtime.CircleLastExpression : runtime.DiamondLastExpression;
            if (previous.HasValue && now - previous.Value < _options.ExpressionCooldown) return false;
            Queue<DateTimeOffset> values = seat == CoopActor.Circle ? runtime.CircleExpressions : runtime.DiamondExpressions;
            Prune(values, now - _options.ExpressionWindow);
            if (values.Count >= _options.MaxExpressionsPerSeatPerWindow) return false;
            values.Enqueue(now);
            if (seat == CoopActor.Circle) runtime.CircleLastExpression = now;
            else runtime.DiamondLastExpression = now;
            return true;
        }

        private static void Prune(Queue<DateTimeOffset> values, DateTimeOffset cutoff)
        {
            while (values.Count > 0 && values.Peek() <= cutoff) values.Dequeue();
        }

        private static bool TryCommandKind(int value, out CoopCommandKind kind)
        {
            kind = (CoopCommandKind)value;
            return value >= (int)CoopCommandKind.Slide && value <= (int)CoopCommandKind.Express;
        }

        private static bool TryDirection(int value, out GameCommand direction)
        {
            direction = (GameCommand)value;
            return value >= (int)GameCommand.Up && value <= (int)GameCommand.Right;
        }

        private static bool TryExpression(int value, out CoopExpression expression)
        {
            expression = (CoopExpression)value;
            return value >= (int)CoopExpression.Look && value <= (int)CoopExpression.Waiting;
        }

        private static CoopCommand ToCoreCommand(CommandRequest request, CoopActor seat, CoopCommandKind kind)
        {
            return new CoopCommand
            {
                CommandId = request.CommandId,
                ExpectedRevision = request.ExpectedRevision,
                Seat = seat,
                Kind = kind,
                Direction = (GameCommand)request.Direction,
                RequestId = request.RequestId,
                Approve = request.Approve,
                Expression = (CoopExpression)request.Expression
            };
        }

        private void LoadOrCreate()
        {
            if (!File.Exists(_statePath))
            {
                _persisted = new PersistedServerState { ServerSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
                _serverSecret = Convert.FromBase64String(_persisted.ServerSecret);
                if (!PersistLocked()) throw new IOException("Initial private state could not be persisted.");
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(_statePath);
                _persisted = JsonSerializer.Deserialize<PersistedServerState>(bytes, WireJson.Options);
            }
            catch (Exception exception) when (exception is IOException || exception is JsonException || exception is NotSupportedException)
            {
                throw new ServerStateCorruptException("Private state is unreadable.");
            }
            ValidatePersistedStateLocked();
            _serverSecret = Convert.FromBase64String(_persisted.ServerSecret);
            for (int index = 0; index < _persisted.Rooms.Count; index++)
            {
                PersistedRoom record = _persisted.Rooms[index];
                RoomRuntime runtime = BuildRuntime(record);
                _runtimes.Add(record.RoomId, runtime);
            }
        }

        private void ValidatePersistedStateLocked()
        {
            if (_persisted == null || _persisted.SchemaVersion != 1 || string.IsNullOrEmpty(_persisted.ServerSecret) || _persisted.Rooms == null || _persisted.Rooms.Count > _options.MaxStoredRooms)
                throw new ServerStateCorruptException("Private state schema is invalid.");
            byte[] secret;
            try { secret = Convert.FromBase64String(_persisted.ServerSecret); }
            catch (FormatException) { throw new ServerStateCorruptException("Private state secret is invalid."); }
            if (secret.Length != 32) throw new ServerStateCorruptException("Private state secret length is invalid.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < _persisted.Rooms.Count; index++)
            {
                PersistedRoom record = _persisted.Rooms[index];
                if (record == null || !IsRoomId(record.RoomId) || !seen.Add(record.RoomId) || record.Circle == null || string.IsNullOrEmpty(record.Circle.TokenHash) || string.IsNullOrEmpty(record.Circle.RecoveryRequestHash) || string.IsNullOrEmpty(record.InviteCodeHash))
                    throw new ServerStateCorruptException("Private room record is invalid.");
                if (record.Diamond != null && (string.IsNullOrEmpty(record.Diamond.TokenHash) || string.IsNullOrEmpty(record.Diamond.RecoveryRequestHash)))
                    throw new ServerStateCorruptException("Private diamond seat is invalid.");
                if (!string.Equals(record.RulesVersion, _template.RulesVersion, StringComparison.Ordinal) || !string.Equals(record.ContentVersion, _template.ContentVersion, StringComparison.Ordinal) || !string.Equals(record.RoomFingerprint, _templateFingerprint, StringComparison.Ordinal))
                    throw new ServerStateCorruptException("Persisted room no longer matches configured C1.");
                if (record.Envelope == null || record.CommandLedger == null || record.ExpressionEvents == null || record.AuditEvents == null || record.CommandLedger.Count > _options.MaxCommandLedgerRecords || record.ExpressionEvents.Count > _options.MaxExpressionEvents || record.AuditEvents.Count > _options.MaxAuditEvents)
                    throw new ServerStateCorruptException("Private room bounded state is invalid.");
                ValidateCommandLedger(record);
                ValidateExpressionEvents(record);
            }
        }

        private RoomRuntime BuildRuntime(PersistedRoom record)
        {
            CoopSession session;
            string error;
            if (!CoopSaveCodec.TryRestore(_template, record.Envelope, out session, out error)) throw new ServerStateCorruptException("Persisted co-op replay is invalid.");
            return new RoomRuntime { Record = record, Session = session };
        }

        private CoopRoomDefinition LoadTemplate(string path)
        {
            try
            {
                CoopRoomDefinition room = JsonSerializer.Deserialize<CoopRoomDefinition>(File.ReadAllText(path), CoreJsonOptions());
                if (room == null || CoopRules.ValidateRoom(room).Length != 0) throw new ServerStateCorruptException("Configured C1 is invalid.");
                return CoopRules.CloneRoom(room);
            }
            catch (ServerStateCorruptException) { throw; }
            catch (Exception exception) when (exception is IOException || exception is JsonException || exception is NotSupportedException)
            {
                throw new ServerStateCorruptException("Configured C1 cannot be read.");
            }
        }

        private static JsonSerializerOptions CoreJsonOptions()
        {
            return new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        }

        private bool PersistLocked()
        {
            if (_options.FailNextWritesForTests > 0)
            {
                _options.FailNextWritesForTests--;
                return false;
            }
            string tempPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_persisted, WireJson.Options);
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                SetPrivateFileMode(tempPath);
                File.Move(tempPath, _statePath, true);
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is NotSupportedException)
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void EnsurePrivateStateRoot()
        {
            Directory.CreateDirectory(_options.StateRoot);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_options.StateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private static void SetPrivateFileMode(string path)
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private StoreRollback CaptureRollbackLocked()
        {
            var snapshot = new StoreRollback { Persisted = ClonePersisted(_persisted) };
            foreach (KeyValuePair<string, RoomRuntime> pair in _runtimes)
            {
                RoomRuntime runtime = pair.Value;
                snapshot.Runtime.Add(pair.Key, new RuntimeSnapshot
                {
                    CircleLastSeen = runtime.CircleLastSeen,
                    DiamondLastSeen = runtime.DiamondLastSeen,
                    CircleCommands = runtime.CircleCommands.ToArray(),
                    DiamondCommands = runtime.DiamondCommands.ToArray(),
                    CircleExpressions = runtime.CircleExpressions.ToArray(),
                    DiamondExpressions = runtime.DiamondExpressions.ToArray(),
                    CircleLastExpression = runtime.CircleLastExpression,
                    DiamondLastExpression = runtime.DiamondLastExpression
                });
            }
            return snapshot;
        }

        private void RestoreRollbackLocked(StoreRollback backup)
        {
            _persisted = backup.Persisted;
            _runtimes.Clear();
            for (int index = 0; index < _persisted.Rooms.Count; index++)
            {
                PersistedRoom record = _persisted.Rooms[index];
                RoomRuntime runtime = BuildRuntime(record);
                RuntimeSnapshot prior;
                if (backup.Runtime.TryGetValue(record.RoomId, out prior))
                {
                    runtime.CircleLastSeen = prior.CircleLastSeen;
                    runtime.DiamondLastSeen = prior.DiamondLastSeen;
                    RestoreQueue(runtime.CircleCommands, prior.CircleCommands);
                    RestoreQueue(runtime.DiamondCommands, prior.DiamondCommands);
                    RestoreQueue(runtime.CircleExpressions, prior.CircleExpressions);
                    RestoreQueue(runtime.DiamondExpressions, prior.DiamondExpressions);
                    runtime.CircleLastExpression = prior.CircleLastExpression;
                    runtime.DiamondLastExpression = prior.DiamondLastExpression;
                }
                _runtimes.Add(record.RoomId, runtime);
            }
        }

        private static PersistedServerState ClonePersisted(PersistedServerState source)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(source, WireJson.Options);
            PersistedServerState clone = JsonSerializer.Deserialize<PersistedServerState>(bytes, WireJson.Options);
            if (clone == null) throw new ServerStateCorruptException("Private state clone failed.");
            return clone;
        }

        private static void RestoreQueue(Queue<DateTimeOffset> target, DateTimeOffset[] values)
        {
            if (values == null) return;
            for (int index = 0; index < values.Length; index++) target.Enqueue(values[index]);
        }

        private PersistedRoom FindCircleRecoveryLocked(string requestHash)
        {
            for (int index = 0; index < _persisted.Rooms.Count; index++)
            {
                PersistedRoom room = _persisted.Rooms[index];
                if (room.Circle != null && ConstantEquals(room.Circle.RecoveryRequestHash, requestHash)) return room;
            }
            return null;
        }

        private PersistedRoom FindDiamondRecoveryLocked(string requestHash)
        {
            for (int index = 0; index < _persisted.Rooms.Count; index++)
            {
                PersistedRoom room = _persisted.Rooms[index];
                if (room.Diamond != null && ConstantEquals(room.Diamond.RecoveryRequestHash, requestHash)) return room;
            }
            return null;
        }

        private PersistedRoom FindInviteLocked(string inviteHash)
        {
            for (int index = 0; index < _persisted.Rooms.Count; index++)
            {
                PersistedRoom room = _persisted.Rooms[index];
                if (ConstantEquals(room.InviteCodeHash, inviteHash)) return room;
            }
            return null;
        }

        private int LiveRoomCountLocked(DateTimeOffset now)
        {
            int count = 0;
            for (int index = 0; index < _persisted.Rooms.Count; index++) if (!IsExpired(_persisted.Rooms[index], now)) count++;
            return count;
        }

        private static PersistedCommandLedgerEntry FindCommand(PersistedRoom room, string commandId)
        {
            for (int index = 0; index < room.CommandLedger.Count; index++)
            {
                PersistedCommandLedgerEntry item = room.CommandLedger[index];
                if (string.Equals(item.CommandId, commandId, StringComparison.Ordinal)) return item;
            }
            return null;
        }

        private void AddAudit(PersistedRoom room, DateTimeOffset now, string type, CoopActor? seat, string code)
        {
            room.AuditEvents.Add(new PersistedAuditEvent { CreatedAtUtc = now, Type = type, Seat = seat, Code = code });
            TrimOldest(room.AuditEvents, _options.MaxAuditEvents);
        }

        private static void TrimOldest<T>(List<T> values, int maximum)
        {
            int excess = values.Count - maximum;
            if (excess > 0) values.RemoveRange(0, excess);
        }

        private static CoopEvent[] CloneEvents(CoopEvent[] source)
        {
            return source == null ? new CoopEvent[0] : CoopRules.CloneEvents(source);
        }

        private CoopEvent[] LimitEvents(CoopEvent[] source)
        {
            CoopEvent[] clone = CloneEvents(source);
            if (clone.Length <= _options.MaxReturnedCoreEvents) return clone;
            var bounded = new CoopEvent[_options.MaxReturnedCoreEvents];
            Array.Copy(clone, 0, bounded, 0, bounded.Length);
            return bounded;
        }

        private static void ValidateCommandLedger(PersistedRoom record)
        {
            var commandIds = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < record.CommandLedger.Count; index++)
            {
                PersistedCommandLedgerEntry entry = record.CommandLedger[index];
                if (entry == null || !IsCommandId(entry.CommandId) || !commandIds.Add(entry.CommandId) || string.IsNullOrEmpty(entry.PayloadHash) || string.IsNullOrEmpty(entry.Reason) || entry.Events == null || entry.StatusCode < 200 || entry.StatusCode > 599)
                    throw new ServerStateCorruptException("Private command ledger is invalid.");
            }
        }

        private static void ValidateExpressionEvents(PersistedRoom record)
        {
            long previous = 0;
            for (int index = 0; index < record.ExpressionEvents.Count; index++)
            {
                ExpressionEventView item = record.ExpressionEvents[index];
                if (item == null || item.Sequence <= previous || item.Sequence > record.ExpressionSequence || (item.Sender != CoopActor.Circle && item.Sender != CoopActor.Diamond) || (item.Expression < CoopExpression.Look || item.Expression > CoopExpression.Waiting))
                    throw new ServerStateCorruptException("Private expression stream is invalid.");
                previous = item.Sequence;
            }
        }

        private ExpressionEventView[] CloneExpressions(List<ExpressionEventView> source)
        {
            if (source == null || source.Count == 0) return new ExpressionEventView[0];
            int count = Math.Min(source.Count, _options.MaxExpressionEvents);
            var output = new ExpressionEventView[count];
            int start = source.Count - count;
            for (int index = 0; index < count; index++)
            {
                ExpressionEventView item = source[start + index];
                output[index] = new ExpressionEventView { Sequence = item.Sequence, Sender = item.Sender, Expression = item.Expression, CreatedAtUtc = item.CreatedAtUtc };
            }
            return output;
        }

        private string DeriveToken(string purpose, string recoverySecret)
        {
            using (var hmac = new HMACSHA256(_serverSecret))
            {
                byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(purpose + "|" + recoverySecret));
                return ToBase64Url(digest);
            }
        }

        private static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Hash(string purpose, string value)
        {
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "|" + value)));
        }

        private static bool ConstantEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            byte[] leftBytes = Encoding.UTF8.GetBytes(left);
            byte[] rightBytes = Encoding.UTF8.GetBytes(right);
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static bool IsOpaqueSecret(string value)
        {
            return IsSafeAscii(value, 22, 128);
        }

        private static bool IsBearerToken(string value)
        {
            return IsSafeAscii(value, 22, 128);
        }

        private static bool IsRoomId(string value)
        {
            return IsSafeAscii(value, 32, 32);
        }

        private static bool IsCommandId(string value)
        {
            return IsSafeAscii(value, 1, 64);
        }

        private static bool IsSafeAscii(string value, int minimumLength, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length < minimumLength || value.Length > maximumLength) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char item = value[index];
                bool alpha = item >= 'A' && item <= 'Z' || item >= 'a' && item <= 'z';
                bool number = item >= '0' && item <= '9';
                if (!alpha && !number && item != '-' && item != '_') return false;
            }
            return true;
        }

        private static string CanonicalPayload(CommandRequest request)
        {
            return request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + request.Kind.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + request.Direction.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + (request.RequestId ?? string.Empty) + "|"
                + (request.Approve ? "1" : "0") + "|"
                + request.Expression.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
