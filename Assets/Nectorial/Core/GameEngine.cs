using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nectorial.TurnEscape
{
    public static class GameEngine
    {
        public static string[] ValidateRoom(RoomDefinition room)
        {
            List<string> errors = new List<string>();
            if (room == null)
            {
                errors.Add("room_null");
                return errors.ToArray();
            }

            if (string.IsNullOrEmpty(room.Id))
            {
                errors.Add("room_id_missing");
            }

            if (room.Width <= 0)
            {
                errors.Add("room_width_invalid");
            }

            if (room.Height <= 0)
            {
                errors.Add("room_height_invalid");
            }

            ValidateRows(room, errors);
            ValidateRequiredFloor(room, room.Start, "start", errors);
            ValidateRequiredFloor(room, room.Exit, "exit", errors);
            if (SamePoint(room.Start, room.Exit))
            {
                errors.Add("start_equals_exit");
            }

            HashSet<string> doorIds = new HashSet<string>(StringComparer.Ordinal);
            List<GridPoint> doorPositions = new List<GridPoint>();
            ValidateDoors(room, doorIds, doorPositions, errors);
            ValidateSwitches(room, doorIds, errors);
            ValidateGuards(room, doorPositions, errors);

            return errors.ToArray();
        }

        public static GameState Create(RoomDefinition room)
        {
            EnsureValidRoom(room);

            int[] guardIndices = new int[room.Guards.Length];
            for (int i = 0; i < room.Guards.Length; i++)
            {
                guardIndices[i] = room.Guards[i].StartIndex;
            }

            GameState state = new GameState
            {
                RoomId = room.Id,
                Player = room.Start,
                Turn = 0,
                Status = RunStatus.Playing,
                OpenDoorIds = new string[0],
                GuardIndices = guardIndices
            };

            EnsureValidState(room, state);
            return state;
        }

        public static StepResult Step(RoomDefinition room, GameState state, GameCommand command)
        {
            string[] roomErrors = ValidateRoom(room);
            if (roomErrors.Length > 0)
            {
                return Rejected(state, "invalid_room");
            }

            string[] stateErrors = ValidateState(room, state);
            if (stateErrors.Length > 0)
            {
                return Rejected(state, "invalid_state");
            }

            if (state.Status != RunStatus.Playing)
            {
                return Rejected(state, "terminal_state");
            }

            if (state.Turn == int.MaxValue)
            {
                return Rejected(state, "turn_overflow");
            }

            GridPoint target;
            bool moved;
            string commandError;
            if (!TryGetTarget(state.Player, command, out target, out moved, out commandError))
            {
                return Rejected(state, commandError);
            }

            if (moved)
            {
                if (!IsInside(room, target))
                {
                    return Rejected(state, "blocked_bounds");
                }

                if (!IsFloor(room, target))
                {
                    return Rejected(state, "blocked_wall");
                }

                string doorAtTarget = FindDoorIdAt(room, target);
                if (doorAtTarget != null && !ContainsId(state.OpenDoorIds, doorAtTarget))
                {
                    return Rejected(state, "blocked_closed_door");
                }
            }

            GameState next = CloneState(state);
            next.Turn = state.Turn + 1;
            next.Player = target;
            List<string> events = new List<string>();
            events.Add(moved ? "moved" : "waited");

            AdvanceGuards(room, state, next, events);

            string capturingGuard = FindGuardIdAt(room, next.GuardIndices, next.Player);
            if (capturingGuard != null)
            {
                next.Status = RunStatus.Captured;
                events.Add("captured_by_guard:" + capturingGuard);
                return Accepted(next, events);
            }

            if (moved)
            {
                ActivateSwitches(room, next, events);
            }

            if (SamePoint(next.Player, room.Exit))
            {
                next.Status = RunStatus.Cleared;
                events.Add("room_cleared");
            }

            return Accepted(next, events);
        }

        public static GridPoint[] NextGuardPositions(RoomDefinition room, GameState state)
        {
            EnsureValidRoom(room);
            EnsureValidState(room, state);

            if (state.Status != RunStatus.Playing)
            {
                return new GridPoint[0];
            }

            GridPoint[] positions = new GridPoint[room.Guards.Length];
            for (int i = 0; i < room.Guards.Length; i++)
            {
                Guard guard = room.Guards[i];
                int nextIndex = NextGuardIndex(guard, state.GuardIndices[i]);
                positions[i] = guard.Patrol[nextIndex];
            }

            return positions;
        }

        public static string Fingerprint(RoomDefinition room, GameState state, bool includeTurn = true)
        {
            EnsureValidRoom(room);
            EnsureValidState(room, state);

            StableFingerprintWriter writer = new StableFingerprintWriter();
            WriteRoom(writer, room);
            writer.WriteString("state");
            writer.WriteString(state.RoomId);
            writer.WritePoint(state.Player);
            writer.WriteInt((int)state.Status);
            if (includeTurn)
            {
                writer.WriteInt(state.Turn);
            }

            WriteSortedIds(writer, state.OpenDoorIds);
            writer.WriteInt(state.GuardIndices.Length);
            for (int i = 0; i < state.GuardIndices.Length; i++)
            {
                writer.WriteInt(state.GuardIndices[i]);
            }

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

            if (!string.Equals(state.RoomId, room.Id, StringComparison.Ordinal))
            {
                errors.Add("state_room_id_mismatch");
            }

            if (!IsInside(room, state.Player))
            {
                errors.Add("state_player_out_of_bounds");
            }
            else if (!IsFloor(room, state.Player))
            {
                errors.Add("state_player_not_floor");
            }
            else
            {
                string doorAtPlayer = FindDoorIdAt(room, state.Player);
                if (doorAtPlayer != null && !ContainsId(state.OpenDoorIds, doorAtPlayer))
                {
                    errors.Add("state_player_on_closed_door");
                }
            }

            if (state.Turn < 0)
            {
                errors.Add("state_turn_invalid");
            }

            if (!IsKnownRunStatus(state.Status))
            {
                errors.Add("state_status_invalid");
            }

            ValidateOpenDoorState(room, state.OpenDoorIds, errors);
            ValidateGuardState(room, state.GuardIndices, errors);

            if (errors.Count == 0)
            {
                string guardAtPlayer = FindGuardIdAt(room, state.GuardIndices, state.Player);
                if (state.Status == RunStatus.Playing)
                {
                    if (SamePoint(state.Player, room.Exit))
                    {
                        errors.Add("state_playing_on_exit");
                    }

                    if (guardAtPlayer != null)
                    {
                        errors.Add("state_playing_on_guard");
                    }
                }
                else if (state.Status == RunStatus.Cleared && !SamePoint(state.Player, room.Exit))
                {
                    errors.Add("state_cleared_not_on_exit");
                }
                else if (state.Status == RunStatus.Cleared && guardAtPlayer != null)
                {
                    errors.Add("state_cleared_on_guard");
                }
                else if (state.Status == RunStatus.Captured && guardAtPlayer == null)
                {
                    errors.Add("state_captured_without_guard");
                }
            }

            return errors.ToArray();
        }

        internal static GameState CloneState(GameState source)
        {
            if (source == null)
            {
                return null;
            }

            return new GameState
            {
                RoomId = source.RoomId,
                Player = source.Player,
                Turn = source.Turn,
                Status = source.Status,
                OpenDoorIds = CopyStrings(source.OpenDoorIds),
                GuardIndices = CopyInts(source.GuardIndices)
            };
        }

        private static void ValidateRows(RoomDefinition room, List<string> errors)
        {
            if (room.Rows == null)
            {
                errors.Add("room_rows_missing");
                return;
            }

            if (room.Rows.Length != room.Height)
            {
                errors.Add("room_row_count_mismatch");
            }

            for (int y = 0; y < room.Rows.Length; y++)
            {
                string row = room.Rows[y];
                if (row == null)
                {
                    errors.Add("room_row_null:" + y.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                if (row.Length != room.Width)
                {
                    errors.Add("room_row_width_mismatch:" + y.ToString(CultureInfo.InvariantCulture));
                }

                for (int x = 0; x < row.Length; x++)
                {
                    char tile = row[x];
                    if (tile != '.' && tile != '#')
                    {
                        errors.Add("room_tile_invalid:" + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        private static void ValidateRequiredFloor(RoomDefinition room, GridPoint point, string name, List<string> errors)
        {
            if (!IsInside(room, point))
            {
                errors.Add(name + "_out_of_bounds");
            }
            else if (!IsFloor(room, point))
            {
                errors.Add(name + "_not_floor");
            }
        }

        private static void ValidateDoors(RoomDefinition room, HashSet<string> doorIds, List<GridPoint> doorPositions, List<string> errors)
        {
            if (room.Doors == null)
            {
                errors.Add("doors_missing");
                return;
            }

            for (int i = 0; i < room.Doors.Length; i++)
            {
                Door door = room.Doors[i];
                string index = i.ToString(CultureInfo.InvariantCulture);
                if (door == null)
                {
                    errors.Add("door_null:" + index);
                    continue;
                }

                if (string.IsNullOrEmpty(door.Id))
                {
                    errors.Add("door_id_missing:" + index);
                }
                else if (!doorIds.Add(door.Id))
                {
                    errors.Add("door_id_duplicate:" + door.Id);
                }

                if (!IsInside(room, door.Position))
                {
                    errors.Add("door_out_of_bounds:" + index);
                }
                else if (!IsFloor(room, door.Position))
                {
                    errors.Add("door_not_floor:" + index);
                }

                if (SamePoint(door.Position, room.Start))
                {
                    errors.Add("door_on_start:" + index);
                }

                if (ContainsPoint(doorPositions, door.Position))
                {
                    errors.Add("door_position_duplicate:" + index);
                }

                doorPositions.Add(door.Position);
            }
        }

        private static void ValidateSwitches(RoomDefinition room, HashSet<string> doorIds, List<string> errors)
        {
            if (room.Switches == null)
            {
                errors.Add("switches_missing");
                return;
            }

            HashSet<string> switchIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < room.Switches.Length; i++)
            {
                Switch item = room.Switches[i];
                string index = i.ToString(CultureInfo.InvariantCulture);
                if (item == null)
                {
                    errors.Add("switch_null:" + index);
                    continue;
                }

                if (string.IsNullOrEmpty(item.Id))
                {
                    errors.Add("switch_id_missing:" + index);
                }
                else if (!switchIds.Add(item.Id))
                {
                    errors.Add("switch_id_duplicate:" + item.Id);
                }

                if (!IsInside(room, item.Position))
                {
                    errors.Add("switch_out_of_bounds:" + index);
                }
                else if (!IsFloor(room, item.Position))
                {
                    errors.Add("switch_not_floor:" + index);
                }

                if (item.DoorIds == null)
                {
                    errors.Add("switch_door_ids_missing:" + index);
                    continue;
                }

                HashSet<string> linkedDoorIds = new HashSet<string>(StringComparer.Ordinal);
                for (int doorIndex = 0; doorIndex < item.DoorIds.Length; doorIndex++)
                {
                    string doorId = item.DoorIds[doorIndex];
                    if (string.IsNullOrEmpty(doorId))
                    {
                        errors.Add("switch_door_id_missing:" + index + ":" + doorIndex.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (!doorIds.Contains(doorId))
                    {
                        errors.Add("switch_door_unknown:" + item.Id + ":" + doorId);
                    }
                    else if (!linkedDoorIds.Add(doorId))
                    {
                        errors.Add("switch_door_duplicate:" + item.Id + ":" + doorId);
                    }
                }
            }
        }

        private static void ValidateGuards(RoomDefinition room, List<GridPoint> doorPositions, List<string> errors)
        {
            if (room.Guards == null)
            {
                errors.Add("guards_missing");
                return;
            }

            HashSet<string> guardIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < room.Guards.Length; i++)
            {
                Guard guard = room.Guards[i];
                string index = i.ToString(CultureInfo.InvariantCulture);
                if (guard == null)
                {
                    errors.Add("guard_null:" + index);
                    continue;
                }

                if (string.IsNullOrEmpty(guard.Id))
                {
                    errors.Add("guard_id_missing:" + index);
                }
                else if (!guardIds.Add(guard.Id))
                {
                    errors.Add("guard_id_duplicate:" + guard.Id);
                }

                if (guard.Patrol == null || guard.Patrol.Length == 0)
                {
                    errors.Add("guard_patrol_missing:" + index);
                    continue;
                }

                if (guard.StartIndex < 0 || guard.StartIndex >= guard.Patrol.Length)
                {
                    errors.Add("guard_start_index_invalid:" + index);
                }

                for (int pointIndex = 0; pointIndex < guard.Patrol.Length; pointIndex++)
                {
                    GridPoint point = guard.Patrol[pointIndex];
                    if (!IsInside(room, point))
                    {
                        errors.Add("guard_patrol_out_of_bounds:" + index + ":" + pointIndex.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (!IsFloor(room, point))
                    {
                        errors.Add("guard_patrol_not_floor:" + index + ":" + pointIndex.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (ContainsPoint(doorPositions, point))
                    {
                        errors.Add("guard_patrol_on_door:" + index + ":" + pointIndex.ToString(CultureInfo.InvariantCulture));
                    }
                }

                if (guard.Patrol.Length > 1)
                {
                    for (int pointIndex = 0; pointIndex < guard.Patrol.Length; pointIndex++)
                    {
                        GridPoint current = guard.Patrol[pointIndex];
                        GridPoint following = guard.Patrol[(pointIndex + 1) % guard.Patrol.Length];
                        long distance = Math.Abs((long)current.X - following.X) + Math.Abs((long)current.Y - following.Y);
                        if (distance != 1L)
                        {
                            errors.Add("guard_patrol_not_cardinal_cycle:" + index + ":" + pointIndex.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }

                if (guard.StartIndex >= 0 && guard.StartIndex < guard.Patrol.Length && SamePoint(guard.Patrol[guard.StartIndex], room.Start))
                {
                    errors.Add("guard_starts_on_player_start:" + index);
                }
            }
        }

        private static void ValidateOpenDoorState(RoomDefinition room, string[] openDoorIds, List<string> errors)
        {
            if (openDoorIds == null)
            {
                errors.Add("state_open_doors_missing");
                return;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < openDoorIds.Length; i++)
            {
                string id = openDoorIds[i];
                if (string.IsNullOrEmpty(id))
                {
                    errors.Add("state_open_door_id_missing:" + i.ToString(CultureInfo.InvariantCulture));
                }
                else if (!FindDoorExists(room, id))
                {
                    errors.Add("state_open_door_unknown:" + id);
                }
                else if (!seen.Add(id))
                {
                    errors.Add("state_open_door_duplicate:" + id);
                }
            }
        }

        private static void ValidateGuardState(RoomDefinition room, int[] guardIndices, List<string> errors)
        {
            if (guardIndices == null)
            {
                errors.Add("state_guard_indices_missing");
                return;
            }

            if (guardIndices.Length != room.Guards.Length)
            {
                errors.Add("state_guard_count_mismatch");
                return;
            }

            for (int i = 0; i < guardIndices.Length; i++)
            {
                int current = guardIndices[i];
                if (current < 0 || current >= room.Guards[i].Patrol.Length)
                {
                    errors.Add("state_guard_index_invalid:" + i.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        private static void ActivateSwitches(RoomDefinition room, GameState next, List<string> events)
        {
            HashSet<string> openDoorIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < next.OpenDoorIds.Length; i++)
            {
                openDoorIds.Add(next.OpenDoorIds[i]);
            }

            List<string> newlyOpened = new List<string>();
            for (int i = 0; i < room.Switches.Length; i++)
            {
                Switch item = room.Switches[i];
                if (!SamePoint(item.Position, next.Player))
                {
                    continue;
                }

                events.Add("switch_activated:" + item.Id);
                for (int doorIndex = 0; doorIndex < item.DoorIds.Length; doorIndex++)
                {
                    string doorId = item.DoorIds[doorIndex];
                    if (openDoorIds.Add(doorId))
                    {
                        newlyOpened.Add(doorId);
                    }
                }
            }

            if (newlyOpened.Count == 0)
            {
                return;
            }

            newlyOpened.Sort(StringComparer.Ordinal);
            for (int i = 0; i < newlyOpened.Count; i++)
            {
                events.Add("door_opened:" + newlyOpened[i]);
            }

            string[] canonicalOpenDoorIds = new string[openDoorIds.Count];
            openDoorIds.CopyTo(canonicalOpenDoorIds);
            Array.Sort(canonicalOpenDoorIds, StringComparer.Ordinal);
            next.OpenDoorIds = canonicalOpenDoorIds;
        }

        private static void AdvanceGuards(RoomDefinition room, GameState state, GameState next, List<string> events)
        {
            for (int i = 0; i < room.Guards.Length; i++)
            {
                next.GuardIndices[i] = NextGuardIndex(room.Guards[i], state.GuardIndices[i]);
                events.Add("guard_advanced:" + room.Guards[i].Id);
            }
        }

        private static StepResult Accepted(GameState state, List<string> events)
        {
            return new StepResult
            {
                State = state,
                Accepted = true,
                Reason = "accepted",
                Events = events.ToArray()
            };
        }

        private static StepResult Rejected(GameState state, string reason)
        {
            return new StepResult
            {
                State = CloneState(state),
                Accepted = false,
                Reason = reason,
                Events = new string[] { reason }
            };
        }

        private static bool TryGetTarget(GridPoint player, GameCommand command, out GridPoint target, out bool moved, out string error)
        {
            target = player;
            moved = true;
            error = null;
            switch (command)
            {
                case GameCommand.Up:
                    target = new GridPoint(player.X, player.Y - 1);
                    return true;
                case GameCommand.Down:
                    target = new GridPoint(player.X, player.Y + 1);
                    return true;
                case GameCommand.Left:
                    target = new GridPoint(player.X - 1, player.Y);
                    return true;
                case GameCommand.Right:
                    target = new GridPoint(player.X + 1, player.Y);
                    return true;
                case GameCommand.Wait:
                    moved = false;
                    return true;
                default:
                    error = "invalid_command";
                    return false;
            }
        }

        private static int NextGuardIndex(Guard guard, int currentIndex)
        {
            int next = currentIndex + 1;
            return next == guard.Patrol.Length ? 0 : next;
        }

        private static string FindGuardIdAt(RoomDefinition room, int[] guardIndices, GridPoint point)
        {
            for (int i = 0; i < room.Guards.Length; i++)
            {
                Guard guard = room.Guards[i];
                if (SamePoint(guard.Patrol[guardIndices[i]], point))
                {
                    return guard.Id;
                }
            }

            return null;
        }

        private static bool FindDoorExists(RoomDefinition room, string id)
        {
            for (int i = 0; i < room.Doors.Length; i++)
            {
                if (string.Equals(room.Doors[i].Id, id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FindDoorIdAt(RoomDefinition room, GridPoint point)
        {
            for (int i = 0; i < room.Doors.Length; i++)
            {
                if (SamePoint(room.Doors[i].Position, point))
                {
                    return room.Doors[i].Id;
                }
            }

            return null;
        }

        private static bool ContainsId(string[] ids, string id)
        {
            if (ids == null)
            {
                return false;
            }

            for (int i = 0; i < ids.Length; i++)
            {
                if (string.Equals(ids[i], id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsPoint(List<GridPoint> points, GridPoint point)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (SamePoint(points[i], point))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInside(RoomDefinition room, GridPoint point)
        {
            return point.X >= 0 && point.X < room.Width && point.Y >= 0 && point.Y < room.Height;
        }

        private static bool SamePoint(GridPoint left, GridPoint right)
        {
            return left.X == right.X && left.Y == right.Y;
        }

        private static bool IsFloor(RoomDefinition room, GridPoint point)
        {
            if (!IsInside(room, point) || room.Rows == null || point.Y >= room.Rows.Length || room.Rows[point.Y] == null || point.X >= room.Rows[point.Y].Length)
            {
                return false;
            }

            return room.Rows[point.Y][point.X] == '.';
        }

        private static bool IsKnownRunStatus(RunStatus status)
        {
            return status == RunStatus.Playing || status == RunStatus.Captured || status == RunStatus.Cleared;
        }

        private static void EnsureValidRoom(RoomDefinition room)
        {
            string[] errors = ValidateRoom(room);
            if (errors.Length > 0)
            {
                throw new ArgumentException("RoomDefinition is invalid: " + string.Join(",", errors), "room");
            }
        }

        private static void EnsureValidState(RoomDefinition room, GameState state)
        {
            string[] errors = ValidateState(room, state);
            if (errors.Length > 0)
            {
                throw new ArgumentException("GameState is invalid: " + string.Join(",", errors), "state");
            }
        }

        private static string[] CopyStrings(string[] source)
        {
            if (source == null)
            {
                return null;
            }

            string[] copy = new string[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static int[] CopyInts(int[] source)
        {
            if (source == null)
            {
                return null;
            }

            int[] copy = new int[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static void WriteRoom(StableFingerprintWriter writer, RoomDefinition room)
        {
            writer.WriteString("room-v1");
            writer.WriteString(room.Id);
            writer.WriteInt(room.Width);
            writer.WriteInt(room.Height);
            writer.WriteInt(room.Rows.Length);
            for (int y = 0; y < room.Rows.Length; y++)
            {
                writer.WriteString(room.Rows[y]);
            }

            writer.WritePoint(room.Start);
            writer.WritePoint(room.Exit);

            Door[] doors = CopyAndSortDoors(room.Doors);
            writer.WriteInt(doors.Length);
            for (int i = 0; i < doors.Length; i++)
            {
                writer.WriteString(doors[i].Id);
                writer.WritePoint(doors[i].Position);
            }

            Switch[] switches = CopyAndSortSwitches(room.Switches);
            writer.WriteInt(switches.Length);
            for (int i = 0; i < switches.Length; i++)
            {
                writer.WriteString(switches[i].Id);
                writer.WritePoint(switches[i].Position);
                WriteSortedIds(writer, switches[i].DoorIds);
            }

            writer.WriteInt(room.Guards.Length);
            for (int i = 0; i < room.Guards.Length; i++)
            {
                Guard guard = room.Guards[i];
                writer.WriteString(guard.Id);
                writer.WriteInt(guard.StartIndex);
                writer.WriteInt(guard.Patrol.Length);
                for (int pointIndex = 0; pointIndex < guard.Patrol.Length; pointIndex++)
                {
                    writer.WritePoint(guard.Patrol[pointIndex]);
                }
            }
        }

        private static void WriteSortedIds(StableFingerprintWriter writer, string[] ids)
        {
            string[] copy = CopyStrings(ids);
            Array.Sort(copy, StringComparer.Ordinal);
            writer.WriteInt(copy.Length);
            for (int i = 0; i < copy.Length; i++)
            {
                writer.WriteString(copy[i]);
            }
        }

        private static Door[] CopyAndSortDoors(Door[] doors)
        {
            Door[] copy = new Door[doors.Length];
            Array.Copy(doors, copy, doors.Length);
            Array.Sort(copy, CompareDoors);
            return copy;
        }

        private static Switch[] CopyAndSortSwitches(Switch[] switches)
        {
            Switch[] copy = new Switch[switches.Length];
            Array.Copy(switches, copy, switches.Length);
            Array.Sort(copy, CompareSwitches);
            return copy;
        }

        private static int CompareDoors(Door left, Door right)
        {
            return string.CompareOrdinal(left.Id, right.Id);
        }

        private static int CompareSwitches(Switch left, Switch right)
        {
            return string.CompareOrdinal(left.Id, right.Id);
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
            using (SHA256 sha256 = SHA256.Create())
            {
                digest = sha256.ComputeHash(bytes);
            }

            char[] hex = new char[digest.Length * 2];
            for (int i = 0; i < digest.Length; i++)
            {
                byte value = digest[i];
                hex[i * 2] = HexDigits[value >> 4];
                hex[(i * 2) + 1] = HexDigits[value & 0x0f];
            }

            return new string(hex);
        }
    }
}
