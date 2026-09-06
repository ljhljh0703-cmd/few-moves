using System;

namespace Nectorial.TurnEscape
{
    public static class SaveCodec
    {
        public const int CurrentSchemaVersion = 1;
        public const string CurrentGameId = "nectorial-turn-escape";

        public static SaveEnvelope Capture(RoomDefinition room, GameState state, string contentVersion)
        {
            EnsureCaptureInputs(room, state, contentVersion);

            return new SaveEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                GameId = CurrentGameId,
                ContentVersion = contentVersion,
                RoomHash = GameEngine.RoomFingerprint(room),
                State = GameEngine.CloneState(state)
            };
        }

        public static bool TryRestore(RoomDefinition room, SaveEnvelope envelope, string contentVersion, out GameState state, out string error)
        {
            state = null;
            error = null;

            if (string.IsNullOrEmpty(contentVersion))
            {
                error = "content_version_missing";
                return false;
            }

            string[] roomErrors = GameEngine.ValidateRoom(room);
            if (roomErrors.Length > 0)
            {
                error = "room_invalid";
                return false;
            }

            if (envelope == null)
            {
                error = "save_missing";
                return false;
            }

            if (envelope.SchemaVersion != CurrentSchemaVersion)
            {
                error = "schema_version_mismatch";
                return false;
            }

            if (!string.Equals(envelope.GameId, CurrentGameId, StringComparison.Ordinal))
            {
                error = "game_id_mismatch";
                return false;
            }

            if (!string.Equals(envelope.ContentVersion, contentVersion, StringComparison.Ordinal))
            {
                error = "content_version_mismatch";
                return false;
            }

            if (!string.Equals(envelope.RoomHash, GameEngine.RoomFingerprint(room), StringComparison.Ordinal))
            {
                error = "room_hash_mismatch";
                return false;
            }

            string[] stateErrors = GameEngine.ValidateState(room, envelope.State);
            if (stateErrors.Length > 0)
            {
                error = "state_invalid:" + stateErrors[0];
                return false;
            }

            state = GameEngine.CloneState(envelope.State);
            return true;
        }

        private static void EnsureCaptureInputs(RoomDefinition room, GameState state, string contentVersion)
        {
            if (string.IsNullOrEmpty(contentVersion))
            {
                throw new ArgumentException("Content version is required.", "contentVersion");
            }

            string[] roomErrors = GameEngine.ValidateRoom(room);
            if (roomErrors.Length > 0)
            {
                throw new ArgumentException("RoomDefinition is invalid: " + string.Join(",", roomErrors), "room");
            }

            string[] stateErrors = GameEngine.ValidateState(room, state);
            if (stateErrors.Length > 0)
            {
                throw new ArgumentException("GameState is invalid: " + string.Join(",", stateErrors), "state");
            }
        }
    }
}
