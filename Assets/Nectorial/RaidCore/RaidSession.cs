using System;
using System.Collections.Generic;
using System.Globalization;
using Nectorial.SlideEscape;

namespace Nectorial.SlideEscape.Raid
{
    public sealed class RaidSession
    {
        private readonly RaidArenaDefinition _arena;
        private readonly RaidState _initialState;
        private RaidState _state;
        private readonly List<RaidMove> _moves = new List<RaidMove>();
        private readonly List<RaidAttempt> _attempts = new List<RaidAttempt>();
        private readonly Dictionary<string, StoredCommand> _commands = new Dictionary<string, StoredCommand>(StringComparer.Ordinal);

        private RaidSession(RaidArenaDefinition arena)
        {
            _arena = RaidRules.CloneArena(arena);
            string[] errors = RaidRules.ValidateArena(_arena);
            if (errors.Length > 0) throw new ArgumentException("Raid arena invalid: " + string.Join(",", errors), "arena");
            _initialState = RaidRules.CreateInitialState(_arena);
            _state = RaidRules.CloneState(_initialState);
        }

        public static RaidSession Create(RaidArenaDefinition arena) { return new RaidSession(arena); }
        public RaidArenaDefinition Arena { get { return RaidRules.CloneArena(_arena); } }
        public RaidState State { get { return RaidRules.CloneState(_state); } }

        public RaidDispatchResult Dispatch(RaidMove move)
        {
            if (move == null) return Rejected("move_missing");
            if (string.IsNullOrEmpty(move.CommandId)) return Rejected("command_id_missing");
            string payload = ((int)move.Direction).ToString(CultureInfo.InvariantCulture);
            StoredCommand existing;
            if (_commands.TryGetValue(move.CommandId, out existing))
            {
                if (!string.Equals(existing.Payload, payload, StringComparison.Ordinal))
                {
                    RaidDispatchResult conflict = Rejected("command_id_payload_conflict");
                    RecordAttempt(move, conflict);
                    return conflict;
                }
                RaidDispatchResult replay = CloneResult(existing.Result);
                replay.Idempotent = true;
                replay.State = RaidRules.CloneState(_state);
                replay.Frames = new RaidFrame[0];
                replay.Events = new RaidEvent[0];
                RecordAttempt(move, replay);
                return CloneResult(replay);
            }

            RaidDispatchResult result;
            try { result = RaidRules.Step(_arena, _state, move.Direction); }
            catch (ArgumentException exception) { result = Rejected("rule_input_invalid:" + exception.ParamName); }
            result.Idempotent = false;
            _commands.Add(move.CommandId, new StoredCommand(payload, result));
            if (result.Accepted) _moves.Add(RaidRules.CloneMove(move));
            _state = RaidRules.CloneState(result.State);
            RecordAttempt(move, result);
            return CloneResult(result);
        }

        public RaidDispatchResult Restart()
        {
            _state = RaidRules.CloneState(_initialState);
            _moves.Clear();
            _attempts.Clear();
            _commands.Clear();
            return new RaidDispatchResult
            {
                Accepted = true,
                Idempotent = false,
                Reason = "restarted",
                State = RaidRules.CloneState(_state),
                Frames = new RaidFrame[0],
                Events = new[] { new RaidEvent { Type = "raid_restarted", Detail = "initial_state" } },
                ActionCompleted = true,
                ActionCancelled = false
            };
        }

        public RaidReplay ExportReplay()
        {
            RaidAttempt[] attempts = CloneAttempts(_attempts.ToArray());
            return new RaidReplay { Moves = RaidRules.CloneMoves(_moves.ToArray()), Attempts = attempts, AttemptCount = attempts.Length };
        }

        private RaidDispatchResult Rejected(string reason)
        {
            return new RaidDispatchResult
            {
                Accepted = false,
                Idempotent = false,
                Reason = reason,
                State = RaidRules.CloneState(_state),
                Frames = new RaidFrame[0],
                Events = new RaidEvent[0],
                ActionCompleted = false,
                ActionCancelled = false
            };
        }

        private void RecordAttempt(RaidMove move, RaidDispatchResult result)
        {
            _attempts.Add(new RaidAttempt
            {
                Move = RaidRules.CloneMove(move),
                Accepted = result.Accepted,
                Idempotent = result.Idempotent,
                Reason = result.Reason,
                StateFingerprint = RaidRules.StateFingerprint(_arena, result.State)
            });
        }

        private static RaidDispatchResult CloneResult(RaidDispatchResult source)
        {
            return new RaidDispatchResult
            {
                Accepted = source.Accepted,
                Idempotent = source.Idempotent,
                Reason = source.Reason,
                State = RaidRules.CloneState(source.State),
                Frames = RaidRules.CloneFrames(source.Frames),
                Events = RaidRules.CloneEvents(source.Events),
                ActionCompleted = source.ActionCompleted,
                ActionCancelled = source.ActionCancelled
            };
        }

        private static RaidAttempt[] CloneAttempts(RaidAttempt[] source)
        {
            if (source == null) return null;
            var clone = new RaidAttempt[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                RaidAttempt item = source[index];
                clone[index] = item == null ? null : new RaidAttempt
                {
                    Move = RaidRules.CloneMove(item.Move),
                    Accepted = item.Accepted,
                    Idempotent = item.Idempotent,
                    Reason = item.Reason,
                    StateFingerprint = item.StateFingerprint
                };
            }
            return clone;
        }

        private sealed class StoredCommand
        {
            public readonly string Payload;
            public readonly RaidDispatchResult Result;
            public StoredCommand(string payload, RaidDispatchResult result) { Payload = payload; Result = CloneResult(result); }
        }
    }
}
