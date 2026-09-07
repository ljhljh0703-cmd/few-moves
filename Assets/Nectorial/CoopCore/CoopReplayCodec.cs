using System;

namespace Nectorial.SlideEscape.Coop
{
    public static class CoopReplayCodec
    {
        public static bool TryReplay(CoopRoomDefinition room, CoopReplay replay, out CoopSession session, out string error)
        {
            session = null;
            error = null;
            if (replay == null)
            {
                error = "replay_missing";
                return false;
            }
            if (CoopRules.ValidateRoom(room).Length > 0)
            {
                error = "room_invalid";
                return false;
            }

            CoopSession next = CoopSession.Create(room);
            if (replay.Attempts != null)
            {
                for (int index = 0; index < replay.Attempts.Length; index++)
                {
                    CoopAttempt attempt = replay.Attempts[index];
                    if (attempt == null || attempt.Command == null)
                    {
                        error = "replay_attempt_missing:" + index;
                        return false;
                    }
                    CoopDispatchResult result = next.Dispatch(attempt.Command);
                    if (result.Accepted != attempt.Accepted || result.Idempotent != attempt.Idempotent || !string.Equals(result.Reason, attempt.Reason, StringComparison.Ordinal))
                    {
                        error = "replay_attempt_result_mismatch:" + index;
                        return false;
                    }
                    if (!string.Equals(CoopRules.StateFingerprint(room, result.State), attempt.StateFingerprint, StringComparison.Ordinal))
                    {
                        error = "replay_attempt_state_mismatch:" + index;
                        return false;
                    }
                }
            }
            else if (replay.Commands != null)
            {
                for (int index = 0; index < replay.Commands.Length; index++)
                {
                    CoopCommand command = replay.Commands[index];
                    if (command == null)
                    {
                        error = "replay_command_missing:" + index;
                        return false;
                    }
                    CoopDispatchResult result = next.Dispatch(command);
                    if (!result.Accepted || result.Idempotent)
                    {
                        error = "replay_command_rejected:" + index + ":" + result.Reason;
                        return false;
                    }
                }
            }
            else { error = "replay_attempts_missing"; return false; }
            session = next;
            return true;
        }

        public static CoopReplay Clone(CoopReplay source)
        {
            if (source == null) return new CoopReplay { Commands = new CoopCommand[0], Attempts = new CoopAttempt[0] };
            CoopCommand[] commands = source.Commands == null ? new CoopCommand[0] : new CoopCommand[source.Commands.Length];
            for (int index = 0; index < commands.Length; index++) commands[index] = CoopRules.CloneCommand(source.Commands[index]);
            CoopAttempt[] attempts = source.Attempts == null ? null : new CoopAttempt[source.Attempts.Length];
            if (attempts != null)
                for (int index = 0; index < attempts.Length; index++) attempts[index] = CoopRules.CloneAttempt(source.Attempts[index]);
            return new CoopReplay { Commands = commands, Attempts = attempts };
        }
    }
}
