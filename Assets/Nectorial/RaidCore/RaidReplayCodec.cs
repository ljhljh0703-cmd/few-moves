using System;
using System.Collections.Generic;

namespace Nectorial.SlideEscape.Raid
{
    public static class RaidReplayCodec
    {
        public static bool TryReplay(RaidArenaDefinition arena, RaidReplay replay, out RaidSession session, out string error)
        {
            session = null;
            error = null;
            if (replay == null) { error = "replay_missing"; return false; }
            if (RaidRules.ValidateArena(arena).Length > 0) { error = "arena_invalid"; return false; }
            RaidSession next = RaidSession.Create(arena);
            if (replay.Attempts != null)
            {
                if (!ValidateMoveProjection(replay.Moves, replay.Attempts, out error)) return false;
                for (int index = 0; index < replay.Attempts.Length; index++)
                {
                    RaidAttempt attempt = replay.Attempts[index];
                    if (attempt == null || attempt.Move == null) { error = "replay_attempt_missing:" + index.ToString(); return false; }
                    RaidDispatchResult result = next.Dispatch(attempt.Move);
                    if (result.Accepted != attempt.Accepted || result.Idempotent != attempt.Idempotent || !string.Equals(result.Reason, attempt.Reason, StringComparison.Ordinal))
                    {
                        error = "replay_attempt_result_mismatch:" + index.ToString();
                        return false;
                    }
                    if (!string.Equals(RaidRules.StateFingerprint(arena, result.State), attempt.StateFingerprint, StringComparison.Ordinal))
                    {
                        error = "replay_attempt_state_mismatch:" + index.ToString();
                        return false;
                    }
                }
            }
            else if (replay.Moves != null)
            {
                for (int index = 0; index < replay.Moves.Length; index++)
                {
                    RaidMove move = replay.Moves[index];
                    if (move == null) { error = "replay_move_missing:" + index.ToString(); return false; }
                    RaidDispatchResult result = next.Dispatch(move);
                    if (!result.Accepted || result.Idempotent) { error = "replay_move_rejected:" + index.ToString() + ":" + result.Reason; return false; }
                }
            }
            else { error = "replay_attempts_missing"; return false; }
            session = next;
            return true;
        }

        public static RaidReplay Clone(RaidReplay source)
        {
            if (source == null) return new RaidReplay { Moves = new RaidMove[0], Attempts = new RaidAttempt[0] };
            return new RaidReplay { Moves = RaidRules.CloneMoves(source.Moves), Attempts = CloneAttempts(source.Attempts) };
        }

        private static bool ValidateMoveProjection(RaidMove[] moves, RaidAttempt[] attempts, out string error)
        {
            error = null;
            if (moves == null) { error = "accepted_moves_missing"; return false; }
            var expected = new List<RaidMove>();
            for (int index = 0; index < attempts.Length; index++)
            {
                RaidAttempt item = attempts[index];
                if (item != null && item.Accepted && !item.Idempotent) expected.Add(item.Move);
            }
            if (moves.Length != expected.Count) { error = "accepted_moves_projection_mismatch"; return false; }
            for (int index = 0; index < moves.Length; index++)
            {
                if (!SameMove(moves[index], expected[index])) { error = "accepted_moves_projection_mismatch:" + index.ToString(); return false; }
            }
            return true;
        }

        private static bool SameMove(RaidMove left, RaidMove right)
        {
            return left == null || right == null ? left == right : string.Equals(left.CommandId, right.CommandId, StringComparison.Ordinal) && left.Direction == right.Direction;
        }

        private static RaidAttempt[] CloneAttempts(RaidAttempt[] source)
        {
            if (source == null) return null;
            var clone = new RaidAttempt[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                RaidAttempt item = source[index];
                clone[index] = item == null ? null : new RaidAttempt { Move = RaidRules.CloneMove(item.Move), Accepted = item.Accepted, Idempotent = item.Idempotent, Reason = item.Reason, StateFingerprint = item.StateFingerprint };
            }
            return clone;
        }
    }
}
