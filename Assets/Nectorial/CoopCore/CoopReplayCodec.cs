using System;

namespace Nectorial.SlideEscape.Coop
{
    public static class CoopReplayCodec
    {
        public static bool TryReplay(CoopRoomDefinition room, CoopReplay replay, out CoopSession session, out string error)
        {
            session = null;
            error = null;
            if (replay == null || replay.Commands == null)
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
            session = next;
            return true;
        }

        public static CoopReplay Clone(CoopReplay source)
        {
            if (source == null || source.Commands == null) return new CoopReplay { Commands = new CoopCommand[0] };
            var commands = new CoopCommand[source.Commands.Length];
            for (int index = 0; index < commands.Length; index++) commands[index] = CoopRules.CloneCommand(source.Commands[index]);
            return new CoopReplay { Commands = commands };
        }
    }
}
