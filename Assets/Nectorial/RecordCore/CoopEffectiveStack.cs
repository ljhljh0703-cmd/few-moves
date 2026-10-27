using System;
using System.Collections.Generic;
using System.Text;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;

namespace Nectorial.SlideEscape.Record
{
    public static class CoopEffectiveStack
    {
        public static bool TryCreateCompletedCapsule(CoopRoomDefinition room, CoopReplay replay, CoopState liveState, out RecordCapsule capsule, out RecordVerification verification, out string error)
        {
            capsule = null;
            verification = null;
            error = null;
            if (CoopRules.ValidateRoom(room).Length > 0) { error = "definition_invalid"; return false; }
            if (liveState == null) { error = "record_state_missing"; return false; }
            if (liveState.Status != CoopRunStatus.Cleared) { error = "record_not_cleared"; return false; }
            if (liveState.PendingConsent != null) { error = "record_consent_pending"; return false; }
            if (replay == null || replay.Attempts == null || replay.Commands == null) { error = "record_attempts_missing"; return false; }

            CoopSession transcript;
            string replayError;
            if (!CoopReplayCodec.TryReplay(room, replay, out transcript, out replayError)) { error = "record_effective_stack_invalid:" + replayError; return false; }
            if (!CoopRules.StatesEqual(transcript.State, liveState)) { error = "record_live_transcript_mismatch"; return false; }

            var stack = new List<EffectiveAction>();
            PendingConsent pending = null;
            for (int index = 0; index < replay.Attempts.Length; index++)
            {
                CoopAttempt attempt = replay.Attempts[index];
                if (attempt == null || attempt.Command == null) { error = "record_attempt_missing:" + index.ToString(); return false; }
                if (!attempt.Accepted || attempt.Idempotent) continue;
                CoopCommand command = attempt.Command;
                switch (command.Kind)
                {
                    case CoopCommandKind.Slide:
                        pending = null;
                        stack.Add(EffectiveAction.Slide(command.Direction));
                        break;
                    case CoopCommandKind.Pass:
                        pending = null;
                        stack.Add(EffectiveAction.Pass());
                        break;
                    case CoopCommandKind.RequestUndo:
                        if (pending != null || string.IsNullOrEmpty(command.RequestId)) { error = "record_consent_sequence_invalid:" + index.ToString(); return false; }
                        pending = new PendingConsent(command.RequestId, CoopConsentKind.Undo);
                        break;
                    case CoopCommandKind.RequestRestart:
                        if (pending != null || string.IsNullOrEmpty(command.RequestId)) { error = "record_consent_sequence_invalid:" + index.ToString(); return false; }
                        pending = new PendingConsent(command.RequestId, CoopConsentKind.Restart);
                        break;
                    case CoopCommandKind.ResolveUndo:
                        if (!TryResolve(command, CoopConsentKind.Undo, ref pending, stack, out error)) return false;
                        break;
                    case CoopCommandKind.ResolveRestart:
                        if (!TryResolve(command, CoopConsentKind.Restart, ref pending, stack, out error)) return false;
                        break;
                    case CoopCommandKind.Express:
                        break;
                    default:
                        error = "record_command_kind_invalid:" + index.ToString();
                        return false;
                }
            }
            if (pending != null) { error = "record_consent_pending"; return false; }
            for (int index = 0; index < stack.Count; index++)
            {
                if (stack[index].IsPass) { error = "record_pass_not_representable"; return false; }
            }

            var input = new StringBuilder(stack.Count);
            for (int index = 0; index < stack.Count; index++) input.Append(DirectionCharacter(stack[index].Direction));
            capsule = new RecordCapsule
            {
                SchemaVersion = RecordCapsuleRules.SchemaVersion,
                ModeId = RecordCapsuleRules.CoopModeId,
                DefinitionId = room.Id,
                RulesVersion = room.RulesVersion,
                ContentVersion = room.ContentVersion,
                DefinitionFingerprint = CoopRules.RoomFingerprint(room),
                InputSequence = input.ToString()
            };
            if (!RecordCapsuleVerifier.TryVerifyCoop(room, capsule, out verification)) { error = verification.ErrorCode; capsule = null; return false; }
            if (verification.StatusCode != liveState.Status.ToString() || verification.LogicalActionCount != liveState.LogicalActionCount
                || verification.ActiveActorCode != (int)liveState.ActiveActor || !CoopRules.Same(verification.CirclePosition, liveState.CirclePosition)
                || !CoopRules.Same(verification.DiamondPosition, liveState.DiamondPosition))
            {
                error = "record_replay_mismatch";
                capsule = null;
                verification.Valid = false;
                verification.ErrorCode = error;
                return false;
            }
            return true;
        }

        private static bool TryResolve(CoopCommand command, CoopConsentKind kind, ref PendingConsent pending, List<EffectiveAction> stack, out string error)
        {
            error = null;
            if (pending == null || pending.Kind != kind || !string.Equals(pending.RequestId, command.RequestId, StringComparison.Ordinal))
            {
                error = "record_consent_sequence_invalid";
                return false;
            }
            if (command.Approve)
            {
                if (kind == CoopConsentKind.Undo)
                {
                    if (stack.Count == 0) { error = "record_undo_stack_empty"; return false; }
                    stack.RemoveAt(stack.Count - 1);
                }
                else stack.Clear();
            }
            pending = null;
            return true;
        }

        private static char DirectionCharacter(GameCommand direction)
        {
            if (direction == GameCommand.Up) return 'U';
            if (direction == GameCommand.Down) return 'D';
            if (direction == GameCommand.Left) return 'L';
            if (direction == GameCommand.Right) return 'R';
            throw new ArgumentException("Unsupported record direction.", "direction");
        }

        private sealed class PendingConsent
        {
            public readonly string RequestId;
            public readonly CoopConsentKind Kind;
            public PendingConsent(string requestId, CoopConsentKind kind) { RequestId = requestId; Kind = kind; }
        }

        private sealed class EffectiveAction
        {
            public bool IsPass;
            public GameCommand Direction;
            public static EffectiveAction Slide(GameCommand direction) { return new EffectiveAction { Direction = direction }; }
            public static EffectiveAction Pass() { return new EffectiveAction { IsPass = true }; }
        }
    }
}
