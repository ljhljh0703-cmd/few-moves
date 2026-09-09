using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using Nectorial.SlideEscape.Record;
using Nectorial.SlideEscape.Unity.Coop;
using Nectorial.SlideEscape.Unity.Record;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity.CoopOnline
{
    public sealed class CoopOnlineBootstrap : MonoBehaviour
    {
        private const string ProductName = "Few Moves Online Pilot";
        private const string DefaultDefinitionId = "coop-c1";
        private const string AtlasResource = "Visuals/turn-escape-tiles";
        private const float PollExpressionCooldownSeconds = 2f;
        private const int SharedRecordRequestIdMaximumLength = 96;
        private static readonly string[] DefinitionIds = { "coop-c1", "coop-c2", "coop-c3" };

        private CoopRoomDefinition _room;
        private readonly Dictionary<string, CoopRoomDefinition> _definitions = new Dictionary<string, CoopRoomDefinition>(StringComparer.Ordinal);
        private OnlineDefinitionView[] _definitionViews = new OnlineDefinitionView[0];
        private CoopBoardView _board;
        private bool _initialized;
        private bool _roomReady;
        private bool _joined;
        private bool _transportLocked = true;
        private bool _transitioning;
        private int _seatCode = -1;
        // Network room UUID for seat/authentication and callback isolation; it is not the Core puzzle ID.
        private string _roomId = string.Empty;
        private string _activeDefinitionId = string.Empty;
        private string _selectedDefinitionId = DefaultDefinitionId;
        private string _inviteCode = string.Empty;
        private string _message = "온라인 방을 준비하세요";
        private string _error = string.Empty;
        private int _availabilityCode = 0;
        private bool _circleConnected;
        private bool _diamondConnected;
        private long _authorityRevision = -1;
        private int _logicalActionCount;
        private CoopState _serverState;
        private long _expressionHighWater;
        private bool _expressionHydrated;
        private long _visibleExpressionSequence;
        private string _visibleExpressionSender = string.Empty;
        private string _visibleExpression = string.Empty;
        private readonly string _commandPrefix = "online-" + Guid.NewGuid().ToString("N") + "-";
        private int _commandSequence;
        private Coroutine _transitionRoutine;
        private OnlineRoomView _roomView;
        private bool _hasMine;
        private bool _hasShared;
        private RecordSummaryObservation _mine;
        private RecordSummaryObservation _shared;
        private string _mineCapsule = string.Empty;
        private string _recordCapsule = string.Empty;
        private string _sharedRecordRequestId = string.Empty;
        private string _recordStatus = "idle";
        private string _recordError = string.Empty;
#if UNITY_EDITOR
        private string _lastObservationJsonForCheck = string.Empty;
#endif
        private string _lastAutoRecordFingerprint = string.Empty;
        private string _pendingAutoRecordFingerprint = string.Empty;
        private int _recordRequestSequence;
        private string _pendingRecordRequestId = string.Empty;
        private string _pendingRecordStateFingerprint = string.Empty;
        private bool _pendingRecordAutomatic;
        private bool _recordErrorFromAutomatic;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateRuntime()
        {
            if (!string.Equals(Application.productName, ProductName, StringComparison.Ordinal)) return;
            if (FindAnyObjectByType<CoopOnlineBootstrap>() != null) return;
            var bootstrap = new GameObject("CoopOnlineBootstrap");
            DontDestroyOnLoad(bootstrap);
            bootstrap.AddComponent<CoopOnlineBootstrap>();
        }

        private void Awake()
        {
            ConfigureCamera();
            InitializeLocalView();
        }

        private void OnDestroy()
        {
            if (_transitionRoutine != null) StopCoroutine(_transitionRoutine);
            if (_board != null) _board.Dispose();
        }

        private void InitializeLocalView()
        {
            try
            {
                Texture2D atlas = Resources.Load<Texture2D>(AtlasResource);
                if (atlas == null || !LoadBundledDefinitions())
                {
                    Fail("필수 협력 화면 자료를 읽을 수 없습니다", "online_asset_missing");
                    return;
                }
                CoopRoomDefinition initial;
                if (!TryGetDefinition(_selectedDefinitionId, out initial))
                {
                    Fail("협력 방 자료가 올바르지 않습니다", "online_default_definition_missing");
                    return;
                }

                _room = initial;
                _activeDefinitionId = _selectedDefinitionId;
                LoadBestRecord();

                _board = new CoopBoardView(atlas);
                _initialized = true;
#if UNITY_WEBGL && !UNITY_EDITOR
                NectorialOnlineResume();
#endif
                PublishState();
            }
            catch (Exception exception)
            {
                Fail("온라인 협력을 시작할 수 없습니다", "online_init:" + exception.GetType().Name);
            }
        }

        public void HandleOnlineCommand(string json)
        {
            if (!_initialized || string.IsNullOrEmpty(json)) return;
            OnlineInput input;
            try { input = JsonUtility.FromJson<OnlineInput>(json); }
            catch (Exception exception) { FailMessage("입력을 읽지 못했습니다", "online_input:" + exception.GetType().Name); return; }
            if (input == null || string.IsNullOrEmpty(input.kind)) return;

            switch (input.kind)
            {
                case "SelectDefinition":
                    SelectDefinition(input.definitionId);
                    return;
                case "Create":
                    string definitionId = string.IsNullOrEmpty(input.definitionId) ? _selectedDefinitionId : input.definitionId;
                    if (!TryGetDefinition(definitionId, out _)) { FailMessage("협력 판을 확인해 주세요", "online_definition_not_allowed"); return; }
                    _selectedDefinitionId = definitionId;
                    BeginRoomAttempt("온라인 방을 만드는 중입니다", definitionId);
#if UNITY_WEBGL && !UNITY_EDITOR
                    NectorialOnlineCreateDefinition(definitionId);
#endif
                    return;
                case "Join":
                    BeginRoomAttempt("온라인 방에 연결하는 중입니다");
#if UNITY_WEBGL && !UNITY_EDITOR
                    NectorialOnlineJoin(input.inviteCode ?? string.Empty);
#endif
                    return;
                case "Resume":
#if UNITY_WEBGL && !UNITY_EDITOR
                    if (string.IsNullOrEmpty(input.inviteCode)) NectorialOnlineResume();
                    else NectorialOnlineResumeInvite(input.inviteCode);
#endif
                    return;
                case "Leave":
#if UNITY_WEBGL && !UNITY_EDITOR
                    NectorialOnlineLeave();
#endif
                    ResetRoomView();
                    return;
                case "GetRecord":
                    GetRecord();
                    return;
                case "LoadSharedRecord":
                    LoadSharedRecord(input.capsule, input.requestId);
                    return;
                case "Challenge":
                    ChallengeSharedRecord();
                    return;
                case "Slide":
                    int direction;
                    if (!TryParseDirection(input.direction, out direction)) { FailMessage("이동 방향을 확인해 주세요", "online_direction_invalid"); return; }
                    SendCommand(input, 0, direction, 0, null, false);
                    return;
                case "RequestUndo": SendCommand(input, 2, 0, 0, input.requestId, false); return;
                case "ResolveUndo":
                    if (!CanResolveConsent(input.requestId)) return;
                    SendCommand(input, 3, 0, 0, input.requestId, input.approve);
                    return;
                case "RequestRestart": SendCommand(input, 4, 0, 0, input.requestId, false); return;
                case "ResolveRestart":
                    if (!CanResolveConsent(input.requestId)) return;
                    SendCommand(input, 5, 0, 0, input.requestId, input.approve);
                    return;
                case "Express":
                    int expression;
                    if (!TryParseExpression(input.expression, out expression)) { FailMessage("표현을 확인해 주세요", "online_expression_invalid"); return; }
                    SendCommand(input, 6, 0, expression, null, false);
                    return;
                default: FailMessage("알 수 없는 온라인 입력입니다", "online_kind_unknown"); return;
            }
        }

        public void OnOnlineResult(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            OnlineResult result;
            try { result = JsonUtility.FromJson<OnlineResult>(json); }
            catch (Exception exception) { FailMessage("서버 응답을 읽지 못했습니다", "online_response:" + exception.GetType().Name); return; }
            if (result == null) return;

            if (result.op == "record")
            {
                ApplyRecordResult(result);
                PublishState();
                return;
            }

            if (!result.ok)
            {
                _error = result.error != null ? result.error.code : "online_request_failed";
                _message = FriendlyError(_error);
                if (!ApplyAuthenticatedResult(result)) LockForTransportFailure(_error);
                PublishState();
                return;
            }

            _error = string.Empty;
            bool sessionPresent = result.seat == 0 || result.seat == 1;
            bool hasRoom = HasRoomPayload(result.room);
            bool hasState = HasStatePayload(result.state);
            if (IsNoSessionResume(result, sessionPresent, hasRoom, hasState))
            {
                ResetRoomView();
                _inviteCode = result.inviteCode ?? string.Empty;
                _message = string.IsNullOrEmpty(result.inviteCode) ? "방을 만들거나 초대 코드로 참여하세요" : "초대 코드에 연결할 저장된 좌석이 없습니다";
                PublishState();
                return;
            }
            if (RequiresAuthenticatedState(result.op) && (!sessionPresent || !hasRoom || !hasState))
            {
                RejectAuthenticatedPayload();
                PublishState();
                return;
            }
            if ((result.op == "created" || result.op == "joined") && (!sessionPresent || !hasRoom))
            {
                RejectAuthenticatedPayload();
                PublishState();
                return;
            }
            if (sessionPresent) _seatCode = result.seat;
            if (!string.IsNullOrEmpty(result.inviteCode)) _inviteCode = result.inviteCode;
            if (hasRoom)
            {
                SetRoom(result.room);
                if (!_roomReady)
                {
                    _message = "서버 방 자료가 현재 빌드와 다릅니다";
                    PublishState();
                    return;
                }
            }
            ApplyAvailability(result.availability);
            bool authenticatedState = ApplyAuthenticatedResult(result);
            if (hasState && !authenticatedState)
            {
                PublishState();
                return;
            }
            if (result.op == "created" || result.op == "joined" || result.op == "resumed" || (result.op == "resume" && sessionPresent))
            {
                _joined = true;
                _message = result.op == "created" ? "초대 코드를 공유하세요" : "온라인 방에 들어왔습니다";
            }
            else if (result.op == "resume" && !sessionPresent)
            {
                _joined = false;
                _message = string.IsNullOrEmpty(result.inviteCode) ? "방을 만들거나 초대 코드로 참여하세요" : "초대 코드에 연결할 저장된 좌석이 없습니다";
            }
            else if (result.op == "command") _message = FriendlyCommand(result.reason, result.accepted, result.idempotent);
            else if (result.op == "state") _message = AvailabilityMessage();
            else if (authenticatedState) _message = AvailabilityMessage();
            PublishState();
        }

        private bool ApplyAuthenticatedResult(OnlineResult result)
        {
            if (!HasAuthenticatedState(result)) return false;
            SetRoom(result.room);
            if (!_roomReady) return false;
            _seatCode = result.seat;
            ApplyAvailability(result.availability);
            if (!ApplyWireState(result.state, result.expressions, result.room)) return false;
            _joined = true;
            _transportLocked = false;
            return true;
        }

        private static bool HasAuthenticatedState(OnlineResult result)
        {
            return result != null && HasRoomPayload(result.room) && HasStatePayload(result.state) &&
                (result.seat == 0 || result.seat == 1);
        }

        private static bool HasRoomPayload(OnlineRoomView room)
        {
            return room != null && !string.IsNullOrEmpty(room.roomId);
        }

        private static bool HasStatePayload(OnlineWireState state)
        {
            return state != null && !string.IsNullOrEmpty(state.roomId);
        }

        private static bool IsNoSessionResume(OnlineResult result, bool sessionPresent, bool hasRoom, bool hasState)
        {
            return result != null && result.op == "resume" && !sessionPresent && !hasRoom && !hasState;
        }

        private static bool RequiresAuthenticatedState(string operation)
        {
            return operation == "state" || operation == "command";
        }

        private void RejectAuthenticatedPayload()
        {
            LockForTransportFailure("online_authenticated_payload_invalid");
            _error = "online_authenticated_payload_invalid";
            _message = "온라인 상태를 안전하게 적용하지 못했습니다";
        }

        private void SetRoom(OnlineRoomView room)
        {
            if (room == null || string.IsNullOrEmpty(room.roomId)) return;
            if (!string.Equals(_roomId, room.roomId, StringComparison.Ordinal)) ClearAuthoritativeState(true);
            _roomView = room;
            _roomId = room.roomId;
            string definitionId = ResolveDefinitionId(room);
            CoopRoomDefinition definition;
            if (!TryGetDefinition(definitionId, out definition))
            {
                _roomReady = false;
                return;
            }
            _room = definition;
            _activeDefinitionId = definitionId;
            _roomReady = MatchesBundledRoom(room, definition);
            LoadBestRecord();
        }

        private bool LoadBundledDefinitions()
        {
            _definitions.Clear();
            var views = new List<OnlineDefinitionView>();
            for (int index = 0; index < DefinitionIds.Length; index++)
            {
                string definitionId = DefinitionIds[index];
                TextAsset asset = Resources.Load<TextAsset>("CoopRooms/" + definitionId);
                if (asset == null)
                {
                    if (definitionId == DefaultDefinitionId) return false;
                    continue;
                }
                CoopRoomDefinition definition = JsonUtility.FromJson<CoopRoomDefinition>(asset.text);
                if (definition == null || !string.Equals(definition.Id, definitionId, StringComparison.Ordinal) || CoopRules.ValidateRoom(definition).Length > 0)
                {
                    if (definitionId == DefaultDefinitionId) return false;
                    continue;
                }
                CoopRoomDefinition clone = CoopRules.CloneRoom(definition);
                _definitions.Add(definitionId, clone);
                views.Add(new OnlineDefinitionView
                {
                    definitionId = clone.Id,
                    roomResource = "CoopRooms/" + clone.Id,
                    rulesVersion = clone.RulesVersion,
                    contentVersion = clone.ContentVersion,
                    roomFingerprint = CoopRules.RoomFingerprint(clone)
                });
            }
            _definitionViews = views.ToArray();
            return _definitions.ContainsKey(DefaultDefinitionId);
        }

        private bool TryGetDefinition(string definitionId, out CoopRoomDefinition definition)
        {
            definition = null;
            if (string.IsNullOrEmpty(definitionId)) return false;
            CoopRoomDefinition source;
            if (!_definitions.TryGetValue(definitionId, out source))
            {
                if (_room == null || !string.Equals(_room.Id, definitionId, StringComparison.Ordinal)) return false;
                source = _room;
            }
            definition = CoopRules.CloneRoom(source);
            return true;
        }

        private string ResolveDefinitionId(OnlineRoomView room)
        {
            if (room == null) return string.Empty;
            if (!string.IsNullOrEmpty(room.definitionId)) return room.definitionId;
            if (!string.IsNullOrEmpty(room.roomResource) && room.roomResource.StartsWith("CoopRooms/", StringComparison.Ordinal)) return room.roomResource.Substring("CoopRooms/".Length);
            return string.Equals(room.contentVersion, "coop-c1-v1", StringComparison.Ordinal) ? DefaultDefinitionId : string.Empty;
        }

        private void ApplyAvailability(OnlineAvailability availability)
        {
            if (availability == null) return;
            _availabilityCode = availability.status;
            _circleConnected = availability.circleConnected;
            _diamondConnected = availability.diamondConnected;
        }

        private void SendCommand(OnlineInput input, int kind, int direction, int expressionCode, string requestId, bool approve)
        {
            if (!_joined || !_roomReady || _serverState == null || _availabilityCode != 1 || _transitioning)
            {
                FailMessage("두 좌석이 연결될 때까지 기다려 주세요", "online_not_active");
                return;
            }
            if (input.expectedRevision < 0) input.expectedRevision = _authorityRevision;
            var command = new OnlineCommandRequest
            {
                commandId = NextCommandId(),
                expectedRevision = input.expectedRevision,
                kind = kind,
                direction = direction,
                requestId = requestId,
                approve = approve,
                expression = expressionCode
            };
#if UNITY_WEBGL && !UNITY_EDITOR
            NectorialOnlineCommand(JsonUtility.ToJson(command));
#endif
        }

        private bool CanResolveConsent(string requestId)
        {
            if (_serverState == null || _serverState.PendingConsent == null || _seatCode < 0)
            {
                FailMessage("동의 요청을 다시 확인해 주세요", "online_consent_missing");
                return false;
            }
            if ((int)_serverState.PendingConsent.Requester == _seatCode)
            {
                FailMessage("상대 좌석의 동의를 기다려 주세요", "online_consent_self");
                return false;
            }
            if (!string.Equals(_serverState.PendingConsent.RequestId, requestId, StringComparison.Ordinal))
            {
                FailMessage("동의 요청이 바뀌었습니다", "online_consent_mismatch");
                return false;
            }
            return true;
        }

        private bool ApplyWireState(OnlineWireState wire, OnlineExpressions expressions, OnlineRoomView room)
        {
            CoopState converted;
            string conversionError;
            if (!_roomReady || !TryToCoreState(_room, wire, out converted, out conversionError))
            {
                RejectWireState();
                return false;
            }
            if (_authorityRevision >= 0 && (wire.authorityRevision < _authorityRevision ||
                (wire.authorityRevision == _authorityRevision && wire.logicalActionCount < _logicalActionCount))) return true;
            _authorityRevision = wire.authorityRevision;
            _logicalActionCount = wire.logicalActionCount;
            _serverState = converted;
            if (_board != null && _roomReady) _board.Render(_room, _serverState);
            ApplyExpressions(expressions);
            if (_serverState.Status == CoopRunStatus.Cleared && _serverState.PendingConsent == null)
            {
                string fingerprint = CoopRules.StateFingerprint(_room, _serverState);
                if (!string.Equals(_lastAutoRecordFingerprint, fingerprint, StringComparison.Ordinal)
                    && !string.Equals(_pendingAutoRecordFingerprint, fingerprint, StringComparison.Ordinal)
                    && RequestRecord(false))
                {
                    _pendingAutoRecordFingerprint = fingerprint;
                }
            }
            else InvalidateAutomaticRecordRequest();
            return true;
        }

        private void RejectWireState()
        {
            LockForTransportFailure("online_state_invalid");
            _error = "online_state_invalid";
            _message = "온라인 상태를 안전하게 적용하지 못했습니다";
        }

        private void ApplyExpressions(OnlineExpressions expressions)
        {
            if (expressions == null) return;
            long incoming = expressions.expressionSequence;
            if (!_expressionHydrated)
            {
                _expressionHighWater = incoming;
                _visibleExpressionSequence = incoming;
                _expressionHydrated = true;
                return;
            }
            if (incoming <= _expressionHighWater) return;
            _expressionHighWater = incoming;
            if (expressions.events == null) return;
            for (int index = 0; index < expressions.events.Length; index++)
            {
                OnlineExpressionEvent item = expressions.events[index];
                if (item == null || item.sequence <= _visibleExpressionSequence || item.sequence > incoming) continue;
                _visibleExpressionSequence = item.sequence;
                _visibleExpressionSender = item.sender == 1 ? "Diamond" : "Circle";
                _visibleExpression = ExpressionName(item.expression);
            }
        }

        private static PendingConsentObservation ToObservation(CoopPendingConsent pending)
        {
            if (pending == null) return null;
            return new PendingConsentObservation
            {
                active = true,
                requestId = pending.RequestId,
                kind = pending.Kind.ToString(),
                statusCode = pending.Kind.ToString(),
                requester = pending.Requester.ToString(),
                requesterCode = (int)pending.Requester,
                requestedAtRevision = pending.RequestedAtRevision
            };
        }

        private bool MatchesBundledRoom(OnlineRoomView room, CoopRoomDefinition definition)
        {
            return room != null && definition != null && string.Equals(ResolveDefinitionId(room), definition.Id, StringComparison.Ordinal)
                && string.Equals(room.contentVersion, definition.ContentVersion, StringComparison.Ordinal)
                && string.Equals(room.rulesVersion, definition.RulesVersion, StringComparison.Ordinal)
                && string.Equals(room.roomFingerprint, CoopRules.RoomFingerprint(definition), StringComparison.Ordinal);
        }

        private static bool TryToCoreState(CoopRoomDefinition room, OnlineWireState wire, out CoopState state, out string error)
        {
            state = null;
            error = null;
            if (room == null || wire == null) { error = "online_state_missing"; return false; }
            if (!string.Equals(wire.roomId, room.Id, StringComparison.Ordinal)) { error = "online_state_room_id_mismatch"; return false; }
            if (wire.circlePosition == null || wire.diamondPosition == null) { error = "online_state_position_missing"; return false; }
            if (wire.activeActor != 0 && wire.activeActor != 1) { error = "online_state_actor_invalid"; return false; }
            if (wire.status != 0 && wire.status != 1 && wire.status != 2) { error = "online_state_status_invalid"; return false; }

            CoopPendingConsent pending;
            if (!TryNormalizeWirePending(wire.pendingConsent, out pending, out error)) return false;
            var converted = new CoopState
            {
                RoomId = wire.roomId,
                CirclePosition = new GridPoint(wire.circlePosition.x, wire.circlePosition.y),
                DiamondPosition = new GridPoint(wire.diamondPosition.x, wire.diamondPosition.y),
                ActiveActor = wire.activeActor == 1 ? CoopActor.Diamond : CoopActor.Circle,
                AuthorityRevision = wire.authorityRevision,
                LogicalActionCount = wire.logicalActionCount,
                Status = wire.status == 1 ? CoopRunStatus.Cleared : (wire.status == 2 ? CoopRunStatus.Paused : CoopRunStatus.Playing),
                PendingConsent = pending
            };
            string[] errors = CoopRules.ValidateState(room, converted);
            if (errors.Length > 0) { error = "online_state_" + errors[0]; return false; }
            state = converted;
            return true;
        }

        private static bool TryNormalizeWirePending(OnlinePendingConsent wire, out CoopPendingConsent pending, out string error)
        {
            pending = null;
            error = null;
            var envelope = new CoopSaveEnvelope
            {
                State = new CoopState
                {
                    PendingConsent = wire == null ? null : new CoopPendingConsent
                    {
                        RequestId = wire.requestId,
                        Kind = (CoopConsentKind)wire.kind,
                        Requester = (CoopActor)wire.requester,
                        RequestedAtRevision = wire.requestedAtRevision
                    }
                }
            };
            string normalizationError;
            if (!CoopSaveSerializationAdapter.TryNormalizePendingConsent(envelope, out normalizationError))
            {
                error = "online_pending_consent_invalid";
                return false;
            }
            pending = envelope.State.PendingConsent;
            return true;
        }

#if UNITY_EDITOR
        public static bool TryDeserializeServerStateForCheck(CoopRoomDefinition room, string json, out CoopState state, out string error)
        {
            state = null;
            error = null;
            OnlineWireState wire;
            try { wire = JsonUtility.FromJson<OnlineWireState>(json); }
            catch (Exception exception) { error = "online_state_json_" + exception.GetType().Name; return false; }
            return TryToCoreState(room, wire, out state, out error);
        }

        public static bool TryDeserializeRecordEnvelopeForCheck(string json, out bool hasMine, out bool hasShared, out string error)
        {
            hasMine = false;
            hasShared = false;
            error = null;
            RecordObservationEnvelope envelope;
            try { envelope = JsonUtility.FromJson<RecordObservationEnvelope>(json); }
            catch (Exception exception) { error = "record_observation_json:" + exception.GetType().Name; return false; }
            if (envelope == null) { error = "record_observation_missing"; return false; }
            hasMine = RecordObservationGuard.IsUsable(envelope.hasMine, envelope.mine);
            hasShared = RecordObservationGuard.IsUsable(envelope.hasShared, envelope.shared);
            if (envelope.hasMine && !hasMine) { error = "mine_record_invalid"; return false; }
            if (envelope.hasShared && !hasShared) { error = "shared_record_invalid"; return false; }
            return true;
        }
#endif

        private void GetRecord()
        {
            RequestRecord(true);
        }

        private bool RequestRecord(bool userRequested)
        {
            if (!_joined || !_roomReady || _serverState == null || _serverState.Status != CoopRunStatus.Cleared)
            {
                if (userRequested) SetRecordFailure("record_not_cleared", "완주한 뒤 기록을 준비할 수 있습니다");
                return false;
            }
            if (_serverState.PendingConsent != null)
            {
                if (userRequested) SetRecordFailure("record_consent_pending", "다시 시작 동의가 끝난 뒤 기록을 확인할 수 있습니다");
                return false;
            }
            string requestId = "record-" + checked(++_recordRequestSequence).ToString();
            _pendingRecordRequestId = requestId;
            _pendingRecordStateFingerprint = CoopRules.StateFingerprint(_room, _serverState);
            _pendingRecordAutomatic = !userRequested;
            _recordStatus = "loading";
            _recordError = string.Empty;
            _recordErrorFromAutomatic = false;
            if (userRequested) _message = "완주 기록을 확인하는 중입니다";
#if UNITY_WEBGL && !UNITY_EDITOR
            NectorialOnlineGetRecord(requestId);
            if (userRequested) PublishState();
            return true;
#else
            ClearPendingRecordRequest(false);
            if (userRequested) PublishState();
            return false;
#endif
        }

        private void ApplyRecordResult(OnlineResult result)
        {
            if (result == null || string.IsNullOrEmpty(_pendingRecordRequestId) || !string.Equals(result.recordRequestId, _pendingRecordRequestId, StringComparison.Ordinal)) return;
            if (!IsPendingRecordStateCurrent())
            {
                ClearPendingRecordRequest(false);
                return;
            }
            bool automatic = _pendingRecordAutomatic;
            if (result == null || !result.ok)
            {
                string code = result == null || result.error == null ? "record_unavailable" : result.error.code;
                _recordStatus = code == "record_not_cleared" ? "unavailable" : "invalid";
                _recordError = code;
                _recordErrorFromAutomatic = automatic;
                _message = "완주 기록을 준비하지 못했습니다";
                ClearPendingRecordRequest(false);
                return;
            }
            RecordSummaryObservation summary;
            string error;
            if (!TryValidateCoopRecord(result.capsule, out summary, out error) || !string.Equals(summary.definitionId, _activeDefinitionId, StringComparison.Ordinal))
            {
                _recordStatus = "invalid";
                _recordError = string.IsNullOrEmpty(error) ? "record_active_definition_mismatch" : error;
                _recordErrorFromAutomatic = automatic;
                _message = "완주 기록이 현재 판과 맞지 않습니다";
                ClearPendingRecordRequest(false);
                return;
            }
            _recordCapsule = result.capsule;
            ConsiderBestRecord(result.capsule, summary);
            _recordStatus = "ready";
            if (string.IsNullOrEmpty(_recordError)) _recordError = string.Empty;
            _message = "완주 기록을 준비했습니다";
            ClearPendingRecordRequest(true);
        }

        private void ClearPendingRecordRequest(bool accepted)
        {
            if (accepted && _pendingRecordAutomatic && !string.IsNullOrEmpty(_pendingAutoRecordFingerprint))
                _lastAutoRecordFingerprint = _pendingAutoRecordFingerprint;
            _pendingRecordRequestId = string.Empty;
            _pendingRecordStateFingerprint = string.Empty;
            _pendingRecordAutomatic = false;
            _pendingAutoRecordFingerprint = string.Empty;
        }

        private bool IsPendingRecordStateCurrent()
        {
            return _room != null && _serverState != null && _serverState.Status == CoopRunStatus.Cleared && _serverState.PendingConsent == null
                && !string.IsNullOrEmpty(_pendingRecordStateFingerprint)
                && string.Equals(_pendingRecordStateFingerprint, CoopRules.StateFingerprint(_room, _serverState), StringComparison.Ordinal);
        }

        private void InvalidateAutomaticRecordRequest()
        {
            bool wasAutomatic = _pendingRecordAutomatic;
            if (!string.IsNullOrEmpty(_pendingRecordRequestId)) ClearPendingRecordRequest(false);
            _pendingAutoRecordFingerprint = string.Empty;
            _lastAutoRecordFingerprint = string.Empty;
            if (wasAutomatic || _recordErrorFromAutomatic)
            {
                _recordStatus = "idle";
                _recordError = string.Empty;
                _recordErrorFromAutomatic = false;
            }
        }

        private void LoadSharedRecord(string encoded, string requestId)
        {
            _sharedRecordRequestId = NormalizeSharedRecordRequestId(requestId);
            _hasShared = false;
            _shared = null;
            RecordSummaryObservation summary;
            string error;
            if (!TryValidateCoopRecord(encoded, out summary, out error))
            {
                SetRecordFailure(error, "공유 기록을 읽지 못했습니다");
                return;
            }
            _shared = summary;
            _hasShared = true;
            _recordStatus = "ready";
            _recordError = string.Empty;
            _message = "공유 기록을 확인했습니다";
            PublishState();
        }

        private static string NormalizeSharedRecordRequestId(string requestId)
        {
            return string.IsNullOrEmpty(requestId) || requestId.Length > SharedRecordRequestIdMaximumLength ? string.Empty : requestId;
        }

        private void ChallengeSharedRecord()
        {
            if (!_hasShared || !RecordObservationGuard.IsUsable(true, _shared))
            {
                SetRecordFailure("shared_record_missing", "먼저 검증된 공유 기록을 불러오세요");
                return;
            }
            if (_joined)
            {
                SetRecordFailure("challenge_requires_new_room", "현재 온라인 판을 나간 뒤 새 판을 만드세요");
                return;
            }
            if (!ApplyPreviewDefinition(_shared.definitionId))
            {
                SetRecordFailure("challenge_definition_unavailable", "공유 기록의 협력 판을 찾지 못했습니다");
                return;
            }
            _recordStatus = "ready";
            _recordError = string.Empty;
            _message = "공유 기록의 판을 골랐습니다. 방 만들기를 선택하세요";
            PublishState();
        }

        private bool TryValidateCoopRecord(string encoded, out RecordSummaryObservation summary, out string error)
        {
            summary = null;
            error = null;
            RecordCapsule capsule;
            if (!RecordCapsuleCodec.TryDecode(encoded, out capsule, out error)) return false;
            CoopRoomDefinition definition;
            if (!TryGetDefinition(capsule.DefinitionId, out definition)) { error = "record_definition_unavailable"; return false; }
            RecordVerification verification;
            if (!RecordCapsuleVerifier.TryVerifyCoop(definition, capsule, out verification)) { error = verification.ErrorCode; return false; }
            summary = RecordSummaryObservation.From(verification);
            if (!RecordObservationGuard.IsUsable(true, summary)) { error = "record_summary_invalid"; summary = null; return false; }
            return true;
        }

        private void LoadBestRecord()
        {
            _hasMine = false;
            _mine = null;
            _mineCapsule = string.Empty;
            if (_room == null) return;
            RecordCapsule identity = CurrentRecordIdentity(_room);
            string encoded;
            if (!LocalRecordBestStore.TryRead(identity, out encoded)) return;
            RecordSummaryObservation summary;
            string error;
            if (!TryValidateCoopRecord(encoded, out summary, out error)) return;
            if (!string.Equals(summary.definitionId, _activeDefinitionId, StringComparison.Ordinal)) return;
            _mine = summary;
            _mineCapsule = encoded;
            _hasMine = true;
        }

        private void ConsiderBestRecord(string encoded, RecordSummaryObservation candidate)
        {
            if (_room == null || !RecordObservationGuard.IsUsable(true, candidate)) return;
            if (_hasMine && !LocalRecordBestStore.IsBetter(candidate, _mine)) return;
            RecordCapsule identity = CurrentRecordIdentity(_room);
            if (!LocalRecordBestStore.TryWrite(identity, encoded))
            {
                _recordError = "record_best_write_failed";
                return;
            }
            _mine = candidate;
            _mineCapsule = encoded;
            _hasMine = true;
        }

        private static RecordCapsule CurrentRecordIdentity(CoopRoomDefinition room)
        {
            return new RecordCapsule
            {
                SchemaVersion = RecordCapsuleRules.SchemaVersion,
                ModeId = RecordCapsuleRules.CoopModeId,
                DefinitionId = room.Id,
                RulesVersion = room.RulesVersion,
                ContentVersion = room.ContentVersion,
                DefinitionFingerprint = CoopRules.RoomFingerprint(room),
                InputSequence = string.Empty
            };
        }

        private void SetRecordFailure(string code, string message)
        {
            _recordStatus = code == "record_not_cleared" ? "unavailable" : "invalid";
            _recordError = string.IsNullOrEmpty(code) ? "record_invalid" : code;
            _message = message;
            PublishState();
        }

        private void SelectDefinition(string definitionId)
        {
            if (_joined)
            {
                FailMessage("현재 온라인 판을 마친 뒤 다음 판을 고르세요", "online_definition_active");
                return;
            }
            if (!ApplyPreviewDefinition(definitionId))
            {
                FailMessage("협력 판을 확인해 주세요", "online_definition_not_allowed");
                return;
            }
            _message = "다음 온라인 판을 골랐습니다";
            _error = string.Empty;
            PublishState();
        }

        private bool ApplyPreviewDefinition(string definitionId)
        {
            CoopRoomDefinition definition;
            if (!TryGetDefinition(definitionId, out definition)) return false;
            _selectedDefinitionId = definitionId;
            _room = definition;
            _activeDefinitionId = definitionId;
            LoadBestRecord();
            return true;
        }

        private void ResetRoomView()
        {
            _joined = false;
            _roomReady = false;
            _roomId = string.Empty;
            _inviteCode = string.Empty;
            _seatCode = -1;
            _roomView = null;
            ClearAuthoritativeState(true);
            ApplyPreviewDefinition(_selectedDefinitionId);
            _message = "온라인 방을 준비하세요";
            _error = string.Empty;
            PublishState();
        }

        private void BeginRoomAttempt(string message)
        {
            BeginRoomAttempt(message, _selectedDefinitionId);
        }

        private void BeginRoomAttempt(string message, string definitionId)
        {
            if (!ApplyPreviewDefinition(definitionId))
            {
                FailMessage("협력 판을 확인해 주세요", "online_definition_not_allowed");
                return;
            }
            _joined = false;
            _roomReady = false;
            _roomId = string.Empty;
            _inviteCode = string.Empty;
            _seatCode = -1;
            _roomView = null;
            ClearAuthoritativeState(true);
            _message = message;
            _error = string.Empty;
            PublishState();
        }

        private void LockForTransportFailure(string code)
        {
            bool terminal = code == "room_expired" || code == "seat_token_invalid" || code == "room_access_denied" || code == "room_not_found";
            if (terminal)
            {
                ClearAuthoritativeState(true);
                _availabilityCode = code == "room_expired" ? 3 : 0;
                _joined = false;
                _roomReady = false;
                _roomId = string.Empty;
                _inviteCode = string.Empty;
                _seatCode = -1;
                _roomView = null;
                return;
            }
            _transportLocked = true;
            _availabilityCode = 0;
            _circleConnected = false;
            _diamondConnected = false;
            _transitioning = false;
        }

        private void ClearAuthoritativeState(bool resetExpressions)
        {
            _serverState = null;
            _authorityRevision = -1;
            _logicalActionCount = 0;
            _availabilityCode = 0;
            _circleConnected = false;
            _diamondConnected = false;
            _transportLocked = true;
            _transitioning = false;
            InvalidateAutomaticRecordRequest();
            if (!resetExpressions) return;
            _expressionHighWater = 0;
            _expressionHydrated = false;
            _visibleExpressionSequence = 0;
            _visibleExpressionSender = string.Empty;
            _visibleExpression = string.Empty;
        }

        private void Fail(string message, string error)
        {
            _initialized = false;
            _error = error;
            _message = message;
            PublishState();
        }

        private void FailMessage(string message, string error)
        {
            _message = message;
            _error = error;
            PublishState();
        }

        private void PublishState()
        {
            var observation = new OnlineObservation
            {
                initialized = _initialized,
                online = true,
                joined = _joined,
                roomId = _roomId,
                inviteCode = _inviteCode,
                seatCode = _seatCode,
                availabilityCode = _availabilityCode,
                availabilityStatus = AvailabilityName(_availabilityCode),
                circleConnected = _circleConnected,
                diamondConnected = _diamondConnected,
                inputEnabled = _joined && _roomReady && _serverState != null && !_transportLocked && _availabilityCode == 1 && !_transitioning && IsMyTurn(),
                activeActorCode = _serverState == null ? string.Empty : _serverState.ActiveActor.ToString(),
                authorityRevision = _authorityRevision,
                logicalActionCount = _logicalActionCount,
                statusCode = _serverState == null ? "Waiting" : _serverState.Status.ToString(),
                message = _message,
                error = _error,
                transitioning = _transitioning,
                circleAtGoal = _serverState != null && _room != null && CoopRules.Same(_serverState.CirclePosition, _room.CircleGoal),
                diamondAtGoal = _serverState != null && _room != null && CoopRules.Same(_serverState.DiamondPosition, _room.DiamondGoal),
                pendingConsent = _serverState == null ? null : ToObservation(_serverState.PendingConsent),
                expressionSequence = _visibleExpressionSequence,
                expressionSender = _visibleExpressionSender,
                expression = _visibleExpression,
                localHotseat = false,
                seatAuthority = "bearer_derived",
                roomMismatch = _joined && !_roomReady,
                transportLocked = _transportLocked,
                definitions = _definitionViews,
                activeDefinitionId = _activeDefinitionId,
                selectedDefinitionId = _selectedDefinitionId,
                recordStatus = _recordStatus,
                recordCapsule = _recordCapsule,
                hasMine = _hasMine && RecordObservationGuard.IsUsable(true, _mine),
                mine = _hasMine && RecordObservationGuard.IsUsable(true, _mine) ? _mine : null,
                hasShared = _hasShared && RecordObservationGuard.IsUsable(true, _shared),
                shared = _hasShared && RecordObservationGuard.IsUsable(true, _shared) ? _shared : null,
                sharedRecordRequestId = _sharedRecordRequestId,
                recordError = _recordError
            };
            string observationJson = JsonUtility.ToJson(observation);
#if UNITY_EDITOR
            _lastObservationJsonForCheck = observationJson;
#endif
            var safeLog = new OnlineSafeLog
            {
                initialized = observation.initialized,
                availabilityCode = observation.availabilityCode,
                circleConnected = observation.circleConnected,
                diamondConnected = observation.diamondConnected,
                activeActorCode = observation.activeActorCode,
                authorityRevision = observation.authorityRevision,
                logicalActionCount = observation.logicalActionCount,
                statusCode = observation.statusCode,
                circleAtGoal = observation.circleAtGoal,
                diamondAtGoal = observation.diamondAtGoal,
                expressionSequence = observation.expressionSequence,
                expressionSender = observation.expressionSender,
                stateFingerprint = _serverState == null || _room == null ? string.Empty : CoopRules.StateFingerprint(_room, _serverState)
            };
            Debug.Log("COOP_ONLINE_STATE_OBSERVATION " + JsonUtility.ToJson(safeLog));
#if UNITY_WEBGL && !UNITY_EDITOR
            NectorialOnlineReportState(observationJson);
#endif
        }

        private bool IsMyTurn()
        {
            return _serverState != null && _seatCode >= 0 && (int)_serverState.ActiveActor == _seatCode;
        }

        private static string AvailabilityName(int code)
        {
            switch (code)
            {
                case 1: return "연결됨";
                case 2: return "상대 재접속 대기";
                case 3: return "방 만료";
                default: return "상대 입장 대기";
            }
        }

        private string AvailabilityMessage()
        {
            return AvailabilityName(_availabilityCode);
        }

        private static string FriendlyError(string code)
        {
            if (code == "room_waiting_for_peer") return "상대 좌석의 입장을 기다리고 있습니다";
            if (code == "room_paused_offline") return "상대 재접속을 기다리고 있습니다";
            if (code == "room_expired") return "온라인 방이 만료되었습니다";
            if (code == "pass_not_supported") return "Pass 없이 다음 좌석의 차례를 기다립니다";
            return "온라인 요청을 처리하지 못했습니다";
        }

        private static string FriendlyCommand(string reason, bool accepted, bool idempotent)
        {
            if (accepted) return idempotent ? "현재 온라인 상태를 확인했습니다" : "온라인 행동을 적용했습니다";
            return FriendlyError(reason);
        }

        private static string ExpressionName(int expression)
        {
            switch (expression)
            {
                case 1: return "ThumbsUp";
                case 2: return "Handshake";
                case 3: return "Waiting";
                default: return "Look";
            }
        }

        private static void ConfigureCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                var cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
            }
            camera.orthographic = true;
            camera.orthographicSize = 4.65f;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(0xf0, 0xec, 0xe2, 0xff);
        }

        private string NextCommandId()
        {
            _commandSequence = checked(_commandSequence + 1);
            return _commandPrefix + _commandSequence;
        }

        private static bool TryParseDirection(string value, out int direction)
        {
            direction = 0;
            if (value == "Up") return true;
            if (value == "Down") { direction = 1; return true; }
            if (value == "Left") { direction = 2; return true; }
            if (value == "Right") { direction = 3; return true; }
            return false;
        }

        private static bool TryParseExpression(string value, out int expression)
        {
            expression = 0;
            if (value == "Look") return true;
            if (value == "ThumbsUp") { expression = 1; return true; }
            if (value == "Handshake") { expression = 2; return true; }
            if (value == "Waiting") { expression = 3; return true; }
            return false;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void NectorialOnlineResume();
        [DllImport("__Internal")]
        private static extern void NectorialOnlineResumeInvite(string inviteCode);
        [DllImport("__Internal")]
        private static extern void NectorialOnlineCreate();
        [DllImport("__Internal")]
        private static extern void NectorialOnlineCreateDefinition(string definitionId);
        [DllImport("__Internal")]
        private static extern void NectorialOnlineJoin(string inviteCode);
        [DllImport("__Internal")]
        private static extern void NectorialOnlineCommand(string json);
        [DllImport("__Internal")]
        private static extern void NectorialOnlineLeave();
        [DllImport("__Internal")]
        private static extern void NectorialOnlineGetRecord(string recordRequestId);
        [DllImport("__Internal")]
        private static extern void NectorialOnlineReportState(string json);
#endif

        [Serializable] private sealed class OnlineInput { public string kind; public string direction; public string expression; public string requestId; public string inviteCode; public string definitionId; public string capsule; public bool approve; public long expectedRevision = -1; }
        [Serializable] private sealed class OnlineCommandRequest { public string commandId; public long expectedRevision; public int kind; public int direction; public string requestId; public bool approve; public int expression; }
        [Serializable] private sealed class OnlineResult { public bool ok; public string op; public string inviteCode; public int seat = -1; public OnlineRoomView room; public OnlineWireState state; public OnlineAvailability availability; public OnlineExpressions expressions; public bool accepted; public bool idempotent; public string reason; public string capsule; public string recordRequestId; public OnlineRecordVerification verification; public OnlineError error; }
        [Serializable] private sealed class OnlineRoomView { public string roomId; public string definitionId; public string roomResource; public string rulesVersion; public string contentVersion; public string roomFingerprint; public string expiresAtUtc; }
        [Serializable] private sealed class OnlineDefinitionView { public string definitionId; public string roomResource; public string rulesVersion; public string contentVersion; public string roomFingerprint; }
        [Serializable] private sealed class OnlineRecordVerification { public string modeId; public string definitionId; public string rulesVersion; public string contentVersion; public string definitionFingerprint; public string statusCode; public int effectiveActionCount; public int logicalActionCount; public int hits; public int circleX; public int circleY; public int diamondX; public int diamondY; }
        [Serializable] private sealed class OnlineWireState { public string roomId; public OnlinePoint circlePosition; public OnlinePoint diamondPosition; public int activeActor; public long authorityRevision; public int logicalActionCount; public int status; public OnlinePendingConsent pendingConsent; }
        [Serializable] private sealed class OnlinePoint { public int x; public int y; }
        [Serializable] private sealed class OnlinePendingConsent { public string requestId; public int kind; public int requester; public long requestedAtRevision; }
        [Serializable] private sealed class OnlineAvailability { public int status; public bool circleConnected; public bool diamondConnected; public string heartbeatExpiresAtUtc; }
        [Serializable] private sealed class OnlineExpressions { public long expressionSequence; public OnlineExpressionEvent[] events; }
        [Serializable] private sealed class OnlineExpressionEvent { public long sequence; public int sender; public int expression; public string occurredAtUtc; }
        [Serializable] private sealed class OnlineError { public string code; }
        [Serializable] private sealed class OnlineObservation
        {
            public bool initialized; public bool online; public bool joined; public string roomId; public string inviteCode; public int seatCode; public int availabilityCode; public string availabilityStatus; public bool circleConnected; public bool diamondConnected; public bool inputEnabled; public string activeActorCode; public long authorityRevision; public int logicalActionCount; public string statusCode; public string message; public string error; public bool transitioning; public bool circleAtGoal; public bool diamondAtGoal; public PendingConsentObservation pendingConsent; public long expressionSequence; public string expressionSender; public string expression; public bool localHotseat; public string seatAuthority; public bool roomMismatch; public bool transportLocked; public OnlineDefinitionView[] definitions; public string activeDefinitionId; public string selectedDefinitionId; public string recordStatus; public string recordCapsule; public bool hasMine; public RecordSummaryObservation mine; public bool hasShared; public RecordSummaryObservation shared; public string sharedRecordRequestId; public string recordError;
        }
        [Serializable] private sealed class OnlineSafeLog
        {
            public bool initialized; public int availabilityCode; public bool circleConnected; public bool diamondConnected; public string activeActorCode; public long authorityRevision; public int logicalActionCount; public string statusCode; public bool circleAtGoal; public bool diamondAtGoal; public long expressionSequence; public string expressionSender; public string stateFingerprint;
        }
        [Serializable] private sealed class PendingConsentObservation { public bool active; public string requestId; public string kind; public string statusCode; public string requester; public int requesterCode; public long requestedAtRevision; }
    }
}
