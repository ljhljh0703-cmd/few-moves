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
        private int _nextCommandId;
        private int _nextSaveRequestId;
        private int _pendingSaveRequestId;
        private Coroutine _saveTimeoutRoutine;
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
            if (!_initialized || _restoreBlocked) return;
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
            if (_board != null) _board.Dispose();
        }

        public void HandleCommand(string json)
        {
            if (!_initialized || string.IsNullOrEmpty(json)) return;

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

            switch (payload.kind)
            {
                case "Save": RequestManualSave(); return;
                case "Slide": SendSlide(ParseDirection(payload.direction)); return;
                case "Pass": SendPass(); return;
                case "RequestUndo": RequestUndo(payload.requestId); return;
                case "RequestRestart": RequestRestart(payload.requestId); return;
                case "ResolveConsent": ResolveConsent(payload); return;
                case "Express": Express(payload.expression); return;
                default:
                    _message = "알 수 없는 협력 입력입니다";
                    PublishState();
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
            if (!_initialized || _restoreBlocked || _manualSavePending) return;
            CoopActor seat = _session.State.ActiveActor;
            string commandId = NextCommandId();
            ApplyDispatch(_session.Dispatch(CoopCommandFactory.Slide(seat, commandId, _session.State.AuthorityRevision, direction)));
        }

        private void SendPass()
        {
            if (!_initialized || _restoreBlocked || _manualSavePending) return;
            CoopActor seat = _session.State.ActiveActor;
            ApplyDispatch(_session.Dispatch(CoopCommandFactory.Pass(seat, NextCommandId(), _session.State.AuthorityRevision)));
        }

        private void RequestUndo(string requestId = null)
        {
            if (!_initialized || _restoreBlocked || _manualSavePending) return;
            CoopState state = _session.State;
            string id = string.IsNullOrEmpty(requestId) ? "undo-" + _nextCommandId.ToString() : requestId;
            ApplyDispatch(_session.Dispatch(CoopCommandFactory.RequestUndo(state.ActiveActor, NextCommandId(), state.AuthorityRevision, id)));
        }

        private void RequestRestart(string requestId = null)
        {
            if (!_initialized || _restoreBlocked || _manualSavePending) return;
            CoopState state = _session.State;
            string id = string.IsNullOrEmpty(requestId) ? "restart-" + _nextCommandId.ToString() : requestId;
            ApplyDispatch(_session.Dispatch(CoopCommandFactory.RequestRestart(state.ActiveActor, NextCommandId(), state.AuthorityRevision, id)));
        }

        private void ResolveConsent(UiCommandPayload payload)
        {
            CoopState state = _session.State;
            if (state.PendingConsent == null) return;
            CoopActor resolver = CoopRules.Opponent(state.PendingConsent.Requester);
            string requestId = state.PendingConsent.RequestId;
            CoopCommand command = state.PendingConsent.Kind == CoopConsentKind.Undo
                ? CoopCommandFactory.ResolveUndo(resolver, NextCommandId(), state.AuthorityRevision, requestId, payload.approve)
                : CoopCommandFactory.ResolveRestart(resolver, NextCommandId(), state.AuthorityRevision, requestId, payload.approve);
            ApplyDispatch(_session.Dispatch(command));
        }

        private void Express(string expression)
        {
            CoopExpression parsed;
            if (!Enum.TryParse(expression, out parsed))
            {
                _message = "표현을 사용할 수 없습니다";
                PublishState();
                return;
            }

            CoopState state = _session.State;
            _lastExpression = parsed.ToString();
            ApplyDispatch(_session.Dispatch(CoopCommandFactory.Express(state.ActiveActor, NextCommandId(), state.AuthorityRevision, parsed)));
        }

        private void ApplyDispatch(CoopDispatchResult result)
        {
            _message = TranslateResult(result);
            if (result.Accepted && result.Events != null)
            {
                for (int index = 0; index < result.Events.Length; index++)
                {
                    if (result.Events[index] != null && string.Equals(result.Events[index].Type, "expression", StringComparison.Ordinal))
                    {
                        _lastExpression = result.Events[index].Detail;
                    }
                }
                AutosaveCurrentState();
            }

            _board.Render(_room, _session.State);
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
            if (!_initialized || _restoreBlocked || _manualSavePending) return;
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

        private void PublishState()
        {
            if (_session == null || _room == null) return;
            CoopState state = _session.State;
            var observation = new StateObservation
            {
                initialized = _initialized,
                inputEnabled = _initialized && !_restoreBlocked && !_manualSavePending && state.Status == CoopRunStatus.Playing,
                passEnabled = _initialized && !_restoreBlocked && !_manualSavePending && state.Status == CoopRunStatus.Playing,
                undoEnabled = _initialized && !_restoreBlocked && !_manualSavePending && state.Status == CoopRunStatus.Playing && state.LogicalActionCount > 0,
                restartEnabled = _initialized && !_restoreBlocked && !_manualSavePending && state.Status == CoopRunStatus.Playing,
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
                transitioning = false,
                activeActor = state.ActiveActor.ToString(),
                activeActorCode = state.ActiveActor.ToString(),
                authorityRevision = state.AuthorityRevision,
                logicalActionCount = state.LogicalActionCount,
                circlePosition = state.CirclePosition,
                diamondPosition = state.DiamondPosition,
                circleGoal = _room.CircleGoal,
                diamondGoal = _room.DiamondGoal,
                pendingConsent = ToObservation(state.PendingConsent),
                lastExpression = _lastExpression,
                localHotseat = true,
                seatAuthority = "session_state_only",
                fingerprint = CoopRules.StateFingerprint(_room, state)
            };

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
            _message = message;
            _loadError = error;
            ConfigureFallbackRoomIfNeeded();
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

        private void ConfigureFallbackRoomIfNeeded()
        {
            if (_room != null && _session != null) return;
            _room = new CoopRoomDefinition { Id = "coop-c1", Width = 8, Height = 8, Rows = new[] { "........", "........", "........", "........", "........", "........", "........", "........" }, RulesVersion = "unknown", ContentVersion = "unknown" };
        }

        private string NextCommandId()
        {
            _nextCommandId = checked(_nextCommandId + 1);
            return "coop-local-" + _nextCommandId;
        }

        private static GameCommand ParseDirection(string value)
        {
            GameCommand result;
            return Enum.TryParse(value, out result) ? result : GameCommand.Up;
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
            public PendingConsentObservation pendingConsent;
            public string lastExpression;
            public bool localHotseat;
            public string seatAuthority;
            public string fingerprint;
        }
    }
}
