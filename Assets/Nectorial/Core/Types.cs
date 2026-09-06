using System;

namespace Nectorial.TurnEscape
{
    [Serializable]
    public struct GridPoint
    {
        public int X;
        public int Y;

        public GridPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

    }

    public enum GameCommand
    {
        Up,
        Down,
        Left,
        Right,
        Wait
    }

    public enum RunStatus
    {
        Playing,
        Captured,
        Cleared
    }

    [Serializable]
    public sealed class Door
    {
        public string Id;
        public GridPoint Position;
    }

    [Serializable]
    public sealed class Switch
    {
        public string Id;
        public GridPoint Position;
        public string[] DoorIds;
    }

    [Serializable]
    public sealed class Guard
    {
        public string Id;
        public GridPoint[] Patrol;
        public int StartIndex;
    }

    [Serializable]
    public sealed class RoomDefinition
    {
        public string Id;
        public int Width;
        public int Height;
        public string[] Rows;
        public GridPoint Start;
        public GridPoint Exit;
        public Door[] Doors;
        public Switch[] Switches;
        public Guard[] Guards;
    }

    [Serializable]
    public sealed class GameState
    {
        public string RoomId;
        public GridPoint Player;
        public int Turn;
        public RunStatus Status;
        public string[] OpenDoorIds;
        public int[] GuardIndices;
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
        public GameCommand[] Commands;
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
    }
}
