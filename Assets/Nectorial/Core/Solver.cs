using System;
using System.Collections.Generic;

namespace Nectorial.TurnEscape
{
    public static class Solver
    {
        private static readonly GameCommand[] SearchCommands =
        {
            GameCommand.Up,
            GameCommand.Right,
            GameCommand.Down,
            GameCommand.Left,
            GameCommand.Wait
        };

        public static SolverResult FindSolution(RoomDefinition room, int maxVisitedStates)
        {
            string[] errors = GameEngine.ValidateRoom(room);
            if (errors.Length > 0)
            {
                return Result(SolverStatus.InvalidRoom, new GameCommand[0], 0);
            }

            if (maxVisitedStates <= 0)
            {
                return Result(SolverStatus.LimitReached, new GameCommand[0], 0);
            }

            GameState initial = GameEngine.Create(room);
            Queue<SearchNode> queue = new Queue<SearchNode>();
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            visited.Add(SearchKey(room, initial));
            queue.Enqueue(new SearchNode(initial, new GameCommand[0]));

            while (queue.Count > 0)
            {
                SearchNode current = queue.Dequeue();
                for (int i = 0; i < SearchCommands.Length; i++)
                {
                    GameCommand command = SearchCommands[i];
                    StepResult step = GameEngine.Step(room, current.State, command);
                    if (!step.Accepted || step.State.Status == RunStatus.Captured)
                    {
                        continue;
                    }

                    string key = SearchKey(room, step.State);
                    if (visited.Contains(key))
                    {
                        continue;
                    }

                    if (visited.Count >= maxVisitedStates)
                    {
                        return Result(SolverStatus.LimitReached, new GameCommand[0], visited.Count);
                    }

                    GameCommand[] commands = Append(current.Commands, command);
                    visited.Add(key);
                    if (step.State.Status == RunStatus.Cleared)
                    {
                        if (ReplaysToClear(room, commands))
                        {
                            return Result(SolverStatus.Solved, commands, visited.Count);
                        }

                        return Result(SolverStatus.Unsolvable, new GameCommand[0], visited.Count);
                    }

                    queue.Enqueue(new SearchNode(step.State, commands));
                }
            }

            return Result(SolverStatus.Unsolvable, new GameCommand[0], visited.Count);
        }

        private static string SearchKey(RoomDefinition room, GameState state)
        {
            return GameEngine.Fingerprint(room, state, false);
        }

        private static bool ReplaysToClear(RoomDefinition room, GameCommand[] commands)
        {
            GameState replay = GameEngine.Create(room);
            for (int i = 0; i < commands.Length; i++)
            {
                StepResult result = GameEngine.Step(room, replay, commands[i]);
                if (!result.Accepted)
                {
                    return false;
                }

                replay = result.State;
            }

            return replay.Status == RunStatus.Cleared;
        }

        private static GameCommand[] Append(GameCommand[] commands, GameCommand command)
        {
            GameCommand[] next = new GameCommand[commands.Length + 1];
            Array.Copy(commands, next, commands.Length);
            next[next.Length - 1] = command;
            return next;
        }

        private static SolverResult Result(SolverStatus status, GameCommand[] commands, int visitedCount)
        {
            return new SolverResult
            {
                Status = status,
                Commands = commands,
                VisitedCount = visitedCount
            };
        }

        private sealed class SearchNode
        {
            public readonly GameState State;
            public readonly GameCommand[] Commands;

            public SearchNode(GameState state, GameCommand[] commands)
            {
                State = state;
                Commands = commands;
            }
        }
    }
}
