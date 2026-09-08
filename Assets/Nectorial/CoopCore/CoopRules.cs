using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nectorial.SlideEscape.Coop
{
    public static class CoopRules
    {
        public const int BoardSize = 8;

        public static string[] ValidateRoom(CoopRoomDefinition room)
        {
            var errors = new List<string>();
            if (room == null)
            {
                errors.Add("room_null");
                return errors.ToArray();
            }

            if (string.IsNullOrEmpty(room.Id)) errors.Add("room_id_missing");
            if (room.Width != BoardSize || room.Height != BoardSize) errors.Add("room_size_must_be_eight");
            if (string.IsNullOrEmpty(room.RulesVersion)) errors.Add("rules_version_missing");
            if (string.IsNullOrEmpty(room.ContentVersion)) errors.Add("content_version_missing");
            ValidateRows(room, errors);
            ValidatePoint(room, room.CircleStart, "circle_start", errors);
            ValidatePoint(room, room.DiamondStart, "diamond_start", errors);
            ValidatePoint(room, room.CircleGoal, "circle_goal", errors);
            ValidatePoint(room, room.DiamondGoal, "diamond_goal", errors);
            if (Same(room.CircleStart, room.DiamondStart)) errors.Add("starts_overlap");
            if (Same(room.CircleGoal, room.DiamondGoal)) errors.Add("goals_overlap");
            if (Same(room.CircleStart, room.CircleGoal) && Same(room.DiamondStart, room.DiamondGoal)) errors.Add("room_already_cleared_at_start");
            return errors.ToArray();
        }

        public static CoopState CreateInitialState(CoopRoomDefinition room)
        {
            EnsureValidRoom(room);
            return new CoopState
            {
                RoomId = room.Id,
                CirclePosition = room.CircleStart,
                DiamondPosition = room.DiamondStart,
                ActiveActor = CoopActor.Circle,
                AuthorityRevision = 0,
                LogicalActionCount = 0,
                Status = CoopRunStatus.Playing,
                PendingConsent = null
            };
        }

        public static string[] ValidateState(CoopRoomDefinition room, CoopState state)
        {
            var errors = new List<string>();
            if (state == null)
            {
                errors.Add("state_null");
                return errors.ToArray();
            }

            if (!string.Equals(state.RoomId, room.Id, StringComparison.Ordinal)) errors.Add("state_room_id_mismatch");
            if (state.AuthorityRevision < 0) errors.Add("state_revision_invalid");
            if (state.LogicalActionCount < 0) errors.Add("state_action_count_invalid");
            if (!IsActor(state.ActiveActor)) errors.Add("state_active_actor_invalid");
            if (!IsStatus(state.Status)) errors.Add("state_status_invalid");
            ValidatePoint(room, state.CirclePosition, "state_circle", errors);
            ValidatePoint(room, state.DiamondPosition, "state_diamond", errors);
            if (Same(state.CirclePosition, state.DiamondPosition)) errors.Add("state_positions_overlap");
            bool bothGoals = IsBothGoals(room, state);
            if (state.Status == CoopRunStatus.Cleared && !bothGoals) errors.Add("cleared_without_both_goals");
            if (state.Status == CoopRunStatus.Playing && bothGoals) errors.Add("playing_with_both_goals");
            if (state.PendingConsent != null)
            {
                if (string.IsNullOrEmpty(state.PendingConsent.RequestId)) errors.Add("pending_request_id_missing");
                if (!IsActor(state.PendingConsent.Requester)) errors.Add("pending_requester_invalid");
                if (state.PendingConsent.Kind != CoopConsentKind.Undo && state.PendingConsent.Kind != CoopConsentKind.Restart) errors.Add("pending_kind_invalid");
                if (state.PendingConsent.RequestedAtRevision < 0 || state.PendingConsent.RequestedAtRevision > state.AuthorityRevision) errors.Add("pending_revision_invalid");
            }
            return errors.ToArray();
        }

        public static bool TrySlide(CoopRoomDefinition room, CoopState state, CoopActor actor, GameCommand direction,
            out GridPoint destination, out int distance, out bool stoppedByPartner, out string reason)
        {
            destination = GetPosition(state, actor);
            distance = 0;
            stoppedByPartner = false;
            reason = null;
            if (!IsActor(actor)) { reason = "invalid_actor"; return false; }
            if (!IsDirection(direction)) { reason = "invalid_direction"; return false; }

            GridPoint partner = GetPosition(state, Opponent(actor));
            while (true)
            {
                GridPoint next;
                if (!TryNeighbor(destination, direction, out next) || !IsFloor(room, next)) break;
                if (Same(next, partner))
                {
                    stoppedByPartner = distance > 0;
                    break;
                }
                destination = next;
                distance++;
            }

            if (distance == 0)
            {
                reason = "blocked_zero";
                return false;
            }
            return true;
        }

        public static CoopState ApplyAction(CoopRoomDefinition room, CoopState state, CoopActor actor, GridPoint destination, bool isPass)
        {
            CoopState next = CloneState(state);
            if (!isPass) SetPosition(next, actor, destination);
            next.ActiveActor = Opponent(actor);
            next.LogicalActionCount = checked(state.LogicalActionCount + 1);
            next.AuthorityRevision = checked(state.AuthorityRevision + 1);
            next.PendingConsent = null;
            next.Status = IsBothGoals(room, next) ? CoopRunStatus.Cleared : CoopRunStatus.Playing;
            return next;
        }

        public static CoopState WithAuthorityRevision(CoopState state, long authorityRevision)
        {
            CoopState copy = CloneState(state);
            copy.AuthorityRevision = authorityRevision;
            return copy;
        }

        public static CoopActor Opponent(CoopActor actor)
        {
            return actor == CoopActor.Circle ? CoopActor.Diamond : CoopActor.Circle;
        }

        public static GridPoint GetPosition(CoopState state, CoopActor actor)
        {
            return actor == CoopActor.Circle ? state.CirclePosition : state.DiamondPosition;
        }

        public static void SetPosition(CoopState state, CoopActor actor, GridPoint point)
        {
            if (actor == CoopActor.Circle) state.CirclePosition = point;
            else state.DiamondPosition = point;
        }

        public static bool IsBothGoals(CoopRoomDefinition room, CoopState state)
        {
            return Same(state.CirclePosition, room.CircleGoal) && Same(state.DiamondPosition, room.DiamondGoal);
        }

        public static CoopState CloneState(CoopState source)
        {
            if (source == null) return null;
            return new CoopState
            {
                RoomId = source.RoomId,
                CirclePosition = source.CirclePosition,
                DiamondPosition = source.DiamondPosition,
                ActiveActor = source.ActiveActor,
                AuthorityRevision = source.AuthorityRevision,
                LogicalActionCount = source.LogicalActionCount,
                Status = source.Status,
                PendingConsent = ClonePending(source.PendingConsent)
            };
        }

        public static CoopRoomDefinition CloneRoom(CoopRoomDefinition source)
        {
            if (source == null) return null;
            string[] rows = null;
            if (source.Rows != null)
            {
                rows = new string[source.Rows.Length];
                Array.Copy(source.Rows, rows, source.Rows.Length);
            }
            return new CoopRoomDefinition
            {
                Id = source.Id,
                Width = source.Width,
                Height = source.Height,
                Rows = rows,
                CircleStart = source.CircleStart,
                DiamondStart = source.DiamondStart,
                CircleGoal = source.CircleGoal,
                DiamondGoal = source.DiamondGoal,
                RulesVersion = source.RulesVersion,
                ContentVersion = source.ContentVersion
            };
        }

        public static CoopCommand CloneCommand(CoopCommand source)
        {
            if (source == null) return null;
            return new CoopCommand
            {
                CommandId = source.CommandId,
                ExpectedRevision = source.ExpectedRevision,
                Seat = source.Seat,
                Kind = source.Kind,
                Direction = source.Direction,
                RequestId = source.RequestId,
                Approve = source.Approve,
                Expression = source.Expression
            };
        }

        public static CoopAttempt CloneAttempt(CoopAttempt source)
        {
            if (source == null) return null;
            return new CoopAttempt
            {
                Command = CloneCommand(source.Command),
                Accepted = source.Accepted,
                Idempotent = source.Idempotent,
                Reason = source.Reason,
                StateFingerprint = source.StateFingerprint
            };
        }

        public static CoopEvent[] CloneEvents(CoopEvent[] source)
        {
            if (source == null) return new CoopEvent[0];
            var copy = new CoopEvent[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                CoopEvent item = source[index];
                copy[index] = item == null ? null : new CoopEvent { Type = item.Type, Actor = item.Actor, Detail = item.Detail };
            }
            return copy;
        }

        public static string RoomFingerprint(CoopRoomDefinition room)
        {
            EnsureValidRoom(room);
            var builder = new StringBuilder();
            Append(builder, "coop-room-v1");
            Append(builder, room.Id);
            Append(builder, room.RulesVersion);
            Append(builder, room.ContentVersion);
            Append(builder, room.Width.ToString(CultureInfo.InvariantCulture));
            Append(builder, room.Height.ToString(CultureInfo.InvariantCulture));
            for (int index = 0; index < room.Rows.Length; index++) Append(builder, room.Rows[index]);
            AppendPoint(builder, room.CircleStart);
            AppendPoint(builder, room.DiamondStart);
            AppendPoint(builder, room.CircleGoal);
            AppendPoint(builder, room.DiamondGoal);
            return Sha(builder.ToString());
        }

        public static string StateFingerprint(CoopRoomDefinition room, CoopState state)
        {
            EnsureValidRoom(room);
            EnsureValidState(room, state);
            var builder = new StringBuilder();
            Append(builder, RoomFingerprint(room));
            Append(builder, "coop-state-v1");
            AppendPoint(builder, state.CirclePosition);
            AppendPoint(builder, state.DiamondPosition);
            Append(builder, ((int)state.ActiveActor).ToString(CultureInfo.InvariantCulture));
            Append(builder, state.AuthorityRevision.ToString(CultureInfo.InvariantCulture));
            Append(builder, state.LogicalActionCount.ToString(CultureInfo.InvariantCulture));
            Append(builder, ((int)state.Status).ToString(CultureInfo.InvariantCulture));
            if (state.PendingConsent == null) Append(builder, "no-pending");
            else
            {
                Append(builder, state.PendingConsent.RequestId);
                Append(builder, ((int)state.PendingConsent.Kind).ToString(CultureInfo.InvariantCulture));
                Append(builder, ((int)state.PendingConsent.Requester).ToString(CultureInfo.InvariantCulture));
                Append(builder, state.PendingConsent.RequestedAtRevision.ToString(CultureInfo.InvariantCulture));
            }
            return Sha(builder.ToString());
        }

        public static bool StatesEqual(CoopState left, CoopState right)
        {
            if (left == null || right == null) return left == right;
            return string.Equals(left.RoomId, right.RoomId, StringComparison.Ordinal)
                && Same(left.CirclePosition, right.CirclePosition)
                && Same(left.DiamondPosition, right.DiamondPosition)
                && left.ActiveActor == right.ActiveActor
                && left.AuthorityRevision == right.AuthorityRevision
                && left.LogicalActionCount == right.LogicalActionCount
                && left.Status == right.Status
                && PendingEqual(left.PendingConsent, right.PendingConsent);
        }

        public static bool Same(GridPoint left, GridPoint right)
        {
            return left.X == right.X && left.Y == right.Y;
        }

        private static CoopPendingConsent ClonePending(CoopPendingConsent source)
        {
            if (source == null) return null;
            return new CoopPendingConsent
            {
                RequestId = source.RequestId,
                Kind = source.Kind,
                Requester = source.Requester,
                RequestedAtRevision = source.RequestedAtRevision
            };
        }

        private static bool PendingEqual(CoopPendingConsent left, CoopPendingConsent right)
        {
            if (left == null || right == null) return left == right;
            return string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
                && left.Kind == right.Kind
                && left.Requester == right.Requester
                && left.RequestedAtRevision == right.RequestedAtRevision;
        }

        private static void ValidateRows(CoopRoomDefinition room, List<string> errors)
        {
            if (room.Rows == null || room.Rows.Length != BoardSize)
            {
                errors.Add("room_rows_invalid");
                return;
            }
            for (int y = 0; y < room.Rows.Length; y++)
            {
                string row = room.Rows[y];
                if (row == null || row.Length != BoardSize) { errors.Add("room_row_invalid:" + y.ToString(CultureInfo.InvariantCulture)); continue; }
                for (int x = 0; x < row.Length; x++) if (row[x] != '.' && row[x] != '#') errors.Add("room_tile_invalid:" + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void ValidatePoint(CoopRoomDefinition room, GridPoint point, string label, List<string> errors)
        {
            if (!IsInside(point)) { errors.Add(label + "_out_of_bounds"); return; }
            if (!IsFloor(room, point)) errors.Add(label + "_not_floor");
        }

        private static bool TryNeighbor(GridPoint point, GameCommand direction, out GridPoint neighbor)
        {
            neighbor = point;
            switch (direction)
            {
                case GameCommand.Up: neighbor.Y--; return neighbor.Y >= 0;
                case GameCommand.Down: neighbor.Y++; return neighbor.Y < BoardSize;
                case GameCommand.Left: neighbor.X--; return neighbor.X >= 0;
                case GameCommand.Right: neighbor.X++; return neighbor.X < BoardSize;
                default: return false;
            }
        }

        private static bool IsFloor(CoopRoomDefinition room, GridPoint point)
        {
            return IsInside(point) && room.Rows != null && point.Y < room.Rows.Length && room.Rows[point.Y] != null && point.X < room.Rows[point.Y].Length && room.Rows[point.Y][point.X] == '.';
        }

        private static bool IsInside(GridPoint point)
        {
            return point.X >= 0 && point.X < BoardSize && point.Y >= 0 && point.Y < BoardSize;
        }

        private static bool IsActor(CoopActor actor)
        {
            return actor == CoopActor.Circle || actor == CoopActor.Diamond;
        }

        private static bool IsStatus(CoopRunStatus status)
        {
            return status == CoopRunStatus.Playing || status == CoopRunStatus.Cleared || status == CoopRunStatus.Paused;
        }

        private static bool IsDirection(GameCommand direction)
        {
            return direction == GameCommand.Up || direction == GameCommand.Down || direction == GameCommand.Left || direction == GameCommand.Right;
        }

        private static void EnsureValidRoom(CoopRoomDefinition room)
        {
            string[] errors = ValidateRoom(room);
            if (errors.Length > 0) throw new ArgumentException("Coop room invalid: " + string.Join(",", errors), "room");
        }

        private static void EnsureValidState(CoopRoomDefinition room, CoopState state)
        {
            string[] errors = ValidateState(room, state);
            if (errors.Length > 0) throw new ArgumentException("Coop state invalid: " + string.Join(",", errors), "state");
        }

        private static void Append(StringBuilder builder, string value)
        {
            builder.Append(value ?? string.Empty).Append('|');
        }

        private static void AppendPoint(StringBuilder builder, GridPoint point)
        {
            Append(builder, point.X.ToString(CultureInfo.InvariantCulture));
            Append(builder, point.Y.ToString(CultureInfo.InvariantCulture));
        }

        private static string Sha(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                byte[] digest = sha.ComputeHash(bytes);
                var builder = new StringBuilder(digest.Length * 2);
                for (int index = 0; index < digest.Length; index++) builder.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }
    }
}
