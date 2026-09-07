using System;

namespace Nectorial.SlideEscape.Coop
{
    public static class CoopSaveCodec
    {
        public const int CurrentSchemaVersion = 1;
        public const string CurrentGameId = "few-moves";
        public const string CurrentModeId = "coop-v1";

        public static CoopSaveEnvelope Capture(CoopSession session, string sessionId)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("Session ID is required.", "sessionId");
            CoopRoomDefinition room = session.Room;
            CoopState state = session.State;
            return new CoopSaveEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                GameId = CurrentGameId,
                ModeId = CurrentModeId,
                RulesVersion = room.RulesVersion,
                ContentVersion = room.ContentVersion,
                RoomFingerprint = CoopRules.RoomFingerprint(room),
                SessionId = sessionId,
                State = CoopRules.CloneState(state),
                Replay = CoopReplayCodec.Clone(session.ExportReplay()),
                StateFingerprint = CoopRules.StateFingerprint(room, state)
            };
        }

        public static bool TryRestore(CoopRoomDefinition room, CoopSaveEnvelope envelope, out CoopSession session, out string error)
        {
            session = null;
            error = null;
            if (CoopRules.ValidateRoom(room).Length > 0) { error = "room_invalid"; return false; }
            if (envelope == null) { error = "save_missing"; return false; }
            if (envelope.SchemaVersion != CurrentSchemaVersion) { error = "schema_version_mismatch"; return false; }
            if (!string.Equals(envelope.GameId, CurrentGameId, StringComparison.Ordinal)) { error = "game_id_mismatch"; return false; }
            if (!string.Equals(envelope.ModeId, CurrentModeId, StringComparison.Ordinal)) { error = "mode_id_mismatch"; return false; }
            if (!string.Equals(envelope.RulesVersion, room.RulesVersion, StringComparison.Ordinal)) { error = "rules_version_mismatch"; return false; }
            if (!string.Equals(envelope.ContentVersion, room.ContentVersion, StringComparison.Ordinal)) { error = "content_version_mismatch"; return false; }
            if (!string.Equals(envelope.RoomFingerprint, CoopRules.RoomFingerprint(room), StringComparison.Ordinal)) { error = "room_fingerprint_mismatch"; return false; }
            if (string.IsNullOrEmpty(envelope.SessionId)) { error = "session_id_missing"; return false; }

            CoopSession restored;
            string replayError;
            if (!CoopReplayCodec.TryReplay(room, envelope.Replay, out restored, out replayError))
            {
                error = replayError;
                return false;
            }
            CoopState state = restored.State;
            if (!CoopRules.StatesEqual(state, envelope.State)) { error = "saved_state_mismatch"; return false; }
            if (!string.Equals(CoopRules.StateFingerprint(room, state), envelope.StateFingerprint, StringComparison.Ordinal)) { error = "state_fingerprint_mismatch"; return false; }
            session = restored;
            return true;
        }
    }
}
