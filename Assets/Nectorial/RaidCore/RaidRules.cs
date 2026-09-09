using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nectorial.SlideEscape;

namespace Nectorial.SlideEscape.Raid
{
    public static class RaidRules
    {
        public static string[] ValidateArena(RaidArenaDefinition arena)
        {
            var errors = new List<string>();
            if (arena == null) { errors.Add("arena_null"); return errors.ToArray(); }
            if (string.IsNullOrEmpty(arena.Id)) errors.Add("arena_id_missing");
            if (arena.Width < 4 || arena.Height < 4) errors.Add("arena_size_invalid");
            if (string.IsNullOrEmpty(arena.RulesVersion)) errors.Add("rules_version_missing");
            if (string.IsNullOrEmpty(arena.ContentVersion)) errors.Add("content_version_missing");
            ValidateRows(arena, errors);
            if (!IsFloor(arena, arena.PlayerStart)) errors.Add("player_start_invalid");
            ValidateRing(arena, errors);
            ValidateTailsAndItems(arena, errors);
            if (arena.InitialShieldCharges < 0) errors.Add("initial_shield_invalid");
            if (arena.MagnetDurationSteps < 1) errors.Add("magnet_duration_invalid");
            if (arena.MagnetRadius < 1) errors.Add("magnet_radius_invalid");
            if (arena.SlowDurationSteps < 1) errors.Add("slow_duration_invalid");
            if (errors.Count == 0 && Contains(SnakeBody(arena, arena.SnakeStartHeadIndex), arena.PlayerStart)) errors.Add("player_start_on_snake");
            return errors.ToArray();
        }

        public static RaidState CreateInitialState(RaidArenaDefinition arena)
        {
            EnsureValidArena(arena);
            return new RaidState
            {
                ArenaId = arena.Id,
                PlayerPosition = arena.PlayerStart,
                SnakeHeadIndex = arena.SnakeStartHeadIndex,
                CollectedTailIds = new string[0],
                CollectedItemIds = new string[0],
                ShieldCharges = arena.InitialShieldCharges,
                MagnetStepsRemaining = 0,
                SlowStepsRemaining = 0,
                Actions = 0,
                Hits = 0,
                Status = RaidRunStatus.Playing
            };
        }

        public static string[] ValidateState(RaidArenaDefinition arena, RaidState state)
        {
            var errors = new List<string>();
            if (state == null) { errors.Add("state_null"); return errors.ToArray(); }
            if (!string.Equals(state.ArenaId, arena.Id, StringComparison.Ordinal)) errors.Add("state_arena_id_mismatch");
            if (!IsFloor(arena, state.PlayerPosition)) errors.Add("state_player_invalid");
            if (state.SnakeHeadIndex < 0 || state.SnakeHeadIndex >= arena.SnakeRing.Length) errors.Add("state_snake_phase_invalid");
            if (state.CollectedTailIds == null || state.CollectedItemIds == null) errors.Add("state_collections_null");
            ValidateCollectedIds(arena.TailFragments, state.CollectedTailIds, "tail", errors);
            ValidateCollectedItemIds(arena.Items, state.CollectedItemIds, errors);
            if (state.ShieldCharges < 0) errors.Add("state_shield_invalid");
            if (state.MagnetStepsRemaining < 0 || state.SlowStepsRemaining < 0) errors.Add("state_buff_clock_invalid");
            if (state.Actions < 0 || state.Hits < 0) errors.Add("state_counters_invalid");
            if (state.Status != RaidRunStatus.Playing && state.Status != RaidRunStatus.Armed && state.Status != RaidRunStatus.Cleared && state.Status != RaidRunStatus.Failed) errors.Add("state_status_invalid");
            int collectedTailCount = state.CollectedTailIds == null ? 0 : state.CollectedTailIds.Length;
            if (state.Status == RaidRunStatus.Playing && collectedTailCount >= arena.TailFragments.Length) errors.Add("playing_with_all_tails");
            if ((state.Status == RaidRunStatus.Armed || state.Status == RaidRunStatus.Cleared) && collectedTailCount != arena.TailFragments.Length) errors.Add("armed_or_cleared_tail_count_invalid");
            if ((state.Status == RaidRunStatus.Playing || state.Status == RaidRunStatus.Armed) && state.SnakeHeadIndex >= 0 && state.SnakeHeadIndex < arena.SnakeRing.Length && Contains(SnakeBody(arena, state.SnakeHeadIndex), state.PlayerPosition)) errors.Add("live_player_on_snake");
            return errors.ToArray();
        }

        public static RaidDispatchResult Step(RaidArenaDefinition arena, RaidState source, GameCommand direction)
        {
            EnsureValidArena(arena);
            EnsureValidState(arena, source);
            if (source.Status == RaidRunStatus.Cleared || source.Status == RaidRunStatus.Failed)
                return Rejected(source, "terminal_state");
            if (!IsDirection(direction)) return Rejected(source, "invalid_direction");

            GridPoint[] path = BuildSlidePath(arena, source.PlayerPosition, direction);
            if (path.Length == 0) return Rejected(source, "blocked_zero");

            RaidState state = CloneState(source);
            bool startedArmed = source.Status == RaidRunStatus.Armed;
            bool actionCancelled = false;
            var frames = new List<RaidFrame>();
            var events = new List<RaidEvent>();
            var safePath = new List<GridPoint> { source.PlayerPosition };

            for (int index = 0; index < path.Length; index++)
            {
                GridPoint playerBefore = state.PlayerPosition;
                GridPoint attemptedPlayerAfter = path[index];
                GridPoint[] snakeBefore = SnakeBody(arena, state.SnakeHeadIndex);
                bool slowWasActive = state.SlowStepsRemaining > 0;
                bool magnetWasActive = state.MagnetStepsRemaining > 0;
                bool snakeMoved = !slowWasActive;
                if (slowWasActive) state.SlowStepsRemaining--;
                int nextHeadIndex = snakeMoved ? NextRingIndex(arena, state.SnakeHeadIndex) : state.SnakeHeadIndex;
                GridPoint[] snakeAfter = SnakeBody(arena, nextHeadIndex);
                state.SnakeHeadIndex = nextHeadIndex;
                RaidCollisionKind collision = DetectCollision(playerBefore, attemptedPlayerAfter, snakeBefore, snakeAfter, snakeMoved);
                var frame = new RaidFrame
                {
                    Microstep = index + 1,
                    PlayerBefore = playerBefore,
                    AttemptedPlayerAfter = attemptedPlayerAfter,
                    PlayerAfter = attemptedPlayerAfter,
                    SnakeBefore = ClonePoints(snakeBefore),
                    SnakeAfter = ClonePoints(snakeAfter),
                    SnakeMoved = snakeMoved,
                    Collision = collision,
                    CollectedTailIds = new string[0],
                    CollectedItemIds = new string[0],
                    MagnetCollectedTailIds = new string[0],
                    Outcome = RaidFrameOutcome.Advanced
                };

                if (collision != RaidCollisionKind.None)
                {
                    if (startedArmed)
                    {
                        state.PlayerPosition = attemptedPlayerAfter;
                        state.Status = RaidRunStatus.Cleared;
                        frame.Outcome = RaidFrameOutcome.Cleared;
                        events.Add(Event("raid_cleared", collision.ToString()));
                        FinishFrame(state, frame);
                        frames.Add(frame);
                        actionCancelled = true;
                        break;
                    }

                    if (magnetWasActive && state.MagnetStepsRemaining > 0) state.MagnetStepsRemaining--;
                    state.Hits++;
                    if (state.ShieldCharges > 0)
                    {
                        state.ShieldCharges--;
                        GridPoint recovery;
                        if (TryFindRecovery(arena, playerBefore, safePath, snakeAfter, out recovery))
                        {
                            state.PlayerPosition = recovery;
                            frame.PlayerAfter = recovery;
                            frame.Outcome = RaidFrameOutcome.Shielded;
                            events.Add(Event("shield_recovered", collision.ToString()));
                        }
                        else
                        {
                            state.PlayerPosition = attemptedPlayerAfter;
                            state.Status = RaidRunStatus.Failed;
                            frame.Outcome = RaidFrameOutcome.Failed;
                            events.Add(Event("shield_recovery_missing", collision.ToString()));
                        }
                    }
                    else
                    {
                        state.PlayerPosition = attemptedPlayerAfter;
                        state.Status = RaidRunStatus.Failed;
                        frame.Outcome = RaidFrameOutcome.Failed;
                        events.Add(Event("raid_failed", collision.ToString()));
                    }
                    FinishFrame(state, frame);
                    frames.Add(frame);
                    actionCancelled = true;
                    break;
                }

                state.PlayerPosition = attemptedPlayerAfter;
                safePath.Add(attemptedPlayerAfter);
                var directTails = new List<string>();
                var directItems = new List<string>();
                CollectDirectTail(arena, state, attemptedPlayerAfter, directTails, events);
                CollectDirectItem(arena, state, attemptedPlayerAfter, directItems, events);

                var magnetTails = new List<string>();
                if (state.MagnetStepsRemaining > 0)
                {
                    CollectMagnetTails(arena, state, attemptedPlayerAfter, snakeAfter, magnetTails, events);
                    state.MagnetStepsRemaining--;
                }
                frame.CollectedTailIds = directTails.ToArray();
                frame.CollectedItemIds = directItems.ToArray();
                frame.MagnetCollectedTailIds = magnetTails.ToArray();
                FinishFrame(state, frame);
                frames.Add(frame);
            }

            state.Actions = checked(state.Actions + 1);
            if (state.Status == RaidRunStatus.Playing && HasAllTails(arena, state))
            {
                state.Status = RaidRunStatus.Armed;
                events.Add(Event("raid_armed", "action_end"));
            }
            string[] validation = ValidateState(arena, state);
            if (validation.Length > 0) throw new InvalidOperationException("Raid result invalid: " + string.Join(",", validation));
            return new RaidDispatchResult
            {
                Accepted = true,
                Idempotent = false,
                Reason = state.Status == RaidRunStatus.Cleared ? "cleared" : (state.Status == RaidRunStatus.Failed ? "failed" : "accepted"),
                State = CloneState(state),
                Frames = CloneFrames(frames.ToArray()),
                Events = CloneEvents(events.ToArray()),
                ActionCompleted = true,
                ActionCancelled = actionCancelled
            };
        }

        public static RaidArenaDefinition CloneArena(RaidArenaDefinition source)
        {
            if (source == null) return null;
            var clone = new RaidArenaDefinition
            {
                Id = source.Id,
                Width = source.Width,
                Height = source.Height,
                Rows = CloneStrings(source.Rows),
                PlayerStart = source.PlayerStart,
                SnakeRing = ClonePoints(source.SnakeRing),
                SnakeStartHeadIndex = source.SnakeStartHeadIndex,
                SnakeBodyLength = source.SnakeBodyLength,
                InitialShieldCharges = source.InitialShieldCharges,
                MagnetDurationSteps = source.MagnetDurationSteps,
                MagnetRadius = source.MagnetRadius,
                SlowDurationSteps = source.SlowDurationSteps,
                RulesVersion = source.RulesVersion,
                ContentVersion = source.ContentVersion
            };
            clone.TailFragments = CloneTails(source.TailFragments);
            clone.Items = CloneItems(source.Items);
            return clone;
        }

        public static RaidState CloneState(RaidState source)
        {
            if (source == null) return null;
            return new RaidState
            {
                ArenaId = source.ArenaId,
                PlayerPosition = source.PlayerPosition,
                SnakeHeadIndex = source.SnakeHeadIndex,
                CollectedTailIds = CloneStrings(source.CollectedTailIds),
                CollectedItemIds = CloneStrings(source.CollectedItemIds),
                ShieldCharges = source.ShieldCharges,
                MagnetStepsRemaining = source.MagnetStepsRemaining,
                SlowStepsRemaining = source.SlowStepsRemaining,
                Actions = source.Actions,
                Hits = source.Hits,
                Status = source.Status
            };
        }

        public static RaidMove CloneMove(RaidMove source)
        {
            return source == null ? null : new RaidMove { CommandId = source.CommandId, Direction = source.Direction };
        }

        public static RaidMove[] CloneMoves(RaidMove[] source)
        {
            if (source == null) return null;
            var clone = new RaidMove[source.Length];
            for (int index = 0; index < source.Length; index++) clone[index] = CloneMove(source[index]);
            return clone;
        }

        public static RaidFrame[] CloneFrames(RaidFrame[] source)
        {
            if (source == null) return null;
            var clone = new RaidFrame[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                RaidFrame item = source[index];
                clone[index] = item == null ? null : new RaidFrame
                {
                    Microstep = item.Microstep,
                    PlayerBefore = item.PlayerBefore,
                    AttemptedPlayerAfter = item.AttemptedPlayerAfter,
                    PlayerAfter = item.PlayerAfter,
                    SnakeBefore = ClonePoints(item.SnakeBefore),
                    SnakeAfter = ClonePoints(item.SnakeAfter),
                    SnakeMoved = item.SnakeMoved,
                    Collision = item.Collision,
                    CollectedTailIds = CloneStrings(item.CollectedTailIds),
                    CollectedItemIds = CloneStrings(item.CollectedItemIds),
                    MagnetCollectedTailIds = CloneStrings(item.MagnetCollectedTailIds),
                    ShieldCharges = item.ShieldCharges,
                    MagnetStepsRemaining = item.MagnetStepsRemaining,
                    SlowStepsRemaining = item.SlowStepsRemaining,
                    Outcome = item.Outcome
                };
            }
            return clone;
        }

        public static RaidEvent[] CloneEvents(RaidEvent[] source)
        {
            if (source == null) return null;
            var clone = new RaidEvent[source.Length];
            for (int index = 0; index < source.Length; index++) clone[index] = source[index] == null ? null : Event(source[index].Type, source[index].Detail);
            return clone;
        }

        public static bool StatesEqual(RaidState left, RaidState right)
        {
            if (left == null || right == null) return left == right;
            return string.Equals(left.ArenaId, right.ArenaId, StringComparison.Ordinal) && Same(left.PlayerPosition, right.PlayerPosition) &&
                left.SnakeHeadIndex == right.SnakeHeadIndex && SameStrings(left.CollectedTailIds, right.CollectedTailIds) &&
                SameStrings(left.CollectedItemIds, right.CollectedItemIds) && left.ShieldCharges == right.ShieldCharges &&
                left.MagnetStepsRemaining == right.MagnetStepsRemaining && left.SlowStepsRemaining == right.SlowStepsRemaining &&
                left.Actions == right.Actions && left.Hits == right.Hits && left.Status == right.Status;
        }

        public static string ArenaFingerprint(RaidArenaDefinition arena)
        {
            EnsureValidArena(arena);
            var builder = new StringBuilder();
            Append(builder, "raid-arena-v1");
            Append(builder, arena.Id); Append(builder, arena.Width); Append(builder, arena.Height);
            for (int index = 0; index < arena.Rows.Length; index++) Append(builder, arena.Rows[index]);
            AppendPoint(builder, arena.PlayerStart);
            for (int index = 0; index < arena.SnakeRing.Length; index++) AppendPoint(builder, arena.SnakeRing[index]);
            Append(builder, arena.SnakeStartHeadIndex); Append(builder, arena.SnakeBodyLength);
            for (int index = 0; index < arena.TailFragments.Length; index++) { Append(builder, arena.TailFragments[index].Id); AppendPoint(builder, arena.TailFragments[index].Position); }
            for (int index = 0; index < arena.Items.Length; index++) { Append(builder, arena.Items[index].Id); Append(builder, (int)arena.Items[index].Kind); AppendPoint(builder, arena.Items[index].Position); }
            Append(builder, arena.InitialShieldCharges); Append(builder, arena.MagnetDurationSteps); Append(builder, arena.MagnetRadius); Append(builder, arena.SlowDurationSteps);
            Append(builder, arena.RulesVersion); Append(builder, arena.ContentVersion);
            return Hash(builder.ToString());
        }

        public static string StateFingerprint(RaidArenaDefinition arena, RaidState state)
        {
            EnsureValidArena(arena); EnsureValidState(arena, state);
            var builder = new StringBuilder();
            Append(builder, ArenaFingerprint(arena)); Append(builder, "raid-state-v1");
            Append(builder, state.ArenaId); AppendPoint(builder, state.PlayerPosition); Append(builder, state.SnakeHeadIndex);
            AppendSorted(builder, state.CollectedTailIds); AppendSorted(builder, state.CollectedItemIds);
            Append(builder, state.ShieldCharges); Append(builder, state.MagnetStepsRemaining); Append(builder, state.SlowStepsRemaining);
            Append(builder, state.Actions); Append(builder, state.Hits); Append(builder, (int)state.Status);
            return Hash(builder.ToString());
        }

        public static string SolverKey(RaidArenaDefinition arena, RaidState state)
        {
            EnsureValidState(arena, state);
            var builder = new StringBuilder();
            AppendPoint(builder, state.PlayerPosition); Append(builder, state.SnakeHeadIndex); AppendSorted(builder, state.CollectedTailIds); AppendSorted(builder, state.CollectedItemIds);
            Append(builder, state.ShieldCharges); Append(builder, state.MagnetStepsRemaining); Append(builder, state.SlowStepsRemaining); Append(builder, (int)state.Status);
            return builder.ToString();
        }

        public static GridPoint[] SnakeBody(RaidArenaDefinition arena, int headIndex)
        {
            var body = new GridPoint[arena.SnakeBodyLength];
            for (int index = 0; index < body.Length; index++) body[index] = arena.SnakeRing[PositiveModulo(headIndex - index, arena.SnakeRing.Length)];
            return body;
        }

        private static RaidDispatchResult Rejected(RaidState state, string reason)
        {
            return new RaidDispatchResult { Accepted = false, Idempotent = false, Reason = reason, State = CloneState(state), Frames = new RaidFrame[0], Events = new RaidEvent[0], ActionCompleted = false, ActionCancelled = false };
        }

        private static void FinishFrame(RaidState state, RaidFrame frame)
        {
            frame.ShieldCharges = state.ShieldCharges;
            frame.MagnetStepsRemaining = state.MagnetStepsRemaining;
            frame.SlowStepsRemaining = state.SlowStepsRemaining;
        }

        private static RaidCollisionKind DetectCollision(GridPoint playerBefore, GridPoint playerAfter, GridPoint[] snakeBefore, GridPoint[] snakeAfter, bool snakeMoved)
        {
            if (snakeMoved)
            {
                for (int index = 0; index < snakeBefore.Length; index++)
                    if (Same(playerBefore, snakeAfter[index]) && Same(playerAfter, snakeBefore[index])) return RaidCollisionKind.EdgeSwap;
                for (int index = 0; index < snakeAfter.Length; index++)
                    if (Same(playerAfter, snakeAfter[index]) && !Same(playerAfter, snakeBefore[index])) return RaidCollisionKind.SharedDestination;
            }
            return Contains(snakeAfter, playerAfter) ? RaidCollisionKind.Body : RaidCollisionKind.None;
        }

        private static bool TryFindRecovery(RaidArenaDefinition arena, GridPoint playerBefore, List<GridPoint> safePath, GridPoint[] snakeAfter, out GridPoint recovery)
        {
            for (int index = safePath.Count - 1; index >= 0; index--)
            {
                GridPoint candidate = safePath[index];
                if (IsFloor(arena, candidate) && !Contains(snakeAfter, candidate)) { recovery = candidate; return true; }
            }
            int bestDistance = int.MaxValue;
            GridPoint best = default(GridPoint);
            bool found = false;
            for (int y = 0; y < arena.Height; y++)
            {
                for (int x = 0; x < arena.Width; x++)
                {
                    var candidate = new GridPoint(x, y);
                    if (!IsFloor(arena, candidate) || Contains(snakeAfter, candidate)) continue;
                    int distance = Math.Abs(candidate.X - playerBefore.X) + Math.Abs(candidate.Y - playerBefore.Y);
                    if (!found || distance < bestDistance || (distance == bestDistance && (candidate.Y < best.Y || (candidate.Y == best.Y && candidate.X < best.X))))
                    {
                        best = candidate; bestDistance = distance; found = true;
                    }
                }
            }
            recovery = best;
            return found;
        }

        private static void CollectDirectTail(RaidArenaDefinition arena, RaidState state, GridPoint position, List<string> collected, List<RaidEvent> events)
        {
            for (int index = 0; index < arena.TailFragments.Length; index++)
            {
                RaidTailDefinition tail = arena.TailFragments[index];
                if (Same(tail.Position, position) && !Contains(state.CollectedTailIds, tail.Id))
                {
                    state.CollectedTailIds = Append(state.CollectedTailIds, tail.Id);
                    collected.Add(tail.Id); events.Add(Event("tail_collected", tail.Id));
                }
            }
        }

        private static void CollectDirectItem(RaidArenaDefinition arena, RaidState state, GridPoint position, List<string> collected, List<RaidEvent> events)
        {
            for (int index = 0; index < arena.Items.Length; index++)
            {
                RaidItemDefinition item = arena.Items[index];
                if (!Same(item.Position, position) || Contains(state.CollectedItemIds, item.Id)) continue;
                state.CollectedItemIds = Append(state.CollectedItemIds, item.Id);
                collected.Add(item.Id);
                if (item.Kind == RaidItemKind.Shield) state.ShieldCharges = Math.Max(state.ShieldCharges, 1);
                else if (item.Kind == RaidItemKind.Magnet) state.MagnetStepsRemaining = Math.Max(state.MagnetStepsRemaining, arena.MagnetDurationSteps);
                else if (item.Kind == RaidItemKind.Slow) state.SlowStepsRemaining = Math.Max(state.SlowStepsRemaining, arena.SlowDurationSteps);
                events.Add(Event("item_collected", item.Kind + ":" + item.Id));
            }
        }

        private static void CollectMagnetTails(RaidArenaDefinition arena, RaidState state, GridPoint player, GridPoint[] snakeAfter, List<string> collected, List<RaidEvent> events)
        {
            for (int index = 0; index < arena.TailFragments.Length; index++)
            {
                RaidTailDefinition tail = arena.TailFragments[index];
                int distance = Math.Abs(tail.Position.X - player.X) + Math.Abs(tail.Position.Y - player.Y);
                if (distance > arena.MagnetRadius || Contains(state.CollectedTailIds, tail.Id) || Contains(snakeAfter, tail.Position)) continue;
                state.CollectedTailIds = Append(state.CollectedTailIds, tail.Id);
                collected.Add(tail.Id); events.Add(Event("tail_magnet_collected", tail.Id));
            }
        }

        private static bool HasAllTails(RaidArenaDefinition arena, RaidState state)
        {
            return state.CollectedTailIds.Length == arena.TailFragments.Length;
        }

        private static GridPoint[] BuildSlidePath(RaidArenaDefinition arena, GridPoint start, GameCommand direction)
        {
            var path = new List<GridPoint>();
            GridPoint current = start;
            while (true)
            {
                GridPoint next;
                if (!TryNeighbor(current, direction, out next) || !IsFloor(arena, next)) break;
                path.Add(next); current = next;
            }
            return path.ToArray();
        }

        private static int NextRingIndex(RaidArenaDefinition arena, int index)
        {
            return (index + 1) % arena.SnakeRing.Length;
        }

        private static bool TryNeighbor(GridPoint source, GameCommand direction, out GridPoint target)
        {
            target = source;
            if (direction == GameCommand.Up) { target.Y--; return true; }
            if (direction == GameCommand.Down) { target.Y++; return true; }
            if (direction == GameCommand.Left) { target.X--; return true; }
            if (direction == GameCommand.Right) { target.X++; return true; }
            return false;
        }

        private static bool IsDirection(GameCommand direction)
        {
            return direction == GameCommand.Up || direction == GameCommand.Down || direction == GameCommand.Left || direction == GameCommand.Right;
        }

        private static void ValidateRows(RaidArenaDefinition arena, List<string> errors)
        {
            if (arena.Rows == null || arena.Rows.Length != arena.Height) { errors.Add("rows_height_invalid"); return; }
            for (int y = 0; y < arena.Rows.Length; y++)
            {
                string row = arena.Rows[y];
                if (row == null || row.Length != arena.Width) { errors.Add("row_width_invalid:" + y.ToString(CultureInfo.InvariantCulture)); continue; }
                for (int x = 0; x < row.Length; x++) if (row[x] != '#' && row[x] != '.') errors.Add("row_tile_invalid:" + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void ValidateRing(RaidArenaDefinition arena, List<string> errors)
        {
            if (arena.SnakeRing == null || arena.SnakeRing.Length < 4) { errors.Add("snake_ring_invalid"); return; }
            if (arena.SnakeBodyLength < 1 || arena.SnakeBodyLength >= arena.SnakeRing.Length) errors.Add("snake_body_length_invalid");
            if (arena.SnakeStartHeadIndex < 0 || arena.SnakeStartHeadIndex >= arena.SnakeRing.Length) errors.Add("snake_start_index_invalid");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < arena.SnakeRing.Length; index++)
            {
                GridPoint point = arena.SnakeRing[index];
                if (!IsFloor(arena, point)) errors.Add("snake_ring_floor_invalid:" + index.ToString(CultureInfo.InvariantCulture));
                if (!seen.Add(PointKey(point))) errors.Add("snake_ring_duplicate:" + index.ToString(CultureInfo.InvariantCulture));
                GridPoint next = arena.SnakeRing[(index + 1) % arena.SnakeRing.Length];
                if (Math.Abs(point.X - next.X) + Math.Abs(point.Y - next.Y) != 1) errors.Add("snake_ring_not_closed_step:" + index.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void ValidateTailsAndItems(RaidArenaDefinition arena, List<string> errors)
        {
            if (arena.TailFragments == null || arena.TailFragments.Length != 3) { errors.Add("tail_count_must_be_three"); return; }
            if (arena.Items == null) { errors.Add("items_null"); return; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var occupied = new HashSet<string>(StringComparer.Ordinal) { PointKey(arena.PlayerStart) };
            for (int index = 0; index < arena.TailFragments.Length; index++)
            {
                RaidTailDefinition tail = arena.TailFragments[index];
                if (tail == null || string.IsNullOrEmpty(tail.Id) || !ids.Add("tail:" + (tail == null ? string.Empty : tail.Id))) { errors.Add("tail_id_invalid:" + index.ToString(CultureInfo.InvariantCulture)); continue; }
                if (!IsFloor(arena, tail.Position) || !occupied.Add(PointKey(tail.Position))) errors.Add("tail_position_invalid:" + tail.Id);
            }
            var kinds = new HashSet<RaidItemKind>();
            for (int index = 0; index < arena.Items.Length; index++)
            {
                RaidItemDefinition item = arena.Items[index];
                if (item == null || string.IsNullOrEmpty(item.Id) || !ids.Add("item:" + (item == null ? string.Empty : item.Id))) { errors.Add("item_id_invalid:" + index.ToString(CultureInfo.InvariantCulture)); continue; }
                if (item.Kind != RaidItemKind.Shield && item.Kind != RaidItemKind.Magnet && item.Kind != RaidItemKind.Slow) errors.Add("item_kind_invalid:" + item.Id);
                if (!kinds.Add(item.Kind)) errors.Add("item_kind_duplicate:" + item.Kind);
                if (!IsFloor(arena, item.Position) || !occupied.Add(PointKey(item.Position))) errors.Add("item_position_invalid:" + item.Id);
            }
            if (!kinds.Contains(RaidItemKind.Shield) || !kinds.Contains(RaidItemKind.Magnet) || !kinds.Contains(RaidItemKind.Slow)) errors.Add("required_item_kind_missing");
        }

        private static void ValidateCollectedIds(RaidTailDefinition[] definitions, string[] ids, string label, List<string> errors)
        {
            if (ids == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < ids.Length; index++)
            {
                string id = ids[index];
                if (string.IsNullOrEmpty(id) || !seen.Add(id) || !ContainsTail(definitions, id)) errors.Add("state_" + label + "_ids_invalid");
            }
        }

        private static void ValidateCollectedItemIds(RaidItemDefinition[] definitions, string[] ids, List<string> errors)
        {
            if (ids == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < ids.Length; index++)
            {
                string id = ids[index];
                if (string.IsNullOrEmpty(id) || !seen.Add(id) || !ContainsItem(definitions, id)) errors.Add("state_item_ids_invalid");
            }
        }

        private static bool ContainsTail(RaidTailDefinition[] tails, string id)
        {
            if (tails == null) return false;
            for (int index = 0; index < tails.Length; index++) if (tails[index] != null && string.Equals(tails[index].Id, id, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool ContainsItem(RaidItemDefinition[] items, string id)
        {
            if (items == null) return false;
            for (int index = 0; index < items.Length; index++) if (items[index] != null && string.Equals(items[index].Id, id, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsFloor(RaidArenaDefinition arena, GridPoint point)
        {
            return arena != null && point.X >= 0 && point.Y >= 0 && point.X < arena.Width && point.Y < arena.Height && arena.Rows != null && point.Y < arena.Rows.Length && arena.Rows[point.Y] != null && point.X < arena.Rows[point.Y].Length && arena.Rows[point.Y][point.X] == '.';
        }

        private static bool Contains(GridPoint[] points, GridPoint target)
        {
            if (points == null) return false;
            for (int index = 0; index < points.Length; index++) if (Same(points[index], target)) return true;
            return false;
        }

        private static bool Contains(string[] values, string target)
        {
            if (values == null) return false;
            for (int index = 0; index < values.Length; index++) if (string.Equals(values[index], target, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string[] Append(string[] source, string value)
        {
            var result = new string[source.Length + 1];
            Array.Copy(source, result, source.Length); result[result.Length - 1] = value; return result;
        }

        private static bool Same(GridPoint left, GridPoint right) { return left.X == right.X && left.Y == right.Y; }
        private static int PositiveModulo(int value, int length) { int result = value % length; return result < 0 ? result + length : result; }
        private static string PointKey(GridPoint point) { return point.X.ToString(CultureInfo.InvariantCulture) + "," + point.Y.ToString(CultureInfo.InvariantCulture); }
        private static RaidEvent Event(string type, string detail) { return new RaidEvent { Type = type, Detail = detail }; }
        private static string[] CloneStrings(string[] source) { if (source == null) return null; var clone = new string[source.Length]; Array.Copy(source, clone, source.Length); return clone; }
        private static GridPoint[] ClonePoints(GridPoint[] source) { if (source == null) return null; var clone = new GridPoint[source.Length]; Array.Copy(source, clone, source.Length); return clone; }
        private static RaidTailDefinition[] CloneTails(RaidTailDefinition[] source) { if (source == null) return null; var clone = new RaidTailDefinition[source.Length]; for (int i = 0; i < source.Length; i++) clone[i] = source[i] == null ? null : new RaidTailDefinition { Id = source[i].Id, Position = source[i].Position }; return clone; }
        private static RaidItemDefinition[] CloneItems(RaidItemDefinition[] source) { if (source == null) return null; var clone = new RaidItemDefinition[source.Length]; for (int i = 0; i < source.Length; i++) clone[i] = source[i] == null ? null : new RaidItemDefinition { Id = source[i].Id, Position = source[i].Position, Kind = source[i].Kind }; return clone; }
        private static bool SameStrings(string[] left, string[] right) { if (left == null || right == null) return left == right; if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false; return true; }
        private static void Append(StringBuilder builder, object value) { builder.Append(value == null ? "<null>" : Convert.ToString(value, CultureInfo.InvariantCulture)); builder.Append('|'); }
        private static void AppendPoint(StringBuilder builder, GridPoint point) { Append(builder, point.X); Append(builder, point.Y); }
        private static void AppendSorted(StringBuilder builder, string[] values) { string[] clone = CloneStrings(values) ?? new string[0]; Array.Sort(clone, StringComparer.Ordinal); for (int i = 0; i < clone.Length; i++) Append(builder, clone[i]); }
        private static string Hash(string value)
        {
            byte[] bytes;
            using (SHA256 hash = SHA256.Create()) bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
            var builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++) builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
        private static void EnsureValidArena(RaidArenaDefinition arena) { string[] errors = ValidateArena(arena); if (errors.Length > 0) throw new ArgumentException("Raid arena invalid: " + string.Join(",", errors), "arena"); }
        private static void EnsureValidState(RaidArenaDefinition arena, RaidState state) { string[] errors = ValidateState(arena, state); if (errors.Length > 0) throw new ArgumentException("Raid state invalid: " + string.Join(",", errors), "state"); }
    }
}
