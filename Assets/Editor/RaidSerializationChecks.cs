using System;
using System.Reflection;
using Nectorial.SlideEscape.Raid;
using Nectorial.SlideEscape.Unity.Raid;
using UnityEditor;
using UnityEngine;

namespace Nectorial.Editor
{
    public static class RaidSerializationChecks
    {
        private const string ArenaResource = "RaidArenas/raid-01";
        private const string SaveKey = "nectorial-raid.save.v1";
        private const string FailedSaveKey = "nectorial-raid.save.v1.restore-failed";

        [MenuItem("Few Moves/Raid/Run JSON serialization checks")]
        public static void Run()
        {
            TextAsset asset = Resources.Load<TextAsset>(ArenaResource);
            if (asset == null) throw new InvalidOperationException("Missing raid arena: " + ArenaResource);
            RaidArenaDefinition arena = JsonUtility.FromJson<RaidArenaDefinition>(asset.text);
            string[] errors = RaidRules.ValidateArena(arena);
            if (errors.Length > 0) throw new InvalidOperationException("Raid arena invalid: " + errors[0]);

            RaidSession fresh = RaidSession.Create(arena);
            CheckRoundTrip(arena, "fresh", fresh);

            RaidSolverResult solution = RaidSolver.FindSolution(arena, 200000);
            if (solution.Status != RaidSolverStatus.Solved) throw new InvalidOperationException("Raid probe solver failed: " + solution.Status);
            RaidSession cleared = RaidSession.Create(arena);
            for (int index = 0; index < solution.Moves.Length; index++)
            {
                RaidDispatchResult result = cleared.Dispatch(solution.Moves[index]);
                if (!result.Accepted) throw new InvalidOperationException("Raid probe move rejected: " + index.ToString());
            }
            CheckRoundTrip(arena, "cleared", cleared);

            RaidSaveEnvelope canonical = RaidSaveCodec.Capture(RaidSession.Create(arena));
            canonical = JsonUtility.FromJson<RaidSaveEnvelope>(JsonUtility.ToJson(canonical));
            canonical.State.CollectedTailIds = null;
            canonical.State.CollectedItemIds = null;
            string canonicalError;
            if (!RaidSaveSerializationAdapter.TryNormalize(canonical, out canonicalError) || canonical.State.CollectedTailIds == null || canonical.State.CollectedItemIds == null || canonical.Replay.Moves == null || canonical.Replay.Attempts == null || canonical.Replay.Moves.Length != 0 || canonical.Replay.Attempts.Length != 0)
            {
                throw new InvalidOperationException("Raid canonical empty normalization failed: " + canonicalError);
            }
            Debug.Log("RAID_JSON_PROBE case=canonical-empty state=pass");

            RaidSaveEnvelope missingTranscript = RaidSaveCodec.Capture(RaidSession.Create(arena));
            missingTranscript.Replay.Attempts = null;
            string missingTranscriptError;
            if (RaidSaveSerializationAdapter.TryNormalize(missingTranscript, out missingTranscriptError) || missingTranscriptError != "raid_save_attempt_transcript_missing")
                throw new InvalidOperationException("Raid missing attempt transcript was accepted: " + missingTranscriptError);
            Debug.Log("RAID_JSON_PROBE case=missing-attempt-transcript state=rejected error=" + missingTranscriptError);

            RaidSaveEnvelope malformed = RaidSaveCodec.Capture(RaidSession.Create(arena));
            malformed.Replay.Attempts = new[] { new RaidAttempt { Move = null } };
            malformed.Replay.AttemptCount = 1;
            string malformedError;
            if (RaidSaveSerializationAdapter.TryNormalize(malformed, out malformedError)) throw new InvalidOperationException("Raid malformed attempt was accepted.");
            Debug.Log("RAID_JSON_PROBE case=malformed-attempt state=rejected error=" + malformedError);
            CheckBootstrapRestoreAndRecovery(arena);
            Debug.Log("RAID_JSON_PROBE_RESULT pass=true");
        }

        private static void CheckRoundTrip(RaidArenaDefinition arena, string label, RaidSession source)
        {
            RaidSaveEnvelope captured = RaidSaveCodec.Capture(source);
            string json = JsonUtility.ToJson(captured);
            RaidSaveEnvelope decoded = JsonUtility.FromJson<RaidSaveEnvelope>(json);
            string normalizeError;
            if (!RaidSaveSerializationAdapter.TryNormalize(decoded, out normalizeError)) throw new InvalidOperationException("Raid JSON normalization failed for " + label + ": " + normalizeError);
            RaidSession restored;
            string restoreError;
            if (!RaidSaveCodec.TryRestore(arena, decoded, out restored, out restoreError)) throw new InvalidOperationException("Raid JSON restore failed for " + label + ": " + restoreError);
            if (!RaidRules.StatesEqual(source.State, restored.State) || !string.Equals(RaidRules.StateFingerprint(arena, source.State), RaidRules.StateFingerprint(arena, restored.State), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Raid JSON state changed for " + label);
            }
            Debug.Log("RAID_JSON_PROBE case=" + label + " state=pass fingerprint=" + RaidRules.StateFingerprint(arena, restored.State));
        }

        private static void CheckBootstrapRestoreAndRecovery(RaidArenaDefinition arena)
        {
            PreferenceSnapshot priorSave = CapturePreference(SaveKey);
            PreferenceSnapshot priorFailedSave = CapturePreference(FailedSaveKey);
            GameObject sourceObject = null;
            GameObject restoredObject = null;
            GameObject blockedObject = null;
            GameObject priorCamera = Camera.main == null ? null : Camera.main.gameObject;
            try
            {
                PlayerPrefs.DeleteKey(SaveKey);
                PlayerPrefs.DeleteKey(FailedSaveKey);
                PlayerPrefs.Save();

                RaidBootstrap source = CreateBootstrap("Raid serialization source", out sourceObject);
                source.HandleCommand("{\"kind\":\"Save\"}");
                if (!PlayerPrefs.HasKey(SaveKey)) throw new InvalidOperationException("Raid bootstrap did not persist a command save.");
                if (ReadPrivateString(source, "_message") != "저장했습니다") throw new InvalidOperationException("Raid bootstrap did not report an explicit save success.");
                RaidDispatchResult first;
                if (!source.TryStartMove(Nectorial.SlideEscape.GameCommand.Right, out first) || !first.Accepted || first.Idempotent)
                    throw new InvalidOperationException("Raid bootstrap source move was not accepted.");
                source.CompleteActionPresentation();
                string sourceFingerprint = RaidRules.StateFingerprint(arena, source.State);
                UnityEngine.Object.DestroyImmediate(sourceObject);
                sourceObject = null;

                RaidBootstrap restored = CreateBootstrap("Raid serialization restored", out restoredObject);
                if (!string.Equals(sourceFingerprint, RaidRules.StateFingerprint(arena, restored.State), StringComparison.Ordinal))
                    throw new InvalidOperationException("Raid bootstrap did not restore the saved state.");
                RaidDispatchResult afterRestore;
                if (!restored.TryStartMove(Nectorial.SlideEscape.GameCommand.Left, out afterRestore) || !afterRestore.Accepted || afterRestore.Idempotent)
                    throw new InvalidOperationException("Raid bootstrap reused a saved command ID after restore.");
                restored.CompleteActionPresentation();
                RaidDispatchResult down;
                if (!restored.TryStartMove(Nectorial.SlideEscape.GameCommand.Down, out down) || !down.Accepted)
                    throw new InvalidOperationException("Raid bootstrap post-restore setup move was not accepted.");
                restored.CompleteActionPresentation();
                RaidDispatchResult arming;
                if (!restored.TryStartMove(Nectorial.SlideEscape.GameCommand.Right, out arming) || !arming.Accepted)
                    throw new InvalidOperationException("Raid bootstrap arming move was not accepted.");
                string transitionFingerprint = RaidRules.StateFingerprint(arena, restored.State);
                restored.HandleCommand("{\"kind\":\"Restart\"}");
                restored.HandleCommand("{\"kind\":\"Save\"}");
                if (!restored.Transitioning || !string.Equals(transitionFingerprint, RaidRules.StateFingerprint(arena, restored.State), StringComparison.Ordinal))
                    throw new InvalidOperationException("Raid bootstrap accepted a save or restart while transitioning.");
                if (!restored.Transitioning || restored.State.Status != RaidRunStatus.Armed || restored.DisplayState.Status != RaidRunStatus.Playing)
                    throw new InvalidOperationException("Raid bootstrap exposed the armed state before the action presentation completed.");
                restored.CompleteActionPresentation();
                if (restored.DisplayState.Status != RaidRunStatus.Armed)
                    throw new InvalidOperationException("Raid bootstrap did not publish armed state after action completion.");
                UnityEngine.Object.DestroyImmediate(restoredObject);
                restoredObject = null;

                RaidSession corruptSource = RaidSession.Create(arena);
                corruptSource.Dispatch(new RaidMove { CommandId = "rejected-ledger-id", Direction = Nectorial.SlideEscape.GameCommand.Up });
                RaidSaveEnvelope corrupt = RaidSaveCodec.Capture(corruptSource);
                corrupt.Replay.Attempts = null;
                string corruptJson = JsonUtility.ToJson(corrupt);
                PlayerPrefs.SetString(SaveKey, corruptJson);
                PlayerPrefs.Save();
                RaidBootstrap blocked = CreateBootstrap("Raid serialization blocked", out blockedObject);
                RaidDispatchResult blockedMove;
                if (blocked.TryStartMove(Nectorial.SlideEscape.GameCommand.Right, out blockedMove))
                    throw new InvalidOperationException("Raid bootstrap accepted input after a failed restore.");
                blocked.HandleCommand("{\"kind\":\"Save\"}");
                if (ReadPrivateString(blocked, "_message") != "저장하지 못했습니다") throw new InvalidOperationException("Raid bootstrap did not report an explicit save failure.");
                blocked.RestartRaid();
                if (!PlayerPrefs.HasKey(FailedSaveKey) || !string.Equals(PlayerPrefs.GetString(FailedSaveKey), corruptJson, StringComparison.Ordinal))
                    throw new InvalidOperationException("Raid bootstrap did not preserve the failed save before restart.");
                RaidSaveEnvelope restartedEnvelope = JsonUtility.FromJson<RaidSaveEnvelope>(PlayerPrefs.GetString(SaveKey));
                string normalizeError;
                if (!RaidSaveSerializationAdapter.TryNormalize(restartedEnvelope, out normalizeError))
                    throw new InvalidOperationException("Raid bootstrap restart wrote an invalid save: " + normalizeError);
                RaidSession restarted;
                string restoreError;
                if (!RaidSaveCodec.TryRestore(arena, restartedEnvelope, out restarted, out restoreError))
                    throw new InvalidOperationException("Raid bootstrap restart save did not restore: " + restoreError);
                Debug.Log("RAID_JSON_PROBE case=bootstrap-restore-new-command-prefix state=pass");
                Debug.Log("RAID_JSON_PROBE case=bootstrap-pre-action-display-state state=pass");
                Debug.Log("RAID_JSON_PROBE case=bootstrap-command-save-and-transition-lock state=pass");
                Debug.Log("RAID_JSON_PROBE case=failed-restore-preserved-before-restart state=pass");
            }
            finally
            {
                if (sourceObject != null) UnityEngine.Object.DestroyImmediate(sourceObject);
                if (restoredObject != null) UnityEngine.Object.DestroyImmediate(restoredObject);
                if (blockedObject != null) UnityEngine.Object.DestroyImmediate(blockedObject);
                RestorePreference(SaveKey, priorSave);
                RestorePreference(FailedSaveKey, priorFailedSave);
                if (priorCamera == null)
                {
                    Camera generatedCamera = Camera.main;
                    if (generatedCamera != null && generatedCamera.gameObject.name == "Main Camera") UnityEngine.Object.DestroyImmediate(generatedCamera.gameObject);
                }
            }
        }

        private static RaidBootstrap CreateBootstrap(string name, out GameObject host)
        {
            host = new GameObject(name);
            RaidBootstrap bootstrap = host.AddComponent<RaidBootstrap>();
            if (bootstrap.State == null)
            {
                MethodInfo awake = typeof(RaidBootstrap).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
                if (awake == null) throw new InvalidOperationException("Raid bootstrap Awake was unavailable.");
                awake.Invoke(bootstrap, null);
            }
            if (bootstrap.State == null) throw new InvalidOperationException("Raid bootstrap did not initialize.");
            return bootstrap;
        }

        private static PreferenceSnapshot CapturePreference(string key)
        {
            return new PreferenceSnapshot { Exists = PlayerPrefs.HasKey(key), Value = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null };
        }

        private static string ReadPrivateString(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Raid bootstrap message field was unavailable.");
            return field.GetValue(target) as string;
        }

        private static void RestorePreference(string key, PreferenceSnapshot snapshot)
        {
            if (snapshot.Exists) PlayerPrefs.SetString(key, snapshot.Value);
            else PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }

        private sealed class PreferenceSnapshot { public bool Exists; public string Value; }
    }
}
