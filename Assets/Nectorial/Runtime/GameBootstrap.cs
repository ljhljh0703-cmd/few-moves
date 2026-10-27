using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nectorial.SlideEscape;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity
{
    public sealed class GameBootstrap : MonoBehaviour
    {
        private const string ContentVersion = "slide-v3";
        private const string SaveKey = "nectorial-turn-escape.save.v3";
        private const float MinimumSlideDuration = 0.12f;
        private const float MaximumSlideDuration = 0.28f;
        private const float AdditionalCellDuration = 0.03f;
        private const string AtlasResource = "Visuals/turn-escape-tiles";
        private const string RoomResources = "SlideRooms";
        private const int AtlasWidth = 128;
        private const int AtlasHeight = 16;

        private RoomDefinition _room;
        private RoomDefinition[] _rooms;
        private int _roomIndex;
        private GameState _state;
        private List<GameMove> _moves = new List<GameMove>();
        private int _selectedPieceIndex;
        private IBoardView _board;
        private TossPlatformAdapter _platform;
        private bool _initialized;
        private bool _restoreBlocked;
        private bool _manualSavePending;
        private bool _platformStartupBlocked;
        private bool _transitioning;
        private bool _reducedMotion;
        private int _transitionEpoch;
        private int _nextSaveRequestId;
        private int _pendingSaveRequestId;
        private int _platformManualSaveUiWatchdogRequestId;
        private int _pendingPlatformShareRequestId;
        private int _platformShareResultRequestId;
        private Coroutine _transitionRoutine;
        private Coroutine _saveTimeoutRoutine;
        private Coroutine _platformManualSaveUiTimeoutRoutine;
        private GameState _transitionBefore;
        private GameState _transitionAfter;
        private GameMove _transitionMove;
        private float _transitionDuration;
        private string _message = "초기화 중";
        private string _loadError = string.Empty;
        private string _saveStatus = "idle";
        private string _saveError = string.Empty;
        private string _pendingPlatformShareFingerprint;
        private string _platformShareStatus = "idle";
        private string _platformShareError = string.Empty;
        private readonly TossNativeSaveCoordinator _platformWrites = new TossNativeSaveCoordinator();

        private const float WebGLPersistenceTimeoutSeconds = 10f;
        private const float PlatformManualSaveUiTimeoutSeconds = 10f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateRuntime()
        {
            if (string.Equals(Application.productName, "Few Moves Coop Pilot", StringComparison.Ordinal) ||
                string.Equals(Application.productName, "Few Moves Online Pilot", StringComparison.Ordinal) ||
                string.Equals(Application.productName, "Few Moves Raid Pilot", StringComparison.Ordinal))
            {
                return;
            }

            if (FindAnyObjectByType<GameBootstrap>() != null)
            {
                return;
            }

            var bootstrap = new GameObject("GameBootstrap");
            DontDestroyOnLoad(bootstrap);
            bootstrap.AddComponent<TossPlatformAdapter>();
            bootstrap.AddComponent<GameBootstrap>();
        }

        private void Awake()
        {
            _platform = GetComponent<TossPlatformAdapter>();
            if (_platform == null) _platform = gameObject.AddComponent<TossPlatformAdapter>();
            _platform.CheckpointRequested += RequestPlatformCheckpoint;
            ConfigureCamera();
            InitializeGame();
        }

        private void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return;
#else
            if (!_initialized)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.UpArrow)) HandleInput("Up");
            else if (Input.GetKeyDown(KeyCode.DownArrow)) HandleInput("Down");
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) HandleInput("Left");
            else if (Input.GetKeyDown(KeyCode.RightArrow)) HandleInput("Right");
            else if (Input.GetKeyDown(KeyCode.Space)) HandleInput("Undo");
            else if (Input.GetKeyDown(KeyCode.R)) HandleInput("Restart");
#endif
        }

        private void OnDestroy()
        {
            CancelTransition();
            StopSaveTimeout();
            StopPlatformManualSaveUiWatchdog();
            if (_platform != null) _platform.CheckpointRequested -= RequestPlatformCheckpoint;
            if (_board != null) _board.Dispose();
        }

        public void SetReducedMotion(string value)
        {
            _reducedMotion = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public void RetryPlatformStartup()
        {
            if (_platform == null || !_platform.IsTossCandidate || !_platformStartupBlocked)
            {
                PublishState();
                return;
            }

            _platformStartupBlocked = false;
            _message = "토스 저장소를 다시 확인하고 있습니다";
            _loadError = string.Empty;
            _saveStatus = "pending";
            _saveError = string.Empty;
            _platform.RetryStartup(CompletePlatformStartup);
            PublishState();
        }

        public void RequestPlatformShare(string value)
        {
            int requestId;
            if (_platform == null || !_platform.IsTossCandidate || !_platform.IsStorageReady
                || !_initialized || _restoreBlocked || _transitioning || _state == null
                || _state.Status != RunStatus.Cleared || _pendingPlatformShareRequestId != 0
                || !int.TryParse(value, out requestId) || requestId <= 0)
            {
                PublishState();
                return;
            }

            string fingerprint = GameEngine.Fingerprint(_room, _state);
            _pendingPlatformShareRequestId = requestId;
            _platformShareResultRequestId = 0;
            _pendingPlatformShareFingerprint = fingerprint;
            _platformShareStatus = "pending";
            _platformShareError = string.Empty;
            string record = "Few Moves | 방 " + (_roomIndex + 1) + " / " + _rooms.Length + " | " + _state.Turn + "수 클리어";
            _platform.Share(requestId, record, (completedRequestId, result) =>
                CompletePlatformShare(completedRequestId, fingerprint, result));
        }

        public void SelectPiece(string value)
        {
            if (!_initialized || _restoreBlocked || _transitioning || _manualSavePending || _state.Status != RunStatus.Playing)
            {
                PublishState();
                return;
            }

            int selected;
            if (!int.TryParse(value, out selected) || selected < 0 || selected >= _room.Pieces.Length)
            {
                _message = "선택할 수 없는 기물입니다";
                PublishState();
                return;
            }

            _selectedPieceIndex = selected;
            _message = "";
            _board.Render(_room, _state, _selectedPieceIndex);
            AutosaveCurrentState();
            PublishState();
        }

        public void HandleInput(string input)
        {
            if (!_initialized)
            {
                return;
            }

            if (string.Equals(input, "Save", StringComparison.Ordinal))
            {
                RequestManualSave();
                return;
            }

            if (string.Equals(input, "StartOver", StringComparison.Ordinal))
            {
                StartOver();
                return;
            }

            if (string.Equals(input, "Restart", StringComparison.Ordinal))
            {
                Restart();
                return;
            }

            if (string.Equals(input, "Next", StringComparison.Ordinal))
            {
                AdvanceToNextRoom();
                return;
            }

            if (string.Equals(input, "Undo", StringComparison.Ordinal))
            {
                Undo();
                return;
            }

            if (_restoreBlocked || _transitioning || _manualSavePending || _state.Status != RunStatus.Playing)
            {
                PublishState();
                return;
            }

            GameCommand command;
            if (!TryParseCommand(input, out command))
            {
                _message = "알 수 없는 입력입니다";
                PublishState();
                return;
            }

            GameState previous = _state;
            var move = new GameMove { PieceIndex = _selectedPieceIndex, Direction = command };
            StepResult result = GameEngine.Step(_room, previous, move);
            string resultMessage = TranslateResult(result);
            if (!result.Accepted)
            {
                _state = result.State;
                _message = resultMessage;
                PublishState();
                return;
            }

            BeginTransition(previous, result.State, move, resultMessage);
        }

        private void BeginTransition(GameState previous, GameState next, GameMove move, string completedMessage)
        {
            _transitionEpoch++;
            int epoch = _transitionEpoch;
            _transitioning = true;
            _message = "이동 중";
            bool canAnimate = _board != null && _board.BeginTransition(_room, previous, next, move.PieceIndex);
            _transitionBefore = previous;
            _transitionAfter = next;
            _transitionMove = move;
            _transitionDuration = canAnimate && !_reducedMotion ? CalculateSlideDuration(previous, next, move.PieceIndex) : 0f;
            ReportMotion("start");
            PublishState();

            if (_transitionDuration <= 0f)
            {
                if (canAnimate) _board.AdvanceTransition(1f);
                CommitTransition(next, move, completedMessage, epoch);
                return;
            }

            _transitionRoutine = StartCoroutine(CompleteTransition(next, move, completedMessage, epoch, _transitionDuration));
        }

        private IEnumerator CompleteTransition(GameState next, GameMove move, string completedMessage, int epoch, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (epoch != _transitionEpoch) yield break;
                elapsed += Time.unscaledDeltaTime;
                _board.AdvanceTransition(Mathf.Clamp01(elapsed / duration));
                yield return null;
            }

            if (epoch != _transitionEpoch) yield break;
            CommitTransition(next, move, completedMessage, epoch);
        }

        private void CommitTransition(GameState next, GameMove move, string completedMessage, int epoch)
        {
            if (epoch != _transitionEpoch) return;
            if (_board != null) _board.CompleteTransition(_room, next, _selectedPieceIndex);
            _state = next;
            _moves.Add(move);
            _message = completedMessage;
            _transitioning = false;
            _transitionRoutine = null;
            ReportMotion("complete");
            ClearMotionObservation();
            AutosaveCurrentState();
            PublishState();
        }

        private void CancelTransition()
        {
            if (_transitioning) ReportMotion("cancel");
            _transitionEpoch++;
            _transitioning = false;
            if (_transitionRoutine != null)
            {
                StopCoroutine(_transitionRoutine);
                _transitionRoutine = null;
            }

            if (_board != null) _board.CancelTransition();
            ClearMotionObservation();
        }

        private void ReportMotion(string phase)
        {
            if (_room == null || _transitionBefore == null || _transitionAfter == null) return;
            Debug.Log(
                "MOTION_OBSERVATION phase=" + phase +
                " room=" + _room.Id +
                " duration=" + _transitionDuration.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                " selected=" + _transitionMove.PieceIndex +
                " before=" + FormatPoint(_transitionBefore.Positions[_transitionMove.PieceIndex]) +
                " after=" + FormatPoint(_transitionAfter.Positions[_transitionMove.PieceIndex]));
        }

        private void ClearMotionObservation()
        {
            _transitionBefore = null;
            _transitionAfter = null;
            _transitionDuration = 0f;
            _transitionMove = default(GameMove);
        }

        private static float CalculateSlideDuration(GameState before, GameState after, int pieceIndex)
        {
            GridPoint start = before.Positions[pieceIndex];
            GridPoint end = after.Positions[pieceIndex];
            int distance = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y);
            return Mathf.Clamp(MinimumSlideDuration + Math.Max(0, distance - 1) * AdditionalCellDuration,
                MinimumSlideDuration, MaximumSlideDuration);
        }

        private static string FormatPoint(GridPoint point)
        {
            return point.X + "," + point.Y;
        }

        private void Undo()
        {
            if (!_initialized || _restoreBlocked || _manualSavePending)
            {
                PublishState();
                return;
            }

            if (_transitioning)
            {
                CancelTransition();
                _message = "이동을 취소했습니다";
                _board.Render(_room, _state, _selectedPieceIndex);
                PublishState();
                return;
            }

            if (_moves.Count == 0)
            {
                _message = "되돌릴 수 없습니다";
                PublishState();
                return;
            }

            GameMove removed = _moves[_moves.Count - 1];
            _moves.RemoveAt(_moves.Count - 1);
            GameState restored;
            string error;
            if (!TryReplayMoves(out restored, out error))
            {
                _moves.Add(removed);
                BlockOnRestoreError("save_transcript_invalid:" + error, "되돌리기를 완료하지 못했습니다");
                PublishState();
                return;
            }

            _state = restored;
            _selectedPieceIndex = removed.PieceIndex;
            _message = "한 수를 되돌렸습니다";
            _board.Render(_room, _state, _selectedPieceIndex);
            AutosaveCurrentState();
            PublishState();
        }

        private bool TryReplayMoves(out GameState replayed, out string error)
        {
            replayed = GameEngine.Create(_room);
            error = null;
            for (int index = 0; index < _moves.Count; index++)
            {
                StepResult step = GameEngine.Step(_room, replayed, _moves[index]);
                if (!step.Accepted)
                {
                    error = "move_" + index + "_" + step.Reason;
                    return false;
                }

                replayed = step.State;
                if (replayed.Status == RunStatus.Cleared && index + 1 < _moves.Count)
                {
                    error = "move_after_clear";
                    return false;
                }
            }

            return true;
        }

        private void InitializeGame()
        {
            try
            {
                Texture2D atlas = Resources.Load<Texture2D>(AtlasResource);
                if (atlas == null)
                {
                    FailInitialization("필수 타일 이미지가 없습니다", "asset_missing:turn-escape-tiles");
                    return;
                }

                if (atlas.width != AtlasWidth || atlas.height != AtlasHeight)
                {
                    FailInitialization("타일 이미지 크기가 올바르지 않습니다", "asset_dimensions_invalid:turn-escape-tiles");
                    return;
                }

                string catalogError;
                _rooms = LoadRoomCatalog(out catalogError);
                if (_rooms == null)
                {
                    FailInitialization("방 데이터를 읽을 수 없습니다", catalogError);
                    return;
                }

                _roomIndex = 0;
                _room = _rooms[_roomIndex];
                _state = GameEngine.Create(_room);
                _selectedPieceIndex = _room.TargetPieceIndex;
                string boardError;
                _board = CreateBoardView(atlas, out boardError);
                if (_board == null)
                {
                    FailInitialization("3D 미리보기 자원을 불러올 수 없습니다", boardError);
                    return;
                }

                if (_platform != null && _platform.IsTossCandidate)
                {
                    _message = "토스 저장소를 확인하고 있습니다";
                    _saveStatus = "pending";
                    _saveError = string.Empty;
                    _platform.BeginStartup(CompletePlatformStartup);
                    PublishState();
                    return;
                }

                CompleteWebStartup();
            }
            catch (Exception exception)
            {
                FailInitialization("게임을 시작할 수 없습니다", "initialization_exception:" + exception.GetType().Name);
            }
        }

        private void CompleteWebStartup()
        {
            _initialized = true;
            RestoreWebIfPresent();
            _board.Render(_room, _state, _selectedPieceIndex);
            PublishState();
        }

        private void CompletePlatformStartup(TossStartupResult result)
        {
            if (result.Kind == TossStartupKind.WebFallback)
            {
                CompleteWebStartup();
                return;
            }

            if (result.Kind == TossStartupKind.Blocked)
            {
                _initialized = false;
                _platformStartupBlocked = true;
                _message = "토스 저장소를 확인하지 못했습니다. 다시 확인해 주세요";
                _loadError = result.Error ?? "startup_unavailable";
                _saveStatus = "failed";
                _saveError = _loadError;
                _board.Render(_room, _state, _selectedPieceIndex);
                PublishState();
                return;
            }

            _platformStartupBlocked = false;
            _initialized = true;
            _saveStatus = "idle";
            _saveError = string.Empty;
            if (result.Kind == TossStartupKind.NewUser)
            {
                _message = "새 진행을 시작합니다";
            }
            else
            {
                RestoreSavedPayload(result.Payload);
            }

            _board.Render(_room, _state, _selectedPieceIndex);
            PublishState();
        }

        private void RestoreWebIfPresent()
        {
            if (!PlayerPrefs.HasKey(SaveKey))
            {
                _message = "화살표나 버튼으로 출구까지 이동하세요";
                return;
            }

            try
            {
                string json = PlayerPrefs.GetString(SaveKey);
                RestoreSavedPayload(json);
            }
            catch (Exception exception)
            {
                BlockOnRestoreError("save_json_invalid:" + exception.GetType().Name, "저장된 진행을 불러오지 못했습니다");
            }
        }

        private void RestoreSavedPayload(string json)
        {
            try
            {
                SaveEnvelope envelope = JsonUtility.FromJson<SaveEnvelope>(json);
                int savedRoomIndex = FindRoomIndex(envelope != null && envelope.State != null
                    ? envelope.State.RoomId
                    : null);
                if (savedRoomIndex < 0)
                {
                    BlockOnRestoreError("save_room_unknown", "저장된 진행을 불러오지 못했습니다");
                    return;
                }

                RoomDefinition savedRoom = _rooms[savedRoomIndex];
                GameState restored;
                GameMove[] restoredMoves;
                int restoredSelection;
                string error;
                if (!SaveCodec.TryRestore(savedRoom, envelope, ContentVersion, out restored, out restoredMoves, out restoredSelection, out error))
                {
                    BlockOnRestoreError(error, "저장된 진행을 불러오지 못했습니다");
                    return;
                }

                _roomIndex = savedRoomIndex;
                _room = savedRoom;
                _state = restored;
                _moves = new List<GameMove>(restoredMoves);
                _selectedPieceIndex = restoredSelection;
                _message = "저장된 진행을 불러왔습니다";
                _saveStatus = "idle";
                _saveError = string.Empty;
            }
            catch (Exception exception)
            {
                BlockOnRestoreError("save_json_invalid:" + exception.GetType().Name, "저장된 진행을 불러오지 못했습니다");
            }
        }

        private void AutosaveCurrentState()
        {
            if (UsesTossPlatformStorage())
            {
                QueuePlatformSave(manual: false);
                return;
            }

            string error;
            if (!TryWriteSave(out error))
            {
                RecordSaveFailure(error, "저장하지 못했습니다. 진행은 계속할 수 있습니다");
                return;
            }

            if (!_manualSavePending)
            {
                _saveStatus = "changed";
                _saveError = string.Empty;
            }
        }

        private bool TryWriteSave(out string error)
        {
            try
            {
                string json;
                if (!TryCaptureSave(out json, out error)) return false;
                PlayerPrefs.SetString(SaveKey, json);
                PlayerPrefs.Save();
                return true;
            }
            catch (Exception exception)
            {
                error = "save_write_failed:" + exception.GetType().Name;
                return false;
            }
        }

        private void RequestManualSave()
        {
            if (!_initialized || _restoreBlocked || _transitioning || _manualSavePending)
            {
                PublishState();
                return;
            }

            if (UsesTossPlatformStorage())
            {
                QueuePlatformSave(manual: true);
                PublishState();
                return;
            }

            string error;
            if (!TryWriteSave(out error))
            {
                RecordSaveFailure(error, "저장하지 못했습니다. 다시 저장할 수 있습니다");
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
            try
            {
                NectorialPersistSave(requestId);
            }
            catch (Exception exception)
            {
                CompleteManualSave(requestId, "bridge_throw:" + exception.GetType().Name);
            }
#else
            CompleteManualSave(requestId, "ok");
#endif
            PublishState();
        }

        private bool UsesTossPlatformStorage()
        {
            return _platform != null && _platform.IsTossCandidate && _platform.IsStorageReady;
        }

        private bool TryCaptureSave(out string json, out string error)
        {
            try
            {
                SaveEnvelope envelope = SaveCodec.Capture(_room, _state, ContentVersion, _moves.ToArray(), _selectedPieceIndex);
                json = JsonUtility.ToJson(envelope);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                json = null;
                error = "save_capture_failed:" + exception.GetType().Name;
                return false;
            }
        }

        private void QueuePlatformSave(bool manual)
        {
            if (!UsesTossPlatformStorage())
            {
                if (manual) RecordSaveFailure("storage_not_ready", "저장소를 다시 확인해 주세요");
                return;
            }

            if (!manual && _platformWrites.ManualPending) return;

            string payload;
            string error;
            if (!TryCaptureSave(out payload, out error))
            {
                RecordSaveFailure(error, manual ? "저장하지 못했습니다. 다시 저장할 수 있습니다" : "저장하지 못했습니다. 진행은 계속할 수 있습니다");
                return;
            }

            int requestId = ++_nextSaveRequestId;
            TossWriteRequest requestToStart;
            if (_platformWrites.Queue(new TossWriteRequest(requestId, payload, manual), out requestToStart))
            {
                if (manual) StartPlatformManualSaveUiWatchdog(requestId);
                BeginPlatformSave(requestToStart);
            }
            else if (manual)
            {
                StartPlatformManualSaveUiWatchdog(requestId);
            }

            SyncPlatformSaveUi();
        }

        private void BeginPlatformSave(TossWriteRequest request)
        {
            _platform.Store(request.Id, request.Payload, CompletePlatformSave);
        }

        private void CompletePlatformSave(int requestId, TossPlatformOperationResult result)
        {
            TossNativeSaveCompletion completion;
            TossWriteRequest nextToStart;
            if (!_platformWrites.Complete(requestId, result.Succeeded, result.Error, out completion, out nextToStart)) return;

            if (completion.Request.IsManual) StopPlatformManualSaveUiWatchdog(requestId);
            SyncPlatformSaveUi();
            if (!result.Succeeded && completion.AffectsCurrentStatus)
            {
                _message = completion.Request.IsManual
                    ? "저장하지 못했습니다. 다시 저장할 수 있습니다"
                    : "저장하지 못했습니다. 진행은 계속할 수 있습니다";
            }

            if (nextToStart.Id != 0)
            {
                BeginPlatformSave(nextToStart);
            }

            PublishState();
        }

        private void RequestPlatformCheckpoint()
        {
            if (!_initialized || _restoreBlocked || _transitioning || _state == null) return;
            QueuePlatformSave(manual: false);
            PublishState();
        }

        private void SyncPlatformSaveUi()
        {
            _manualSavePending = _platformWrites.ManualPending;
            _pendingSaveRequestId = _platformWrites.PendingManualRequestId;
            _saveStatus = _platformWrites.Status;
            _saveError = _platformWrites.Error;
        }

        private void StartPlatformManualSaveUiWatchdog(int requestId)
        {
            StopPlatformManualSaveUiWatchdog();
            _platformManualSaveUiWatchdogRequestId = requestId;
            _platformManualSaveUiTimeoutRoutine = StartCoroutine(WatchPlatformManualSaveUi(requestId));
        }

        private void StopPlatformManualSaveUiWatchdog(int requestId = 0)
        {
            if (_platformManualSaveUiTimeoutRoutine == null) return;
            if (requestId != 0 && _platformManualSaveUiWatchdogRequestId != requestId) return;
            StopCoroutine(_platformManualSaveUiTimeoutRoutine);
            _platformManualSaveUiTimeoutRoutine = null;
            _platformManualSaveUiWatchdogRequestId = 0;
        }

        private IEnumerator WatchPlatformManualSaveUi(int requestId)
        {
            yield return new WaitForSecondsRealtime(PlatformManualSaveUiTimeoutSeconds);
            if (_platformManualSaveUiWatchdogRequestId != requestId) yield break;
            _platformManualSaveUiTimeoutRoutine = null;
            _platformManualSaveUiWatchdogRequestId = 0;
            if (!_platformWrites.TimeoutManual(requestId)) yield break;

            SyncPlatformSaveUi();
            _message = "저장이 아직 끝나지 않았습니다. 진행은 계속할 수 있습니다";
            PublishState();
        }

        private void CompletePlatformShare(int requestId, string fingerprint, TossPlatformOperationResult result)
        {
            if (_pendingPlatformShareRequestId != requestId
                || !string.Equals(_pendingPlatformShareFingerprint, fingerprint, StringComparison.Ordinal)
                || _state == null
                || !string.Equals(GameEngine.Fingerprint(_room, _state), fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _pendingPlatformShareRequestId = 0;
            _pendingPlatformShareFingerprint = null;
            _platformShareResultRequestId = requestId;
            if (result.Succeeded)
            {
                _platformShareStatus = "shared";
                _platformShareError = string.Empty;
            }
            else
            {
                _platformShareStatus = "failed";
                _platformShareError = result.Error ?? "share_unknown";
            }

            PublishState();
        }

        private void InvalidatePendingPlatformShare()
        {
            _pendingPlatformShareRequestId = 0;
            _platformShareResultRequestId = 0;
            _pendingPlatformShareFingerprint = null;
            _platformShareStatus = "idle";
            _platformShareError = string.Empty;
        }

        private IEnumerator WatchWebGLPersistence(int requestId)
        {
            yield return new WaitForSecondsRealtime(WebGLPersistenceTimeoutSeconds);
            CompleteManualSave(requestId, "timeout");
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

        private void CompleteManualSave(int requestId, string result)
        {
            if (!_manualSavePending || requestId != _pendingSaveRequestId)
            {
                return;
            }

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
                RecordSaveFailure("webgl_persist_" + (result ?? "unknown"), "저장하지 못했습니다. 다시 저장할 수 있습니다");
            }

            PublishState();
        }

        private void StopSaveTimeout()
        {
            if (_saveTimeoutRoutine == null) return;
            StopCoroutine(_saveTimeoutRoutine);
            _saveTimeoutRoutine = null;
        }

        private void StartOver()
        {
            if (!_initialized || _manualSavePending)
            {
                PublishState();
                return;
            }

            CancelTransition();
            InvalidatePendingPlatformShare();
            _restoreBlocked = false;
            _loadError = string.Empty;
            _roomIndex = 0;
            _room = _rooms[_roomIndex];
            _state = GameEngine.Create(_room);
            _moves.Clear();
            _selectedPieceIndex = _room.TargetPieceIndex;
            _message = "처음부터 시작했습니다";
            _board.Render(_room, _state, _selectedPieceIndex);
            RequestManualSave();
        }

        private void Restart()
        {
            if (!_initialized || _manualSavePending)
            {
                PublishState();
                return;
            }

            CancelTransition();
            InvalidatePendingPlatformShare();
            _restoreBlocked = false;
            _loadError = string.Empty;
            _state = GameEngine.Create(_room);
            _moves.Clear();
            _selectedPieceIndex = _room.TargetPieceIndex;
            _message = "이 방을 다시 시작했습니다";
            AutosaveCurrentState();
            _board.Render(_room, _state, _selectedPieceIndex);
            PublishState();
        }

        private void AdvanceToNextRoom()
        {
            if (_restoreBlocked || _transitioning || _manualSavePending || _state.Status != RunStatus.Cleared || _roomIndex + 1 >= _rooms.Length)
            {
                PublishState();
                return;
            }

            CancelTransition();
            InvalidatePendingPlatformShare();
            _roomIndex++;
            _room = _rooms[_roomIndex];
            _state = GameEngine.Create(_room);
            _moves.Clear();
            _selectedPieceIndex = _room.TargetPieceIndex;
            _message = "다음 방을 시작했습니다";
            AutosaveCurrentState();
            _board.Render(_room, _state, _selectedPieceIndex);
            PublishState();
        }

        private void RecordSaveFailure(string error, string message)
        {
            _saveStatus = "failed";
            _saveError = error ?? "save_unknown";
            if (!string.IsNullOrEmpty(message)) _message = message;
        }

        private void BlockOnRestoreError(string error, string message)
        {
            _restoreBlocked = true;
            _loadError = error ?? "save_invalid";
            RecordSaveFailure(_loadError, message);
        }

        private void FailInitialization(string message, string error)
        {
            _initialized = false;
            _message = message;
            _loadError = error;
            PublishState();
        }

        private void PublishState()
        {
            var observation = new StateObservation
            {
                initialized = _initialized,
                inputEnabled = _initialized && !_restoreBlocked && !_manualSavePending && !_transitioning && _state != null && _state.Status == RunStatus.Playing,
                restartEnabled = _initialized && !_manualSavePending,
                nextEnabled = _initialized && !_restoreBlocked && !_manualSavePending && !_transitioning && _state != null &&
                    _state.Status == RunStatus.Cleared && _roomIndex + 1 < _rooms.Length,
                manualSaveEnabled = _initialized && !_restoreBlocked && !_manualSavePending && !_transitioning && _state != null,
                startOverEnabled = _initialized && !_manualSavePending && !_transitioning && _state != null,
                savePending = _manualSavePending,
                saveStatus = _saveStatus,
                saveError = _saveError,
                saveRequestId = _manualSavePending ? _pendingSaveRequestId : 0,
                tossCandidate = _platform != null && _platform.IsTossCandidate,
                tossStorageReady = _platform != null && _platform.IsStorageReady,
                startupRetryEnabled = _platform != null && _platform.IsTossCandidate && _platformStartupBlocked,
                platformShareEnabled = _platform != null && _platform.IsTossCandidate && _platform.IsStorageReady
                    && _initialized && !_restoreBlocked && !_transitioning && _state != null && _state.Status == RunStatus.Cleared,
                platformShareRequestId = _pendingPlatformShareRequestId,
                platformShareResultRequestId = _platformShareResultRequestId,
                platformShareStatus = _platformShareStatus,
                platformShareError = _platformShareError,
                room = _room != null ? _room.Id : string.Empty,
                roomNumber = _initialized ? _roomIndex + 1 : 0,
                roomCount = _rooms != null ? _rooms.Length : 0,
                turn = _state != null ? _state.Turn : 0,
                status = _initialized && _state != null ? TranslateStatus(_state.Status) : "시작 실패",
                statusCode = _initialized && _state != null ? _state.Status.ToString() : "InitializationFailed",
                message = _message,
                error = _loadError,
                transitioning = _transitioning,
                selectedPieceIndex = _selectedPieceIndex,
                pieceCount = _room != null && _room.Pieces != null ? _room.Pieces.Length : 0,
                parMoves = _room != null ? _room.ParMoves : 0,
                fingerprint = _room != null && _state != null
                    ? GameEngine.Fingerprint(_room, _state)
                    : string.Empty
            };

            string json = JsonUtility.ToJson(observation);
            Debug.Log("STATE_OBSERVATION " + json);
#if UNITY_WEBGL && !UNITY_EDITOR
            NectorialReportState(json);
#endif
        }

        private static bool TryParseCommand(string input, out GameCommand command)
        {
            switch (input)
            {
                case "Up": command = GameCommand.Up; return true;
                case "Down": command = GameCommand.Down; return true;
                case "Left": command = GameCommand.Left; return true;
                case "Right": command = GameCommand.Right; return true;
                default: command = GameCommand.Up; return false;
            }
        }

        private static RoomDefinition[] LoadRoomCatalog(out string error)
        {
            error = null;
            TextAsset[] assets = Resources.LoadAll<TextAsset>(RoomResources);
            if (assets == null || assets.Length == 0)
            {
                error = "content_missing:rooms";
                return null;
            }

            var rooms = new List<RoomDefinition>(assets.Length);
            for (int index = 0; index < assets.Length; index++)
            {
                RoomDefinition room;
                try
                {
                    room = JsonUtility.FromJson<RoomDefinition>(assets[index].text);
                }
                catch (Exception exception)
                {
                    error = "content_json_invalid:" + exception.GetType().Name;
                    return null;
                }

                string[] roomErrors = GameEngine.ValidateRoom(room);
                if (roomErrors.Length > 0)
                {
                    error = "content_invalid:" + roomErrors[0];
                    return null;
                }

                rooms.Add(room);
            }

            rooms.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            for (int index = 1; index < rooms.Count; index++)
            {
                if (string.Equals(rooms[index - 1].Id, rooms[index].Id, StringComparison.Ordinal))
                {
                    error = "content_duplicate_room_id:" + rooms[index].Id;
                    return null;
                }
            }

            if (!string.Equals(rooms[0].Id, "slide-01", StringComparison.Ordinal))
            {
                error = "content_first_room_missing";
                return null;
            }

            return rooms.ToArray();
        }

        private int FindRoomIndex(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                return -1;
            }

            for (int index = 0; index < _rooms.Length; index++)
            {
                if (string.Equals(_rooms[index].Id, roomId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        private static string TranslateResult(StepResult result)
        {
            if (!result.Accepted)
            {
                switch (result.Reason)
                {
                    case "blocked_zero": return "그 방향으로는 미끄러질 수 없습니다";
                    case "invalid_piece_index": return "선택한 기물을 확인할 수 없습니다";
                    case "terminal_state": return "다시 시작해 주세요";
                    default: return "움직일 수 없습니다";
                }
            }

            if (result.State.Status == RunStatus.Cleared) return "목표에 도착했습니다";
            return "한 수를 밀었습니다";
        }

        private static string TranslateStatus(RunStatus status)
        {
            switch (status)
            {
                case RunStatus.Cleared: return "클리어";
                default: return "풀이 중";
            }
        }

        // Only the separately built "Few Moves 3D Preview" product uses the 3D board; Few Moves keeps the 2D board.
        // A preview without its imported meshes fails visibly instead of falling back to 2D.
        private static IBoardView CreateBoardView(Texture2D atlas, out string error)
        {
            error = string.Empty;
            if (!Board3DView.IsPreviewProduct) return new GameBoardView(atlas);
            IBoardView view;
            return Board3DView.TryCreate(out view, out error) ? view : null;
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

            if (Board3DView.IsPreviewProduct)
            {
                Preview3DCamera.Configure(camera);
                return;
            }

            camera.orthographic = true;
            camera.orthographicSize = 4.65f;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(0xf0, 0xec, 0xe2, 0xff);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void NectorialReportState(string json);

        [DllImport("__Internal")]
        private static extern void NectorialPersistSave(int requestId);
#endif

        [Serializable]
        private sealed class StateObservation
        {
            public bool initialized;
            public bool inputEnabled;
            public bool restartEnabled;
            public bool nextEnabled;
            public bool manualSaveEnabled;
            public bool startOverEnabled;
            public bool savePending;
            public string saveStatus;
            public string saveError;
            public int saveRequestId;
            public bool tossCandidate;
            public bool tossStorageReady;
            public bool startupRetryEnabled;
            public bool platformShareEnabled;
            public int platformShareRequestId;
            public int platformShareResultRequestId;
            public string platformShareStatus;
            public string platformShareError;
            public string room;
            public int roomNumber;
            public int roomCount;
            public int turn;
            public string status;
            public string statusCode;
            public string message;
            public string error;
            public bool transitioning;
            public int selectedPieceIndex;
            public int pieceCount;
            public int parMoves;
            public string fingerprint;
        }
    }
}
