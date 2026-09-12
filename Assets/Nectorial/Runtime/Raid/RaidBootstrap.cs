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
        private string _sharedCapsule = string.Empty;
        private string _sharedRecordRequestId = string.Empty;
        private string _recordStatus = "idle";
        private string _recordError = string.Empty;
#if UNITY_EDITOR
        private string _lastObservationJsonForCheck = string.Empty;
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
            ConfigureCamera();
            InitializeRaid();
        }

        private void OnDestroy()
        {
            CancelActionPresentation();
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
            if (!_initialized || string.IsNullOrEmpty(json)) return;
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
            float duration = PresentationDuration(_pendingAction.Frames.Length);
            _transitionRoutine = StartCoroutine(CompleteActionAfter(duration));
        }

        private IEnumerator CompleteActionAfter(float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                if (_board != null) _board.AdvanceAction(Mathf.Clamp01(elapsed / duration));
                yield return null;
            }
            if (_board != null) _board.AdvanceAction(1f);
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
            SaveCurrent();
            if (_session.State.Status == RaidRunStatus.Cleared) CaptureCurrentRecord(false);
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
            RaidDispatchResult restarted = _session.Restart();
            _displayState = _session.State;
            if (_board != null) _board.Render(_arena, _session.State);
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
            RecordCapsule capsule;
            string error;
            RecordVerification verification;
            if (!RecordCapsuleCodec.TryDecode(encoded, out capsule, out error) || !RecordCapsuleVerifier.TryVerifyRaid(_arena, capsule, out verification)) return;
            RecordSummaryObservation summary = RecordSummaryObservation.From(verification);
            if (!RecordObservationGuard.IsUsable(true, summary)) return;
            _mine = summary;
            _mineCapsule = encoded;
            _hasMine = true;
        }

        private void ConsiderBest(RecordCapsule identity, string encoded, RecordSummaryObservation candidate)
        {
            if (_hasMine && !LocalRecordBestStore.IsBetter(candidate, _mine)) return;
            if (!LocalRecordBestStore.TryWrite(identity, encoded))
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
            if (reason == "accepted") return "레이드 이동을 적용했습니다";
            return reason ?? string.Empty;
        }

        private static string Describe(RaidDispatchResult result)
        {
            if (result == null) return string.Empty;
            if (result.State != null && result.State.Status == RaidRunStatus.Armed) return "다음 몸통 접촉으로 격파하세요";
            if (result.Frames != null && result.Frames.Length > 0 && result.Frames[result.Frames.Length - 1].Outcome == RaidFrameOutcome.Shielded) return "보호막으로 안전 위치에 복구했습니다";
            return Translate(result.Reason);
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
            public bool initialized; public bool inputEnabled; public bool transitioning; public string statusCode;
            public int actions; public int hits; public int shieldCharges; public int magnetStepsRemaining; public int slowStepsRemaining;
            public int tailCount; public int tailTarget; public int playerX; public int playerY; public int snakeHeadIndex;
            public string activeDefinitionId; public string selectedDefinitionId; public string recordStatus; public string recordCapsule;
            public bool hasMine; public RecordSummaryObservation mine; public bool hasShared; public RecordSummaryObservation shared; public string sharedRecordRequestId; public string recordError;
            public string message; public string saveStatus; public string saveError; public string stateFingerprint;
        }
    }
}
