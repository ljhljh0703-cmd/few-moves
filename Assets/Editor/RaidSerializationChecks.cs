using System;
using System.Reflection;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Raid;
using Nectorial.SlideEscape.Unity.Raid;
using UnityEditor;
using UnityEngine;

namespace Nectorial.Editor
{
    public static class RaidSerializationChecks
    {
        private const string ArenaResource = RaidContent.DefaultArenaResource;
        private const string SaveKey = "nectorial-raid.save.v1.raid-v1." + RaidContent.DefaultArenaId;
        private const string FailedSaveKey = SaveKey + ".restore-failed";
        private const string LegacyV2SaveKey = "nectorial-raid.save.v1";
        private const string DefaultArenaFingerprint = "d28fe769aec762eeea0db782f2d5e2d08b93325c7070fedffa3573d663ca4bd3";
        private const string LegacyV2ArenaFingerprint = "9b1d5ed6c3cdda15d42956570fee57de4c629718a0d45b49f701470c287430da";

        [MenuItem("Few Moves/Raid/Run JSON serialization checks")]
        public static void Run()
        {
            TextAsset asset = Resources.Load<TextAsset>(ArenaResource);
            if (asset == null) throw new InvalidOperationException("Missing raid arena: " + ArenaResource);
            RaidArenaDefinition arena = JsonUtility.FromJson<RaidArenaDefinition>(asset.text);
            string[] errors = RaidRules.ValidateArena(arena);
            if (errors.Length > 0) throw new InvalidOperationException("Raid arena invalid: " + errors[0]);
            CheckDefaultV3Witnesses(arena);
            CheckLegacyV2Witnesses();

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
            bool hasMine;
            bool hasShared;
            string recordEnvelopeError;
            if (!RaidBootstrap.TryDeserializeRecordEnvelopeForCheck("{\"hasMine\":false,\"hasShared\":false,\"mine\":null,\"shared\":null}", out hasMine, out hasShared, out recordEnvelopeError) || hasMine || hasShared)
                throw new InvalidOperationException("Raid empty record observation did not remain absent: " + recordEnvelopeError);
            if (RaidBootstrap.TryDeserializeRecordEnvelopeForCheck("{\"hasMine\":true,\"hasShared\":false,\"mine\":null,\"shared\":null}", out hasMine, out hasShared, out recordEnvelopeError) || recordEnvelopeError != "mine_record_invalid")
                throw new InvalidOperationException("Raid empty mine observation was accepted: " + recordEnvelopeError);
            Debug.Log("RAID_JSON_PROBE case=record-empty-guard state=pass");
            CheckBootstrapRestoreAndRecovery(arena);
            CheckRaidRecordRuntime(arena);
            Debug.Log("RAID_JSON_PROBE_RESULT pass=true");
        }

        private static void CheckDefaultV3Witnesses(RaidArenaDefinition arena)
        {
            if (arena.Id != RaidContent.DefaultArenaId || RaidRules.ArenaFingerprint(arena) != DefaultArenaFingerprint)
                throw new InvalidOperationException("Default Raid arena identity is not v3.");
            if (arena.Width != 16 || arena.Height != 16 || arena.SnakeRing == null || arena.SnakeRing.Length != 28 || arena.SnakeBodyLength != 12)
                throw new InvalidOperationException("Default Raid v3 scale contract did not match.");
            RaidSolverResult shortest = RaidSolver.FindSolution(arena, 200000);
            if (shortest.Status != RaidSolverStatus.Solved || shortest.OptimalActionCount != 11 || Directions(shortest.Moves) != "RLRDRDRURLU")
                throw new InvalidOperationException("Default Raid v3 shortest witness did not match.");
            RaidState allItemsState = Replay(arena, "RLDRURLULDR");
            if (allItemsState.Status != RaidRunStatus.Cleared || allItemsState.Actions != 11 || allItemsState.CollectedTailIds == null || allItemsState.CollectedTailIds.Length != 3 || allItemsState.CollectedItemIds == null || allItemsState.CollectedItemIds.Length != 3)
                throw new InvalidOperationException("Default Raid v3 all-item witness did not clear.");
            RaidState failureState = Replay(arena, "DRULRLUU");
            if (failureState.Status != RaidRunStatus.Failed) throw new InvalidOperationException("Default Raid v3 danger witness did not fail.");
            RaidState initial = RaidRules.CreateInitialState(arena);
            RaidDispatchResult zero = RaidRules.Step(arena, initial, GameCommand.Up);
            if (zero.Accepted || zero.Reason != "blocked_zero" || RaidRules.StateFingerprint(arena, initial) != RaidRules.StateFingerprint(arena, zero.State))
                throw new InvalidOperationException("Default Raid v3 blocked input changed state.");
            Debug.Log("RAID_JSON_PROBE case=default-v3-witnesses state=pass fingerprint=" + DefaultArenaFingerprint);
        }

        private static void CheckLegacyV2Witnesses()
        {
            TextAsset asset = Resources.Load<TextAsset>("RaidArenas/raid-01-v2");
            if (asset == null) throw new InvalidOperationException("Missing legacy Raid v2 arena.");
            RaidArenaDefinition arena = JsonUtility.FromJson<RaidArenaDefinition>(asset.text);
            string[] errors = RaidRules.ValidateArena(arena);
            if (errors.Length > 0) throw new InvalidOperationException("Legacy Raid v2 arena invalid: " + errors[0]);
            if (arena.Id != "raid-01-v2" || RaidRules.ArenaFingerprint(arena) != LegacyV2ArenaFingerprint)
                throw new InvalidOperationException("Legacy Raid v2 identity changed.");
            RaidSolverResult shortest = RaidSolver.FindSolution(arena, 200000);
            if (shortest.Status != RaidSolverStatus.Solved || shortest.OptimalActionCount != 5 || Directions(shortest.Moves) != "RDULR")
                throw new InvalidOperationException("Legacy Raid v2 shortest witness did not match.");
            RaidState noHitState = Replay(arena, "DURDLRU");
            if (noHitState.Status != RaidRunStatus.Cleared || noHitState.Actions != 7 || noHitState.Hits != 0 || noHitState.CollectedItemIds == null || noHitState.CollectedItemIds.Length != 3)
                throw new InvalidOperationException("Legacy Raid v2 no-hit witness did not collect all items.");
            RaidState failureState = Replay(arena, "DRUU");
            if (failureState.Status != RaidRunStatus.Failed) throw new InvalidOperationException("Legacy Raid v2 danger witness did not fail.");
            Debug.Log("RAID_JSON_PROBE case=legacy-v2-witnesses state=pass fingerprint=" + LegacyV2ArenaFingerprint);
        }

        private static RaidState Replay(RaidArenaDefinition arena, string trace)
        {
            RaidState state = RaidRules.CreateInitialState(arena);
            for (int index = 0; index < trace.Length; index++)
            {
                GameCommand direction = trace[index] == 'U' ? GameCommand.Up : trace[index] == 'D' ? GameCommand.Down : trace[index] == 'L' ? GameCommand.Left : GameCommand.Right;
                RaidDispatchResult result = RaidRules.Step(arena, state, direction);
                if (!result.Accepted) throw new InvalidOperationException("Raid witness rejected: " + index.ToString());
                state = result.State;
            }
            return state;
        }

        private static string Directions(RaidMove[] moves)
        {
            var values = new char[moves.Length];
            for (int index = 0; index < moves.Length; index++) values[index] = moves[index].Direction == GameCommand.Up ? 'U' : moves[index].Direction == GameCommand.Down ? 'D' : moves[index].Direction == GameCommand.Left ? 'L' : 'R';
            return new string(values);
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
            PreferenceSnapshot priorLegacyV2Save = CapturePreference(LegacyV2SaveKey);
            GameObject sourceObject = null;
            GameObject restoredObject = null;
            GameObject blockedObject = null;
            GameObject priorCamera = Camera.main == null ? null : Camera.main.gameObject;
            try
            {
                PlayerPrefs.DeleteKey(SaveKey);
                PlayerPrefs.DeleteKey(FailedSaveKey);
                PlayerPrefs.SetString(LegacyV2SaveKey, "legacy-v2-save-sentinel");
                PlayerPrefs.Save();
                RaidSolverResult solution = RaidSolver.FindSolution(arena, 200000);
                if (solution.Status != RaidSolverStatus.Solved || solution.Moves == null || solution.Moves.Length < 2 || solution.Moves[0].Direction == solution.Moves[1].Direction)
                    throw new InvalidOperationException("Raid bootstrap probe requires a distinct first and second v3 move.");
                int armingIndex = FindArmingIndex(arena, solution.Moves);
                if (armingIndex < 1) throw new InvalidOperationException("Raid bootstrap probe could not find an Armed transition.");

                RaidBootstrap source = CreateBootstrap("Raid serialization source", out sourceObject);
                float expectedCameraSize = Mathf.Max(4.65f, Mathf.Max(arena.Width, arena.Height) * 0.5f + 0.65f);
                if (Camera.main == null || Mathf.Abs(Camera.main.orthographicSize - expectedCameraSize) > 0.001f)
                    throw new InvalidOperationException("Raid bootstrap did not fit the camera to the v3 arena.");
                source.HandleCommand("{\"kind\":\"Save\"}");
                if (!PlayerPrefs.HasKey(SaveKey)) throw new InvalidOperationException("Raid bootstrap did not persist a command save.");
                if (!PlayerPrefs.HasKey(LegacyV2SaveKey) || PlayerPrefs.GetString(LegacyV2SaveKey) != "legacy-v2-save-sentinel")
                    throw new InvalidOperationException("Raid bootstrap overwrote a legacy v2 save.");
                if (ReadPrivateString(source, "_message") != "저장했습니다") throw new InvalidOperationException("Raid bootstrap did not report an explicit save success.");
                RaidDispatchResult first;
                if (!source.TryStartMove(solution.Moves[0].Direction, out first) || !first.Accepted || first.Idempotent)
                    throw new InvalidOperationException("Raid bootstrap source move was not accepted.");
                source.CompleteActionPresentation();
                source.RestartRaid();
                if (source.State.Actions != 0 || ReadBoardPlayerLocalPosition(source) != new Vector3(arena.PlayerStart.X, -arena.PlayerStart.Y, 0f))
                    throw new InvalidOperationException("Raid bootstrap restart did not render the initial board state.");
                if (!source.TryStartMove(solution.Moves[0].Direction, out first) || !first.Accepted || first.Idempotent)
                    throw new InvalidOperationException("Raid bootstrap source replay move was not accepted.");
                source.CompleteActionPresentation();
                string sourceFingerprint = RaidRules.StateFingerprint(arena, source.State);
                UnityEngine.Object.DestroyImmediate(sourceObject);
                sourceObject = null;

                RaidBootstrap restored = CreateBootstrap("Raid serialization restored", out restoredObject);
                if (!string.Equals(sourceFingerprint, RaidRules.StateFingerprint(arena, restored.State), StringComparison.Ordinal))
                    throw new InvalidOperationException("Raid bootstrap did not restore the saved state.");
                RaidDispatchResult afterRestore;
                if (!restored.TryStartMove(solution.Moves[1].Direction, out afterRestore) || !afterRestore.Accepted || afterRestore.Idempotent)
                    throw new InvalidOperationException("Raid bootstrap reused a saved command ID after restore.");
                restored.CompleteActionPresentation();
                for (int index = 2; index < armingIndex; index++)
                {
                    RaidDispatchResult setup;
                    if (!restored.TryStartMove(solution.Moves[index].Direction, out setup) || !setup.Accepted)
                        throw new InvalidOperationException("Raid bootstrap post-restore setup move was not accepted: " + index.ToString());
                    restored.CompleteActionPresentation();
                }
                RaidDispatchResult arming;
                if (!restored.TryStartMove(solution.Moves[armingIndex].Direction, out arming) || !arming.Accepted)
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
                Debug.Log("RAID_JSON_PROBE case=v3-save-key-isolated-from-v2 state=pass");
                Debug.Log("RAID_JSON_PROBE case=v3-camera-fit state=pass size=" + expectedCameraSize.ToString());
            }
            finally
            {
                if (sourceObject != null) UnityEngine.Object.DestroyImmediate(sourceObject);
                if (restoredObject != null) UnityEngine.Object.DestroyImmediate(restoredObject);
                if (blockedObject != null) UnityEngine.Object.DestroyImmediate(blockedObject);
                RestorePreference(SaveKey, priorSave);
                RestorePreference(FailedSaveKey, priorFailedSave);
                RestorePreference(LegacyV2SaveKey, priorLegacyV2Save);
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

        private static int FindArmingIndex(RaidArenaDefinition arena, RaidMove[] moves)
        {
            RaidSession session = RaidSession.Create(arena);
            for (int index = 0; index < moves.Length; index++)
            {
                RaidDispatchResult result = session.Dispatch(moves[index]);
                if (!result.Accepted) return -1;
                if (result.State.Status == RaidRunStatus.Armed) return index;
            }
            return -1;
        }

        private static void CheckRaidRecordRuntime(RaidArenaDefinition arena)
        {
            PreferenceSnapshot priorSave = CapturePreference(SaveKey);
            PreferenceSnapshot priorFailedSave = CapturePreference(FailedSaveKey);
            string bestKey = "nectorial.record.best.v1.raid-v1." + arena.Id + "." + RaidRules.ArenaFingerprint(arena);
            PreferenceSnapshot priorBest = CapturePreference(bestKey);
            string legacyV2BestKey = "nectorial.record.best.v1.raid-v1.raid-01-v2." + LegacyV2ArenaFingerprint;
            PreferenceSnapshot priorLegacyV2Best = CapturePreference(legacyV2BestKey);
            GameObject host = null;
            GameObject reloadedHost = null;
            try
            {
                PlayerPrefs.DeleteKey(SaveKey);
                PlayerPrefs.DeleteKey(FailedSaveKey);
                PlayerPrefs.SetString(legacyV2BestKey, "legacy-v2-best-sentinel");
                PlayerPrefs.Save();
                RaidBootstrap bootstrap = CreateBootstrap("Raid record probe", out host);
                if (!PlayerPrefs.HasKey(legacyV2BestKey) || PlayerPrefs.GetString(legacyV2BestKey) != "legacy-v2-best-sentinel")
                    throw new InvalidOperationException("Raid v3 record load overwrote a legacy v2 best.");
                RaidSolverResult solution = RaidSolver.FindSolution(arena, 200000);
                if (solution.Status != RaidSolverStatus.Solved) throw new InvalidOperationException("Raid record probe solver did not solve.");
                for (int index = 0; index < solution.Moves.Length; index++)
                {
                    RaidDispatchResult result;
                    if (!bootstrap.TryStartMove(solution.Moves[index].Direction, out result) || !result.Accepted) throw new InvalidOperationException("Raid record probe move rejected: " + index.ToString());
                    bootstrap.CompleteActionPresentation();
                }
                if (bootstrap.State.Status != RaidRunStatus.Cleared) throw new InvalidOperationException("Raid record probe did not clear.");
                string beforeChallenge = RaidRules.StateFingerprint(arena, bootstrap.State);
                bootstrap.HandleCommand("{\"kind\":\"GetRecord\"}");
                if (!ReadPrivate<bool>(bootstrap, "_hasMine")) throw new InvalidOperationException("Raid runtime did not create mine record.");
                string capsule = ReadPrivateString(bootstrap, "_recordCapsule");
                if (string.IsNullOrEmpty(capsule)) throw new InvalidOperationException("Raid runtime did not encode record capsule.");
                string bestBeforeShared = ReadPrivateString(bootstrap, "_mineCapsule");
                bootstrap.HandleCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + capsule + "\",\"requestId\":\"shared-a\"}");
                if (!ReadPrivate<bool>(bootstrap, "_hasShared")) throw new InvalidOperationException("Raid runtime did not validate shared record.");
                if (ReadPrivateString(bootstrap, "_mineCapsule") != bestBeforeShared) throw new InvalidOperationException("Raid shared import changed the local best record.");
                string delayedAObservation = ReadPrivateString(bootstrap, "_lastObservationJsonForCheck");
                bootstrap.HandleCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + capsule + "\",\"requestId\":\"shared-b\"}");
                AssertSharedRecordRequestId(delayedAObservation, "shared-a", "Raid delayed A observation lost its request id.");
                AssertSharedRecordRequestId(ReadPrivateString(bootstrap, "_lastObservationJsonForCheck"), "shared-b", "Raid B success observation did not carry B request id.");
                bootstrap.HandleCommand("{\"kind\":\"Challenge\"}");
                if (!string.Equals(beforeChallenge, RaidRules.StateFingerprint(arena, bootstrap.State), StringComparison.Ordinal)) throw new InvalidOperationException("Raid challenge mutated the active save state.");
                AssertSharedRecordRequestId(ReadPrivateString(bootstrap, "_lastObservationJsonForCheck"), "shared-b", "Raid subsequent observation did not preserve B request id.");
                bootstrap.HandleCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"fm1.invalid\",\"requestId\":\"shared-b\"}");
                AssertSharedRecordRequestId(ReadPrivateString(bootstrap, "_lastObservationJsonForCheck"), "shared-b", "Raid B failure observation did not carry B request id.");
                bootstrap.HandleCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + capsule + "\"}");
                if (!ReadPrivate<bool>(bootstrap, "_hasShared")) throw new InvalidOperationException("Raid legacy shared record input no longer validates.");
                AssertSharedRecordRequestId(ReadPrivateString(bootstrap, "_lastObservationJsonForCheck"), string.Empty, "Raid legacy shared record input did not expose an empty request id.");
                bootstrap.HandleCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + capsule + "\",\"requestId\":\"" + new string('x', 97) + "\"}");
                AssertSharedRecordRequestId(ReadPrivateString(bootstrap, "_lastObservationJsonForCheck"), string.Empty, "Raid oversized shared record request id was not bounded.");
                bootstrap.HandleCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"fm1.invalid\"}");
                if (ReadPrivate<bool>(bootstrap, "_hasShared")) throw new InvalidOperationException("Raid runtime retained invalid shared record as valid.");
                UnityEngine.Object.DestroyImmediate(host);
                host = null;
                RaidBootstrap reloaded = CreateBootstrap("Raid record reload probe", out reloadedHost);
                if (!ReadPrivate<bool>(reloaded, "_hasMine") || ReadPrivateString(reloaded, "_mineCapsule") != bestBeforeShared)
                    throw new InvalidOperationException("Raid runtime did not revalidate the local best record after reload.");
                Debug.Log("RAID_JSON_PROBE case=record-mine-shared-challenge-and-request-correlation state=pass");
                Debug.Log("RAID_JSON_PROBE case=v3-best-key-isolated-from-v2 state=pass");
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                if (reloadedHost != null) UnityEngine.Object.DestroyImmediate(reloadedHost);
                RestorePreference(SaveKey, priorSave);
                RestorePreference(FailedSaveKey, priorFailedSave);
                RestorePreference(bestKey, priorBest);
                RestorePreference(legacyV2BestKey, priorLegacyV2Best);
            }
        }

        private static PreferenceSnapshot CapturePreference(string key)
        {
            return new PreferenceSnapshot { Exists = PlayerPrefs.HasKey(key), Value = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null };
        }

        private static string ReadPrivateString(object target, string fieldName)
        {
            return ReadPrivate<string>(target, fieldName);
        }

        private static void AssertSharedRecordRequestId(string json, string expected, string failure)
        {
            SharedRecordRequestObservation observation = JsonUtility.FromJson<SharedRecordRequestObservation>(json);
            if (observation == null || !string.Equals(observation.sharedRecordRequestId, expected, StringComparison.Ordinal)) throw new InvalidOperationException(failure);
        }

        private static T ReadPrivate<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Raid bootstrap field was unavailable: " + fieldName);
            return (T)field.GetValue(target);
        }

        private static Vector3 ReadBoardPlayerLocalPosition(RaidBootstrap bootstrap)
        {
            FieldInfo boardField = typeof(RaidBootstrap).GetField("_board", BindingFlags.Instance | BindingFlags.NonPublic);
            object board = boardField == null ? null : boardField.GetValue(bootstrap);
            if (board == null) throw new InvalidOperationException("Raid board was unavailable.");
            FieldInfo rootField = board.GetType().GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic);
            Transform root = rootField == null ? null : rootField.GetValue(board) as Transform;
            Transform player = root == null ? null : root.Find("Player");
            if (player == null) throw new InvalidOperationException("Raid board player was unavailable.");
            return player.localPosition;
        }

        private static void RestorePreference(string key, PreferenceSnapshot snapshot)
        {
            if (snapshot.Exists) PlayerPrefs.SetString(key, snapshot.Value);
            else PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }

        [Serializable]
        private sealed class SharedRecordRequestObservation { public string sharedRecordRequestId; }

        private sealed class PreferenceSnapshot { public bool Exists; public string Value; }
    }
}
