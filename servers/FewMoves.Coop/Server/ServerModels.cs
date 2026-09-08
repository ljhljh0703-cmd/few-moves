using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nectorial.SlideEscape.Coop;

namespace FewMoves.Coop.Server
{
    public enum RoomAvailabilityStatus
    {
        WaitingForPeer,
        Active,
        PausedOffline,
        Expired
    }

    public sealed class CreateRoomRequest
    {
        public string CreateRequestId;
    }

    public sealed class JoinRoomRequest
    {
        public string InviteCode;
        public string JoinRequestId;
    }

    public sealed class CommandRequest
    {
        public string CommandId;
        public long ExpectedRevision;
        public int Kind;
        public int Direction;
        public string RequestId;
        public bool Approve;
        public int Expression;
        internal bool HasExpectedRevision;
        internal bool HasKind;
        internal bool HasDirection;
        internal bool HasRequestId;
        internal bool HasApprove;
        internal bool HasExpression;
    }

    public sealed class ApiError
    {
        public string Code;
    }

    public sealed class PublicRoomView
    {
        public string RoomId;
        public string RoomResource = "CoopRooms/coop-c1";
        public string RulesVersion;
        public string ContentVersion;
        public string RoomFingerprint;
        public DateTimeOffset ExpiresAtUtc;
    }

    public sealed class AvailabilityView
    {
        public RoomAvailabilityStatus Status;
        public bool CircleConnected;
        public bool DiamondConnected;
        public DateTimeOffset? HeartbeatExpiresAtUtc;
    }

    public sealed class ExpressionEventView
    {
        public long Sequence;
        public CoopActor Sender;
        public CoopExpression Expression;
        public DateTimeOffset CreatedAtUtc;
    }

    public sealed class ExpressionStreamView
    {
        public long ExpressionSequence;
        public ExpressionEventView[] Events;
    }

    public class StateView
    {
        public bool Ok = true;
        public CoopActor Seat;
        public PublicRoomView Room;
        public CoopState State;
        public AvailabilityView Availability;
        public ExpressionStreamView Expressions;
    }

    public sealed class CommandView : StateView
    {
        public bool Accepted;
        public bool Idempotent;
        public string Reason;
        public CoopEvent[] Events;
        public ApiError Error;
    }

    public sealed class CreateRoomView
    {
        public bool Ok = true;
        public string RoomId;
        public string InviteCode;
        public CoopActor Seat;
        public string SeatToken;
        public PublicRoomView Room;
    }

    public sealed class JoinRoomView
    {
        public bool Ok = true;
        public string RoomId;
        public CoopActor Seat;
        public string SeatToken;
        public PublicRoomView Room;
    }

    public sealed class ErrorView
    {
        public bool Ok;
        public ApiError Error;
        public CoopActor? Seat;
        public PublicRoomView Room;
        public CoopState State;
        public AvailabilityView Availability;
        public ExpressionStreamView Expressions;
    }

    public sealed class HealthView
    {
        public bool Ok = true;
        public string Service = "few-moves-coop";
        public string Version;
        public string BuildId;
        public bool PublicRootConfigured;
    }

    public sealed class PersistedServerState
    {
        public int SchemaVersion = 1;
        public string ServerSecret;
        public List<PersistedRoom> Rooms = new List<PersistedRoom>();
    }

    public sealed class PersistedSeat
    {
        public string TokenHash;
        public string RecoveryRequestHash;
    }

    public sealed class PersistedCommandLedgerEntry
    {
        public string CommandId;
        public string PayloadHash;
        public bool Accepted;
        public int StatusCode;
        public string Reason;
        public CoopEvent[] Events;
        public DateTimeOffset CreatedAtUtc;
    }

    public sealed class PersistedAuditEvent
    {
        public DateTimeOffset CreatedAtUtc;
        public string Type;
        public CoopActor? Seat;
        public string Code;
    }

    public sealed class PersistedRoom
    {
        public string RoomId;
        public string InviteCodeHash;
        public PersistedSeat Circle;
        public PersistedSeat Diamond;
        public DateTimeOffset CreatedAtUtc;
        public DateTimeOffset UpdatedAtUtc;
        public DateTimeOffset ExpiresAtUtc;
        public string RulesVersion;
        public string ContentVersion;
        public string RoomFingerprint;
        public CoopSaveEnvelope Envelope;
        public List<PersistedCommandLedgerEntry> CommandLedger = new List<PersistedCommandLedgerEntry>();
        public long ExpressionSequence;
        public List<ExpressionEventView> ExpressionEvents = new List<ExpressionEventView>();
        public List<PersistedAuditEvent> AuditEvents = new List<PersistedAuditEvent>();
    }

    internal sealed class RoomRuntime
    {
        public PersistedRoom Record;
        public CoopSession Session;
        public DateTimeOffset? CircleLastSeen;
        public DateTimeOffset? DiamondLastSeen;
        public readonly Queue<DateTimeOffset> CircleCommands = new Queue<DateTimeOffset>();
        public readonly Queue<DateTimeOffset> DiamondCommands = new Queue<DateTimeOffset>();
        public readonly Queue<DateTimeOffset> CircleExpressions = new Queue<DateTimeOffset>();
        public readonly Queue<DateTimeOffset> DiamondExpressions = new Queue<DateTimeOffset>();
        public DateTimeOffset? CircleLastExpression;
        public DateTimeOffset? DiamondLastExpression;
    }

    internal sealed class RuntimeSnapshot
    {
        public DateTimeOffset? CircleLastSeen;
        public DateTimeOffset? DiamondLastSeen;
        public DateTimeOffset[] CircleCommands;
        public DateTimeOffset[] DiamondCommands;
        public DateTimeOffset[] CircleExpressions;
        public DateTimeOffset[] DiamondExpressions;
        public DateTimeOffset? CircleLastExpression;
        public DateTimeOffset? DiamondLastExpression;
    }

    internal sealed class StoreRollback
    {
        public PersistedServerState Persisted;
        public Dictionary<string, RuntimeSnapshot> Runtime = new Dictionary<string, RuntimeSnapshot>(StringComparer.Ordinal);
    }

    public sealed class StoreResult
    {
        public bool Ok;
        public int StatusCode;
        public string ErrorCode;
        public string RoomId;
        public string InviteCode;
        public string SeatToken;
        public CoopActor Seat;
        public bool Accepted;
        public bool Idempotent;
        public string Reason;
        public CoopEvent[] Events;
        public StateSnapshot Snapshot;
    }

    public sealed class StateSnapshot
    {
        public CoopActor Seat;
        public PublicRoomView Room;
        public CoopState State;
        public AvailabilityView Availability;
        public ExpressionStreamView Expressions;
    }

    public sealed class ServerStateCorruptException : Exception
    {
        public ServerStateCorruptException(string message) : base(message) { }
    }

    internal static class WireJson
    {
        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }
}
