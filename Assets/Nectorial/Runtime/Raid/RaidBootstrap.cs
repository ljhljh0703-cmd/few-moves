using System;
using System.Collections;
using System.Runtime.InteropServices;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Raid;
using Nectorial.SlideEscape.Record;
using Nectorial.SlideEscape.Unity.Record;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity.Raid
{
    public sealed class RaidBootstrap : MonoBehaviour
    {
        private const string ProductName = "Few Moves Raid Pilot";
        private const string ArenaResource = RaidContent.DefaultArenaResource;
        private const string SaveKey = "nectorial-raid.save.v1.raid-v1." + RaidContent.DefaultArenaId;
        private const string FailedSaveKey = SaveKey + ".restore-failed";
        private const int SharedRecordRequestIdMaximumLength = 96;

        private RaidArenaDefinition _arena;
        private RaidSession _session;
        private RaidState _displayState;
        private RaidBoardView _board;
        private RaidFrame[] _lastFrames = new RaidFrame[0];
        private RaidDispatchResult _pendingAction;
        private bool _initialized;
        private bool _restoreBlocked;
        private bool _transitioning;
        private int _nextCommandId;
        private readonly string _commandPrefix = "raid-runtime-" + Guid.NewGuid().ToString("N");
        private Coroutine _transitionRoutine;
        private string _message = "초기화 중";
        private string _saveStatus = "idle";
        private string _saveError = string.Empty;
        private bool _hasMine;
        private bool _hasShared;
        private RecordSummaryObservation _mine;
        private RecordSummaryObservation _shared;
        private string _mineCapsule = string.Empty;
        private string _recordCapsule = string.Empty;
        // Presentation only: OS reduced motion makes slides instant and cell cues static; rules and order are unchanged.
        private bool _reducedMotion;
        // Brief hold after a caught/clear action so its cell mark is seen before the result is published.
        private const float TerminalMarkHoldSeconds = 0.35f;
        // Result comparison for a live clear against the best that existed before it; never set by a reload.
        private string _clearComparison = string.Empty;
        private int _clearComparisonDelta;
        private string _clearComparisonFingerprint = string.Empty;
        private RecordSummaryObservation _lastCapturedSummary;
        // Set only when an accepted move commits a clear; the first of checkpoint save or presentation completion
        // settles its record/best and comparison exactly once, so a checkpoint never pairs the clear with an older best.
        private bool _clearSettlementPending;
        private string _sharedCapsule = string.Empty;
        private string _sharedRecordRequestId = string.Empty;
        private string _recordStatus = "idle";
        private string _recordError = string.Empty;
        // Toss native storage (Toss builds only): one raid-scoped payload holds progress and the verified best.
        // Ordinary web keeps PlayerPrefs; a Toss candidate never falls back to it.
        private const float PlatformManualSaveUiTimeoutSeconds = 10f;
        private TossPlatformAdapter _platform;
        private TossNativeSaveCoordinator _platformWrites = new TossNativeSaveCoordinator();
        private bool _platformStartupBlocked;
        private int _nextSaveRequestId;
        private bool _manualSaveAwaiting;
        private int _manualSaveWatchRequestId;
        private Coroutine _manualSaveWatchRoutine;
#if UNITY_EDITOR
        private string _lastObservationJsonForCheck = string.Empty;
        // Editor probe seam: when set, the bootstrap takes the Toss path and records writes instead of calling the SDK.
        private System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, string>> _tossWritesForCheck;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateRuntime()
        {
            if (!string.Equals(Application.productName, ProductName, StringComparison.Ordinal)) return;
            if (FindAnyObjectByType<RaidBootstrap>() != null) return;
            var bootstrap = new GameObject("RaidBootstrap");
            DontDestroyOnLoad(bootstrap);
            bootstrap.AddComponent<RaidBootstrap>();
        }

        public RaidArenaDefinition Arena { get { return RaidRules.CloneArena(_arena); } }
        public RaidState State { get { return _session == null ? null : _session.State; } }
        public RaidState DisplayState { get { return _displayState == null ? null : RaidRules.CloneState(_displayState); } }
        public RaidFrame[] LastActionFrames { get { return RaidRules.CloneFrames(_lastFrames); } }
        public bool Transitioning { get { return _transitioning; } }

        private void Awake()
        {
            _platform = GetComponent<TossPlatformAdapter>();
            if (_platform == null) _platform = gameObject.AddComponent<TossPlatformAdapter>();
            _platform.CheckpointRequested += RequestPlatformCheckpoint;
            ConfigureCamera();
            InitializeRaid();
        }

        public void SetReducedMotion(string value)
        {
            _reducedMotion = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            if (_board != null) _board.SetReducedMotion(_reducedMotion);
        }

        private void LateUpdate()
        {
            if (_board != null) _board.TickEffects(Time.unscaledDeltaTime);
        }

        private void OnDestroy()
        {
            CancelActionPresentation();
            StopManualSaveWatch();
            if (_platform != null) _platform.CheckpointRequested -= RequestPlatformCheckpoint;
            if (_board != null) _board.Dispose();
        }

        private void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return;
#else
            if (Input.GetKeyDown(KeyCode.UpArrow)) HandleRaidInput("Up");
            else if (Input.GetKeyDown(KeyCode.DownArrow)) HandleRaidInput("Down");
            else if (Input.GetKeyDown(KeyCode.LeftArrow)) HandleRaidInput("Left");
            else if (Input.GetKeyDown(KeyCode.RightArrow)) HandleRaidInput("Right");
            else if (Input.GetKeyDown(KeyCode.R)) HandleRaidInput("Restart");
#endif
        }

        public void HandleRaidInput(string input)
        {
            if (!_initialized) return;
            if (_transitioning)
            {
                _message = "이동이 끝난 뒤 다시 입력하세요";
                PublishState();
                return;
            }
            if (string.Equals(input, "Restart", StringComparison.Ordinal))
            {
                RestartRaid();
                return;
            }
            if (string.Equals(input, "Save", StringComparison.Ordinal))
            {
                if (UsesTossStorage())
                {
                    if (!_manualSaveAwaiting) QueuePlatformSave(true);
                    PublishState();
                    return;
                }
                SaveCurrent();
                _message = _saveStatus == "saved" ? "저장했습니다" : "저장하지 못했습니다";
                PublishState();
                return;
            }
            GameCommand direction;
            if (!TryParseDirection(input, out direction))
            {
                _message = "알 수 없는 레이드 입력입니다";
                PublishState();
                return;
            }
            RaidDispatchResult result;
            if (!TryStartMove(direction, out result)) return;
            BeginActionPresentation();
        }

        public void HandleCommand(string json)
        {
            if (string.IsNullOrEmpty(json) || (!_initialized && !_platformStartupBlocked)) return;
            RaidInput input;
            try { input = JsonUtility.FromJson<RaidInput>(json); }
            catch (Exception exception)
            {
                _message = "레이드 입력을 읽을 수 없습니다";
                _saveError = "raid_input_json:" + exception.GetType().Name;
                PublishState();
                return;
            }
            if (input == null || string.IsNullOrEmpty(input.kind)) return;
            if (string.Equals(input.kind, "RetryStorage", StringComparison.Ordinal))
            {
                RetryPlatformStartup();
                return;
            }
            if (!_initialized) return;
            if (string.Equals(input.kind, "Restart", StringComparison.Ordinal))
            {
                HandleRaidInput("Restart");
                return;
            }
            if (string.Equals(input.kind, "Save", StringComparison.Ordinal))
            {
                HandleRaidInput("Save");
                return;
            }
            if (string.Equals(input.kind, "GetRecord", StringComparison.Ordinal))
            {
                GetRecord();
                return;
            }
            if (string.Equals(input.kind, "LoadSharedRecord", StringComparison.Ordinal))
            {
                LoadSharedRecord(input.capsule, input.requestId);
                return;
            }
            if (string.Equals(input.kind, "Challenge", StringComparison.Ordinal))
            {
                ChallengeSharedRecord();
                return;
            }
            if (string.Equals(input.kind, "Slide", StringComparison.Ordinal))
            {
                HandleRaidInput(input.direction);
                return;
            }
            _message = "알 수 없는 레이드 입력입니다";
            PublishState();
        }

        public bool TryStartMove(GameCommand direction, out RaidDispatchResult result)
        {
            result = null;
            if (!_initialized || _restoreBlocked || _transitioning || _session == null)
            {
                PublishState();
                return false;
            }
            RaidState beforeAction = _session.State;
            result = _session.Dispatch(new RaidMove { CommandId = NextCommandId(), Direction = direction });
            _lastFrames = RaidRules.CloneFrames(result.Frames);
            if (!result.Accepted)
            {
                _message = Translate(result.Reason);
                PublishState();
                return false;
            }
            ClearComparison();
            _clearSettlementPending = result.State != null && result.State.Status == RaidRunStatus.Cleared;
            _pendingAction = result;
            _displayState = beforeAction;
            _transitioning = true;
            _message = "이동 중";
            PublishState();
            return true;
        }

        private void BeginActionPresentation()
        {
            if (_pendingAction == null) return;
            if (_board == null || !_board.BeginAction(_arena, _pendingAction.Frames))
            {
                CompleteActionPresentation();
                return;
            }
            float duration = _reducedMotion ? 0f : PresentationDuration(_pendingAction.Frames.Length);
            RaidRunStatus finalStatus = _pendingAction.State == null ? RaidRunStatus.Playing : _pendingAction.State.Status;
            float hold = finalStatus == RaidRunStatus.Cleared || finalStatus == RaidRunStatus.Failed ? TerminalMarkHoldSeconds : 0f;
            _transitionRoutine = StartCoroutine(CompleteActionAfter(duration, hold));
        }

        private IEnumerator CompleteActionAfter(float duration, float hold)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                if (_board != null) _board.AdvanceAction(Mathf.Clamp01(elapsed / duration));
                yield return null;
            }
            if (_board != null) _board.AdvanceAction(1f);
            float held = 0f;
            while (held < hold)
            {
                held += Time.unscaledDeltaTime;
                yield return null;
            }
            _transitionRoutine = null;
            CompleteActionPresentation();
        }

        public void CompleteActionPresentation()
        {
            if (_pendingAction == null) return;
            RaidDispatchResult completed = _pendingAction;
            _pendingAction = null;
            _transitioning = false;
            _displayState = _session.State;
            if (_board != null) _board.CompleteAction(_arena, _session.State);
            _message = Describe(completed);
            SettleClearIfPending();
            // After the best is settled, so a Toss payload written here carries both the clear and the new best.
            SaveCurrent();
            PublishState();
        }

        public void RestartRaid()
        {
            if (!_initialized || _session == null) return;
            if (_transitioning)
            {
                _message = "이동이 끝난 뒤 다시 입력하세요";
                PublishState();
                return;
            }
            if (_restoreBlocked)
            {
                string preservationError;
                if (!TryPreserveFailedSave(out preservationError))
                {
                    _saveStatus = "failed";
                    _saveError = preservationError;
                    _message = "저장된 레이드를 보존하지 못했습니다";
                    PublishState();
                    return;
                }
            }
            CancelActionPresentation();
            _restoreBlocked = false;
            _saveStatus = "idle";
            _saveError = string.Empty;
            _lastFrames = new RaidFrame[0];
            _clearSettlementPending = false;
            RaidDispatchResult restarted = _session.Restart();
            _displayState = _session.State;
            if (_board != null) { _board.ClearEffects(); _board.Render(_arena, _session.State); }
            ClearComparison();
            _message = Translate(restarted.Reason);
            SaveCurrent();
            PublishState();
        }

        private void InitializeRaid()
        {
            try
            {
                TextAsset asset = Resources.Load<TextAsset>(ArenaResource);
                if (asset == null)
                {
                    Fail("레이드 arena를 읽을 수 없습니다", "raid_arena_missing");
                    return;
                }
                _arena = JsonUtility.FromJson<RaidArenaDefinition>(asset.text);
                string[] errors = RaidRules.ValidateArena(_arena);
                if (errors.Length > 0)
                {
                    Fail("레이드 arena가 올바르지 않습니다", "raid_arena_invalid:" + errors[0]);
                    return;
                }
                ConfigureCameraForArena(_arena);
                _session = RaidSession.Create(_arena);
                _displayState = _session.State;
                _board = new RaidBoardView();
                _board.SetReducedMotion(_reducedMotion);
                if (UsesTossStorage())
                {
                    // Input stays off until the raid-scoped Toss payload is read and verified (or is absent).
                    _message = "토스 저장소를 확인하고 있습니다";
                    _saveStatus = "pending";
                    _saveError = string.Empty;
                    _board.Render(_arena, _session.State);
                    _platform.BeginRaidStartup(_arena.Id, RaidRules.ArenaFingerprint(_arena), CompletePlatformStartup);
                    PublishState();
                    return;
                }
                _initialized = true;
                RestoreIfPresent();
                LoadBestRecord();
                if (_board != null) _board.Render(_arena, _session.State);
                if (!_restoreBlocked && string.IsNullOrEmpty(_message)) _message = "레이드를 시작하세요";
                PublishState();
            }
            catch (Exception exception)
            {
                Fail("레이드를 시작할 수 없습니다", "raid_init:" + exception.GetType().Name);
            }
        }

        private void RestoreIfPresent()
        {
            if (!PlayerPrefs.HasKey(SaveKey))
            {
                _message = "레이드를 시작하세요";
                return;
            }
            try
            {
                RaidSaveEnvelope envelope = JsonUtility.FromJson<RaidSaveEnvelope>(PlayerPrefs.GetString(SaveKey));
                string normalizeError;
                if (!RaidSaveSerializationAdapter.TryNormalize(envelope, out normalizeError))
                {
                    BlockRestore(normalizeError);
                    return;
                }
                RaidSession restored;
                string restoreError;
                if (!RaidSaveCodec.TryRestore(_arena, envelope, out restored, out restoreError))
                {
                    BlockRestore(restoreError);
                    return;
                }
                _session = restored;
                _displayState = _session.State;
                _message = "저장된 레이드를 불러왔습니다";
                _saveStatus = "saved";
            }
            catch (Exception exception)
            {
                BlockRestore("raid_save_json:" + exception.GetType().Name);
            }
        }

        private void SaveCurrent()
        {
            if (!_initialized || _restoreBlocked || _session == null) return;
            if (UsesTossStorage())
            {
                QueuePlatformSave(false);
                return;
            }
            try
            {
                RaidSaveEnvelope envelope = RaidSaveCodec.Capture(_session);
                PlayerPrefs.SetString(SaveKey, JsonUtility.ToJson(envelope));
                PlayerPrefs.Save();
                _saveStatus = "saved";
                _saveError = string.Empty;
            }
            catch (Exception exception)
            {
                _saveStatus = "failed";
                _saveError = "raid_save_write:" + exception.GetType().Name;
            }
        }

        private bool UsesTossStorage()
        {
#if UNITY_EDITOR
            if (_tossWritesForCheck != null) return true;
#endif
            return _platform != null && _platform.IsTossCandidate;
        }

        private void CompletePlatformStartup(TossStartupResult result)
        {
            ResetPlatformProgress();
            if (result.Kind == TossStartupKind.Blocked || result.Kind == TossStartupKind.WebFallback)
            {
                // A Toss candidate never adopts PlayerPrefs, even if the adapter unexpectedly reports web.
                BlockPlatformStartup(result.Kind == TossStartupKind.Blocked ? (result.Error ?? "startup_unavailable") : "startup_unexpected_web");
                return;
            }
            if (result.Kind == TossStartupKind.ExistingPayload)
            {
                string error;
                if (!TryRestorePlatformPayload(result.Payload, out error))
                {
                    ResetPlatformProgress();
                    BlockPlatformStartup(error);
                    return;
                }
                _message = "저장된 레이드를 불러왔습니다";
                _saveStatus = "saved";
            }
            else
            {
                _message = "레이드를 시작하세요";
                _saveStatus = "idle";
            }
            _platformStartupBlocked = false;
            _initialized = true;
            _saveError = string.Empty;
            if (_board != null) _board.Render(_arena, _session.State);
            PublishState();
        }

        private void ResetPlatformProgress()
        {
            CancelActionPresentation();
            _session = RaidSession.Create(_arena);
            _displayState = _session.State;
            _lastFrames = new RaidFrame[0];
            _restoreBlocked = false;
            _hasMine = false;
            _mine = null;
            _mineCapsule = string.Empty;
            _recordCapsule = string.Empty;
            _recordStatus = "idle";
            _recordError = string.Empty;
            _clearSettlementPending = false;
            ClearComparison();
        }

        private void BlockPlatformStartup(string error)
        {
            _initialized = false;
            _platformStartupBlocked = true;
            _message = "토스 저장소를 확인하지 못했습니다. 다시 확인해 주세요";
            _saveStatus = "failed";
            _saveError = string.IsNullOrEmpty(error) ? "startup_unavailable" : error;
            if (_board != null) _board.Render(_arena, _session.State);
            PublishState();
        }

        public void RetryPlatformStartup()
        {
            if (!_platformStartupBlocked || !UsesTossStorage())
            {
                PublishState();
                return;
            }
            _platformStartupBlocked = false;
            _message = "토스 저장소를 다시 확인하고 있습니다";
            _saveStatus = "pending";
            _saveError = string.Empty;
            PublishState();
            if (_platform != null && _platform.IsTossCandidate) _platform.RetryStartup(CompletePlatformStartup);
        }

        // Both parts must verify against the live arena; either failing blocks startup rather than dropping data.
        private bool TryRestorePlatformPayload(string payload, out string error)
        {
            string best;
            string progress;
            if (!TossPlatformPolicy.TryParseRaidPayload(payload, _arena.Id, RaidRules.ArenaFingerprint(_arena), out best, out progress, out error)) return false;
            try
            {
                RaidSaveEnvelope envelope = JsonUtility.FromJson<RaidSaveEnvelope>(progress);
                if (envelope == null) { error = "raid_toss_progress_missing"; return false; }
                if (!RaidSaveSerializationAdapter.TryNormalize(envelope, out error)) return false;
                RaidSession restored;
                if (!RaidSaveCodec.TryRestore(_arena, envelope, out restored, out error)) return false;
                RecordSummaryObservation bestSummary = null;
                if (best.Length > 0 && !TryVerifyBestCapsule(best, out bestSummary))
                {
                    error = "raid_toss_best_invalid";
                    return false;
                }
                _session = restored;
                _displayState = _session.State;
                if (bestSummary != null)
                {
                    _mine = bestSummary;
                    _mineCapsule = best;
                    _hasMine = true;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "raid_toss_progress_json:" + exception.GetType().Name;
                return false;
            }
        }

        private void QueuePlatformSave(bool manual)
        {
            if (!_initialized || _platformStartupBlocked || _session == null || _arena == null) return;
            // A checkpoint can arrive while the clearing move is still animating; settle first so the payload carries
            // the best derived from that committed clear.
            SettleClearIfPending();
            string payload;
            try
            {
                string best = _hasMine && RecordObservationGuard.IsUsable(true, _mine) ? _mineCapsule : string.Empty;
                payload = TossPlatformPolicy.FormatRaidPayload(_arena.Id, RaidRules.ArenaFingerprint(_arena), best, JsonUtility.ToJson(RaidSaveCodec.Capture(_session)));
            }
            catch (Exception exception)
            {
                _saveStatus = "failed";
                _saveError = "raid_save_write:" + exception.GetType().Name;
                if (manual) _message = "저장하지 못했습니다. 다시 저장할 수 있습니다";
                return;
            }
            int requestId = ++_nextSaveRequestId;
            TossWriteRequest requestToStart;
            bool startNow = _platformWrites.Queue(new TossWriteRequest(requestId, payload, manual), out requestToStart);
            if (manual)
            {
                _manualSaveAwaiting = true;
                _message = "저장하고 있습니다";
                StartManualSaveWatch(requestId);
            }
            SyncPlatformSaveUi();
            // The coordinator never overlaps native writes: a newer payload waits behind the active one.
            if (startNow) StorePlatform(requestToStart);
        }

        private void StorePlatform(TossWriteRequest request)
        {
#if UNITY_EDITOR
            if (_tossWritesForCheck != null)
            {
                _tossWritesForCheck.Add(new System.Collections.Generic.KeyValuePair<int, string>(request.Id, request.Payload));
                return;
            }
#endif
            _platform.Store(request.Id, request.Payload, CompletePlatformSave);
        }

        private void CompletePlatformSave(int requestId, TossPlatformOperationResult result)
        {
            TossNativeSaveCompletion completion;
            TossWriteRequest nextToStart;
            if (!_platformWrites.Complete(requestId, result.Succeeded, result.Error, out completion, out nextToStart)) return;
            // Only the newest payload's completion may change what the player sees; older ones are superseded.
            if (completion.AffectsCurrentStatus)
            {
                SyncPlatformSaveUi();
                if (_manualSaveAwaiting)
                {
                    _manualSaveAwaiting = false;
                    StopManualSaveWatch();
                    _message = result.Succeeded ? "저장했습니다" : "저장하지 못했습니다. 다시 저장할 수 있습니다";
                }
                else if (!result.Succeeded)
                {
                    _message = "저장하지 못했습니다. 진행은 계속할 수 있습니다";
                }
            }
            if (nextToStart.Id != 0) StorePlatform(nextToStart);
            PublishState();
        }

        private void RequestPlatformCheckpoint()
        {
            if (!_initialized || _platformStartupBlocked || _session == null) return;
            QueuePlatformSave(false);
            PublishState();
        }

        private void SyncPlatformSaveUi()
        {
            _saveStatus = _platformWrites.Status;
            _saveError = _platformWrites.Error;
        }

        private void StartManualSaveWatch(int requestId)
        {
            StopManualSaveWatch();
            _manualSaveWatchRequestId = requestId;
            if (isActiveAndEnabled) _manualSaveWatchRoutine = StartCoroutine(WatchManualSave(requestId));
        }

        private void StopManualSaveWatch()
        {
            if (_manualSaveWatchRoutine != null) StopCoroutine(_manualSaveWatchRoutine);
            _manualSaveWatchRoutine = null;
            _manualSaveWatchRequestId = 0;
        }

        private IEnumerator WatchManualSave(int requestId)
        {
            yield return new WaitForSecondsRealtime(PlatformManualSaveUiTimeoutSeconds);
            _manualSaveWatchRoutine = null;
            TimeoutManualSave(requestId);
        }

        // The native write keeps running; only the waiting UI is released. A later completion of the newest
        // payload still reports its real result.
        private void TimeoutManualSave(int requestId)
        {
            if (!_manualSaveAwaiting || _manualSaveWatchRequestId != requestId) return;
            _manualSaveAwaiting = false;
            _manualSaveWatchRequestId = 0;
            _platformWrites.TimeoutManual(requestId);
            _saveStatus = "failed";
            _saveError = "storage_ui_timeout";
            _message = "저장이 아직 끝나지 않았습니다. 진행은 계속할 수 있습니다";
            PublishState();
        }

        private void GetRecord()
        {
            CaptureCurrentRecord(true);
        }

        private bool CaptureCurrentRecord(bool userRequested)
        {
            if (!_initialized || _arena == null || _session == null || _session.State.Status != RaidRunStatus.Cleared)
            {
                if (userRequested) SetRecordFailure("record_not_cleared", "완주한 뒤 기록을 준비할 수 있습니다");
                return false;
            }
            string inputSequence;
            string error;
            if (!TryRecordInputSequence(_session.ExportReplay().Moves, out inputSequence, out error))
            {
                if (userRequested) SetRecordFailure(error, "기록 입력을 확인하지 못했습니다");
                else _recordError = error;
                return false;
            }
            RecordCapsule capsule = CurrentRecordIdentity();
            capsule.InputSequence = inputSequence;
            RecordVerification verification;
            string encoded;
            if (!RecordCapsuleVerifier.TryVerifyRaid(_arena, capsule, out verification) || !RecordCapsuleCodec.TryEncode(capsule, out encoded, out error))
            {
                if (userRequested) SetRecordFailure(string.IsNullOrEmpty(error) ? verification.ErrorCode : error, "기록을 검증하지 못했습니다");
                else _recordError = string.IsNullOrEmpty(error) ? verification.ErrorCode : error;
                return false;
            }
            RecordSummaryObservation summary = RecordSummaryObservation.From(verification);
            if (!RecordObservationGuard.IsUsable(true, summary))
            {
                if (userRequested) SetRecordFailure("record_summary_invalid", "기록 요약을 확인하지 못했습니다");
                else _recordError = "record_summary_invalid";
                return false;
            }
            _recordCapsule = encoded;
            _lastCapturedSummary = summary;
            ConsiderBest(capsule, encoded, summary);
            _recordStatus = "ready";
            if (string.IsNullOrEmpty(_recordError)) _recordError = string.Empty;
            if (userRequested)
            {
                _message = "완주 기록을 준비했습니다";
                PublishState();
            }
            return true;
        }

        // Runs at most once per committed clear. The comparison uses the best as it was before this clear.
        private void SettleClearIfPending()
        {
            if (!_clearSettlementPending) return;
            _clearSettlementPending = false;
            if (_session == null || _session.State.Status != RaidRunStatus.Cleared) return;
            bool hadBest = _hasMine && _mine != null;
            int priorBest = hadBest ? _mine.effectiveActionCount : 0;
            if (CaptureCurrentRecord(false) && _lastCapturedSummary != null)
                SetClearComparison(hadBest, priorBest, _lastCapturedSummary.effectiveActionCount);
        }

        private void ClearComparison()
        {
            _clearComparison = string.Empty;
            _clearComparisonDelta = 0;
            _clearComparisonFingerprint = string.Empty;
        }

        // Moves only: "first" (no earlier best), "improved" by N, "tied", or "slower" by N than the earlier best.
        private void SetClearComparison(bool hadBest, int priorBest, int current)
        {
            if (!hadBest) { _clearComparison = "first"; _clearComparisonDelta = 0; }
            else if (current < priorBest) { _clearComparison = "improved"; _clearComparisonDelta = priorBest - current; }
            else if (current == priorBest) { _clearComparison = "tied"; _clearComparisonDelta = 0; }
            else { _clearComparison = "slower"; _clearComparisonDelta = current - priorBest; }
            _clearComparisonFingerprint = RaidRules.StateFingerprint(_arena, _session.State);
        }

        private RecordCapsule CurrentRecordIdentity()
        {
            return new RecordCapsule
            {
                SchemaVersion = RecordCapsuleRules.SchemaVersion,
                ModeId = RecordCapsuleRules.RaidModeId,
                DefinitionId = _arena.Id,
                RulesVersion = _arena.RulesVersion,
                ContentVersion = _arena.ContentVersion,
                DefinitionFingerprint = RaidRules.ArenaFingerprint(_arena),
                InputSequence = string.Empty
            };
        }

        private void LoadBestRecord()
        {
            if (_arena == null) return;
            string encoded;
            if (!LocalRecordBestStore.TryRead(CurrentRecordIdentity(), out encoded)) return;
            RecordSummaryObservation summary;
            if (!TryVerifyBestCapsule(encoded, out summary)) return;
            _mine = summary;
            _mineCapsule = encoded;
            _hasMine = true;
        }

        private bool TryVerifyBestCapsule(string encoded, out RecordSummaryObservation summary)
        {
            summary = null;
            RecordCapsule capsule;
            string error;
            RecordVerification verification;
            if (!RecordCapsuleCodec.TryDecode(encoded, out capsule, out error) || !RecordCapsuleVerifier.TryVerifyRaid(_arena, capsule, out verification)) return false;
            RecordSummaryObservation candidate = RecordSummaryObservation.From(verification);
            if (!RecordObservationGuard.IsUsable(true, candidate)) return false;
            summary = candidate;
            return true;
        }

        private void ConsiderBest(RecordCapsule identity, string encoded, RecordSummaryObservation candidate)
        {
            if (_hasMine && !LocalRecordBestStore.IsBetter(candidate, _mine)) return;
            // Toss persists the best inside the next raid payload (SaveCurrent after capture), not in PlayerPrefs.
            if (!UsesTossStorage() && !LocalRecordBestStore.TryWrite(identity, encoded))
            {
                _recordError = "record_best_write_failed";
                return;
            }
            _mine = candidate;
            _mineCapsule = encoded;
            _hasMine = true;
        }

        private void LoadSharedRecord(string encoded, string requestId)
        {
            _sharedRecordRequestId = NormalizeSharedRecordRequestId(requestId);
            _hasShared = false;
            _shared = null;
            _sharedCapsule = string.Empty;
            RecordCapsule capsule;
            string error;
            if (!RecordCapsuleCodec.TryDecode(encoded, out capsule, out error))
            {
                SetRecordFailure(error, "공유 기록을 읽지 못했습니다");
                return;
            }
            RecordVerification verification = null;
            if (!_initialized || _arena == null || !RecordCapsuleVerifier.TryVerifyRaid(_arena, capsule, out verification))
            {
                SetRecordFailure(verification == null ? "record_definition_unavailable" : verification.ErrorCode, "공유 기록이 현재 레이드와 맞지 않습니다");
                return;
            }
            RecordSummaryObservation summary = RecordSummaryObservation.From(verification);
            if (!RecordObservationGuard.IsUsable(true, summary))
            {
                SetRecordFailure("shared_summary_invalid", "공유 기록 요약을 확인하지 못했습니다");
                return;
            }
            _shared = summary;
            _hasShared = true;
            _sharedCapsule = encoded;
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
            _recordStatus = "ready";
            _recordError = string.Empty;
            _message = "공유 기록에 도전할 준비가 되었습니다. 다시 시작을 선택하세요";
            PublishState();
        }

        private void SetRecordFailure(string code, string message)
        {
            _recordStatus = "invalid";
            _recordError = string.IsNullOrEmpty(code) ? "record_invalid" : code;
            _message = message;
            PublishState();
        }

        private static bool TryRecordInputSequence(RaidMove[] moves, out string sequence, out string error)
        {
            sequence = string.Empty;
            error = null;
            if (moves == null || moves.Length == 0) { error = "record_moves_missing"; return false; }
            if (moves.Length > RecordCapsuleRules.MaximumInputCharacters) { error = "record_input_too_long"; return false; }
            var characters = new char[moves.Length];
            for (int index = 0; index < moves.Length; index++)
            {
                RaidMove move = moves[index];
                if (move == null) { error = "record_move_missing:" + index.ToString(); return false; }
                if (!TryDirectionCharacter(move.Direction, out characters[index])) { error = "record_direction_invalid:" + index.ToString(); return false; }
            }
            sequence = new string(characters);
            return true;
        }

        private static bool TryDirectionCharacter(GameCommand direction, out char value)
        {
            value = 'U';
            if (direction == GameCommand.Up) return true;
            if (direction == GameCommand.Down) { value = 'D'; return true; }
            if (direction == GameCommand.Left) { value = 'L'; return true; }
            if (direction == GameCommand.Right) { value = 'R'; return true; }
            return false;
        }

        private void BlockRestore(string error)
        {
            _restoreBlocked = true;
            _saveStatus = "failed";
            _saveError = error;
            _message = "저장된 레이드를 불러오지 못했습니다";
        }

        private static bool TryPreserveFailedSave(out string error)
        {
            error = null;
            try
            {
                if (!PlayerPrefs.HasKey(SaveKey))
                {
                    error = "raid_save_backup_missing";
                    return false;
                }
                PlayerPrefs.SetString(FailedSaveKey, PlayerPrefs.GetString(SaveKey));
                PlayerPrefs.Save();
                return true;
            }
            catch (Exception exception)
            {
                error = "raid_save_backup:" + exception.GetType().Name;
                return false;
            }
        }

        private void CancelActionPresentation()
        {
            if (_transitionRoutine != null)
            {
                StopCoroutine(_transitionRoutine);
                _transitionRoutine = null;
            }
            if (_board != null) _board.CancelAction();
            _pendingAction = null;
            _transitioning = false;
            _displayState = _session == null ? null : _session.State;
        }

        private float PresentationDuration(int frameCount)
        {
            bool expandedArena = _arena != null && Mathf.Max(_arena.Width, _arena.Height) >= 16;
            float perFrame = expandedArena ? 0.09f : 0.08f;
            float maximum = expandedArena ? 0.90f : 0.72f;
            return Mathf.Clamp(perFrame * frameCount, 0.16f, maximum);
        }

        private void Fail(string message, string error)
        {
            _initialized = false;
            _message = message;
            _saveStatus = "failed";
            _saveError = error;
            PublishState();
        }

        private void PublishState()
        {
            RaidState state = _session == null ? null : (_transitioning && _displayState != null ? _displayState : _session.State);
            var observation = new RaidObservation
            {
                initialized = _initialized,
                startupRetryEnabled = _platformStartupBlocked,
                savePending = _manualSaveAwaiting,
                inputEnabled = _initialized && !_restoreBlocked && !_transitioning && state != null && (state.Status == RaidRunStatus.Playing || state.Status == RaidRunStatus.Armed),
                transitioning = _transitioning,
                statusCode = state == null ? "Waiting" : state.Status.ToString(),
                actions = state == null ? 0 : state.Actions,
                hits = state == null ? 0 : state.Hits,
                shieldCharges = state == null ? 0 : state.ShieldCharges,
                magnetStepsRemaining = state == null ? 0 : state.MagnetStepsRemaining,
                slowStepsRemaining = state == null ? 0 : state.SlowStepsRemaining,
                tailCount = state == null || state.CollectedTailIds == null ? 0 : state.CollectedTailIds.Length,
                tailTarget = _arena == null || _arena.TailFragments == null ? 0 : _arena.TailFragments.Length,
                playerX = state == null ? 0 : state.PlayerPosition.X,
                playerY = state == null ? 0 : state.PlayerPosition.Y,
                snakeHeadIndex = state == null ? -1 : state.SnakeHeadIndex,
                activeDefinitionId = _arena == null ? string.Empty : _arena.Id,
                selectedDefinitionId = _arena == null ? string.Empty : _arena.Id,
                recordStatus = _recordStatus,
                recordCapsule = _recordCapsule,
                mineCapsule = _hasMine && RecordObservationGuard.IsUsable(true, _mine) ? _mineCapsule : string.Empty,
                clearComparison = _clearComparison,
                clearComparisonDelta = _clearComparisonDelta,
                clearComparisonFingerprint = _clearComparisonFingerprint,
                hasMine = _hasMine && RecordObservationGuard.IsUsable(true, _mine),
                mine = _hasMine && RecordObservationGuard.IsUsable(true, _mine) ? _mine : null,
                hasShared = _hasShared && RecordObservationGuard.IsUsable(true, _shared),
                shared = _hasShared && RecordObservationGuard.IsUsable(true, _shared) ? _shared : null,
                sharedRecordRequestId = _sharedRecordRequestId,
                recordError = _recordError,
                message = _message,
                saveStatus = _saveStatus,
                saveError = _saveError,
                stateFingerprint = state == null || _arena == null ? string.Empty : RaidRules.StateFingerprint(_arena, state)
            };
            string observationJson = JsonUtility.ToJson(observation);
#if UNITY_EDITOR
            _lastObservationJsonForCheck = observationJson;
#endif
            Debug.Log("RAID_STATE_OBSERVATION " + observationJson);
#if UNITY_WEBGL && !UNITY_EDITOR
            NectorialRaidReportState(observationJson);
#endif
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

        private static void ConfigureCameraForArena(RaidArenaDefinition arena)
        {
            if (arena == null) return;
            ConfigureCamera();
            Camera camera = Camera.main;
            if (camera == null) return;
            float halfExtent = Mathf.Max(arena.Width, arena.Height) * 0.5f;
            camera.orthographicSize = Mathf.Max(4.65f, halfExtent + 0.65f);
        }

        private string NextCommandId()
        {
            _nextCommandId = checked(_nextCommandId + 1);
            return _commandPrefix + "-" + _nextCommandId.ToString();
        }

        private static bool TryParseDirection(string input, out GameCommand direction)
        {
            direction = GameCommand.Up;
            if (input == "Up") return true;
            if (input == "Down") { direction = GameCommand.Down; return true; }
            if (input == "Left") { direction = GameCommand.Left; return true; }
            if (input == "Right") { direction = GameCommand.Right; return true; }
            return false;
        }

        private static string Translate(string reason)
        {
            if (reason == "blocked_zero") return "그 방향으로는 움직일 수 없습니다";
            if (reason == "restarted") return "처음 상태로 돌아왔습니다";
            if (reason == "cleared") return "뱀을 격파했습니다";
            if (reason == "failed") return "충돌했습니다. 다시 시작하세요";
            if (reason == "accepted") return "이동했습니다";
            return reason ?? string.Empty;
        }

        private static string Describe(RaidDispatchResult result)
        {
            if (result == null) return string.Empty;
            if (result.State != null && result.State.Status == RaidRunStatus.Cleared) return Translate("cleared");
            if (result.State != null && result.State.Status == RaidRunStatus.Failed) return Translate("failed");
            // Shown beside the "돌진!" quest line and the full charge pips, so it stays short enough to fit whole on a
            // 360px phone. Any collision with the snake (head or body) clears an armed run.
            if (result.State != null && result.State.Status == RaidRunStatus.Armed) return "머리든 몸통이든 부딪히면 잡아요";
            if (HasShieldedFrame(result)) return "보호막으로 충돌을 막았습니다";

            bool shield = false;
            bool magnet = false;
            bool slow = false;
            int directTailCount = 0;
            int magnetTailCount = 0;
            if (result.Events != null)
            {
                for (int index = 0; index < result.Events.Length; index++)
                {
                    RaidEvent item = result.Events[index];
                    if (item == null) continue;
                    if (item.Type == "item_collected")
                    {
                        if (item.Detail != null && item.Detail.StartsWith("Shield:", StringComparison.Ordinal)) shield = true;
                        else if (item.Detail != null && item.Detail.StartsWith("Magnet:", StringComparison.Ordinal)) magnet = true;
                        else if (item.Detail != null && item.Detail.StartsWith("Slow:", StringComparison.Ordinal)) slow = true;
                    }
                    else if (item.Type == "tail_collected") directTailCount++;
                    else if (item.Type == "tail_magnet_collected") magnetTailCount++;
                }
            }

            int tailCount = directTailCount + magnetTailCount;
            string itemName = ItemName(shield, magnet, slow);
            if (!string.IsNullOrEmpty(itemName) && tailCount > 0)
                return itemName + " 획득 · 금빛 조각 " + tailCount.ToString() + "개 수집";
            if (shield && !magnet && !slow && result.State != null) return "보호막 준비 · 충돌 " + result.State.ShieldCharges.ToString() + "회 방어";
            if (magnet && !shield && !slow) return "자석 획득 · 주변 금빛 조각 당기기";
            if (slow && !shield && !magnet) return "모래시계 획득 · 뱀이 잠시 멈춰요";
            if (!string.IsNullOrEmpty(itemName)) return itemName + " 획득";
            if (tailCount > 0)
            {
                if (magnetTailCount > 0 && directTailCount == 0 && tailCount == 1) return "자석으로 금빛 조각을 모았습니다";
                if (tailCount == 1) return "금빛 조각을 모았습니다";
                return "금빛 조각 " + tailCount.ToString() + "개 수집";
            }
            return Translate(result.Reason);
        }

        private static bool HasShieldedFrame(RaidDispatchResult result)
        {
            if (result == null || result.Frames == null) return false;
            for (int index = 0; index < result.Frames.Length; index++)
                if (result.Frames[index] != null && result.Frames[index].Outcome == RaidFrameOutcome.Shielded) return true;
            return false;
        }

        private static string ItemName(bool shield, bool magnet, bool slow)
        {
            if (shield && !magnet && !slow) return "보호막";
            if (magnet && !shield && !slow) return "자석";
            if (slow && !shield && !magnet) return "모래시계";
            if (shield && magnet && slow) return "보호막·자석·모래시계";
            if (shield && magnet) return "보호막·자석";
            if (shield && slow) return "보호막·모래시계";
            if (magnet && slow) return "자석·모래시계";
            return string.Empty;
        }

#if UNITY_EDITOR
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

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void NectorialRaidReportState(string json);
#endif

        [Serializable]
        private sealed class RaidInput { public string kind; public string direction; public string capsule; public string requestId; }
        [Serializable]
        private sealed class RaidObservation
        {
            public bool initialized; public bool startupRetryEnabled; public bool savePending; public bool inputEnabled; public bool transitioning; public string statusCode;
            public int actions; public int hits; public int shieldCharges; public int magnetStepsRemaining; public int slowStepsRemaining;
            public int tailCount; public int tailTarget; public int playerX; public int playerY; public int snakeHeadIndex;
            public string activeDefinitionId; public string selectedDefinitionId; public string recordStatus; public string recordCapsule;
            public string mineCapsule; public string clearComparison; public int clearComparisonDelta; public string clearComparisonFingerprint;
            public bool hasMine; public RecordSummaryObservation mine; public bool hasShared; public RecordSummaryObservation shared; public string sharedRecordRequestId; public string recordError;
            public string message; public string saveStatus; public string saveError; public string stateFingerprint;
        }
    }
}
