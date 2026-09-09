using System;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Raid;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity.Raid
{
    public sealed class RaidBootstrap : MonoBehaviour
    {
        private const string ProductName = "Few Moves Raid Pilot";
        private const string ArenaResource = "RaidArenas/raid-01";
        private const string SaveKey = "nectorial-raid.save.v1";

        private RaidArenaDefinition _arena;
        private RaidSession _session;
        private RaidFrame[] _lastFrames = new RaidFrame[0];
        private RaidDispatchResult _pendingAction;
        private bool _initialized;
        private bool _restoreBlocked;
        private bool _transitioning;
        private int _nextCommandId;
        private string _message = "초기화 중";
        private string _saveStatus = "idle";
        private string _saveError = string.Empty;

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
        public RaidFrame[] LastActionFrames { get { return RaidRules.CloneFrames(_lastFrames); } }
        public bool Transitioning { get { return _transitioning; } }

        private void Awake()
        {
            InitializeRaid();
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
            if (string.Equals(input, "Restart", StringComparison.Ordinal))
            {
                RestartRaid();
                return;
            }
            if (string.Equals(input, "Save", StringComparison.Ordinal))
            {
                SaveCurrent();
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
            CompleteActionPresentation();
        }

        public bool TryStartMove(GameCommand direction, out RaidDispatchResult result)
        {
            result = null;
            if (!_initialized || _restoreBlocked || _transitioning || _session == null)
            {
                PublishState();
                return false;
            }
            result = _session.Dispatch(new RaidMove { CommandId = NextCommandId(), Direction = direction });
            _lastFrames = RaidRules.CloneFrames(result.Frames);
            if (!result.Accepted)
            {
                _message = Translate(result.Reason);
                PublishState();
                return false;
            }
            _pendingAction = result;
            _transitioning = true;
            _message = "이동 중";
            PublishState();
            return true;
        }

        public void CompleteActionPresentation()
        {
            if (_pendingAction == null) return;
            RaidDispatchResult completed = _pendingAction;
            _pendingAction = null;
            _transitioning = false;
            _message = Describe(completed);
            SaveCurrent();
            PublishState();
        }

        public void RestartRaid()
        {
            if (!_initialized || _session == null) return;
            _pendingAction = null;
            _transitioning = false;
            _lastFrames = new RaidFrame[0];
            RaidDispatchResult restarted = _session.Restart();
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
                _session = RaidSession.Create(_arena);
                _initialized = true;
                RestoreIfPresent();
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

        private void BlockRestore(string error)
        {
            _restoreBlocked = true;
            _saveStatus = "failed";
            _saveError = error;
            _message = "저장된 레이드를 불러오지 못했습니다";
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
            RaidState state = _session == null ? null : _session.State;
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
                message = _message,
                saveStatus = _saveStatus,
                saveError = _saveError,
                stateFingerprint = state == null || _arena == null ? string.Empty : RaidRules.StateFingerprint(_arena, state)
            };
            Debug.Log("RAID_STATE_OBSERVATION " + JsonUtility.ToJson(observation));
        }

        private string NextCommandId()
        {
            _nextCommandId = checked(_nextCommandId + 1);
            return "raid-" + _nextCommandId.ToString();
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

        [Serializable]
        private sealed class RaidObservation
        {
            public bool initialized; public bool inputEnabled; public bool transitioning; public string statusCode;
            public int actions; public int hits; public int shieldCharges; public int magnetStepsRemaining; public int slowStepsRemaining;
            public int tailCount; public int tailTarget; public int playerX; public int playerY; public int snakeHeadIndex;
            public string message; public string saveStatus; public string saveError; public string stateFingerprint;
        }
    }
}
