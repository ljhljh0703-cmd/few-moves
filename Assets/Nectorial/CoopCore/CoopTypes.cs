using System;

namespace Nectorial.SlideEscape.Coop
{
    public enum CoopActor
    {
        Circle,
        Diamond
    }

    public enum CoopRunStatus
    {
        Playing,
        Cleared,
        Paused
    }

    public enum CoopCommandKind
    {
        Slide,
        Pass,
        RequestUndo,
        ResolveUndo,
        RequestRestart,
        ResolveRestart,
        Express
    }

    public enum CoopConsentKind
    {
        Undo,
        Restart
    }

    public enum CoopExpression
    {
        Look,
        ThumbsUp,
        Handshake,
        Waiting
    }

    public enum CoopSolverStatus
    {
        Solved,
        Unsolvable,
        LimitReached,
        InvalidRoom
    }

    [Serializable]
    public sealed class CoopRoomDefinition
    {
        public string Id;
        public int Width;
        public int Height;
        public string[] Rows;
        public GridPoint CircleStart;
        public GridPoint DiamondStart;
        public GridPoint CircleGoal;
        public GridPoint DiamondGoal;
        public string RulesVersion;
        public string ContentVersion;
    }

    [Serializable]
    public sealed class CoopPendingConsent
    {
        public string RequestId;
        public CoopConsentKind Kind;
        public CoopActor Requester;
        public long RequestedAtRevision;
    }

    [Serializable]
    public sealed class CoopState
    {
        public string RoomId;
        public GridPoint CirclePosition;
        public GridPoint DiamondPosition;
        public CoopActor ActiveActor;
        public long AuthorityRevision;
        public int LogicalActionCount;
        public CoopRunStatus Status;
        public CoopPendingConsent PendingConsent;
    }

    [Serializable]
    public sealed class CoopCommand
    {
        public string CommandId;
        public long ExpectedRevision;
        public CoopActor Seat;
        public CoopCommandKind Kind;
        public GameCommand Direction;
        public string RequestId;
        public bool Approve;
        public CoopExpression Expression;
    }

    [Serializable]
    public sealed class CoopEvent
    {
        public string Type;
        public CoopActor Actor;
        public string Detail;
    }

    [Serializable]
    public sealed class CoopDispatchResult
    {
        public bool Accepted;
        public bool Idempotent;
        public string Reason;
        public CoopState State;
        public CoopEvent[] Events;
    }

    [Serializable]
    public sealed class CoopAttempt
    {
        public CoopCommand Command;
        public bool Accepted;
        public bool Idempotent;
        public string Reason;
        public string StateFingerprint;
    }

    [Serializable]
    public sealed class CoopReplay
    {
        public CoopCommand[] Commands;
        public CoopAttempt[] Attempts;
    }

    [Serializable]
    public sealed class CoopSaveEnvelope
    {
        public int SchemaVersion;
        public string GameId;
        public string ModeId;
        public string RulesVersion;
        public string ContentVersion;
        public string RoomFingerprint;
        public string SessionId;
        public CoopState State;
        public CoopReplay Replay;
        public string StateFingerprint;
    }

    [Serializable]
    public sealed class CoopSolverOptions
    {
        public bool AllowPass = true;
        public bool ForbidCircleStoppedByDiamond;
        public bool ForbidDiamondStoppedByCircle;

        public static CoopSolverOptions Normal()
        {
            return new CoopSolverOptions();
        }

        public static CoopSolverOptions ForbidPartnerStopper(CoopActor actor)
        {
            return new CoopSolverOptions
            {
                ForbidCircleStoppedByDiamond = actor == CoopActor.Circle,
                ForbidDiamondStoppedByCircle = actor == CoopActor.Diamond
            };
        }
    }

    [Serializable]
    public sealed class CoopSolverResult
    {
        public CoopSolverStatus Status;
        public CoopCommand[] Commands;
        public int VisitedCount;
        public int OptimalActionCount;
    }
}
