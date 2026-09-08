using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using Nectorial.SlideEscape.Unity.Coop;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity.CoopOnline
{
    public sealed class CoopOnlineBootstrap : MonoBehaviour
    {
        private const string ProductName = "Few Moves Online Pilot";
        private const string RoomResource = "CoopRooms/coop-c1";
        private const string AtlasResource = "Visuals/turn-escape-tiles";
        private const float PollExpressionCooldownSeconds = 2f;

        private CoopRoomDefinition _room;
        private CoopBoardView _board;
        private bool _initialized;
        private bool _roomReady;
        private bool _joined;
        private bool _transitioning;
        private int _seatCode = -1;
        // Network room UUID for seat/authentication and callback isolation; it is not the Core puzzle ID.
        private string _roomId = string.Empty;
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
                TextAsset roomAsset = Resources.Load<TextAsset>(RoomResource);
                if (atlas == null || roomAsset == null)
                {
                    Fail("필수 협력 화면 자료를 읽을 수 없습니다", "online_asset_missing");
                    return;
                }

                _room = JsonUtility.FromJson<CoopRoomDefinition>(roomAsset.text);
                string[] errors = CoopRules.ValidateRoom(_room);
                if (errors.Length > 0)
                {
                    Fail("협력 방 자료가 올바르지 않습니다", "online_room_invalid:" + errors[0]);
                    return;
                }

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
                case "Create":
                    BeginRoomAttempt("온라인 방을 만드는 중입니다");
#if UNITY_WEBGL && !UNITY_EDITOR
                    NectorialOnlineCreate();
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

            if (!result.ok)
            {
                _error = result.error != null ? result.error.code : "online_request_failed";
                _message = FriendlyError(_error);
                if (!ApplyAuthenticatedResult(result)) LockForTransportFailure(_error);
                PublishState();
                return;
            }

            _error = string.Empty;
            if (result.seat == 0 || result.seat == 1) _seatCode = result.seat;
            if (!string.IsNullOrEmpty(result.inviteCode)) _inviteCode = result.inviteCode;
            if (result.room != null)
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
            bool sessionPresent = result.seat == 0 || result.seat == 1;
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
            if (result == null || result.room == null || result.state == null || (result.seat != 0 && result.seat != 1)) return false;
            SetRoom(result.room);
            if (!_roomReady) return false;
            _seatCode = result.seat;
            ApplyAvailability(result.availability);
            ApplyWireState(result.state, result.expressions, result.room);
            _joined = true;
            return true;
        }

        private void SetRoom(OnlineRoomView room)
        {
            if (room == null || string.IsNullOrEmpty(room.roomId)) return;
            if (!string.Equals(_roomId, room.roomId, StringComparison.Ordinal)) ClearAuthoritativeState(true);
            _roomView = room;
            _roomId = room.roomId;
            _roomReady = MatchesBundledRoom(room);
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

        private void ApplyWireState(OnlineWireState wire, OnlineExpressions expressions, OnlineRoomView room)
        {
            // Core state carries the bundled puzzle ID (for C1: coop-c1); _roomId is the server session UUID.
            if (wire == null || !_roomReady || _room == null || !string.Equals(wire.roomId, _room.Id, StringComparison.Ordinal)) return;
            if (_authorityRevision >= 0 && (wire.authorityRevision < _authorityRevision ||
                (wire.authorityRevision == _authorityRevision && wire.logicalActionCount < _logicalActionCount))) return;
            _authorityRevision = wire.authorityRevision;
            _logicalActionCount = wire.logicalActionCount;
            _serverState = ToCoreState(wire);
            if (_board != null && _roomReady) _board.Render(_room, _serverState);
            ApplyExpressions(expressions);
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

        private bool MatchesBundledRoom(OnlineRoomView room)
        {
            return room != null && string.Equals(room.contentVersion, _room.ContentVersion, StringComparison.Ordinal) &&
                string.Equals(room.rulesVersion, _room.RulesVersion, StringComparison.Ordinal) &&
                string.Equals(room.roomFingerprint, CoopRules.RoomFingerprint(_room), StringComparison.Ordinal);
        }

        private CoopState ToCoreState(OnlineWireState wire)
        {
            return new CoopState
            {
                RoomId = wire.roomId,
                CirclePosition = new GridPoint(wire.circlePosition.x, wire.circlePosition.y),
                DiamondPosition = new GridPoint(wire.diamondPosition.x, wire.diamondPosition.y),
                ActiveActor = wire.activeActor == 1 ? CoopActor.Diamond : CoopActor.Circle,
                AuthorityRevision = wire.authorityRevision,
                LogicalActionCount = wire.logicalActionCount,
                Status = wire.status == 1 ? CoopRunStatus.Cleared : (wire.status == 2 ? CoopRunStatus.Paused : CoopRunStatus.Playing),
                PendingConsent = wire.pendingConsent == null ? null : new CoopPendingConsent
                {
                    RequestId = wire.pendingConsent.requestId,
                    Kind = wire.pendingConsent.kind == 1 ? CoopConsentKind.Restart : CoopConsentKind.Undo,
                    Requester = wire.pendingConsent.requester == 1 ? CoopActor.Diamond : CoopActor.Circle,
                    RequestedAtRevision = wire.pendingConsent.requestedAtRevision
                }
            };
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
            _message = "온라인 방을 준비하세요";
            _error = string.Empty;
            PublishState();
        }

        private void BeginRoomAttempt(string message)
        {
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
            ClearAuthoritativeState(terminal);
            _availabilityCode = code == "room_expired" ? 3 : 0;
            if (terminal)
            {
                _joined = false;
                _roomReady = false;
                _roomId = string.Empty;
                _inviteCode = string.Empty;
                _seatCode = -1;
                _roomView = null;
            }
        }

        private void ClearAuthoritativeState(bool resetExpressions)
        {
            _serverState = null;
            _authorityRevision = -1;
            _logicalActionCount = 0;
            _availabilityCode = 0;
            _circleConnected = false;
            _diamondConnected = false;
            _transitioning = false;
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
                inputEnabled = _joined && _roomReady && _serverState != null && _availabilityCode == 1 && !_transitioning && IsMyTurn(),
                activeActorCode = _serverState == null ? string.Empty : _serverState.ActiveActor.ToString(),
                authorityRevision = _authorityRevision,
                logicalActionCount = _logicalActionCount,
                statusCode = _serverState == null ? "Waiting" : _serverState.Status.ToString(),
                message = _message,
                error = _error,
                transitioning = _transitioning,
                circleAtGoal = _serverState != null && CoopRules.Same(_serverState.CirclePosition, _room.CircleGoal),
                diamondAtGoal = _serverState != null && CoopRules.Same(_serverState.DiamondPosition, _room.DiamondGoal),
                pendingConsent = _serverState == null ? null : ToObservation(_serverState.PendingConsent),
                expressionSequence = _visibleExpressionSequence,
                expressionSender = _visibleExpressionSender,
                expression = _visibleExpression,
                localHotseat = false,
                seatAuthority = "bearer_derived",
                roomMismatch = _joined && !_roomReady,
                transportLocked = _serverState == null || _availabilityCode != 1
            };
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
            NectorialOnlineReportState(JsonUtility.ToJson(observation));
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
        private static extern void NectorialOnlineJoin(string inviteCode);
        [DllImport("__Internal")]
        private static extern void NectorialOnlineCommand(string json);
        [DllImport("__Internal")]
        private static extern void NectorialOnlineLeave();
        [DllImport("__Internal")]
        private static extern void NectorialOnlineReportState(string json);
#endif

        [Serializable] private sealed class OnlineInput { public string kind; public string direction; public string expression; public string requestId; public string inviteCode; public bool approve; public long expectedRevision = -1; }
        [Serializable] private sealed class OnlineCommandRequest { public string commandId; public long expectedRevision; public int kind; public int direction; public string requestId; public bool approve; public int expression; }
        [Serializable] private sealed class OnlineResult { public bool ok; public string op; public string inviteCode; public int seat = -1; public OnlineRoomView room; public OnlineWireState state; public OnlineAvailability availability; public OnlineExpressions expressions; public bool accepted; public bool idempotent; public string reason; public OnlineError error; }
        [Serializable] private sealed class OnlineRoomView { public string roomId; public string rulesVersion; public string contentVersion; public string roomFingerprint; public string expiresAtUtc; }
        [Serializable] private sealed class OnlineWireState { public string roomId; public OnlinePoint circlePosition; public OnlinePoint diamondPosition; public int activeActor; public long authorityRevision; public int logicalActionCount; public int status; public OnlinePendingConsent pendingConsent; }
        [Serializable] private sealed class OnlinePoint { public int x; public int y; }
        [Serializable] private sealed class OnlinePendingConsent { public string requestId; public int kind; public int requester; public long requestedAtRevision; }
        [Serializable] private sealed class OnlineAvailability { public int status; public bool circleConnected; public bool diamondConnected; public string heartbeatExpiresAtUtc; }
        [Serializable] private sealed class OnlineExpressions { public long expressionSequence; public OnlineExpressionEvent[] events; }
        [Serializable] private sealed class OnlineExpressionEvent { public long sequence; public int sender; public int expression; public string occurredAtUtc; }
        [Serializable] private sealed class OnlineError { public string code; }
        [Serializable] private sealed class OnlineObservation
        {
            public bool initialized; public bool online; public bool joined; public string roomId; public string inviteCode; public int seatCode; public int availabilityCode; public string availabilityStatus; public bool circleConnected; public bool diamondConnected; public bool inputEnabled; public string activeActorCode; public long authorityRevision; public int logicalActionCount; public string statusCode; public string message; public string error; public bool transitioning; public bool circleAtGoal; public bool diamondAtGoal; public PendingConsentObservation pendingConsent; public long expressionSequence; public string expressionSender; public string expression; public bool localHotseat; public string seatAuthority; public bool roomMismatch; public bool transportLocked;
        }
        [Serializable] private sealed class OnlineSafeLog
        {
            public bool initialized; public int availabilityCode; public bool circleConnected; public bool diamondConnected; public string activeActorCode; public long authorityRevision; public int logicalActionCount; public string statusCode; public bool circleAtGoal; public bool diamondAtGoal; public long expressionSequence; public string expressionSender; public string stateFingerprint;
        }
        [Serializable] private sealed class PendingConsentObservation { public bool active; public string requestId; public string kind; public string statusCode; public string requester; public int requesterCode; public long requestedAtRevision; }
    }
}
