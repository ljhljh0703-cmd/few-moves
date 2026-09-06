using System;

namespace Nectorial.SlideEscape
{
    [Serializable]
    public struct GridPoint : IEquatable<GridPoint>
    {
        public int X;
        public int Y;

        public GridPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(GridPoint other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is GridPoint && Equals((GridPoint)obj);
        }

        public override int GetHashCode()
        {
            return (X * 397) ^ Y;
        }
    }

    public enum GameCommand
    {
        Up,
        Down,
        Left,
        Right
    }

    public enum RunStatus
    {
        Playing,
        Cleared
    }

    [Serializable]
    public sealed class PieceDefinition
    {
        public string Id;
        public GridPoint Start;
    }

    [Serializable]
    public sealed class RoomDefinition
    {
        public string Id;
        public int Width;
        public int Height;
        public string[] Rows;
        public PieceDefinition[] Pieces;
        public int TargetPieceIndex;
        public GridPoint Goal;
        public int ParMoves;
    }

    [Serializable]
    public sealed class GameMove
    {
        public int PieceIndex;
        public GameCommand Direction;
    }

    [Serializable]
    public sealed class GameState
    {
        public string RoomId;
        public GridPoint[] Positions;
        public int Turn;
        public RunStatus Status;
    }

    [Serializable]
    public sealed class StepResult
    {
        public GameState State;
        public bool Accepted;
        public string Reason;
        public string[] Events;
    }

    public enum SolverStatus
    {
        Solved,
        Unsolvable,
        LimitReached,
        InvalidRoom
    }

    [Serializable]
    public sealed class SolverResult
    {
        public SolverStatus Status;
        public GameMove[] Moves;
        public int VisitedCount;
    }

    [Serializable]
    public sealed class SaveEnvelope
    {
        public int SchemaVersion;
        public string GameId;
        public string ContentVersion;
        public string RoomHash;
        public GameState State;
        public GameMove[] Moves;
        public int SelectedPieceIndex;
    }
}
