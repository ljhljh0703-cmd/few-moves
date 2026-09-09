using System;
using Nectorial.SlideEscape.Raid;
using Nectorial.SlideEscape.Unity.Raid;
using UnityEditor;
using UnityEngine;

namespace Nectorial.Editor
{
    public static class RaidSerializationChecks
    {
        private const string ArenaResource = "RaidArenas/raid-01";

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
            canonical.State.CollectedTailIds = null;
            canonical.State.CollectedItemIds = null;
            canonical.Replay.Moves = null;
            canonical.Replay.Attempts = null;
            string canonicalError;
            if (!RaidSaveSerializationAdapter.TryNormalize(canonical, out canonicalError) || canonical.State.CollectedTailIds == null || canonical.State.CollectedItemIds == null || canonical.Replay.Moves == null || canonical.Replay.Attempts == null)
            {
                throw new InvalidOperationException("Raid canonical empty normalization failed: " + canonicalError);
            }
            Debug.Log("RAID_JSON_PROBE case=canonical-empty state=pass");

            RaidSaveEnvelope malformed = RaidSaveCodec.Capture(RaidSession.Create(arena));
            malformed.Replay.Attempts = new[] { new RaidAttempt { Move = null } };
            string malformedError;
            if (RaidSaveSerializationAdapter.TryNormalize(malformed, out malformedError)) throw new InvalidOperationException("Raid malformed attempt was accepted.");
            Debug.Log("RAID_JSON_PROBE case=malformed-attempt state=rejected error=" + malformedError);
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
    }
}
