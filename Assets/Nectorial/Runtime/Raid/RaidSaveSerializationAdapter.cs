using System;
using Nectorial.SlideEscape.Raid;

namespace Nectorial.SlideEscape.Unity.Raid
{
    public static class RaidSaveSerializationAdapter
    {
        public static bool TryNormalize(RaidSaveEnvelope envelope, out string error)
        {
            error = null;
            if (envelope == null) return true;
            if (envelope.State != null)
            {
                if (envelope.State.CollectedTailIds == null) envelope.State.CollectedTailIds = new string[0];
                if (envelope.State.CollectedItemIds == null) envelope.State.CollectedItemIds = new string[0];
            }
            if (envelope.Replay == null) return true;
            if (envelope.Replay.Moves == null || envelope.Replay.Attempts == null)
            {
                error = "raid_save_attempt_transcript_missing";
                return false;
            }
            if (envelope.Replay.AttemptCount != envelope.Replay.Attempts.Length)
            {
                error = "raid_save_attempt_count_mismatch";
                return false;
            }
            for (int index = 0; index < envelope.Replay.Attempts.Length; index++)
            {
                RaidAttempt attempt = envelope.Replay.Attempts[index];
                if (attempt == null || attempt.Move == null || string.IsNullOrEmpty(attempt.Move.CommandId))
                {
                    error = "raid_save_attempt_invalid";
                    return false;
                }
            }
            return true;
        }
    }
}
