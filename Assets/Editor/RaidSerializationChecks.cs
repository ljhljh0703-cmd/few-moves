using System;
using System.Collections.Generic;
using System.Reflection;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Raid;
using Nectorial.SlideEscape.Record;
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
        private const string DefaultArenaFingerprint = "3215b96aed7249f73ecc11b7e4f8b9f34df6e404dd7a1c4c43e12ddf2cddfe80";
        private const string LegacyV4ArenaResource = "RaidArenas/raid-01-v4";
        private const string LegacyV4ArenaFingerprint = "a0a60b8e1577ece9e270826d2d52eca68ccdf07349c6626ba7f789baf87b8988";
        private const string LegacyV4SaveKey = "nectorial-raid.save.v1.raid-v1.raid-01-v4";
        private const string LegacyV3ArenaResource = "RaidArenas/raid-01-v3";
        private const string LegacyV3ArenaFingerprint = "d28fe769aec762eeea0db782f2d5e2d08b93325c7070fedffa3573d663ca4bd3";
        private const string LegacyV3SaveKey = "nectorial-raid.save.v1.raid-v1.raid-01-v3";
        private const string LegacyV2ArenaFingerprint = "9b1d5ed6c3cdda15d42956570fee57de4c629718a0d45b49f701470c287430da";

        [MenuItem("Few Moves/Raid/Run JSON serialization checks")]
        public static void Run()
        {
            TextAsset asset = Resources.Load<TextAsset>(ArenaResource);
            if (asset == null) throw new InvalidOperationException("Missing raid arena: " + ArenaResource);
            RaidArenaDefinition arena = JsonUtility.FromJson<RaidArenaDefinition>(asset.text);
            string[] errors = RaidRules.ValidateArena(arena);
            if (errors.Length > 0) throw new InvalidOperationException("Raid arena invalid: " + errors[0]);
            CheckDefaultV5Witnesses(arena);
            CheckLegacyV4Witnesses();
            RaidArenaDefinition legacyV3 = CheckLegacyV3Witnesses();
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
            // Shield and magnet feedback remains for legacy v3 saves and records, so it is checked on the v3 fixture.
            CheckActionFeedback(legacyV3);
            CheckBootstrapRestoreAndRecovery(arena);
            CheckRaidRecordRuntime(arena);
            CheckTossBootstrapIntegration(arena);
            CheckTossBootstrapRegressionEdges(arena);
            CheckBoardCellCues(arena);
            Debug.Log("RAID_JSON_PROBE_RESULT pass=true");
        }

        private static void CheckDefaultV5Witnesses(RaidArenaDefinition arena)
        {
            if (arena.Id != RaidContent.DefaultArenaId || arena.Id != "raid-01-v5" || arena.ContentVersion != "raid-01-v5" || RaidRules.ArenaFingerprint(arena) != DefaultArenaFingerprint)
                throw new InvalidOperationException("Default Raid arena identity is not v5.");
            if (arena.Width != 16 || arena.Height != 16 || arena.SnakeRing == null || arena.SnakeRing.Length != 28 || arena.SnakeBodyLength != 12 || arena.TailFragments == null || arena.TailFragments.Length != 3)
                throw new InvalidOperationException("Default Raid v5 scale contract did not match.");
            if (arena.Items == null || arena.Items.Length != 0 || arena.InitialShieldCharges != 0)
                throw new InvalidOperationException("Default Raid v5 must place no helper items and grant no initial shield.");
            RaidState initial = RaidRules.CreateInitialState(arena);
            if (initial.ShieldCharges != 0 || initial.MagnetStepsRemaining != 0 || initial.SlowStepsRemaining != 0) throw new InvalidOperationException("Default Raid v5 starts with a helper effect.");
            RaidSolverResult shortest = RaidSolver.FindSolution(arena, 200000);
            if (shortest.Status != RaidSolverStatus.Solved || shortest.OptimalActionCount != 12 || Directions(shortest.Moves) != "DURDULDRURLU")
                throw new InvalidOperationException("Default Raid v5 shortest witness did not match.");
            RaidState clearState = Replay(arena, "DURDULDRURLU");
            if (clearState.Status != RaidRunStatus.Cleared || clearState.Actions != 12 || clearState.Hits != 0 || clearState.CollectedTailIds == null || clearState.CollectedTailIds.Length != 3 || clearState.CollectedItemIds == null || clearState.CollectedItemIds.Length != 0)
                throw new InvalidOperationException("Default Raid v5 witness did not clear with three collectibles and no items.");
            if (RaidRules.StateFingerprint(arena, clearState) != "b574d509923eb2c3e7192cdb4e48380ef53a13fda384fe6b238e09ce80576370")
                throw new InvalidOperationException("Default Raid v5 witness final state changed.");
            RaidState failureState = Replay(arena, "RD");
            if (failureState.Status != RaidRunStatus.Failed) throw new InvalidOperationException("Default Raid v5 danger witness did not fail.");
            RaidDispatchResult zero = RaidRules.Step(arena, initial, GameCommand.Up);
            if (zero.Accepted || zero.Reason != "blocked_zero" || RaidRules.StateFingerprint(arena, initial) != RaidRules.StateFingerprint(arena, zero.State))
                throw new InvalidOperationException("Default Raid v5 blocked input changed state.");
            Debug.Log("RAID_JSON_PROBE case=default-v5-witnesses state=pass fingerprint=" + DefaultArenaFingerprint);
        }

        private static void CheckLegacyV4Witnesses()
        {
            TextAsset asset = Resources.Load<TextAsset>(LegacyV4ArenaResource);
            if (asset == null) throw new InvalidOperationException("Missing legacy Raid v4 arena.");
            RaidArenaDefinition arena = JsonUtility.FromJson<RaidArenaDefinition>(asset.text);
            string[] errors = RaidRules.ValidateArena(arena);
            if (errors.Length > 0) throw new InvalidOperationException("Legacy Raid v4 arena invalid: " + errors[0]);
            if (arena.Id != "raid-01-v4" || arena.ContentVersion != "raid-01-v4" || RaidRules.ArenaFingerprint(arena) != LegacyV4ArenaFingerprint)
                throw new InvalidOperationException("Legacy Raid v4 identity changed.");
            if (arena.Width != 16 || arena.Height != 16 || arena.SnakeRing == null || arena.SnakeRing.Length != 28 || arena.SnakeBodyLength != 12 || arena.TailFragments == null || arena.TailFragments.Length != 3)
                throw new InvalidOperationException("Legacy Raid v4 scale contract did not match.");
            if (arena.Items == null || arena.Items.Length != 1 || arena.Items[0].Kind != RaidItemKind.Slow || arena.InitialShieldCharges != 0)
                throw new InvalidOperationException("Legacy Raid v4 must place only Slow and grant no initial shield.");
            RaidState initial = RaidRules.CreateInitialState(arena);
            if (initial.ShieldCharges != 0 || initial.MagnetStepsRemaining != 0) throw new InvalidOperationException("Legacy Raid v4 starts with a shield or magnet effect.");
            RaidSolverResult shortest = RaidSolver.FindSolution(arena, 200000);
            if (shortest.Status != RaidSolverStatus.Solved || shortest.OptimalActionCount != 12 || Directions(shortest.Moves) != "DURDULDRURLU")
                throw new InvalidOperationException("Legacy Raid v4 shortest witness did not match.");
            RaidState clearState = Replay(arena, "DURDULDRURLU");
            if (clearState.Status != RaidRunStatus.Cleared || clearState.Actions != 12 || clearState.Hits != 0 || clearState.CollectedTailIds == null || clearState.CollectedTailIds.Length != 3 || clearState.CollectedItemIds == null || clearState.CollectedItemIds.Length != 1)
                throw new InvalidOperationException("Legacy Raid v4 witness did not clear with three tails and the Slow item.");
            if (RaidRules.StateFingerprint(arena, clearState) != "64261835492ef9f7c9ad0b6629e2c3f443dd4b5940660753ba06628ac8eea834")
                throw new InvalidOperationException("Legacy Raid v4 witness final state changed.");
            RaidState failureState = Replay(arena, "RD");
            if (failureState.Status != RaidRunStatus.Failed || failureState.Hits != 1) throw new InvalidOperationException("Legacy Raid v4 danger witness did not fail.");
            RaidDispatchResult zero = RaidRules.Step(arena, initial, GameCommand.Up);
            if (zero.Accepted || zero.Reason != "blocked_zero" || RaidRules.StateFingerprint(arena, initial) != RaidRules.StateFingerprint(arena, zero.State))
                throw new InvalidOperationException("Legacy Raid v4 blocked input changed state.");
            Debug.Log("RAID_JSON_PROBE case=legacy-v4-witnesses state=pass fingerprint=" + LegacyV4ArenaFingerprint);
        }

        private static RaidArenaDefinition CheckLegacyV3Witnesses()
        {
            TextAsset asset = Resources.Load<TextAsset>(LegacyV3ArenaResource);
            if (asset == null) throw new InvalidOperationException("Missing legacy Raid v3 arena.");
            RaidArenaDefinition arena = JsonUtility.FromJson<RaidArenaDefinition>(asset.text);
            string[] errors = RaidRules.ValidateArena(arena);
            if (errors.Length > 0) throw new InvalidOperationException("Legacy Raid v3 arena invalid: " + errors[0]);
            if (arena.Id != "raid-01-v3" || RaidRules.ArenaFingerprint(arena) != LegacyV3ArenaFingerprint)
                throw new InvalidOperationException("Legacy Raid v3 identity changed.");
            if (arena.Width != 16 || arena.Height != 16 || arena.SnakeRing == null || arena.SnakeRing.Length != 28 || arena.SnakeBodyLength != 12)
                throw new InvalidOperationException("Legacy Raid v3 scale contract did not match.");
            RaidSolverResult shortest = RaidSolver.FindSolution(arena, 200000);
            if (shortest.Status != RaidSolverStatus.Solved || shortest.OptimalActionCount != 11 || Directions(shortest.Moves) != "RLRDRDRURLU")
                throw new InvalidOperationException("Legacy Raid v3 shortest witness did not match.");
            RaidState allItemsState = Replay(arena, "RLDRURLULDR");
            if (allItemsState.Status != RaidRunStatus.Cleared || allItemsState.Actions != 11 || allItemsState.CollectedTailIds == null || allItemsState.CollectedTailIds.Length != 3 || allItemsState.CollectedItemIds == null || allItemsState.CollectedItemIds.Length != 3)
                throw new InvalidOperationException("Legacy Raid v3 all-item witness did not clear.");
            RaidState failureState = Replay(arena, "DRULRLUU");
            if (failureState.Status != RaidRunStatus.Failed) throw new InvalidOperationException("Legacy Raid v3 danger witness did not fail.");
            RaidState initial = RaidRules.CreateInitialState(arena);
            RaidDispatchResult zero = RaidRules.Step(arena, initial, GameCommand.Up);
            if (zero.Accepted || zero.Reason != "blocked_zero" || RaidRules.StateFingerprint(arena, initial) != RaidRules.StateFingerprint(arena, zero.State))
                throw new InvalidOperationException("Legacy Raid v3 blocked input changed state.");
            Debug.Log("RAID_JSON_PROBE case=legacy-v3-witnesses state=pass fingerprint=" + LegacyV3ArenaFingerprint);
            return arena;
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

        private static void CheckActionFeedback(RaidArenaDefinition arena)
        {
            RaidState initial = RaidRules.CreateInitialState(arena);
            RaidDispatchResult shieldPickup = RaidRules.Step(arena, initial, GameCommand.Right);
            if (!HasEvent(shieldPickup, "item_collected", "Shield:")) throw new InvalidOperationException("Raid feedback probe did not reach the Shield pickup.");
            if (shieldPickup.State == null || shieldPickup.State.ShieldCharges != 1) throw new InvalidOperationException("Raid feedback probe Shield charge did not remain at its actual cap.");
            AssertFeedback(shieldPickup, "보호막 준비 · 충돌 1회 방어", "Shield pickup feedback");

            RaidDispatchResult ordinaryMove = RaidRules.Step(arena, initial, GameCommand.Down);
            if (ordinaryMove.Events == null || ordinaryMove.Events.Length != 0) throw new InvalidOperationException("Raid feedback probe ordinary move unexpectedly collected an event.");
            AssertFeedback(ordinaryMove, "이동했습니다", "ordinary movement feedback");

            RaidDispatchResult directTail = ReplayResult(arena, "RLDR", "feedback-tail");
            if (!HasEvent(directTail, "tail_collected", null)) throw new InvalidOperationException("Raid feedback probe did not reach a direct tail pickup.");
            AssertFeedback(directTail, "금빛 조각을 모았습니다", "direct tail feedback");

            RaidDispatchResult magnetTail = FindResultWithEvent(arena, "RLDRURLULDR", "tail_magnet_collected", null, "feedback-magnet");
            AssertFeedback(magnetTail, "자석 획득 · 금빛 조각 1개 수집", "magnet pickup and tail feedback");

            RaidDispatchResult slowPickup = FindResultWithEvent(arena, "RLDRURLULDR", "item_collected", "Slow:", "feedback-slow");
            AssertFeedback(slowPickup, "모래시계 획득 · 뱀이 잠시 멈춰요", "Slow pickup feedback");

            RaidDispatchResult shielded = ReplayResult(arena, "RLDRURLU", "feedback-shielded");
            if (!HasFrameOutcome(shielded, RaidFrameOutcome.Shielded)) throw new InvalidOperationException("Raid feedback probe did not reach a shielded collision.");
            AssertFeedback(shielded, "보호막으로 충돌을 막았습니다", "shielded collision feedback");

            RaidDispatchResult armed = ReplayResult(arena, "RLDRURLUL", "feedback-armed");
            if (armed.State.Status != RaidRunStatus.Armed) throw new InvalidOperationException("Raid feedback probe Armed state did not persist.");
            AssertFeedback(armed, "머리든 몸통이든 부딪히면 잡아요", "Armed feedback");

            RaidDispatchResult cleared = ReplayResult(arena, "RLDRURLULDR", "feedback-cleared");
            if (cleared.State.Status != RaidRunStatus.Cleared) throw new InvalidOperationException("Raid feedback probe did not clear.");
            AssertFeedback(cleared, "뱀을 격파했습니다", "cleared feedback");

            RaidDispatchResult failed = ReplayResult(arena, "DRULRLUU", "feedback-failed");
            if (failed.State.Status != RaidRunStatus.Failed) throw new InvalidOperationException("Raid feedback probe did not fail.");
            AssertFeedback(failed, "충돌했습니다. 다시 시작하세요", "failed feedback");
            Debug.Log("RAID_JSON_PROBE case=action-feedback-authoritative-events state=pass");
        }

        private static RaidDispatchResult ReplayResult(RaidArenaDefinition arena, string trace, string commandPrefix)
        {
            RaidSession session = RaidSession.Create(arena);
            RaidDispatchResult result = null;
            for (int index = 0; index < trace.Length; index++)
            {
                result = session.Dispatch(new RaidMove { CommandId = commandPrefix + "-" + index.ToString(), Direction = DirectionFromTrace(trace[index]) });
                if (!result.Accepted) throw new InvalidOperationException("Raid feedback replay rejected: " + commandPrefix + ":" + index.ToString());
            }
            return result;
        }

        private static RaidDispatchResult FindResultWithEvent(RaidArenaDefinition arena, string trace, string type, string detailPrefix, string commandPrefix)
        {
            RaidSession session = RaidSession.Create(arena);
            for (int index = 0; index < trace.Length; index++)
            {
                RaidDispatchResult result = session.Dispatch(new RaidMove { CommandId = commandPrefix + "-" + index.ToString(), Direction = DirectionFromTrace(trace[index]) });
                if (!result.Accepted) throw new InvalidOperationException("Raid feedback event replay rejected: " + commandPrefix + ":" + index.ToString());
                if (HasEvent(result, type, detailPrefix)) return result;
            }
            throw new InvalidOperationException("Raid feedback event was not found: " + type);
        }

        private static GameCommand DirectionFromTrace(char value)
        {
            if (value == 'U') return GameCommand.Up;
            if (value == 'D') return GameCommand.Down;
            if (value == 'L') return GameCommand.Left;
            if (value == 'R') return GameCommand.Right;
            throw new ArgumentException("Raid feedback trace direction invalid: " + value.ToString());
        }

        private static bool HasEvent(RaidDispatchResult result, string type, string detailPrefix)
        {
            if (result == null || result.Events == null) return false;
            for (int index = 0; index < result.Events.Length; index++)
            {
                RaidEvent item = result.Events[index];
                if (item == null || item.Type != type) continue;
                if (detailPrefix == null || (item.Detail != null && item.Detail.StartsWith(detailPrefix, StringComparison.Ordinal))) return true;
            }
            return false;
        }

        private static bool HasFrameOutcome(RaidDispatchResult result, RaidFrameOutcome outcome)
        {
            if (result == null || result.Frames == null) return false;
            for (int index = 0; index < result.Frames.Length; index++) if (result.Frames[index] != null && result.Frames[index].Outcome == outcome) return true;
            return false;
        }

        private static void AssertFeedback(RaidDispatchResult result, string expected, string label)
        {
            string actual = DescribeFeedback(result);
            if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException(label + " expected=" + expected + " actual=" + actual);
        }

        private static string DescribeFeedback(RaidDispatchResult result)
        {
            MethodInfo method = typeof(RaidBootstrap).GetMethod("Describe", BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Raid feedback formatter was unavailable.");
            string feedback = method.Invoke(null, new object[] { result }) as string;
            if (feedback == null) throw new InvalidOperationException("Raid feedback formatter did not return text.");
            return feedback;
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
            PreferenceSnapshot priorLegacyV3Save = CapturePreference(LegacyV3SaveKey);
            PreferenceSnapshot priorLegacyV4Save = CapturePreference(LegacyV4SaveKey);
            GameObject sourceObject = null;
            GameObject restoredObject = null;
            GameObject blockedObject = null;
            GameObject priorCamera = Camera.main == null ? null : Camera.main.gameObject;
            try
            {
                PlayerPrefs.DeleteKey(SaveKey);
                PlayerPrefs.DeleteKey(FailedSaveKey);
                PlayerPrefs.SetString(LegacyV2SaveKey, "legacy-v2-save-sentinel");
                PlayerPrefs.SetString(LegacyV3SaveKey, "legacy-v3-save-sentinel");
                PlayerPrefs.SetString(LegacyV4SaveKey, "legacy-v4-save-sentinel");
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
                if (SaveKey == LegacyV3SaveKey || !PlayerPrefs.HasKey(LegacyV3SaveKey) || PlayerPrefs.GetString(LegacyV3SaveKey) != "legacy-v3-save-sentinel")
                    throw new InvalidOperationException("Raid default bootstrap touched the legacy v3 save.");
                if (SaveKey == LegacyV4SaveKey || !PlayerPrefs.HasKey(LegacyV4SaveKey) || PlayerPrefs.GetString(LegacyV4SaveKey) != "legacy-v4-save-sentinel")
                    throw new InvalidOperationException("Raid default bootstrap touched the legacy v4 save.");
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
                Debug.Log("RAID_JSON_PROBE case=v5-save-key-isolated-from-v4-v3-v2 state=pass");
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
                RestorePreference(LegacyV3SaveKey, priorLegacyV3Save);
                RestorePreference(LegacyV4SaveKey, priorLegacyV4Save);
                if (priorCamera == null)
                {
                    Camera generatedCamera = Camera.main;
                    if (generatedCamera != null && generatedCamera.gameObject.name == "Main Camera") UnityEngine.Object.DestroyImmediate(generatedCamera.gameObject);
                }
            }
        }

        // Cell cues fire once per collected cell and once for a caught/clear cell; Render (restore) adds none; cancel/clear removes them.
        private static void CheckBoardCellCues(RaidArenaDefinition arena)
        {
            PreferenceSnapshot priorSave = CapturePreference(SaveKey);
            PreferenceSnapshot priorFailedSave = CapturePreference(FailedSaveKey);
            GameObject host = null;
            try
            {
                PlayerPrefs.DeleteKey(SaveKey);
                PlayerPrefs.DeleteKey(FailedSaveKey);
                PlayerPrefs.Save();
                RaidBootstrap bootstrap = CreateBootstrap("Raid cue probe", out host);
                object board = typeof(RaidBootstrap).GetField("_board", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bootstrap);
                if (board == null) throw new InvalidOperationException("Raid cue probe has no board.");
                Type boardType = board.GetType();
                Func<string, MethodInfo> method = name => boardType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Func<int> active = () => (int)boardType.GetProperty("ActiveEffectCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(board);

                RaidSession session = RaidSession.Create(arena);
                RaidDispatchResult pickup = null;
                foreach (char step in "DURDULDRURLU")
                {
                    RaidDispatchResult result = session.Dispatch(new RaidMove { CommandId = "cue-" + session.State.Actions.ToString(), Direction = DirectionFromTrace(step) });
                    if (!result.Accepted) throw new InvalidOperationException("Raid cue probe move rejected.");
                    if (HasEvent(result, "tail_collected", null)) { pickup = result; break; }
                }
                if (pickup == null) throw new InvalidOperationException("Raid cue probe found no pickup action.");
                int expected = 0;
                foreach (RaidFrame frame in pickup.Frames) expected += frame.CollectedTailIds == null ? 0 : frame.CollectedTailIds.Length;

                method("ClearEffects").Invoke(board, null);
                method("BeginAction").Invoke(board, new object[] { arena, pickup.Frames });
                method("AdvanceAction").Invoke(board, new object[] { 0.5f });
                method("AdvanceAction").Invoke(board, new object[] { 0.5f });
                method("AdvanceAction").Invoke(board, new object[] { 1f });
                method("AdvanceAction").Invoke(board, new object[] { 1f });
                method("CompleteAction").Invoke(board, new object[] { arena, pickup.State });
                if (active() != expected) throw new InvalidOperationException("Raid pickup cues were not emitted exactly once per collected cell: " + active().ToString() + "/" + expected.ToString());
                method("Render").Invoke(board, new object[] { arena, pickup.State });
                if (active() != expected) throw new InvalidOperationException("Raid render (restore) emitted extra cues.");
                method("ClearEffects").Invoke(board, null);
                if (active() != 0) throw new InvalidOperationException("Raid cues were not cleared.");

                RaidSession danger = RaidSession.Create(arena);
                danger.Dispatch(new RaidMove { CommandId = "cue-danger-0", Direction = DirectionFromTrace('R') });
                RaidDispatchResult caught = danger.Dispatch(new RaidMove { CommandId = "cue-danger-1", Direction = DirectionFromTrace('D') });
                if (caught.State.Status != RaidRunStatus.Failed) throw new InvalidOperationException("Raid cue probe failure trace did not fail.");
                method("BeginAction").Invoke(board, new object[] { arena, caught.Frames });
                method("CompleteAction").Invoke(board, new object[] { arena, caught.State });
                if (active() != 1) throw new InvalidOperationException("Raid caught cell mark was not emitted exactly once.");
                method("CancelAction").Invoke(board, null);
                if (active() != 0) throw new InvalidOperationException("Raid cancel did not clear cues.");

                bootstrap.SetReducedMotion("true");
                if (!(bool)boardType.GetField("_reducedMotion", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(board))
                    throw new InvalidOperationException("Raid reduced motion did not reach the board.");
                Debug.Log("RAID_JSON_PROBE case=board-cell-cues-once-and-cleared state=pass pickups=" + expected.ToString());
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                RestorePreference(SaveKey, priorSave);
                RestorePreference(FailedSaveKey, priorFailedSave);
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
            string legacyV3BestKey = "nectorial.record.best.v1.raid-v1.raid-01-v3." + LegacyV3ArenaFingerprint;
            PreferenceSnapshot priorLegacyV3Best = CapturePreference(legacyV3BestKey);
            string legacyV4BestKey = "nectorial.record.best.v1.raid-v1.raid-01-v4." + LegacyV4ArenaFingerprint;
            PreferenceSnapshot priorLegacyV4Best = CapturePreference(legacyV4BestKey);
            GameObject host = null;
            GameObject reloadedHost = null;
            try
            {
                PlayerPrefs.DeleteKey(SaveKey);
                PlayerPrefs.DeleteKey(FailedSaveKey);
                PlayerPrefs.SetString(legacyV2BestKey, "legacy-v2-best-sentinel");
                PlayerPrefs.SetString(legacyV3BestKey, "legacy-v3-best-sentinel");
                PlayerPrefs.SetString(legacyV4BestKey, "legacy-v4-best-sentinel");
                PlayerPrefs.DeleteKey(bestKey);
                PlayerPrefs.Save();
                RaidBootstrap bootstrap = CreateBootstrap("Raid record probe", out host);
                if (!PlayerPrefs.HasKey(legacyV2BestKey) || PlayerPrefs.GetString(legacyV2BestKey) != "legacy-v2-best-sentinel")
                    throw new InvalidOperationException("Raid default record load overwrote a legacy v2 best.");
                if (bestKey == legacyV3BestKey || !PlayerPrefs.HasKey(legacyV3BestKey) || PlayerPrefs.GetString(legacyV3BestKey) != "legacy-v3-best-sentinel")
                    throw new InvalidOperationException("Raid default record load touched the legacy v3 best.");
                if (bestKey == legacyV4BestKey || !PlayerPrefs.HasKey(legacyV4BestKey) || PlayerPrefs.GetString(legacyV4BestKey) != "legacy-v4-best-sentinel")
                    throw new InvalidOperationException("Raid default record load touched the legacy v4 best.");
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
                // A live first clear publishes the verified best capsule and a "first" comparison bound to this cleared state.
                string clearObservation = ReadPrivateString(bootstrap, "_lastObservationJsonForCheck");
                string liveBest = ReadPrivateString(bootstrap, "_mineCapsule");
                if (string.IsNullOrEmpty(liveBest) || !clearObservation.Contains("\"mineCapsule\":\"" + liveBest + "\""))
                    throw new InvalidOperationException("Raid clear did not publish the best record for sharing.");
                if (!clearObservation.Contains("\"clearComparison\":\"first\"") || !clearObservation.Contains("\"clearComparisonFingerprint\":\"" + beforeChallenge + "\""))
                    throw new InvalidOperationException("Raid first clear did not publish a first-record comparison for this state.");
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
                // After a reload the best stays shareable, and no new-best claim is made for a restored state.
                string reloadObservation = ReadPrivateString(reloaded, "_lastObservationJsonForCheck");
                if (!reloadObservation.Contains("\"mineCapsule\":\"" + bestBeforeShared + "\""))
                    throw new InvalidOperationException("Raid reload did not publish the best record for sharing.");
                if (!reloadObservation.Contains("\"clearComparison\":\"\"") || !reloadObservation.Contains("\"clearComparisonFingerprint\":\"\""))
                    throw new InvalidOperationException("Raid reload fabricated a result comparison.");
                Debug.Log("RAID_JSON_PROBE case=best-record-shareable-live-and-after-reload state=pass");
                Debug.Log("RAID_JSON_PROBE case=record-mine-shared-challenge-and-request-correlation state=pass");
                Debug.Log("RAID_JSON_PROBE case=v5-best-key-isolated-from-v4-v3-v2 state=pass");
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                if (reloadedHost != null) UnityEngine.Object.DestroyImmediate(reloadedHost);
                RestorePreference(SaveKey, priorSave);
                RestorePreference(FailedSaveKey, priorFailedSave);
                RestorePreference(bestKey, priorBest);
                RestorePreference(legacyV2BestKey, priorLegacyV2Best);
                RestorePreference(legacyV3BestKey, priorLegacyV3Best);
                RestorePreference(legacyV4BestKey, priorLegacyV4Best);
            }
        }

        private static void CheckTossBootstrapIntegration(RaidArenaDefinition arena)
        {
            string bestKey = "nectorial.record.best.v1.raid-v1." + arena.Id + "." + RaidRules.ArenaFingerprint(arena);
            string legacyBestKey = "nectorial.record.best.v1.raid-v1.raid-01-v4." + LegacyV4ArenaFingerprint;
            PreferenceSnapshot priorSave = CapturePreference(SaveKey);
            PreferenceSnapshot priorFailed = CapturePreference(FailedSaveKey);
            PreferenceSnapshot priorBest = CapturePreference(bestKey);
            PreferenceSnapshot priorLegacySave = CapturePreference(LegacyV4SaveKey);
            PreferenceSnapshot priorLegacyBest = CapturePreference(legacyBestKey);
            GameObject blockedHost = null;
            GameObject clearHost = null;
            GameObject restoredHost = null;
            GameObject manualHost = null;
            try
            {
                PlayerPrefs.SetString(SaveKey, "toss-probe-local-save-sentinel");
                PlayerPrefs.SetString(FailedSaveKey, "toss-probe-failed-save-sentinel");
                PlayerPrefs.SetString(bestKey, "toss-probe-local-best-sentinel");
                PlayerPrefs.SetString(LegacyV4SaveKey, "toss-probe-v4-save-sentinel");
                PlayerPrefs.SetString(legacyBestKey, "toss-probe-v4-best-sentinel");
                PlayerPrefs.Save();

                List<KeyValuePair<int, string>> blockedWrites;
                RaidBootstrap blocked = CreateTossBootstrap("Raid Toss blocked probe", out blockedHost, out blockedWrites);
                AssertTossBlocked(blocked, "startup_unexpected_web");
                InvokePrivate(blocked, "CompletePlatformStartup", TossFactory("TossStartupResult", "Blocked", "startup_api_error"));
                AssertTossBlocked(blocked, "startup_api_error");
                blocked.HandleCommand("{\"kind\":\"Save\"}");
                blocked.HandleCommand("{\"kind\":\"Restart\"}");
                if (blockedWrites.Count != 0 || blocked.State.Actions != 0) throw new InvalidOperationException("Raid blocked Toss startup accepted input or queued a write.");
                AssertTossPlayerPrefsUntouched(SaveKey, FailedSaveKey, bestKey, legacyBestKey);
                Debug.Log("RAID_JSON_PROBE case=toss-bootstrap-startup-fail-closed-no-web-fallback state=pass");

                RaidSolverResult solution = RaidSolver.FindSolution(arena, 200000);
                if (solution.Status != RaidSolverStatus.Solved || Directions(solution.Moves) != "DURDULDRURLU")
                    throw new InvalidOperationException("Raid Toss probe could not find the fixed v5 clear witness.");
                List<KeyValuePair<int, string>> clearWrites;
                RaidBootstrap clear = CreateTossBootstrap("Raid Toss clear probe", out clearHost, out clearWrites);
                InvokePrivate(clear, "CompletePlatformStartup", TossFactory("TossStartupResult", "NewUser"));
                if (!ReadPrivate<bool>(clear, "_initialized") || clear.State.Actions != 0) throw new InvalidOperationException("Raid Toss new-user startup did not initialize fresh progress.");
                clearHost.SetActive(false); // Keep manual watchdog coroutines out of this synchronous Editor probe.
                for (int index = 0; index < solution.Moves.Length; index++)
                {
                    RaidDispatchResult result;
                    if (!clear.TryStartMove(solution.Moves[index].Direction, out result) || !result.Accepted)
                        throw new InvalidOperationException("Raid Toss clear move was rejected: " + index);
                    clear.CompleteActionPresentation();
                    if (clearWrites.Count != index + 1) throw new InvalidOperationException("Raid Toss clear did not start exactly one checkpoint per action: " + index);
                    InvokePrivate(clear, "CompletePlatformSave", clearWrites[index].Key, TossFactory("TossPlatformOperationResult", "Success"));
                }
                if (clear.State.Status != RaidRunStatus.Cleared || clear.State.Hits != 0 || !ReadPrivate<bool>(clear, "_hasMine"))
                    throw new InvalidOperationException("Raid Toss live witness did not clear and verify its best record.");
                string clearBest;
                string clearProgress;
                ParseTossPayload(clearWrites[clearWrites.Count - 1].Value, arena, out clearBest, out clearProgress);
                if (string.IsNullOrEmpty(clearBest) || clearBest != ReadPrivateString(clear, "_mineCapsule"))
                    throw new InvalidOperationException("Raid Toss final write omitted the new verified best.");
                RaidSaveEnvelope clearEnvelope = JsonUtility.FromJson<RaidSaveEnvelope>(clearProgress);
                RaidSession clearRestored;
                string clearError = null;
                if (clearEnvelope == null || !RaidSaveSerializationAdapter.TryNormalize(clearEnvelope, out clearError)
                    || !RaidSaveCodec.TryRestore(arena, clearEnvelope, out clearRestored, out clearError)
                    || clearRestored.State.Status != RaidRunStatus.Cleared)
                    throw new InvalidOperationException("Raid Toss final write omitted valid cleared progress: " + clearError);
                RecordCapsule decodedBest;
                RecordVerification bestVerification;
                if (!RecordCapsuleCodec.TryDecode(clearBest, out decodedBest, out clearError)
                    || !RecordCapsuleVerifier.TryVerifyRaid(arena, decodedBest, out bestVerification))
                    throw new InvalidOperationException("Raid Toss final write did not contain a verifiable best capsule: " + clearError);
                AssertTossPlayerPrefsUntouched(SaveKey, FailedSaveKey, bestKey, legacyBestKey);
                Debug.Log("RAID_JSON_PROBE case=toss-bootstrap-clear-payload-progress-and-best state=pass");

                RaidSession partial = RaidSession.Create(arena);
                for (int index = 0; index < 2; index++)
                    if (!partial.Dispatch(solution.Moves[index]).Accepted) throw new InvalidOperationException("Raid Toss partial witness was rejected.");
                string partialJson = JsonUtility.ToJson(RaidSaveCodec.Capture(partial));
                string fingerprint = RaidRules.ArenaFingerprint(arena);
                string validPayload = (string)TossFactory("TossPlatformPolicy", "FormatRaidPayload", arena.Id, fingerprint, clearBest, partialJson);
                List<KeyValuePair<int, string>> restoredWrites;
                RaidBootstrap restored = CreateTossBootstrap("Raid Toss restore probe", out restoredHost, out restoredWrites);
                InvokePrivate(restored, "CompletePlatformStartup", TossFactory("TossStartupResult", "ExistingPayload", validPayload));
                if (!ReadPrivate<bool>(restored, "_initialized") || restored.State.Actions != 2
                    || RaidRules.StateFingerprint(arena, restored.State) != RaidRules.StateFingerprint(arena, partial.State)
                    || !ReadPrivate<bool>(restored, "_hasMine") || ReadPrivateString(restored, "_mineCapsule") != clearBest)
                    throw new InvalidOperationException("Raid Toss bootstrap did not adopt valid progress and best together.");
                if (restoredWrites.Count != 0) throw new InvalidOperationException("Raid Toss restore wrote back during startup.");
                AssertTossPlayerPrefsUntouched(SaveKey, FailedSaveKey, bestKey, legacyBestKey);
                Debug.Log("RAID_JSON_PROBE case=toss-bootstrap-valid-progress-best-restore state=pass");

                AssertTossPayloadRejected(restored, "", "raid_toss_payload_empty");
                AssertTossPayloadRejected(restored, (string)TossFactory("TossPlatformPolicy", "FormatRaidPayload", arena.Id, fingerprint, "fm1.invalid", partialJson), "raid_toss_best_invalid");
                AssertTossPayloadRejected(restored, (string)TossFactory("TossPlatformPolicy", "FormatRaidPayload", arena.Id, fingerprint, clearBest, "{}"), null);
                string mismatchedJson = partialJson.Replace("\"ContentVersion\":\"raid-01-v5\"", "\"ContentVersion\":\"raid-01-v4\"");
                if (mismatchedJson == partialJson) throw new InvalidOperationException("Raid Toss progress mismatch fixture did not change content identity.");
                AssertTossPayloadRejected(restored, (string)TossFactory("TossPlatformPolicy", "FormatRaidPayload", arena.Id, fingerprint, clearBest, mismatchedJson), "content_version_mismatch");
                AssertTossPayloadRejected(restored, (string)TossFactory("TossPlatformPolicy", "FormatRaidPayload", "raid-01-v4", fingerprint, clearBest, partialJson), "raid_toss_payload_arena");
                AssertTossPayloadRejected(restored, (string)TossFactory("TossPlatformPolicy", "FormatRaidPayload", arena.Id, new string('a', 64), clearBest, partialJson), "raid_toss_payload_fingerprint");
                if (restoredWrites.Count != 0) throw new InvalidOperationException("Raid Toss rejected payload queued a write.");
                AssertTossPlayerPrefsUntouched(SaveKey, FailedSaveKey, bestKey, legacyBestKey);
                Debug.Log("RAID_JSON_PROBE case=toss-bootstrap-corrupt-mismatched-progress-best-rejected state=pass");

                List<KeyValuePair<int, string>> manualWrites;
                RaidBootstrap manual = CreateTossBootstrap("Raid Toss manual probe", out manualHost, out manualWrites);
                InvokePrivate(manual, "CompletePlatformStartup", TossFactory("TossStartupResult", "NewUser"));
                manualHost.SetActive(false);
                manual.HandleCommand("{\"kind\":\"Save\"}");
                AssertTossSaveState(manual, "pending", true);
                if (manualWrites.Count != 1) throw new InvalidOperationException("Raid Toss manual save did not start its native write.");
                InvokePrivate(manual, "CompletePlatformSave", manualWrites[0].Key, TossFactory("TossPlatformOperationResult", "Success"));
                AssertTossSaveState(manual, "saved", false);
                manual.HandleCommand("{\"kind\":\"Save\"}");
                if (manualWrites.Count != 2) throw new InvalidOperationException("Raid Toss second manual save did not start.");
                InvokePrivate(manual, "CompletePlatformSave", manualWrites[1].Key, TossFactory("TossPlatformOperationResult", "Failure", "storage_api_error"));
                AssertTossSaveState(manual, "failed", false);
                if (ReadPrivateString(manual, "_saveError") != "storage_api_error") throw new InvalidOperationException("Raid Toss native failure was hidden.");
                Debug.Log("RAID_JSON_PROBE case=toss-bootstrap-manual-save-pending-success-failure state=pass");

                manual.HandleCommand("{\"kind\":\"Save\"}");
                if (manualWrites.Count != 3) throw new InvalidOperationException("Raid Toss timeout write did not start.");
                int timedOutId = manualWrites[2].Key;
                InvokePrivate(manual, "TimeoutManualSave", timedOutId);
                AssertTossSaveState(manual, "failed", false);
                if (ReadPrivateString(manual, "_saveError") != "storage_ui_timeout") throw new InvalidOperationException("Raid Toss timeout was not visible.");
                RaidDispatchResult moved;
                if (!manual.TryStartMove(solution.Moves[0].Direction, out moved) || !moved.Accepted)
                    throw new InvalidOperationException("Raid Toss could not advance after UI timeout.");
                manual.CompleteActionPresentation(); // Newer automatic checkpoint waits behind the timed-out native write.
                manual.HandleCommand("{\"kind\":\"Save\"}"); // Newest manual payload replaces that queued checkpoint.
                AssertTossSaveState(manual, "pending", true);
                if (manualWrites.Count != 3) throw new InvalidOperationException("Raid Toss overlapped a timed-out native write.");
                InvokePrivate(manual, "CompletePlatformSave", timedOutId, TossFactory("TossPlatformOperationResult", "Failure", "late_failure"));
                AssertTossSaveState(manual, "pending", true);
                if (manualWrites.Count != 4) throw new InvalidOperationException("Raid Toss did not start the newest queued snapshot after completion.");
                string newestBest;
                string newestProgress;
                ParseTossPayload(manualWrites[3].Value, arena, out newestBest, out newestProgress);
                RaidSaveEnvelope newestEnvelope = JsonUtility.FromJson<RaidSaveEnvelope>(newestProgress);
                if (newestEnvelope == null || newestEnvelope.State == null || newestEnvelope.State.Actions != 1)
                    throw new InvalidOperationException("Raid Toss newest queued write lost the post-timeout move.");
                InvokePrivate(manual, "CompletePlatformSave", manualWrites[3].Key, TossFactory("TossPlatformOperationResult", "Success"));
                AssertTossSaveState(manual, "saved", false);
                InvokePrivate(manual, "CompletePlatformSave", timedOutId, TossFactory("TossPlatformOperationResult", "Failure", "stale_failure"));
                AssertTossSaveState(manual, "saved", false);
                AssertTossPlayerPrefsUntouched(SaveKey, FailedSaveKey, bestKey, legacyBestKey);
                Debug.Log("RAID_JSON_PROBE case=toss-bootstrap-timeout-inflight-latest-snapshot-stale-completion state=pass");
            }
            finally
            {
                if (blockedHost != null) UnityEngine.Object.DestroyImmediate(blockedHost);
                if (clearHost != null) UnityEngine.Object.DestroyImmediate(clearHost);
                if (restoredHost != null) UnityEngine.Object.DestroyImmediate(restoredHost);
                if (manualHost != null) UnityEngine.Object.DestroyImmediate(manualHost);
                RestorePreference(SaveKey, priorSave);
                RestorePreference(FailedSaveKey, priorFailed);
                RestorePreference(bestKey, priorBest);
                RestorePreference(LegacyV4SaveKey, priorLegacySave);
                RestorePreference(legacyBestKey, priorLegacyBest);
            }
        }

        // Both known edge paths run before reporting failure, so one red assertion cannot hide the other.
        private static void CheckTossBootstrapRegressionEdges(RaidArenaDefinition arena)
        {
            string failures = string.Empty;
            try { CheckTossTerminalCheckpoint(arena); }
            catch (Exception exception)
            {
                failures += " terminal-checkpoint=" + exception.GetBaseException().Message;
                Debug.LogError("RAID_JSON_PROBE case=toss-terminal-checkpoint-before-presentation state=fail detail=" + exception.GetBaseException().Message);
            }
            try { CheckTossSupersededManual(arena); }
            catch (Exception exception)
            {
                failures += " superseded-manual=" + exception.GetBaseException().Message;
                Debug.LogError("RAID_JSON_PROBE case=toss-superseded-manual-auto-status state=fail detail=" + exception.GetBaseException().Message);
            }
            if (failures.Length > 0) throw new InvalidOperationException("Raid Toss bootstrap regression probes failed:" + failures);
        }

        private static void CheckTossTerminalCheckpoint(RaidArenaDefinition arena)
        {
            string bestKey = "nectorial.record.best.v1.raid-v1." + arena.Id + "." + RaidRules.ArenaFingerprint(arena);
            PreferenceSnapshot priorSave = CapturePreference(SaveKey);
            PreferenceSnapshot priorBest = CapturePreference(bestKey);
            GameObject host = null;
            GameObject restoredHost = null;
            try
            {
                PlayerPrefs.SetString(SaveKey, "terminal-checkpoint-local-sentinel");
                PlayerPrefs.SetString(bestKey, "terminal-checkpoint-best-sentinel");
                PlayerPrefs.Save();
                List<KeyValuePair<int, string>> writes;
                RaidBootstrap bootstrap = CreateTossBootstrap("Raid Toss terminal checkpoint", out host, out writes);
                InvokePrivate(bootstrap, "CompletePlatformStartup", TossFactory("TossStartupResult", "NewUser"));
                host.SetActive(false);
                RaidSolverResult solution = RaidSolver.FindSolution(arena, 200000);
                if (solution.Status != RaidSolverStatus.Solved || Directions(solution.Moves) != "DURDULDRURLU") throw new InvalidOperationException("fixed clear witness unavailable");
                for (int index = 0; index < solution.Moves.Length - 1; index++)
                {
                    RaidDispatchResult step;
                    if (!bootstrap.TryStartMove(solution.Moves[index].Direction, out step) || !step.Accepted) throw new InvalidOperationException("pre-clear action rejected: " + index);
                    bootstrap.CompleteActionPresentation();
                    if (writes.Count != index + 1) throw new InvalidOperationException("pre-clear write did not start: " + index);
                    InvokePrivate(bootstrap, "CompletePlatformSave", writes[index].Key, TossFactory("TossPlatformOperationResult", "Success"));
                }
                int before = writes.Count;
                RaidDispatchResult finalMove;
                if (!bootstrap.TryStartMove(solution.Moves[solution.Moves.Length - 1].Direction, out finalMove)
                    || !finalMove.Accepted || bootstrap.State.Status != RaidRunStatus.Cleared || !bootstrap.Transitioning)
                    throw new InvalidOperationException("terminal action was not pending presentation");
                InvokePrivate(bootstrap, "RequestPlatformCheckpoint");
                string earlyPayload = writes.Count > before ? writes[writes.Count - 1].Value : null;
                bootstrap.CompleteActionPresentation();
                string liveObservation = ReadPrivateString(bootstrap, "_lastObservationJsonForCheck");
                if (!liveObservation.Contains("\"clearComparison\":\"first\"")) throw new InvalidOperationException("live first achievement was lost after checkpoint");
                for (int index = before; index < writes.Count; index++)
                    InvokePrivate(bootstrap, "CompletePlatformSave", writes[index].Key, TossFactory("TossPlatformOperationResult", "Success"));
                if (writes.Count == before) throw new InvalidOperationException("terminal progress was never saved");
                string checkpointPayload = earlyPayload ?? writes[writes.Count - 1].Value;
                string best;
                string progress;
                ParseTossPayload(checkpointPayload, arena, out best, out progress);
                RaidSaveEnvelope checkpoint = JsonUtility.FromJson<RaidSaveEnvelope>(progress);
                if (checkpoint == null || checkpoint.State == null || checkpoint.State.Status != RaidRunStatus.Cleared)
                    throw new InvalidOperationException("checkpoint did not capture terminal progress");
                List<KeyValuePair<int, string>> restoredWrites;
                RaidBootstrap restored = CreateTossBootstrap("Raid Toss terminal checkpoint restore", out restoredHost, out restoredWrites);
                InvokePrivate(restored, "CompletePlatformStartup", TossFactory("TossStartupResult", "ExistingPayload", checkpointPayload));
                if (!ReadPrivate<bool>(restored, "_initialized") || restored.State.Status != RaidRunStatus.Cleared
                    || !ReadPrivate<bool>(restored, "_hasMine") || string.IsNullOrEmpty(best)
                    || ReadPrivateString(restored, "_mineCapsule") != best)
                    throw new InvalidOperationException("terminal checkpoint restored a clear without its verified best");
                if (PlayerPrefs.GetString(SaveKey) != "terminal-checkpoint-local-sentinel" || PlayerPrefs.GetString(bestKey) != "terminal-checkpoint-best-sentinel")
                    throw new InvalidOperationException("terminal checkpoint changed browser preferences");
                Debug.Log("RAID_JSON_PROBE case=toss-terminal-checkpoint-before-presentation state=pass");
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                if (restoredHost != null) UnityEngine.Object.DestroyImmediate(restoredHost);
                RestorePreference(SaveKey, priorSave);
                RestorePreference(bestKey, priorBest);
            }
        }

        private static void CheckTossSupersededManual(RaidArenaDefinition arena)
        {
            PreferenceSnapshot priorSave = CapturePreference(SaveKey);
            GameObject host = null;
            try
            {
                PlayerPrefs.SetString(SaveKey, "superseded-manual-local-sentinel");
                PlayerPrefs.Save();
                List<KeyValuePair<int, string>> writes;
                RaidBootstrap bootstrap = CreateTossBootstrap("Raid Toss superseded manual", out host, out writes);
                InvokePrivate(bootstrap, "CompletePlatformStartup", TossFactory("TossStartupResult", "NewUser"));
                host.SetActive(false);
                RaidSolverResult solution = RaidSolver.FindSolution(arena, 200000);
                if (solution.Status != RaidSolverStatus.Solved || solution.Moves.Length < 3) throw new InvalidOperationException("three-action witness unavailable");
                RaidDispatchResult step;
                if (!bootstrap.TryStartMove(solution.Moves[0].Direction, out step) || !step.Accepted) throw new InvalidOperationException("auto1 action rejected");
                bootstrap.CompleteActionPresentation(); // auto1 is active.
                if (writes.Count != 1) throw new InvalidOperationException("auto1 did not start");
                bootstrap.HandleCommand("{\"kind\":\"Save\"}"); // manual2 waits.
                AssertTossSaveState(bootstrap, "pending", true);
                if (!bootstrap.TryStartMove(solution.Moves[1].Direction, out step) || !step.Accepted) throw new InvalidOperationException("auto3 action rejected");
                bootstrap.CompleteActionPresentation(); // auto3 supersedes queued manual2.
                if (writes.Count != 1) throw new InvalidOperationException("auto3 overlapped auto1");
                InvokePrivate(bootstrap, "CompletePlatformSave", writes[0].Key, TossFactory("TossPlatformOperationResult", "Success"));
                if (writes.Count != 2) throw new InvalidOperationException("auto3 did not start after auto1");
                InvokePrivate(bootstrap, "CompletePlatformSave", writes[1].Key, TossFactory("TossPlatformOperationResult", "Success"));
                if (ReadPrivate<bool>(bootstrap, "_manualSaveAwaiting")) throw new InvalidOperationException("superseded manual2 still controls UI after auto3");
                if (!bootstrap.TryStartMove(solution.Moves[2].Direction, out step) || !step.Accepted) throw new InvalidOperationException("auto4 action rejected");
                bootstrap.CompleteActionPresentation(); // auto4 has not completed its native write.
                if (writes.Count != 3 || ReadTossObservation(bootstrap).saveStatus == "saved")
                    throw new InvalidOperationException("auto4 reported saved before its native completion");
                InvokePrivate(bootstrap, "CompletePlatformSave", writes[2].Key, TossFactory("TossPlatformOperationResult", "Success"));
                AssertTossSaveState(bootstrap, "saved", false);
                if (PlayerPrefs.GetString(SaveKey) != "superseded-manual-local-sentinel") throw new InvalidOperationException("superseded manual wrote browser preference");
                Debug.Log("RAID_JSON_PROBE case=toss-superseded-manual-auto-status state=pass");
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                RestorePreference(SaveKey, priorSave);
            }
        }

        private static RaidBootstrap CreateTossBootstrap(string name, out GameObject host, out List<KeyValuePair<int, string>> writes)
        {
            host = new GameObject(name);
            host.SetActive(false); // Awake is deferred until the Editor-only Toss seam is installed.
            RaidBootstrap bootstrap = host.AddComponent<RaidBootstrap>();
            writes = new List<KeyValuePair<int, string>>();
            SetPrivate(bootstrap, "_tossWritesForCheck", writes);
            host.SetActive(true);
            // Edit-mode executeMethod can defer Awake even after activation; use the established probe fallback.
            if (bootstrap.State == null) InvokePrivate(bootstrap, "Awake");
            if (bootstrap.State == null) throw new InvalidOperationException("Raid Toss bootstrap did not initialize on activation.");
            return bootstrap;
        }

        private static void AssertTossBlocked(RaidBootstrap bootstrap, string expectedError)
        {
            TossProbeObservation observation = ReadTossObservation(bootstrap);
            if (observation.initialized || observation.inputEnabled || !observation.startupRetryEnabled
                || !ReadPrivate<bool>(bootstrap, "_platformStartupBlocked") || bootstrap.State.Actions != 0
                || !string.Equals(observation.saveError, expectedError, StringComparison.Ordinal))
                throw new InvalidOperationException("Raid Toss bootstrap did not show blocked startup: " + expectedError);
        }

        private static void AssertTossPayloadRejected(RaidBootstrap bootstrap, string payload, string expectedError)
        {
            InvokePrivate(bootstrap, "CompletePlatformStartup", TossFactory("TossStartupResult", "ExistingPayload", payload));
            TossProbeObservation observation = ReadTossObservation(bootstrap);
            if (observation.initialized || observation.inputEnabled || !observation.startupRetryEnabled
                || !ReadPrivate<bool>(bootstrap, "_platformStartupBlocked") || bootstrap.State.Actions != 0
                || ReadPrivate<bool>(bootstrap, "_hasMine") || string.IsNullOrEmpty(observation.saveError)
                || (expectedError != null && !string.Equals(observation.saveError, expectedError, StringComparison.Ordinal)))
                throw new InvalidOperationException("Raid Toss bootstrap adopted invalid payload: " + expectedError + " actual=" + observation.saveError);
            bootstrap.HandleCommand("{\"kind\":\"Restart\"}");
            bootstrap.HandleCommand("{\"kind\":\"Save\"}");
            if (bootstrap.State.Actions != 0) throw new InvalidOperationException("Raid Toss blocked restore accepted a command.");
        }

        private static void AssertTossSaveState(RaidBootstrap bootstrap, string status, bool pending)
        {
            TossProbeObservation observation = ReadTossObservation(bootstrap);
            if (!string.Equals(observation.saveStatus, status, StringComparison.Ordinal) || observation.savePending != pending
                || ReadPrivate<bool>(bootstrap, "_manualSaveAwaiting") != pending)
                throw new InvalidOperationException("Raid Toss save observation mismatch: expected=" + status + " actual=" + observation.saveStatus);
        }

        private static void AssertTossPlayerPrefsUntouched(string saveKey, string failedKey, string bestKey, string legacyBestKey)
        {
            if (PlayerPrefs.GetString(saveKey) != "toss-probe-local-save-sentinel"
                || PlayerPrefs.GetString(failedKey) != "toss-probe-failed-save-sentinel"
                || PlayerPrefs.GetString(bestKey) != "toss-probe-local-best-sentinel"
                || PlayerPrefs.GetString(LegacyV4SaveKey) != "toss-probe-v4-save-sentinel"
                || PlayerPrefs.GetString(legacyBestKey) != "toss-probe-v4-best-sentinel")
                throw new InvalidOperationException("Raid Toss bootstrap read or changed a browser or legacy preference.");
        }

        private static TossProbeObservation ReadTossObservation(RaidBootstrap bootstrap)
        {
            TossProbeObservation observation = JsonUtility.FromJson<TossProbeObservation>(ReadPrivateString(bootstrap, "_lastObservationJsonForCheck"));
            if (observation == null) throw new InvalidOperationException("Raid Toss observation was missing.");
            return observation;
        }

        private static void ParseTossPayload(string payload, RaidArenaDefinition arena, out string best, out string progress)
        {
            object[] arguments = { payload, arena.Id, RaidRules.ArenaFingerprint(arena), null, null, null };
            if (!(bool)TossFactory("TossPlatformPolicy", "TryParseRaidPayload", arguments))
                throw new InvalidOperationException("Raid Toss queued payload did not parse: " + arguments[5]);
            best = (string)arguments[3];
            progress = (string)arguments[4];
        }

        private static object TossFactory(string typeName, string methodName, params object[] arguments)
        {
            Type type = typeof(RaidBootstrap).Assembly.GetType("Nectorial.SlideEscape.Unity." + typeName);
            MethodInfo method = type == null ? null : type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Raid Toss factory was unavailable: " + typeName + "." + methodName);
            return method.Invoke(null, arguments);
        }

        private static object InvokePrivate(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Raid bootstrap method was unavailable: " + methodName);
            return method.Invoke(target, arguments);
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Raid bootstrap field was unavailable: " + fieldName);
            field.SetValue(target, value);
        }

        [Serializable]
        private sealed class TossProbeObservation
        {
            public bool initialized;
            public bool inputEnabled;
            public bool startupRetryEnabled;
            public bool savePending;
            public string saveStatus;
            public string saveError;
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
