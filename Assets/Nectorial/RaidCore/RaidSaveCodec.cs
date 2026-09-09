using System;

namespace Nectorial.SlideEscape.Raid
{
    public static class RaidSaveCodec
    {
        public const int CurrentSchemaVersion = 1;
        public const string CurrentGameId = "few-moves";
        public const string CurrentModeId = "raid-v1";

        public static RaidSaveEnvelope Capture(RaidSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            RaidArenaDefinition arena = session.Arena;
            RaidState state = session.State;
            return new RaidSaveEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                GameId = CurrentGameId,
                ModeId = CurrentModeId,
                RulesVersion = arena.RulesVersion,
                ContentVersion = arena.ContentVersion,
                ArenaFingerprint = RaidRules.ArenaFingerprint(arena),
                State = RaidRules.CloneState(state),
                Replay = RaidReplayCodec.Clone(session.ExportReplay()),
                StateFingerprint = RaidRules.StateFingerprint(arena, state)
            };
        }

        public static bool TryRestore(RaidArenaDefinition arena, RaidSaveEnvelope envelope, out RaidSession session, out string error)
        {
            session = null;
            error = null;
            if (RaidRules.ValidateArena(arena).Length > 0) { error = "arena_invalid"; return false; }
            if (envelope == null) { error = "save_missing"; return false; }
            if (envelope.SchemaVersion != CurrentSchemaVersion) { error = "schema_version_mismatch"; return false; }
            if (!string.Equals(envelope.GameId, CurrentGameId, StringComparison.Ordinal)) { error = "game_id_mismatch"; return false; }
            if (!string.Equals(envelope.ModeId, CurrentModeId, StringComparison.Ordinal)) { error = "mode_id_mismatch"; return false; }
            if (!string.Equals(envelope.RulesVersion, arena.RulesVersion, StringComparison.Ordinal)) { error = "rules_version_mismatch"; return false; }
            if (!string.Equals(envelope.ContentVersion, arena.ContentVersion, StringComparison.Ordinal)) { error = "content_version_mismatch"; return false; }
            if (!string.Equals(envelope.ArenaFingerprint, RaidRules.ArenaFingerprint(arena), StringComparison.Ordinal)) { error = "arena_fingerprint_mismatch"; return false; }
            if (envelope.State == null || envelope.Replay == null) { error = "save_payload_missing"; return false; }
            RaidSession restored;
            string replayError;
            if (!RaidReplayCodec.TryReplay(arena, envelope.Replay, out restored, out replayError)) { error = replayError; return false; }
            RaidState state = restored.State;
            if (!RaidRules.StatesEqual(state, envelope.State)) { error = "saved_state_mismatch"; return false; }
            if (!string.Equals(RaidRules.StateFingerprint(arena, state), envelope.StateFingerprint, StringComparison.Ordinal)) { error = "state_fingerprint_mismatch"; return false; }
            session = restored;
            return true;
        }
    }
}
