using System;

namespace Nectorial.SlideEscape
{
    public static class SaveCodec
    {
        public const int CurrentSchemaVersion = 2;
        public const string CurrentGameId = "nectorial-turn-escape";

        public static SaveEnvelope Capture(RoomDefinition room, GameState state, string contentVersion, GameMove[] moves, int selectedPieceIndex)
        {
            EnsureRoomAndState(room, state, contentVersion);
            EnsureSelection(room, selectedPieceIndex);
            string transcriptError;
            if (!TryValidateTranscript(room, state, moves, out transcriptError))
                throw new ArgumentException("Move transcript is invalid: " + transcriptError, "moves");

            return new SaveEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                GameId = CurrentGameId,
                ContentVersion = contentVersion,
                RoomHash = GameEngine.RoomFingerprint(room),
                State = GameEngine.CloneState(state),
                Moves = GameEngine.CloneMoves(moves),
                SelectedPieceIndex = selectedPieceIndex
            };
        }

        public static bool TryRestore(RoomDefinition room, SaveEnvelope envelope, string contentVersion,
            out GameState state, out GameMove[] moves, out int selectedPieceIndex, out string error)
        {
            state = null;
            moves = null;
            selectedPieceIndex = -1;
            error = null;

            if (string.IsNullOrEmpty(contentVersion)) { error = "content_version_missing"; return false; }
            string[] roomErrors = GameEngine.ValidateRoom(room);
            if (roomErrors.Length > 0) { error = "room_invalid"; return false; }
            if (envelope == null) { error = "save_missing"; return false; }
            if (envelope.SchemaVersion != CurrentSchemaVersion) { error = "schema_version_mismatch"; return false; }
            if (!string.Equals(envelope.GameId, CurrentGameId, StringComparison.Ordinal)) { error = "game_id_mismatch"; return false; }
            if (!string.Equals(envelope.ContentVersion, contentVersion, StringComparison.Ordinal)) { error = "content_version_mismatch"; return false; }
            if (!string.Equals(envelope.RoomHash, GameEngine.RoomFingerprint(room), StringComparison.Ordinal)) { error = "room_hash_mismatch"; return false; }

            string[] stateErrors = GameEngine.ValidateState(room, envelope.State);
            if (stateErrors.Length > 0) { error = "state_invalid:" + stateErrors[0]; return false; }
            if (envelope.SelectedPieceIndex < 0 || envelope.SelectedPieceIndex >= room.Pieces.Length) { error = "selected_piece_index_invalid"; return false; }

            string transcriptError;
            if (!TryValidateTranscript(room, envelope.State, envelope.Moves, out transcriptError))
            {
                error = transcriptError;
                return false;
            }

            state = GameEngine.CloneState(envelope.State);
            moves = GameEngine.CloneMoves(envelope.Moves);
            selectedPieceIndex = envelope.SelectedPieceIndex;
            return true;
        }

        private static void EnsureRoomAndState(RoomDefinition room, GameState state, string contentVersion)
        {
            if (string.IsNullOrEmpty(contentVersion)) throw new ArgumentException("Content version is required.", "contentVersion");
            string[] roomErrors = GameEngine.ValidateRoom(room);
            if (roomErrors.Length > 0) throw new ArgumentException("RoomDefinition is invalid: " + string.Join(",", roomErrors), "room");
            string[] stateErrors = GameEngine.ValidateState(room, state);
            if (stateErrors.Length > 0) throw new ArgumentException("GameState is invalid: " + string.Join(",", stateErrors), "state");
        }

        private static void EnsureSelection(RoomDefinition room, int selectedPieceIndex)
        {
            if (selectedPieceIndex < 0 || selectedPieceIndex >= room.Pieces.Length)
                throw new ArgumentOutOfRangeException("selectedPieceIndex", "Selected piece index is invalid.");
        }

        private static bool TryValidateTranscript(RoomDefinition room, GameState expectedState, GameMove[] moves, out string error)
        {
            error = null;
            if (moves == null) { error = "moves_missing"; return false; }

            GameState replay = GameEngine.Create(room);
            for (int index = 0; index < moves.Length; index++)
            {
                GameMove move = moves[index];
                if (move == null) { error = "transcript_move_missing:" + index; return false; }
                StepResult result = GameEngine.Step(room, replay, move);
                if (!result.Accepted) { error = "transcript_move_rejected:" + index + ":" + result.Reason; return false; }
                replay = result.State;
            }

            if (replay.Turn != moves.Length) { error = "transcript_turn_mismatch"; return false; }
            if (!GameEngine.StatesEqual(replay, expectedState)) { error = "transcript_state_mismatch"; return false; }
            return true;
        }
    }
}
