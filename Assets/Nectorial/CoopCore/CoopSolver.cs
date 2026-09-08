using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nectorial.SlideEscape.Coop
{
    public static class CoopSolver
    {
        private static readonly GameCommand[] Directions =
        {
            GameCommand.Up,
            GameCommand.Right,
            GameCommand.Down,
            GameCommand.Left
        };

        public static CoopSolverResult FindSolution(CoopRoomDefinition room, int maxVisitedStates, CoopSolverOptions options)
        {
            if (CoopRules.ValidateRoom(room).Length > 0) return Result(CoopSolverStatus.InvalidRoom, new CoopCommand[0], 0);
            if (maxVisitedStates <= 0) return Result(CoopSolverStatus.LimitReached, new CoopCommand[0], 0);
            if (options == null) options = CoopSolverOptions.Normal();

            CoopState start = CoopRules.CreateInitialState(room);
            var nodes = new List<SearchNode>();
            var visited = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<int>();
            nodes.Add(new SearchNode(start, -1, null));
            visited.Add(Key(start), 0);
            queue.Enqueue(0);

            while (queue.Count > 0)
            {
                int nodeIndex = queue.Dequeue();
                SearchNode node = nodes[nodeIndex];
                CoopState state = node.State;
                CoopActor actor = state.ActiveActor;
                for (int directionIndex = 0; directionIndex < Directions.Length; directionIndex++)
                {
                    GameCommand direction = Directions[directionIndex];
                    GridPoint destination;
                    int distance;
                    bool stoppedByPartner;
                    string reason;
                    if (!CoopRules.TrySlide(room, state, actor, direction, out destination, out distance, out stoppedByPartner, out reason)) continue;
                    if (stoppedByPartner && IsForbidden(options, actor)) continue;
                    CoopState next = CoopRules.ApplyAction(room, state, actor, destination, false);
                    CoopCommand command = CoopCommandFactory.Slide(actor, string.Empty, 0, direction);
                    int nextIndex;
                    if (!AddNode(next, nodeIndex, command, nodes, visited, queue, maxVisitedStates, out nextIndex)) return Result(CoopSolverStatus.LimitReached, new CoopCommand[0], visited.Count);
                    if (next.Status == CoopRunStatus.Cleared) return Result(CoopSolverStatus.Solved, Reconstruct(nodes, nextIndex), visited.Count);
                }

                if (options.AllowPass)
                {
                    CoopState next = CoopRules.ApplyAction(room, state, actor, CoopRules.GetPosition(state, actor), true);
                    CoopCommand command = CoopCommandFactory.Pass(actor, string.Empty, 0);
                    int nextIndex;
                    if (!AddNode(next, nodeIndex, command, nodes, visited, queue, maxVisitedStates, out nextIndex)) return Result(CoopSolverStatus.LimitReached, new CoopCommand[0], visited.Count);
                    if (next.Status == CoopRunStatus.Cleared) return Result(CoopSolverStatus.Solved, Reconstruct(nodes, nextIndex), visited.Count);
                }
            }

            return Result(CoopSolverStatus.Unsolvable, new CoopCommand[0], visited.Count);
        }

        private static bool AddNode(CoopState next, int parentIndex, CoopCommand command, List<SearchNode> nodes, Dictionary<string, int> visited, Queue<int> queue, int maxVisitedStates, out int nextIndex)
        {
            nextIndex = -1;
            string key = Key(next);
            if (visited.ContainsKey(key)) return true;
            if (visited.Count >= maxVisitedStates) return false;
            nextIndex = nodes.Count;
            nodes.Add(new SearchNode(next, parentIndex, command));
            visited.Add(key, nextIndex);
            queue.Enqueue(nextIndex);
            return true;
        }

        private static CoopCommand[] Reconstruct(List<SearchNode> nodes, int nodeIndex)
        {
            var reversed = new List<CoopCommand>();
            int current = nodeIndex;
            while (current >= 0)
            {
                SearchNode node = nodes[current];
                if (node.Command != null) reversed.Add(node.Command);
                current = node.ParentIndex;
            }
            reversed.Reverse();
            long revision = 0;
            for (int index = 0; index < reversed.Count; index++)
            {
                CoopCommand command = reversed[index];
                command.CommandId = "solver-" + index.ToString(CultureInfo.InvariantCulture);
                command.ExpectedRevision = revision;
                revision++;
            }
            return reversed.ToArray();
        }

        private static string Key(CoopState state)
        {
            return state.CirclePosition.X.ToString(CultureInfo.InvariantCulture) + "," + state.CirclePosition.Y.ToString(CultureInfo.InvariantCulture)
                + ";" + state.DiamondPosition.X.ToString(CultureInfo.InvariantCulture) + "," + state.DiamondPosition.Y.ToString(CultureInfo.InvariantCulture)
                + ";" + ((int)state.ActiveActor).ToString(CultureInfo.InvariantCulture)
                + ";" + ((int)state.Status).ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsForbidden(CoopSolverOptions options, CoopActor actor)
        {
            return (actor == CoopActor.Circle && options.ForbidCircleStoppedByDiamond)
                || (actor == CoopActor.Diamond && options.ForbidDiamondStoppedByCircle);
        }

        private static CoopSolverResult Result(CoopSolverStatus status, CoopCommand[] commands, int visitedCount)
        {
            return new CoopSolverResult { Status = status, Commands = commands, VisitedCount = visitedCount, OptimalActionCount = commands.Length };
        }

        private sealed class SearchNode
        {
            public readonly CoopState State;
            public readonly int ParentIndex;
            public readonly CoopCommand Command;
            public SearchNode(CoopState state, int parentIndex, CoopCommand command)
            {
                State = CoopRules.CloneState(state);
                ParentIndex = parentIndex;
                Command = CoopRules.CloneCommand(command);
            }
        }
    }
}
