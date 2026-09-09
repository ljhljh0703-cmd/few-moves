using System;
using Nectorial.SlideEscape;

namespace Nectorial.SlideEscape.Raid
{
    public enum RaidItemKind
    {
        Shield,
        Magnet,
        Slow
    }

    public enum RaidRunStatus
    {
        Playing,
        Armed,
        Cleared,
        Failed
    }

    public enum RaidCollisionKind
    {
        None,
        Body,
        SharedDestination,
        EdgeSwap
    }

    public enum RaidFrameOutcome
    {
        Advanced,
        Shielded,
        Failed,
        Cleared
    }

    public enum RaidSolverStatus
    {
        Solved,
        Unsolvable,
        LimitReached,
        InvalidArena
    }

    [Serializable]
    public sealed class RaidTailDefinition
    {
        public string Id;
        public GridPoint Position;
    }

    [Serializable]
    public sealed class RaidItemDefinition
    {
        public string Id;
        public GridPoint Position;
        public RaidItemKind Kind;
    }

    [Serializable]
    public sealed class RaidArenaDefinition
    {
        public string Id;
        public int Width;
        public int Height;
        public string[] Rows;
        public GridPoint PlayerStart;
        public GridPoint[] SnakeRing;
        public int SnakeStartHeadIndex;
        public int SnakeBodyLength;
        public RaidTailDefinition[] TailFragments;
        public RaidItemDefinition[] Items;
        public int InitialShieldCharges;
        public int MagnetDurationSteps;
        public int MagnetRadius;
        public int SlowDurationSteps;
        public string RulesVersion;
        public string ContentVersion;
    }

    [Serializable]
    public sealed class RaidState
    {
        public string ArenaId;
        public GridPoint PlayerPosition;
        public int SnakeHeadIndex;
        public string[] CollectedTailIds;
        public string[] CollectedItemIds;
        public int ShieldCharges;
        public int MagnetStepsRemaining;
        public int SlowStepsRemaining;
        public int Actions;
        public int Hits;
        public RaidRunStatus Status;
    }

    [Serializable]
    public sealed class RaidMove
    {
        public string CommandId;
        public GameCommand Direction;
    }

    [Serializable]
    public sealed class RaidFrame
    {
        public int Microstep;
        public GridPoint PlayerBefore;
        public GridPoint AttemptedPlayerAfter;
        public GridPoint PlayerAfter;
        public GridPoint[] SnakeBefore;
        public GridPoint[] SnakeAfter;
        public bool SnakeMoved;
        public RaidCollisionKind Collision;
        public string[] CollectedTailIds;
        public string[] CollectedItemIds;
        public string[] MagnetCollectedTailIds;
        public int ShieldCharges;
        public int MagnetStepsRemaining;
        public int SlowStepsRemaining;
        public RaidFrameOutcome Outcome;
    }

    [Serializable]
    public sealed class RaidEvent
    {
        public string Type;
        public string Detail;
    }

    [Serializable]
    public sealed class RaidDispatchResult
    {
        public bool Accepted;
        public bool Idempotent;
        public string Reason;
        public RaidState State;
        public RaidFrame[] Frames;
        public RaidEvent[] Events;
        public bool ActionCompleted;
        public bool ActionCancelled;
    }

    [Serializable]
    public sealed class RaidAttempt
    {
        public RaidMove Move;
        public bool Accepted;
        public bool Idempotent;
        public string Reason;
        public string StateFingerprint;
    }

    [Serializable]
    public sealed class RaidReplay
    {
        public RaidMove[] Moves;
        public RaidAttempt[] Attempts;
    }

    [Serializable]
    public sealed class RaidSaveEnvelope
    {
        public int SchemaVersion;
        public string GameId;
        public string ModeId;
        public string RulesVersion;
        public string ContentVersion;
        public string ArenaFingerprint;
        public RaidState State;
        public RaidReplay Replay;
        public string StateFingerprint;
    }

    [Serializable]
    public sealed class RaidSolverResult
    {
        public RaidSolverStatus Status;
        public RaidMove[] Moves;
        public int VisitedCount;
        public int OptimalActionCount;
    }
}
