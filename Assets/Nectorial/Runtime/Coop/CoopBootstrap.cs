using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity.Coop
{
    public sealed class CoopBootstrap : MonoBehaviour
    {
        private const string ProductName = "Few Moves Coop Pilot";
        private const string RoomResource = "CoopRooms/coop-c1";
        private const string AtlasResource = "Visuals/turn-escape-tiles";
        private const string SaveKey = "nectorial-coop.save.v1";
        private const float WebGLPersistenceTimeoutSeconds = 10f;

        private CoopRoomDefinition _room;
        private CoopSession _session;
        private CoopBoardView _board;
        private bool _initialized;
        private bool _restoreBlocked;
        private bool _manualSavePending;
        private bool _transitioning;
        private bool _initializationFailed;
        private readonly string _commandPrefix = "coop-" + Guid.NewGuid().ToString("N") + "-";
        private int _nextCommandId;
        private int _nextSaveRequestId;
        private int _pendingSaveRequestId;
        private Coroutine _saveTimeoutRoutine;
        private Coroutine _transitionRoutine;
        private float _expressionCooldownUntil;
        private string _message = "초기화 중";
        private string _loadError = string.Empty;
        private string _saveStatus = "idle";
        private string _saveError = string.Empty;
        private string _lastExpression = string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateRuntime()
        {
            if (!string.Equals(Application.productName, ProductName, StringComparison.Ordinal)) return;
            if (FindAnyObjectByType<CoopBootstrap>() != null) return;

            var bootstrap = new GameObject("CoopBootstrap");
            DontDestroyOnLoad(bootstrap);
            bootstrap.AddComponent<CoopBootstrap>();
        }

        private void Awake()
        {
            ConfigureCamera();
            Initialize();
        }

        private void Update()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            if (!_initialized || _restoreBlocked || _transitioning) return;
            if (Input.GetKeyDown(KeyCode.UpArrow)) SendSlide(GameCommand.Up);
            else if (Input.GetKeyDown(KeyCode.DownArrow)) SendSlide(GameCommand.Down);
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) SendSlide(GameCommand.Left);
            else if (Input.GetKeyDown(KeyCode.RightArrow)) SendSlide(GameCommand.Right);
            else if (Input.GetKeyDown(KeyCode.P)) SendPass();
            else if (Input.GetKeyDown(KeyCode.U)) RequestUndo();
            else if (Input.GetKeyDown(KeyCode.R)) RequestRestart();
#endif
        }

        private void OnDestroy()
        {
            StopSaveTimeout();
            StopTransition();
            if (_board != null) _board.Dispose();
        }

        public void HandleCommand(string json)
        {
            if (!_initialized || _restoreBlocked || _initializationFailed || string.IsNullOrEmpty(json)) return;

            UiCommandPayload payload;
            try
            {
                payload = JsonUtility.FromJson<UiCommandPayload>(json);
            }
            catch (Exception exception)
            {
                _message = "협력 입력을 읽지 못했습니다";
                _loadError = "command_json_invalid:" + exception.GetType().Name;
                PublishState();
                return;
            }

            if (payload == null || string.IsNullOrEmpty(payload.kind))
            {
                _message = "협력 입력을 읽지 못했습니다";
                PublishState();
                return;
            }

            if (!string.Equals(payload.kind, "Save", StringComparison.Ordinal) && !HasCommandContext(payload))
            {
                RejectCommand("협력 상태가 오래되었습니다");
                return;
            }

            if (_transitioning && !string.Equals(payload.kind, "Save", StringComparison.Ordinal)) return;

            switch (payload.kind)
            {
                case "Save": RequestManualSave(); return;
                case "Slide":
                    GameCommand direction;
                    if (!TryParseDirection(payload.direction, out direction)) { RejectCommand("알 수 없는 이동입니다"); return; }
                    SendSlide(direction, payload); return;
                case "Pass": SendPass(payload); return;
                case "RequestUndo": RequestUndo(payload); return;
                case "RequestRestart": RequestRestart(payload); return;
                case "ResolveConsent": ResolveConsent(payload); return;
                case "Express": Express(payload); return;
                default:
                    RejectCommand("알 수 없는 협력 입력입니다");
                    return;
            }
        }

        public void OnWebGLPersistenceResult(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            int separator = payload.IndexOf('|');
            if (separator <= 0 || separator + 1 >= payload.Length) return;
            int requestId;
            if (!int.TryParse(payload.Substring(0, separator), out requestId)) return;
            CompleteManualSave(requestId, payload.Substring(separator + 1));
        }

        private void Initialize()
        {
            try
            {
                Texture2D atlas = Resources.Load<Texture2D>(AtlasResource);
                if (atlas == null || atlas.width != 128 || atlas.height != 16)
                {
                    FailInitialization("필수 게임 이미지를 읽을 수 없습니다", "asset_missing:turn-escape-tiles");
                    return;
                }

                TextAsset asset = Resources.Load<TextAsset>(RoomResource);
                if (asset == null)
                {
                    FailInitialization("협력 방 데이터를 읽을 수 없습니다", "content_missing:coop-c1");
                    return;
                }

                _room = JsonUtility.FromJson<CoopRoomDefinition>(asset.text);
                string[] errors = CoopRules.ValidateRoom(_room);
                if (errors.Length > 0)
                {
                    FailInitialization("협력 방 데이터가 올바르지 않습니다", "content_invalid:" + errors[0]);
                    return;
                }

                _session = CoopSession.Create(_room);
                _board = new CoopBoardView(atlas);
                _initialized = true;
                RestoreIfPresent();
                _board.Render(_room, _session.State);
                PublishState();
            }
            catch (Exception exception)
            {
                FailInitialization("협력을 시작할 수 없습니다", "initialization_exception:" + exception.GetType().Name);
            }
        }

        private void RestoreIfPresent()
        {
            if (!PlayerPrefs.HasKey(SaveKey))
            {
                _message = "원부터 움직여 보세요";
                return;
            }

            try
            {
                CoopSaveEnvelope envelope = JsonUtility.FromJson<CoopSaveEnvelope>(PlayerPrefs.GetString(SaveKey));
                string error;
                if (!TryRestore(envelope, out error))
                {
                    BlockOnRestoreError(error, "저장된 협력을 불러오지 못했습니다");
                    return;
                }

                _message = "저장된 협력을 불러왔습니다";
            }
            catch (Exception exception)
            {
                BlockOnRestoreError("save_json_invalid:" + exception.GetType().Name, "저장된 협력을 불러오지 못했습니다");
            }
        }

        private bool TryRestore(CoopSaveEnvelope envelope, out string error)
        {
            error = null;
            CoopSession restored;
            if (!CoopSaveCodec.TryRestore(_room, envelope, out restored, out error)) return false;
            _session = restored;
            return true;
        }

        private void SendSlide(GameCommand direction)
        {
            SendSlide(direction, CreateLocalPayload("Slide"));
        }

        private void SendSlide(GameCommand direction, UiCommandPayload payload)
        {
            CoopActor seat;
            if (!CanDispatch(payload) || !TryParseActor(payload.seat, out seat)) return;
            DispatchCommand(CoopCommandFactory.Slide(seat, NextCommandId(), payload.expectedRevision, direction));
        }

        private void SendPass()
        {
            SendPass(CreateLocalPayload("Pass"));
        }

        private void SendPass(UiCommandPayload payload)
        {
            CoopActor seat;
            if (!CanDispatch(payload) || !TryParseActor(payload.seat, out seat)) return;
            DispatchCommand(CoopCommandFactory.Pass(seat, NextCommandId(), payload.expectedRevision));
        }

        private void RequestUndo()
        {
            UiCommandPayload payload = CreateLocalPayload("RequestUndo");
            payload.requestId = "undo-" + NextCommandId();
            RequestUndo(payload);
        }

        private void RequestUndo(UiCommandPayload payload)
        {
            CoopActor seat;
            if (!CanDispatch(payload) || !TryParseActor(payload.seat, out seat) || string.IsNullOrEmpty(payload.requestId)) return;
            DispatchCommand(CoopCommandFactory.RequestUndo(seat, NextCommandId(), payload.expectedRevision, payload.requestId));
        }

        private void RequestRestart()
        {
            UiCommandPayload payload = CreateLocalPayload("RequestRestart");
            payload.requestId = "restart-" + NextCommandId();
            RequestRestart(payload);
        }

        private void RequestRestart(UiCommandPayload payload)
        {
            CoopActor seat;
            if (!CanDispatch(payload) || !TryParseActor(payload.seat, out seat) || string.IsNullOrEmpty(payload.requestId)) return;
            DispatchCommand(CoopCommandFactory.RequestRestart(seat, NextCommandId(), payload.expectedRevision, payload.requestId));
        }

        private void ResolveConsent(UiCommandPayload payload)
        {
            if (!CanDispatch(payload) || string.IsNullOrEmpty(payload.requestId)) return;
            CoopActor seat;
            if (!TryParseActor(payload.seat, out seat)) return;
            CoopState state = _session.State;
            if (state.PendingConsent == null || !string.Equals(state.PendingConsent.RequestId, payload.requestId, StringComparison.Ordinal))
            {
                RejectCommand("동의 요청이 이미 바뀌었습니다");
                return;
            }

            CoopCommand command = state.PendingConsent.Kind == CoopConsentKind.Undo
                ? CoopCommandFactory.ResolveUndo(seat, NextCommandId(), payload.expectedRevision, payload.requestId, payload.approve)
                : CoopCommandFactory.ResolveRestart(seat, NextCommandId(), payload.expectedRevision, payload.requestId, payload.approve);
            DispatchCommand(command);
        }

        private void Express(UiCommandPayload payload)
        {
            if (!CanDispatch(payload) || Time.unscaledTime < _expressionCooldownUntil) return;
            CoopActor seat;
            CoopExpression expression;
            if (!TryParseActor(payload.seat, out seat) || !TryParseExpression(payload.expression, out expression))
            {
                RejectCommand("표현을 사용할 수 없습니다");
                return;
            }

            DispatchCommand(CoopCommandFactory.Express(seat, NextCommandId(), payload.expectedRevision, expression));
        }

        private void DispatchCommand(CoopCommand command)
        {
            if (!CanDispatchNow() || command == null) return;
            CoopState before = _session.State;
            CoopDispatchResult result = _session.Dispatch(command);
            if (result.Accepted && !result.Idempotent && command.Kind == CoopCommandKind.Slide)
            {
                StartTransition(before, result.State, result, command);
                return;
            }
            ApplyDispatch(result, command, true);
        }

        private void StartTransition(CoopState before, CoopState after, CoopDispatchResult result, CoopCommand command)
        {
            if (_board == null || !_board.BeginTransition(_room, before, after, command.Seat))
            {
                ApplyDispatch(result, command, true);
                return;
            }

            _transitioning = true;
            _message = "";
            PublishState();
            float duration = CalculateTransitionDuration(before, after, command.Seat);
            _transitionRoutine = StartCoroutine(CompleteTransition(duration, after, result, command));
        }

        private IEnumerator CompleteTransition(float duration, CoopState after, CoopDispatchResult result, CoopCommand command)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                _board.AdvanceTransition(Mathf.Clamp01(elapsed / duration));
                yield return null;
            }

            _transitionRoutine = null;
            _transitioning = false;
            _board.CompleteTransition(_room, after);
            ApplyDispatch(result, command, false);
        }

        private void StopTransition()
        {
            if (_transitionRoutine != null)
            {
                StopCoroutine(_transitionRoutine);
                _transitionRoutine = null;
            }
            _transitioning = false;
            if (_board != null) _board.CancelTransition();
        }

        private static float CalculateTransitionDuration(CoopState before, CoopState after, CoopActor actor)
        {
            GridPoint from = actor == CoopActor.Circle ? before.CirclePosition : before.DiamondPosition;
            GridPoint to = actor == CoopActor.Circle ? after.CirclePosition : after.DiamondPosition;
            int distance = Math.Abs(to.X - from.X) + Math.Abs(to.Y - from.Y);
            return Mathf.Clamp(0.12f + Math.Max(0, distance - 1) * 0.03f, 0.12f, 0.28f);
        }

        private void ApplyDispatch(CoopDispatchResult result, CoopCommand command, bool render)
        {
            _message = TranslateResult(result);
            if (result != null && result.Accepted && result.Events != null)
            {
                for (int index = 0; index < result.Events.Length; index++)
                {
                    if (result.Events[index] != null && string.Equals(result.Events[index].Type, "expression", StringComparison.Ordinal))
                    {
                        _lastExpression = result.Events[index].Detail;
                        _expressionCooldownUntil = Time.unscaledTime + 0.35f;
                        _message = "표현을 보냈습니다";
                    }
                }
                AutosaveCurrentState();
            }

            if (render && _board != null) _board.Render(_room, _session.State);
            PublishState();
        }

        private void AutosaveCurrentState()
        {
            string error;
            if (TryWriteSave(out error))
            {
                if (!_manualSavePending) _saveStatus = "changed";
                _saveError = string.Empty;
            }
            else
            {
                _saveStatus = "failed";
                _saveError = error;
            }
        }

        private void RequestManualSave()
        {
            if (!_initialized || _restoreBlocked || _manualSavePending || _transitioning) return;
            string error;
            if (!TryWriteSave(out error))
            {
                _saveStatus = "failed";
                _saveError = error;
                PublishState();
                return;
            }

            int requestId = ++_nextSaveRequestId;
            _pendingSaveRequestId = requestId;
            _manualSavePending = true;
            _saveStatus = "pending";
            _saveError = string.Empty;
#if UNITY_WEBGL && !UNITY_EDITOR
            _saveTimeoutRoutine = StartCoroutine(WatchWebGLPersistence(requestId));
            try { NectorialCoopPersistSave(requestId); }
            catch (Exception exception) { CompleteManualSave(requestId, "bridge_throw:" + exception.GetType().Name); }
#else
            CompleteManualSave(requestId, "ok");
#endif
            PublishState();
        }

        private bool TryWriteSave(out string error)
        {
            error = null;
            try
            {
                CoopSaveEnvelope envelope = CoopSaveCodec.Capture(_session, "local-hotseat");
                PlayerPrefs.SetString(SaveKey, JsonUtility.ToJson(envelope));
                PlayerPrefs.Save();
                return true;
            }
            catch (Exception exception)
            {
                error = "save_write_failed:" + exception.GetType().Name;
                return false;
            }
        }

        private IEnumerator WatchWebGLPersistence(int requestId)
        {
            yield return new WaitForSecondsRealtime(WebGLPersistenceTimeoutSeconds);
            CompleteManualSave(requestId, "timeout");
        }

        private void CompleteManualSave(int requestId, string result)
        {
            if (!_manualSavePending || requestId != _pendingSaveRequestId) return;
            StopSaveTimeout();
            _manualSavePending = false;
            _pendingSaveRequestId = 0;
            if (string.Equals(result, "ok", StringComparison.Ordinal))
            {
                _saveStatus = "saved";
                _saveError = string.Empty;
            }
            else
            {
                _saveStatus = "failed";
                _saveError = "webgl_coop_persist_" + (result ?? "unknown");
            }
            PublishState();
        }

        private void StopSaveTimeout()
        {
            if (_saveTimeoutRoutine == null) return;
            StopCoroutine(_saveTimeoutRoutine);
            _saveTimeoutRoutine = null;
        }

        private bool CanDispatchNow()
        {
            return _initialized && !_restoreBlocked && !_initializationFailed && !_manualSavePending && !_transitioning && _session != null;
        }

        private bool CanDispatch(UiCommandPayload payload)
        {
            return CanDispatchNow() && payload != null && payload.hasExpectedRevision && !string.IsNullOrEmpty(payload.seat);
        }

        private static bool HasCommandContext(UiCommandPayload payload)
        {
            CoopActor actor;
            return payload != null && payload.hasExpectedRevision && TryParseActor(payload.seat, out actor);
        }

        private UiCommandPayload CreateLocalPayload(string kind)
        {
            CoopState state = _session.State;
            return new UiCommandPayload
            {
                kind = kind,
                expectedRevision = state.AuthorityRevision,
                hasExpectedRevision = true,
                seat = state.ActiveActor.ToString()
            };
        }

        private void RejectCommand(string message)
        {
            _message = message;
            PublishState();
        }

        private void PublishState()
        {
            if (_session == null || _room == null)
            {
                PublishObservation(new StateObservation
                {
                    initialized = false,
                    inputEnabled = false,
                    passEnabled = false,
                    undoEnabled = false,
                    restartEnabled = false,
                    manualSaveEnabled = false,
                    savePending = false,
                    saveStatus = _saveStatus,
                    saveError = _saveError,
                    status = "시작 실패",
                    statusCode = "InitializationFailed",
                    message = _message,
                    error = _loadError,
                    localHotseat = true,
                    seatAuthority = "session_state_only",
                    fingerprint = string.Empty
                });
                return;
            }
            CoopState state = _session.State;
            var observation = new StateObservation
            {
                initialized = _initialized,
                inputEnabled = _initialized && !_restoreBlocked && !_manualSavePending && state.Status == CoopRunStatus.Playing,
                passEnabled = _initialized && !_restoreBlocked && !_manualSavePending && state.Status == CoopRunStatus.Playing,
                undoEnabled = _initialized && !_restoreBlocked && !_manualSavePending && state.Status == CoopRunStatus.Playing && state.LogicalActionCount > 0,
                restartEnabled = _initialized && !_restoreBlocked && !_manualSavePending && (state.Status == CoopRunStatus.Playing || state.Status == CoopRunStatus.Cleared),
                manualSaveEnabled = _initialized && !_restoreBlocked && !_manualSavePending,
                savePending = _manualSavePending,
                saveStatus = _saveStatus,
                saveError = _saveError,
                saveRequestId = _manualSavePending ? _pendingSaveRequestId : 0,
                room = state.RoomId,
                roomNumber = 1,
                roomCount = 1,
                status = TranslateStatus(state.Status),
                statusCode = state.Status.ToString(),
                message = _message,
                error = _loadError,
                transitioning = _transitioning,
                expressionEnabled = _initialized && !_restoreBlocked && !_manualSavePending && !_transitioning,
                activeActor = state.ActiveActor.ToString(),
                activeActorCode = state.ActiveActor.ToString(),
                authorityRevision = state.AuthorityRevision,
                logicalActionCount = state.LogicalActionCount,
                circlePosition = state.CirclePosition,
                diamondPosition = state.DiamondPosition,
                circleGoal = _room.CircleGoal,
                diamondGoal = _room.DiamondGoal,
                circleAtGoal = CoopRules.Same(state.CirclePosition, _room.CircleGoal),
                diamondAtGoal = CoopRules.Same(state.DiamondPosition, _room.DiamondGoal),
                pendingConsent = ToObservation(state.PendingConsent),
                lastExpression = _lastExpression,
                localHotseat = true,
                seatAuthority = "session_state_only",
                fingerprint = CoopRules.StateFingerprint(_room, state)
            };

            PublishObservation(observation);
        }

        private static void PublishObservation(StateObservation observation)
        {
            string json = JsonUtility.ToJson(observation);
            Debug.Log("COOP_STATE_OBSERVATION " + json);
#if UNITY_WEBGL && !UNITY_EDITOR
            NectorialCoopReportState(json);
#endif
        }

        private static PendingConsentObservation ToObservation(CoopPendingConsent pending)
        {
            if (pending == null) return null;
            return new PendingConsentObservation
            {
                requestId = pending.RequestId,
                kind = pending.Kind.ToString(),
                statusCode = pending.Kind.ToString(),
                requester = pending.Requester.ToString(),
                requestedAtRevision = pending.RequestedAtRevision
            };
        }

        private void FailInitialization(string message, string error)
        {
            _initialized = false;
            _initializationFailed = true;
            _message = message;
            _loadError = error;
            PublishState();
        }

        private void BlockOnRestoreError(string error, string message)
        {
            _restoreBlocked = true;
            _loadError = error;
            _saveStatus = "failed";
            _saveError = error;
            _message = message;
        }

        private string NextCommandId()
        {
            _nextCommandId = checked(_nextCommandId + 1);
            return _commandPrefix + _nextCommandId;
        }

        private static bool TryParseDirection(string value, out GameCommand result)
        {
            result = GameCommand.Up;
            if (string.Equals(value, "Up", StringComparison.Ordinal)) { result = GameCommand.Up; return true; }
            if (string.Equals(value, "Down", StringComparison.Ordinal)) { result = GameCommand.Down; return true; }
            if (string.Equals(value, "Left", StringComparison.Ordinal)) { result = GameCommand.Left; return true; }
            if (string.Equals(value, "Right", StringComparison.Ordinal)) { result = GameCommand.Right; return true; }
            return false;
        }

        private static bool TryParseActor(string value, out CoopActor result)
        {
            result = CoopActor.Circle;
            if (string.Equals(value, "Circle", StringComparison.Ordinal)) return true;
            if (string.Equals(value, "Diamond", StringComparison.Ordinal)) { result = CoopActor.Diamond; return true; }
            return false;
        }

        private static bool TryParseExpression(string value, out CoopExpression result)
        {
            result = CoopExpression.Look;
            if (string.Equals(value, "Look", StringComparison.Ordinal)) return true;
            if (string.Equals(value, "ThumbsUp", StringComparison.Ordinal)) { result = CoopExpression.ThumbsUp; return true; }
            if (string.Equals(value, "Handshake", StringComparison.Ordinal)) { result = CoopExpression.Handshake; return true; }
            if (string.Equals(value, "Waiting", StringComparison.Ordinal)) { result = CoopExpression.Waiting; return true; }
            return false;
        }

        private static string TranslateResult(CoopDispatchResult result)
        {
            if (result == null) return "협력 결과를 확인할 수 없습니다";
            if (result.Accepted) return result.Idempotent ? "현재 협력 상태를 확인했습니다" : "협력 상태가 바뀌었습니다";
            switch (result.Reason)
            {
                case "blocked_zero": return "그 방향으로는 미끄러질 수 없습니다";
                case "wrong_active_actor": return "지금은 다른 좌석의 차례입니다";
                case "stale_revision": return "최신 협력 상태를 확인했습니다";
                case "consent_pending": return "다른 좌석의 동의를 기다리고 있습니다";
                case "consent_missing": return "동의 요청이 없습니다";
                default: return "협력 행동을 적용하지 못했습니다";
            }
        }

        private static string TranslateStatus(CoopRunStatus status)
        {
            switch (status)
            {
                case CoopRunStatus.Cleared: return "둘 다 도착";
                case CoopRunStatus.Paused: return "잠시 멈춤";
                default: return "협력 중";
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

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void NectorialCoopReportState(string json);

        [DllImport("__Internal")]
        private static extern void NectorialCoopPersistSave(int requestId);
#endif

        [Serializable]
        private sealed class UiCommandPayload
        {
            public string kind;
            public string direction;
            public string expression;
            public string requestId;
            public bool approve;
            public long expectedRevision;
            public bool hasExpectedRevision;
            public string seat;
        }

        [Serializable]
        private sealed class PendingConsentObservation
        {
            public string requestId;
            public string kind;
            public string statusCode;
            public string requester;
            public long requestedAtRevision;
        }

        [Serializable]
        private sealed class StateObservation
        {
            public bool initialized;
            public bool inputEnabled;
            public bool passEnabled;
            public bool undoEnabled;
            public bool restartEnabled;
            public bool manualSaveEnabled;
            public bool expressionEnabled;
            public bool savePending;
            public string saveStatus;
            public string saveError;
            public int saveRequestId;
            public string room;
            public int roomNumber;
            public int roomCount;
            public string status;
            public string statusCode;
            public string message;
            public string error;
            public bool transitioning;
            public string activeActor;
            public string activeActorCode;
            public long authorityRevision;
            public int logicalActionCount;
            public GridPoint circlePosition;
            public GridPoint diamondPosition;
            public GridPoint circleGoal;
            public GridPoint diamondGoal;
            public bool circleAtGoal;
            public bool diamondAtGoal;
            public PendingConsentObservation pendingConsent;
            public string lastExpression;
            public bool localHotseat;
            public string seatAuthority;
            public string fingerprint;
        }
    }
}
