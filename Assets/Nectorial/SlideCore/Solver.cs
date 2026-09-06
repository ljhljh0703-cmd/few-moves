using System;
using System.Collections.Generic;

namespace Nectorial.SlideEscape
{
    public static class Solver
    {
        private static readonly GameCommand[] SearchDirections =
        {
            GameCommand.Up,
            GameCommand.Right,
            GameCommand.Down,
            GameCommand.Left
        };

        public static SolverResult FindSolution(RoomDefinition room, int maxVisitedStates, int movableMask = -1)
        {
            string[] errors = GameEngine.ValidateRoom(room);
            if (errors.Length > 0) return Result(SolverStatus.InvalidRoom, new GameMove[0], 0);
            if (maxVisitedStates <= 0) return Result(SolverStatus.LimitReached, new GameMove[0], 0);

            int allPiecesMask = (1 << room.Pieces.Length) - 1;
            int normalizedMask = movableMask == -1 ? allPiecesMask : movableMask;
            if ((normalizedMask & ~allPiecesMask) != 0 || normalizedMask < 0) return Result(SolverStatus.InvalidRoom, new GameMove[0], 0);

            ulong startKey = PackStarts(room);
            int goalCell = GameEngine.ToCell(room.Goal);
            List<SearchNode> nodes = new List<SearchNode>();
            Dictionary<ulong, int> visited = new Dictionary<ulong, int>();
            Queue<int> queue = new Queue<int>();
            nodes.Add(new SearchNode(startKey, -1, -1, GameCommand.Up));
            visited.Add(startKey, 0);
            queue.Enqueue(0);

            while (queue.Count > 0)
            {
                int nodeIndex = queue.Dequeue();
                SearchNode node = nodes[nodeIndex];
                for (int pieceIndex = 0; pieceIndex < room.Pieces.Length; pieceIndex++)
                {
                    if ((normalizedMask & (1 << pieceIndex)) == 0) continue;
                    for (int directionIndex = 0; directionIndex < SearchDirections.Length; directionIndex++)
                    {
                        ulong nextKey;
                        GameCommand direction = SearchDirections[directionIndex];
                        if (!TrySlidePacked(room, node.Key, room.Pieces.Length, pieceIndex, direction, out nextKey)) continue;
                        if (visited.ContainsKey(nextKey)) continue;
                        if (visited.Count >= maxVisitedStates) return Result(SolverStatus.LimitReached, new GameMove[0], visited.Count);

                        int nextNodeIndex = nodes.Count;
                        nodes.Add(new SearchNode(nextKey, nodeIndex, pieceIndex, direction));
                        visited.Add(nextKey, nextNodeIndex);
                        if (GameEngine.ExtractPackedCell(nextKey, room.TargetPieceIndex) == goalCell)
                        {
                            GameMove[] moves = ReconstructMoves(nodes, nextNodeIndex);
                            if (ReplaysToClear(room, moves)) return Result(SolverStatus.Solved, moves, visited.Count);
                            throw new InvalidOperationException("solver_replay_mismatch");
                        }

                        queue.Enqueue(nextNodeIndex);
                    }
                }
            }

            return Result(SolverStatus.Unsolvable, new GameMove[0], visited.Count);
        }

        private static bool TrySlidePacked(RoomDefinition room, ulong key, int pieceCount, int pieceIndex, GameCommand direction, out ulong nextKey)
        {
            nextKey = key;
            int destinationCell;
            int distance;
            string error;
            if (!GameEngine.TryGetSlideDestination(room, key, pieceCount, pieceIndex, direction, out destinationCell, out distance, out error))
                throw new InvalidOperationException("solver_slide_primitive_rejected:" + error);
            if (distance == 0) return false;
            nextKey = GameEngine.ReplacePackedCell(key, pieceIndex, destinationCell);
            return true;
        }

        private static GameMove[] ReconstructMoves(List<SearchNode> nodes, int nodeIndex)
        {
            List<GameMove> reversed = new List<GameMove>();
            int current = nodeIndex;
            while (current >= 0)
            {
                SearchNode node = nodes[current];
                if (node.PieceIndex >= 0) reversed.Add(new GameMove { PieceIndex = node.PieceIndex, Direction = node.Direction });
                current = node.ParentIndex;
            }

            reversed.Reverse();
            return reversed.ToArray();
        }

        private static bool ReplaysToClear(RoomDefinition room, GameMove[] moves)
        {
            GameState state = GameEngine.Create(room);
            for (int index = 0; index < moves.Length; index++)
            {
                StepResult result = GameEngine.Step(room, state, moves[index]);
                if (!result.Accepted) return false;
                state = result.State;
            }

            return state.Status == RunStatus.Cleared && state.Turn == moves.Length;
        }

        private static ulong PackStarts(RoomDefinition room)
        {
            ulong key = 0UL;
            for (int index = 0; index < room.Pieces.Length; index++) key = GameEngine.ReplacePackedCell(key, index, GameEngine.ToCell(room.Pieces[index].Start));
            return key;
        }

        private static SolverResult Result(SolverStatus status, GameMove[] moves, int visitedCount)
        {
            return new SolverResult { Status = status, Moves = moves, VisitedCount = visitedCount };
        }

        private struct SearchNode
        {
            public readonly ulong Key;
            public readonly int ParentIndex;
            public readonly int PieceIndex;
            public readonly GameCommand Direction;

            public SearchNode(ulong key, int parentIndex, int pieceIndex, GameCommand direction)
            {
                Key = key;
                ParentIndex = parentIndex;
                PieceIndex = pieceIndex;
                Direction = direction;
            }
        }
    }
}
