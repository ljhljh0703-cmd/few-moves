using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nectorial.SlideEscape.Coop
{
    public sealed class CoopSession
    {
        private readonly CoopRoomDefinition _room;
        private readonly CoopState _initialState;
        private readonly List<ActionFrame> _actionFrames = new List<ActionFrame>();
        private readonly List<CoopCommand> _acceptedCommands = new List<CoopCommand>();
        private readonly Dictionary<string, StoredCommand> _commandsById = new Dictionary<string, StoredCommand>(StringComparer.Ordinal);
        private CoopState _state;

        private CoopSession(CoopRoomDefinition room)
        {
            _room = room;
            _initialState = CoopRules.CreateInitialState(room);
            _state = CoopRules.CloneState(_initialState);
        }

        public static CoopSession Create(CoopRoomDefinition room)
        {
            return new CoopSession(room);
        }

        public CoopRoomDefinition Room { get { return _room; } }
        public CoopState State { get { return CoopRules.CloneState(_state); } }

        public CoopReplay ExportReplay()
        {
            var commands = new CoopCommand[_acceptedCommands.Count];
            for (int index = 0; index < commands.Length; index++) commands[index] = CoopRules.CloneCommand(_acceptedCommands[index]);
            return new CoopReplay { Commands = commands };
        }

        public CoopDispatchResult Dispatch(CoopCommand command)
        {
            if (command == null) return Rejected("command_missing");
            if (string.IsNullOrEmpty(command.CommandId)) return Rejected("command_id_missing");
            string payload = CommandPayload(command);
            StoredCommand prior;
            if (_commandsById.TryGetValue(command.CommandId, out prior))
            {
                if (!string.Equals(prior.Payload, payload, StringComparison.Ordinal)) return Rejected("command_id_payload_conflict");
                CoopDispatchResult replay = CloneResult(prior.Result);
                replay.Idempotent = true;
                replay.State = CoopRules.CloneState(_state);
                return replay;
            }
            if (command.ExpectedRevision != _state.AuthorityRevision) return Rejected("stale_revision");
            if (!IsActor(command.Seat)) return Rejected("invalid_seat");

            switch (command.Kind)
            {
                case CoopCommandKind.Slide: return DispatchSlide(command, payload);
                case CoopCommandKind.Pass: return DispatchPass(command, payload);
                case CoopCommandKind.RequestUndo: return DispatchConsentRequest(command, payload, CoopConsentKind.Undo);
                case CoopCommandKind.ResolveUndo: return DispatchConsentResolution(command, payload, CoopConsentKind.Undo);
                case CoopCommandKind.RequestRestart: return DispatchConsentRequest(command, payload, CoopConsentKind.Restart);
                case CoopCommandKind.ResolveRestart: return DispatchConsentResolution(command, payload, CoopConsentKind.Restart);
                case CoopCommandKind.Express: return DispatchExpression(command, payload);
                default: return Rejected("invalid_command_kind");
            }
        }

        private CoopDispatchResult DispatchSlide(CoopCommand command, string payload)
        {
            if (!CanTakeAction(command.Seat)) return Rejected("wrong_active_actor");
            GridPoint destination;
            int distance;
            bool stoppedByPartner;
            string reason;
            if (!CoopRules.TrySlide(_room, _state, command.Seat, command.Direction, out destination, out distance, out stoppedByPartner, out reason)) return Rejected(reason);

            CoopState before = CoopRules.CloneState(_state);
            var events = InvalidatePendingForAction(command.Seat);
            before.PendingConsent = null;
            _state = CoopRules.ApplyAction(_room, _state, command.Seat, destination, false);
            _actionFrames.Add(new ActionFrame(before));
            events.Add(new CoopEvent { Type = "slide", Actor = command.Seat, Detail = distance.ToString(CultureInfo.InvariantCulture) });
            if (stoppedByPartner) events.Add(new CoopEvent { Type = "stopped_by_partner", Actor = command.Seat, Detail = CoopRules.Opponent(command.Seat).ToString() });
            if (_state.Status == CoopRunStatus.Cleared) events.Add(new CoopEvent { Type = "coop_cleared", Actor = command.Seat, Detail = "both_goals" });
            return Accepted(command, payload, events.ToArray());
        }

        private CoopDispatchResult DispatchPass(CoopCommand command, string payload)
        {
            if (!CanTakeAction(command.Seat)) return Rejected("wrong_active_actor");
            CoopState before = CoopRules.CloneState(_state);
            var events = InvalidatePendingForAction(command.Seat);
            before.PendingConsent = null;
            _state = CoopRules.ApplyAction(_room, _state, command.Seat, CoopRules.GetPosition(_state, command.Seat), true);
            _actionFrames.Add(new ActionFrame(before));
            events.Add(new CoopEvent { Type = "pass", Actor = command.Seat, Detail = "team_action" });
            return Accepted(command, payload, events.ToArray());
        }

        private CoopDispatchResult DispatchConsentRequest(CoopCommand command, string payload, CoopConsentKind kind)
        {
            if (_state.Status != CoopRunStatus.Playing) return Rejected("terminal_state");
            if (command.Seat != _state.ActiveActor) return Rejected("wrong_active_actor");
            if (_state.PendingConsent != null) return Rejected("consent_pending");
            if (string.IsNullOrEmpty(command.RequestId)) return Rejected("request_id_missing");
            if (kind == CoopConsentKind.Undo && _actionFrames.Count == 0) return Rejected("undo_empty");
            _state = CoopRules.CloneState(_state);
            _state.PendingConsent = new CoopPendingConsent
            {
                RequestId = command.RequestId,
                Kind = kind,
                Requester = command.Seat,
                RequestedAtRevision = checked(_state.AuthorityRevision + 1)
            };
            _state.AuthorityRevision = _state.PendingConsent.RequestedAtRevision;
            return Accepted(command, payload, new[] { new CoopEvent { Type = "consent_requested", Actor = command.Seat, Detail = kind.ToString() } });
        }

        private CoopDispatchResult DispatchConsentResolution(CoopCommand command, string payload, CoopConsentKind kind)
        {
            CoopPendingConsent pending = _state.PendingConsent;
            if (pending == null) return Rejected("consent_missing");
            if (pending.Kind != kind || !string.Equals(pending.RequestId, command.RequestId, StringComparison.Ordinal)) return Rejected("consent_request_mismatch");
            if (pending.Requester == command.Seat) return Rejected("consent_same_seat");
            if (!command.Approve)
            {
                _state = CoopRules.CloneState(_state);
                _state.PendingConsent = null;
                _state.AuthorityRevision = checked(_state.AuthorityRevision + 1);
                return Accepted(command, payload, new[] { new CoopEvent { Type = "consent_rejected", Actor = command.Seat, Detail = kind.ToString() } });
            }

            if (kind == CoopConsentKind.Undo)
            {
                if (_actionFrames.Count == 0) return Rejected("undo_empty");
                ActionFrame frame = _actionFrames[_actionFrames.Count - 1];
                _actionFrames.RemoveAt(_actionFrames.Count - 1);
                long nextRevision = checked(_state.AuthorityRevision + 1);
                _state = CoopRules.WithAuthorityRevision(frame.Before, nextRevision);
                _state.PendingConsent = null;
                return Accepted(command, payload, new[] { new CoopEvent { Type = "undo_accepted", Actor = command.Seat, Detail = "authority_revision_increased" } });
            }

            long restartRevision = checked(_state.AuthorityRevision + 1);
            _state = CoopRules.WithAuthorityRevision(_initialState, restartRevision);
            _state.PendingConsent = null;
            _actionFrames.Clear();
            return Accepted(command, payload, new[] { new CoopEvent { Type = "restart_accepted", Actor = command.Seat, Detail = "authority_revision_increased" } });
        }

        private CoopDispatchResult DispatchExpression(CoopCommand command, string payload)
        {
            if (_state.Status != CoopRunStatus.Playing) return Rejected("terminal_state");
            if (!IsExpression(command.Expression)) return Rejected("expression_not_allowed");
            return Accepted(command, payload, new[] { new CoopEvent { Type = "expression", Actor = command.Seat, Detail = command.Expression.ToString() } });
        }

        private bool CanTakeAction(CoopActor actor)
        {
            return _state.Status == CoopRunStatus.Playing && actor == _state.ActiveActor;
        }

        private List<CoopEvent> InvalidatePendingForAction(CoopActor actor)
        {
            var events = new List<CoopEvent>();
            if (_state.PendingConsent != null)
            {
                _state = CoopRules.CloneState(_state);
                _state.PendingConsent = null;
                events.Add(new CoopEvent { Type = "consent_invalidated", Actor = actor, Detail = "valid_action" });
            }
            return events;
        }

        private CoopDispatchResult Accepted(CoopCommand command, string payload, CoopEvent[] events)
        {
            var result = new CoopDispatchResult
            {
                Accepted = true,
                Idempotent = false,
                Reason = "accepted",
                State = CoopRules.CloneState(_state),
                Events = CoopRules.CloneEvents(events)
            };
            _commandsById.Add(command.CommandId, new StoredCommand(payload, result));
            _acceptedCommands.Add(CoopRules.CloneCommand(command));
            return CloneResult(result);
        }

        private CoopDispatchResult Rejected(string reason)
        {
            return new CoopDispatchResult
            {
                Accepted = false,
                Idempotent = false,
                Reason = reason,
                State = CoopRules.CloneState(_state),
                Events = new CoopEvent[0]
            };
        }

        private static CoopDispatchResult CloneResult(CoopDispatchResult source)
        {
            return new CoopDispatchResult
            {
                Accepted = source.Accepted,
                Idempotent = source.Idempotent,
                Reason = source.Reason,
                State = CoopRules.CloneState(source.State),
                Events = CoopRules.CloneEvents(source.Events)
            };
        }

        private static string CommandPayload(CoopCommand command)
        {
            return string.Join("|", new[]
            {
                command.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
                ((int)command.Seat).ToString(CultureInfo.InvariantCulture),
                ((int)command.Kind).ToString(CultureInfo.InvariantCulture),
                ((int)command.Direction).ToString(CultureInfo.InvariantCulture),
                command.RequestId ?? string.Empty,
                command.Approve ? "1" : "0",
                ((int)command.Expression).ToString(CultureInfo.InvariantCulture)
            });
        }

        private static bool IsActor(CoopActor actor)
        {
            return actor == CoopActor.Circle || actor == CoopActor.Diamond;
        }

        private static bool IsExpression(CoopExpression expression)
        {
            return expression == CoopExpression.Look || expression == CoopExpression.ThumbsUp || expression == CoopExpression.Handshake || expression == CoopExpression.Waiting;
        }

        private sealed class ActionFrame
        {
            public readonly CoopState Before;
            public ActionFrame(CoopState before) { Before = before; }
        }

        private sealed class StoredCommand
        {
            public readonly string Payload;
            public readonly CoopDispatchResult Result;
            public StoredCommand(string payload, CoopDispatchResult result) { Payload = payload; Result = CloneResult(result); }
        }
    }

    public static class CoopCommandFactory
    {
        public static CoopCommand Slide(CoopActor seat, string commandId, long expectedRevision, GameCommand direction)
        {
            return new CoopCommand { Seat = seat, CommandId = commandId, ExpectedRevision = expectedRevision, Kind = CoopCommandKind.Slide, Direction = direction };
        }

        public static CoopCommand Pass(CoopActor seat, string commandId, long expectedRevision)
        {
            return new CoopCommand { Seat = seat, CommandId = commandId, ExpectedRevision = expectedRevision, Kind = CoopCommandKind.Pass };
        }

        public static CoopCommand RequestUndo(CoopActor seat, string commandId, long expectedRevision, string requestId)
        {
            return new CoopCommand { Seat = seat, CommandId = commandId, ExpectedRevision = expectedRevision, Kind = CoopCommandKind.RequestUndo, RequestId = requestId };
        }

        public static CoopCommand ResolveUndo(CoopActor seat, string commandId, long expectedRevision, string requestId, bool approve)
        {
            return new CoopCommand { Seat = seat, CommandId = commandId, ExpectedRevision = expectedRevision, Kind = CoopCommandKind.ResolveUndo, RequestId = requestId, Approve = approve };
        }

        public static CoopCommand RequestRestart(CoopActor seat, string commandId, long expectedRevision, string requestId)
        {
            return new CoopCommand { Seat = seat, CommandId = commandId, ExpectedRevision = expectedRevision, Kind = CoopCommandKind.RequestRestart, RequestId = requestId };
        }

        public static CoopCommand ResolveRestart(CoopActor seat, string commandId, long expectedRevision, string requestId, bool approve)
        {
            return new CoopCommand { Seat = seat, CommandId = commandId, ExpectedRevision = expectedRevision, Kind = CoopCommandKind.ResolveRestart, RequestId = requestId, Approve = approve };
        }

        public static CoopCommand Express(CoopActor seat, string commandId, long expectedRevision, CoopExpression expression)
        {
            return new CoopCommand { Seat = seat, CommandId = commandId, ExpectedRevision = expectedRevision, Kind = CoopCommandKind.Express, Expression = expression };
        }
    }
}
