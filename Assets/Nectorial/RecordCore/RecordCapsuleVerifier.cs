using System;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using Nectorial.SlideEscape.Raid;

namespace Nectorial.SlideEscape.Record
{
    public static class RecordCapsuleVerifier
    {
        public static bool TryVerifyRaid(RaidArenaDefinition arena, RecordCapsule capsule, out RecordVerification verification)
        {
            verification = NewVerification(capsule);
            if (RaidRules.ValidateArena(arena).Length > 0) return Fail(verification, "definition_invalid");
            string fingerprint = RaidRules.ArenaFingerprint(arena);
            if (!TryValidateCapsule(capsule, RecordCapsuleRules.RaidModeId, arena.Id, arena.RulesVersion, arena.ContentVersion, fingerprint, verification)) return false;

            RaidState state = RaidRules.CreateInitialState(arena);
            for (int index = 0; index < capsule.InputSequence.Length; index++)
            {
                if (state.Status == RaidRunStatus.Cleared) return Fail(verification, "input_after_cleared:" + index.ToString());
                if (state.Status == RaidRunStatus.Failed) return Fail(verification, "input_after_terminal:" + index.ToString());
                GameCommand direction;
                if (!TryDirection(capsule.InputSequence[index], out direction)) return Fail(verification, "input_invalid:" + index.ToString());
                RaidDispatchResult result = RaidRules.Step(arena, state, direction);
                if (!result.Accepted) return Fail(verification, "input_rejected:" + index.ToString() + ":" + result.Reason);
                state = result.State;
            }
            if (state.Status != RaidRunStatus.Cleared) return Fail(verification, "record_not_cleared");

            verification.Valid = true;
            verification.StatusCode = state.Status.ToString();
            verification.InputCount = capsule.InputSequence.Length;
            verification.EffectiveActionCount = state.Actions;
            verification.Hits = state.Hits;
            return true;
        }

        public static bool TryVerifyCoop(CoopRoomDefinition room, RecordCapsule capsule, out RecordVerification verification)
        {
            verification = NewVerification(capsule);
            if (CoopRules.ValidateRoom(room).Length > 0) return Fail(verification, "definition_invalid");
            string fingerprint = CoopRules.RoomFingerprint(room);
            if (!TryValidateCapsule(capsule, RecordCapsuleRules.CoopModeId, room.Id, room.RulesVersion, room.ContentVersion, fingerprint, verification)) return false;

            CoopSession session = CoopSession.Create(room);
            for (int index = 0; index < capsule.InputSequence.Length; index++)
            {
                CoopState before = session.State;
                if (before.Status == CoopRunStatus.Cleared) return Fail(verification, "input_after_cleared:" + index.ToString());
                if (before.Status != CoopRunStatus.Playing) return Fail(verification, "input_after_terminal:" + index.ToString());
                GameCommand direction;
                if (!TryDirection(capsule.InputSequence[index], out direction)) return Fail(verification, "input_invalid:" + index.ToString());
                CoopDispatchResult result = session.Dispatch(CoopCommandFactory.Slide(before.ActiveActor, "record-" + index.ToString(), before.AuthorityRevision, direction));
                if (!result.Accepted || result.Idempotent) return Fail(verification, "input_rejected:" + index.ToString() + ":" + result.Reason);
            }

            CoopState state = session.State;
            if (state.Status != CoopRunStatus.Cleared) return Fail(verification, "record_not_cleared");
            verification.Valid = true;
            verification.StatusCode = state.Status.ToString();
            verification.InputCount = capsule.InputSequence.Length;
            verification.EffectiveActionCount = capsule.InputSequence.Length;
            verification.LogicalActionCount = state.LogicalActionCount;
            verification.ActiveActorCode = (int)state.ActiveActor;
            verification.CirclePosition = state.CirclePosition;
            verification.DiamondPosition = state.DiamondPosition;
            return true;
        }

        private static RecordVerification NewVerification(RecordCapsule capsule)
        {
            return new RecordVerification
            {
                Valid = false,
                ErrorCode = string.Empty,
                ModeId = capsule == null ? string.Empty : capsule.ModeId,
                DefinitionId = capsule == null ? string.Empty : capsule.DefinitionId,
                RulesVersion = capsule == null ? string.Empty : capsule.RulesVersion,
                ContentVersion = capsule == null ? string.Empty : capsule.ContentVersion,
                DefinitionFingerprint = capsule == null ? string.Empty : capsule.DefinitionFingerprint,
                StatusCode = "Unknown"
            };
        }

        private static bool TryValidateCapsule(RecordCapsule capsule, string modeId, string definitionId, string rulesVersion, string contentVersion, string fingerprint, RecordVerification verification)
        {
            string ignored;
            string encoded;
            if (!RecordCapsuleCodec.TryEncode(capsule, out encoded, out ignored)) return Fail(verification, ignored);
            if (!string.Equals(capsule.ModeId, modeId, StringComparison.Ordinal) || !string.Equals(capsule.DefinitionId, definitionId, StringComparison.Ordinal)
                || !string.Equals(capsule.RulesVersion, rulesVersion, StringComparison.Ordinal) || !string.Equals(capsule.ContentVersion, contentVersion, StringComparison.Ordinal)
                || !string.Equals(capsule.DefinitionFingerprint, fingerprint, StringComparison.Ordinal)) return Fail(verification, "definition_identity_mismatch");
            return true;
        }

        private static bool TryDirection(char value, out GameCommand direction)
        {
            direction = GameCommand.Up;
            if (value == 'U') return true;
            if (value == 'D') { direction = GameCommand.Down; return true; }
            if (value == 'L') { direction = GameCommand.Left; return true; }
            if (value == 'R') { direction = GameCommand.Right; return true; }
            return false;
        }

        private static bool Fail(RecordVerification verification, string error)
        {
            verification.Valid = false;
            verification.ErrorCode = error;
            return false;
        }
    }
}
