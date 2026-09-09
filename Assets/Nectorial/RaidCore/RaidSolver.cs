using System;
using System.Collections.Generic;
using Nectorial.SlideEscape;

namespace Nectorial.SlideEscape.Raid
{
    public static class RaidSolver
    {
        private static readonly GameCommand[] Directions = { GameCommand.Up, GameCommand.Right, GameCommand.Down, GameCommand.Left };

        public static RaidSolverResult FindSolution(RaidArenaDefinition arena, int maxVisitedStates)
        {
            if (RaidRules.ValidateArena(arena).Length > 0) return new RaidSolverResult { Status = RaidSolverStatus.InvalidArena, Moves = new RaidMove[0], VisitedCount = 0, OptimalActionCount = 0 };
            if (maxVisitedStates < 1) return new RaidSolverResult { Status = RaidSolverStatus.LimitReached, Moves = new RaidMove[0], VisitedCount = 0, OptimalActionCount = 0 };
            var queue = new Queue<Node>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            RaidState initial = RaidRules.CreateInitialState(arena);
            queue.Enqueue(new Node(initial, new RaidMove[0]));
            visited.Add(RaidRules.SolverKey(arena, initial));

            while (queue.Count > 0)
            {
                Node current = queue.Dequeue();
                if (current.State.Status == RaidRunStatus.Cleared)
                    return new RaidSolverResult { Status = RaidSolverStatus.Solved, Moves = RaidRules.CloneMoves(current.Moves), VisitedCount = visited.Count, OptimalActionCount = current.Moves.Length };
                for (int index = 0; index < Directions.Length; index++)
                {
                    RaidDispatchResult result = RaidRules.Step(arena, current.State, Directions[index]);
                    if (!result.Accepted || result.State.Status == RaidRunStatus.Failed) continue;
                    string key = RaidRules.SolverKey(arena, result.State);
                    if (!visited.Add(key)) continue;
                    RaidMove[] moves = Append(current.Moves, new RaidMove { CommandId = "solver-" + current.Moves.Length.ToString(), Direction = Directions[index] });
                    if (visited.Count > maxVisitedStates) return new RaidSolverResult { Status = RaidSolverStatus.LimitReached, Moves = new RaidMove[0], VisitedCount = visited.Count, OptimalActionCount = 0 };
                    queue.Enqueue(new Node(result.State, moves));
                }
            }
            return new RaidSolverResult { Status = RaidSolverStatus.Unsolvable, Moves = new RaidMove[0], VisitedCount = visited.Count, OptimalActionCount = 0 };
        }

        private static RaidMove[] Append(RaidMove[] source, RaidMove move)
        {
            var result = new RaidMove[source.Length + 1];
            Array.Copy(source, result, source.Length);
            result[result.Length - 1] = move;
            return result;
        }

        private sealed class Node
        {
            public readonly RaidState State;
            public readonly RaidMove[] Moves;
            public Node(RaidState state, RaidMove[] moves) { State = RaidRules.CloneState(state); Moves = RaidRules.CloneMoves(moves); }
        }
    }
}
