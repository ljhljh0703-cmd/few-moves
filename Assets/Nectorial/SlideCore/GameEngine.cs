using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nectorial.SlideEscape
{
    public static class GameEngine
    {
        public const int BoardSize = 8;

        public static string[] ValidateRoom(RoomDefinition room)
        {
            List<string> errors = new List<string>();
            if (room == null)
            {
                errors.Add("room_null");
                return errors.ToArray();
            }

            if (string.IsNullOrEmpty(room.Id)) errors.Add("room_id_missing");
            if (room.Width != BoardSize) errors.Add("room_width_must_be_eight");
            if (room.Height != BoardSize) errors.Add("room_height_must_be_eight");
            ValidateRows(room, errors);
            ValidatePieces(room, errors);
            ValidateGoal(room, errors);
            if (room.ParMoves <= 0) errors.Add("par_moves_invalid");
            return errors.ToArray();
        }

        public static GameState Create(RoomDefinition room)
        {
            EnsureValidRoom(room);
            GridPoint[] positions = new GridPoint[room.Pieces.Length];
            for (int index = 0; index < positions.Length; index++) positions[index] = room.Pieces[index].Start;
            return new GameState
            {
                RoomId = room.Id,
                Positions = positions,
                Turn = 0,
                Status = RunStatus.Playing
            };
        }

        public static StepResult Step(RoomDefinition room, GameState state, GameMove move)
        {
            string[] roomErrors = ValidateRoom(room);
            if (roomErrors.Length > 0) return Rejected(state, "invalid_room");
            string[] stateErrors = ValidateState(room, state);
            if (stateErrors.Length > 0) return Rejected(state, "invalid_state");
            if (state.Status != RunStatus.Playing) return Rejected(state, "terminal_state");
            if (state.Turn == int.MaxValue) return Rejected(state, "turn_overflow");
            if (move == null) return Rejected(state, "move_missing");
            if (move.PieceIndex < 0 || move.PieceIndex >= room.Pieces.Length) return Rejected(state, "invalid_piece_index");

            int destinationCell;
            int distance;
            string slideError;
            if (!TryGetSlideDestination(room, PackPositions(state.Positions), state.Positions.Length, move.PieceIndex, move.Direction,
                out destinationCell, out distance, out slideError))
                return Rejected(state, slideError);
            if (distance == 0) return Rejected(state, "blocked_zero");

            GridPoint destination = FromCell(destinationCell);
            GameState next = CloneState(state);
            next.Turn = state.Turn + 1;
            next.Positions[move.PieceIndex] = destination;
            List<string> events = new List<string>();
            events.Add("slid:" + room.Pieces[move.PieceIndex].Id + ":" + distance.ToString(CultureInfo.InvariantCulture));
            if (move.PieceIndex == room.TargetPieceIndex && SamePoint(destination, room.Goal))
            {
                next.Status = RunStatus.Cleared;
                events.Add("room_cleared");
            }

            return Accepted(next, events);
        }

        public static string Fingerprint(RoomDefinition room, GameState state, bool includeTurn = true)
        {
            EnsureValidRoom(room);
            EnsureValidState(room, state);
            StableFingerprintWriter writer = new StableFingerprintWriter();
            WriteRoom(writer, room);
            writer.WriteString("state-slide-v3");
            writer.WriteString(state.RoomId);
            writer.WriteInt(state.Positions.Length);
            for (int index = 0; index < state.Positions.Length; index++) writer.WritePoint(state.Positions[index]);
            writer.WriteInt((int)state.Status);
            if (includeTurn) writer.WriteInt(state.Turn);
            return writer.ToSha256();
        }

        internal static string RoomFingerprint(RoomDefinition room)
        {
            EnsureValidRoom(room);
            StableFingerprintWriter writer = new StableFingerprintWriter();
            WriteRoom(writer, room);
            return writer.ToSha256();
        }

        internal static string[] ValidateState(RoomDefinition room, GameState state)
        {
            List<string> errors = new List<string>();
            if (state == null)
            {
                errors.Add("state_null");
                return errors.ToArray();
            }

            if (!string.Equals(state.RoomId, room.Id, StringComparison.Ordinal)) errors.Add("state_room_id_mismatch");
            if (state.Turn < 0) errors.Add("state_turn_invalid");
            if (!IsKnownRunStatus(state.Status)) errors.Add("state_status_invalid");
            ValidatePositions(room, state.Positions, errors);
            if (errors.Count == 0)
            {
                bool targetOnGoal = SamePoint(state.Positions[room.TargetPieceIndex], room.Goal);
                if (state.Status == RunStatus.Playing && targetOnGoal) errors.Add("state_playing_target_on_goal");
                if (state.Status == RunStatus.Cleared && !targetOnGoal) errors.Add("state_cleared_target_not_on_goal");
            }

            return errors.ToArray();
        }

        internal static GameState CloneState(GameState source)
        {
            if (source == null) return null;
            return new GameState
            {
                RoomId = source.RoomId,
                Positions = CopyPoints(source.Positions),
                Turn = source.Turn,
                Status = source.Status
            };
        }

        internal static GameMove[] CloneMoves(GameMove[] source)
        {
            if (source == null) return null;
            GameMove[] copy = new GameMove[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                GameMove move = source[index];
                copy[index] = move == null ? null : new GameMove { PieceIndex = move.PieceIndex, Direction = move.Direction };
            }

            return copy;
        }

        internal static bool StatesEqual(GameState left, GameState right)
        {
            if (left == null || right == null) return left == right;
            if (!string.Equals(left.RoomId, right.RoomId, StringComparison.Ordinal) || left.Turn != right.Turn || left.Status != right.Status) return false;
            if (left.Positions == null || right.Positions == null || left.Positions.Length != right.Positions.Length) return false;
            for (int index = 0; index < left.Positions.Length; index++)
                if (!SamePoint(left.Positions[index], right.Positions[index])) return false;
            return true;
        }

        internal static bool TryGetSlideDestination(RoomDefinition room, ulong packedPositions, int pieceCount, int pieceIndex,
            GameCommand direction, out int destinationCell, out int distance, out string error)
        {
            destinationCell = ExtractPackedCell(packedPositions, pieceIndex);
            distance = 0;
            error = null;
            if (!IsKnownDirection(direction))
            {
                error = "invalid_direction";
                return false;
            }

            while (true)
            {
                int candidate;
                if (!TryGetNeighbor(destinationCell, direction, out candidate) || !IsFloorCell(room, candidate) ||
                    ContainsPackedCell(packedPositions, pieceCount, pieceIndex, candidate)) break;
                destinationCell = candidate;
                distance++;
            }

            return true;
        }

        internal static ulong PackPositions(GridPoint[] positions)
        {
            ulong packed = 0UL;
            for (int index = 0; index < positions.Length; index++) packed = ReplacePackedCell(packed, index, ToCell(positions[index]));
            return packed;
        }

        internal static int ToCell(GridPoint point)
        {
            return point.Y * BoardSize + point.X;
        }

        internal static GridPoint FromCell(int cell)
        {
            return new GridPoint(cell % BoardSize, cell / BoardSize);
        }

        internal static int ExtractPackedCell(ulong packedPositions, int pieceIndex)
        {
            return (int)((packedPositions >> (pieceIndex * 6)) & 0x3fUL);
        }

        internal static ulong ReplacePackedCell(ulong packedPositions, int pieceIndex, int cell)
        {
            int shift = pieceIndex * 6;
            ulong clearMask = ~(0x3fUL << shift);
            return (packedPositions & clearMask) | ((ulong)cell << shift);
        }

        private static void ValidateRows(RoomDefinition room, List<string> errors)
        {
            if (room.Rows == null)
            {
                errors.Add("room_rows_missing");
                return;
            }

            if (room.Rows.Length != BoardSize) errors.Add("room_row_count_mismatch");
            for (int y = 0; y < room.Rows.Length; y++)
            {
                string row = room.Rows[y];
                if (row == null)
                {
                    errors.Add("room_row_null:" + y.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                if (row.Length != BoardSize) errors.Add("room_row_width_mismatch:" + y.ToString(CultureInfo.InvariantCulture));
                for (int x = 0; x < row.Length; x++)
                {
                    char tile = row[x];
                    if (tile != '.' && tile != '#') errors.Add("room_tile_invalid:" + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        private static void ValidatePieces(RoomDefinition room, List<string> errors)
        {
            if (room.Pieces == null)
            {
                errors.Add("pieces_missing");
                return;
            }

            if (room.Pieces.Length < 1 || room.Pieces.Length > 3) errors.Add("piece_count_invalid");
            if (room.TargetPieceIndex != 0) errors.Add("target_piece_index_must_be_zero");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            List<GridPoint> starts = new List<GridPoint>();
            for (int index = 0; index < room.Pieces.Length; index++)
            {
                PieceDefinition piece = room.Pieces[index];
                string item = index.ToString(CultureInfo.InvariantCulture);
                if (piece == null)
                {
                    errors.Add("piece_null:" + item);
                    continue;
                }

                if (string.IsNullOrEmpty(piece.Id)) errors.Add("piece_id_missing:" + item);
                else if (!ids.Add(piece.Id)) errors.Add("piece_id_duplicate:" + piece.Id);
                if (!IsInside(room, piece.Start)) errors.Add("piece_start_out_of_bounds:" + item);
                else if (!IsFloor(room, piece.Start)) errors.Add("piece_start_not_floor:" + item);
                if (ContainsPoint(starts, piece.Start)) errors.Add("piece_start_overlap:" + item);
                starts.Add(piece.Start);
            }
        }

        private static void ValidateGoal(RoomDefinition room, List<string> errors)
        {
            if (!IsInside(room, room.Goal)) errors.Add("goal_out_of_bounds");
            else if (!IsFloor(room, room.Goal)) errors.Add("goal_not_floor");
            if (room.Pieces == null || room.Pieces.Length == 0) return;

            for (int index = 0; index < room.Pieces.Length; index++)
            {
                PieceDefinition piece = room.Pieces[index];
                if (piece != null && SamePoint(piece.Start, room.Goal)) errors.Add("goal_occupied_at_start:" + index.ToString(CultureInfo.InvariantCulture));
            }

            if (room.TargetPieceIndex == 0 && room.Pieces[0] != null &&
                (room.Pieces[0].Start.X == room.Goal.X || room.Pieces[0].Start.Y == room.Goal.Y)) errors.Add("target_goal_axis_aligned");
        }

        private static void ValidatePositions(RoomDefinition room, GridPoint[] positions, List<string> errors)
        {
            if (positions == null)
            {
                errors.Add("state_positions_missing");
                return;
            }

            if (positions.Length != room.Pieces.Length)
            {
                errors.Add("state_piece_count_mismatch");
                return;
            }

            List<GridPoint> seen = new List<GridPoint>();
            for (int index = 0; index < positions.Length; index++)
            {
                GridPoint point = positions[index];
                if (!IsInside(room, point)) errors.Add("state_position_out_of_bounds:" + index.ToString(CultureInfo.InvariantCulture));
                else if (!IsFloor(room, point)) errors.Add("state_position_not_floor:" + index.ToString(CultureInfo.InvariantCulture));
                if (ContainsPoint(seen, point)) errors.Add("state_position_overlap:" + index.ToString(CultureInfo.InvariantCulture));
                seen.Add(point);
            }
        }

        private static StepResult Accepted(GameState state, List<string> events)
        {
            return new StepResult { State = state, Accepted = true, Reason = "accepted", Events = events.ToArray() };
        }

        private static StepResult Rejected(GameState state, string reason)
        {
            return new StepResult { State = CloneState(state), Accepted = false, Reason = reason, Events = new string[] { reason } };
        }

        private static bool TryGetNeighbor(int cell, GameCommand direction, out int candidate)
        {
            candidate = cell;
            switch (direction)
            {
                case GameCommand.Up:
                    if (cell < BoardSize) return false;
                    candidate = cell - BoardSize;
                    return true;
                case GameCommand.Down:
                    if (cell >= BoardSize * (BoardSize - 1)) return false;
                    candidate = cell + BoardSize;
                    return true;
                case GameCommand.Left:
                    if (cell % BoardSize == 0) return false;
                    candidate = cell - 1;
                    return true;
                case GameCommand.Right:
                    if (cell % BoardSize == BoardSize - 1) return false;
                    candidate = cell + 1;
                    return true;
                default: return false;
            }
        }

        private static bool IsKnownDirection(GameCommand direction)
        {
            return direction == GameCommand.Up || direction == GameCommand.Down || direction == GameCommand.Left || direction == GameCommand.Right;
        }

        private static bool ContainsPackedCell(ulong packedPositions, int pieceCount, int ignoredPieceIndex, int cell)
        {
            for (int index = 0; index < pieceCount; index++)
                if (index != ignoredPieceIndex && ExtractPackedCell(packedPositions, index) == cell) return true;
            return false;
        }

        private static bool IsFloorCell(RoomDefinition room, int cell)
        {
            int y = cell / BoardSize;
            int x = cell % BoardSize;
            return y >= 0 && y < BoardSize && x >= 0 && x < BoardSize && room.Rows != null && y < room.Rows.Length &&
                room.Rows[y] != null && x < room.Rows[y].Length && room.Rows[y][x] == '.';
        }

        private static bool ContainsPoint(List<GridPoint> points, GridPoint point)
        {
            for (int index = 0; index < points.Count; index++) if (SamePoint(points[index], point)) return true;
            return false;
        }

        private static bool IsInside(RoomDefinition room, GridPoint point)
        {
            return room != null && point.X >= 0 && point.X < BoardSize && point.Y >= 0 && point.Y < BoardSize;
        }

        private static bool IsFloor(RoomDefinition room, GridPoint point)
        {
            return IsInside(room, point) && IsFloorCell(room, ToCell(point));
        }

        private static bool SamePoint(GridPoint left, GridPoint right)
        {
            return left.X == right.X && left.Y == right.Y;
        }

        private static bool IsKnownRunStatus(RunStatus status)
        {
            return status == RunStatus.Playing || status == RunStatus.Cleared;
        }

        private static void EnsureValidRoom(RoomDefinition room)
        {
            string[] errors = ValidateRoom(room);
            if (errors.Length > 0) throw new ArgumentException("RoomDefinition is invalid: " + string.Join(",", errors), "room");
        }

        private static void EnsureValidState(RoomDefinition room, GameState state)
        {
            string[] errors = ValidateState(room, state);
            if (errors.Length > 0) throw new ArgumentException("GameState is invalid: " + string.Join(",", errors), "state");
        }

        private static GridPoint[] CopyPoints(GridPoint[] source)
        {
            if (source == null) return null;
            GridPoint[] copy = new GridPoint[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static void WriteRoom(StableFingerprintWriter writer, RoomDefinition room)
        {
            writer.WriteString("room-slide-v3");
            writer.WriteString(room.Id);
            writer.WriteInt(room.Width);
            writer.WriteInt(room.Height);
            writer.WriteInt(room.Rows.Length);
            for (int y = 0; y < room.Rows.Length; y++) writer.WriteString(room.Rows[y]);
            writer.WriteInt(room.Pieces.Length);
            for (int index = 0; index < room.Pieces.Length; index++)
            {
                writer.WriteString(room.Pieces[index].Id);
                writer.WritePoint(room.Pieces[index].Start);
            }

            writer.WriteInt(room.TargetPieceIndex);
            writer.WritePoint(room.Goal);
            writer.WriteInt(room.ParMoves);
        }
    }

    internal sealed class StableFingerprintWriter
    {
        private const string HexDigits = "0123456789abcdef";
        private readonly StringBuilder builder = new StringBuilder();

        public void WriteString(string value)
        {
            if (value == null)
            {
                builder.Append("n;");
                return;
            }

            builder.Append('s');
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
            builder.Append(';');
        }

        public void WriteInt(int value)
        {
            builder.Append('i');
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            builder.Append(';');
        }

        public void WritePoint(GridPoint point)
        {
            builder.Append('p');
            WriteInt(point.X);
            WriteInt(point.Y);
        }

        public string ToSha256()
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(builder.ToString());
            byte[] digest;
            using (SHA256 sha256 = SHA256.Create()) digest = sha256.ComputeHash(bytes);
            char[] hex = new char[digest.Length * 2];
            for (int index = 0; index < digest.Length; index++)
            {
                byte value = digest[index];
                hex[index * 2] = HexDigits[value >> 4];
                hex[(index * 2) + 1] = HexDigits[value & 0x0f];
            }

            return new string(hex);
        }
    }
}
