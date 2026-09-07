using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;

internal static class Program
{
    private static readonly List<CheckRecord> Records = new List<CheckRecord>();
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };
    private static CoopRoomDefinition C1;
    private static CoopSolverResult C1Solution;
    private static CoopSolverResult C1NoCircleStop;
    private static CoopSolverResult C1NoDiamondStop;

    private static int Main()
    {
        Run("c1_resource_solver_replay_and_witness", CheckC1);
        Run("geometry_parity_wall_boundary_partner_goal", CheckGeometryParity);
        Run("ownership_alternation_pass_and_soft_goals", CheckOwnershipAndPass);
        Run("idempotency_stale_and_revision_monotonic_undo", CheckIdempotencyAndUndo);
        Run("consent_invalidation_and_restart", CheckConsentAndRestart);
        Run("coop_save_replay_round_trip_and_tamper", CheckSaveReplay);

        int passed = 0;
        for (int index = 0; index < Records.Count; index++) if (Records[index].Passed) passed++;
        Console.WriteLine(JsonSerializer.Serialize(new CheckSummary
        {
            Suite = "few-moves-coop-core",
            Passed = passed,
            Failed = Records.Count - passed,
            Checks = Records.ToArray(),
            C1OptimalActionCount = C1Solution == null ? -1 : C1Solution.OptimalActionCount,
            C1VisitedStates = C1Solution == null ? -1 : C1Solution.VisitedCount,
            C1ForbidCircleStopStatus = C1NoCircleStop == null ? "not_run" : C1NoCircleStop.Status.ToString(),
            C1ForbidCircleStopCost = C1NoCircleStop == null ? -1 : C1NoCircleStop.OptimalActionCount,
            C1ForbidDiamondStopStatus = C1NoDiamondStop == null ? "not_run" : C1NoDiamondStop.Status.ToString(),
            C1ForbidDiamondStopCost = C1NoDiamondStop == null ? -1 : C1NoDiamondStop.OptimalActionCount,
            C1RoomFingerprint = C1 == null ? null : CoopRules.RoomFingerprint(C1),
            C1RulesVersion = C1 == null ? null : C1.RulesVersion,
            C1ContentVersion = C1 == null ? null : C1.ContentVersion,
            C1Trace = C1Solution == null ? new CoopCommand[0] : C1Solution.Commands
        }, JsonOptions));
        return passed == Records.Count ? 0 : 1;
    }

    private static void Run(string name, Action action)
    {
        try
        {
            action();
            Records.Add(new CheckRecord { Name = name, Passed = true, Detail = "ok" });
        }
        catch (Exception exception)
        {
            Records.Add(new CheckRecord { Name = name, Passed = false, Detail = exception.GetType().Name + ": " + exception.Message.Replace('\n', ' ') });
        }
    }

    private static void CheckC1()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "CoopRooms", "coop-c1.json");
        Assert(File.Exists(path), "C1 resource missing");
        C1 = JsonSerializer.Deserialize<CoopRoomDefinition>(File.ReadAllText(path), JsonOptions);
        Assert(C1 != null, "C1 JSON did not deserialize");
        AssertEqual(0, CoopRules.ValidateRoom(C1).Length, "C1 room must validate");
        C1Solution = CoopSolver.FindSolution(C1, 500000, CoopSolverOptions.Normal());
        AssertEqual(CoopSolverStatus.Solved, C1Solution.Status, "C1 must solve");
        Assert(C1Solution.OptimalActionCount >= 5 && C1Solution.OptimalActionCount <= 12, "C1 measured optimum must stay in starter range");
        AssertEqual(C1Solution.OptimalActionCount, C1Solution.Commands.Length, "reported optimum must equal trace length");

        CoopSession session = CoopSession.Create(C1);
        bool circleStop = false;
        bool diamondStop = false;
        for (int index = 0; index < C1Solution.Commands.Length; index++)
        {
            CoopDispatchResult result = session.Dispatch(C1Solution.Commands[index]);
            Assert(result.Accepted, "solver command must replay: " + index);
            for (int eventIndex = 0; eventIndex < result.Events.Length; eventIndex++)
            {
                CoopEvent item = result.Events[eventIndex];
                if (item.Type == "stopped_by_partner" && item.Actor == CoopActor.Circle) circleStop = true;
                if (item.Type == "stopped_by_partner" && item.Actor == CoopActor.Diamond) diamondStop = true;
            }
        }
        AssertEqual(CoopRunStatus.Cleared, session.State.Status, "C1 replay must clear only with both goals");
        Assert(circleStop && diamondStop, "C1 selected witness must contain stopper events for both actors");

        C1NoCircleStop = CoopSolver.FindSolution(C1, 500000, CoopSolverOptions.ForbidPartnerStopper(CoopActor.Circle));
        C1NoDiamondStop = CoopSolver.FindSolution(C1, 500000, CoopSolverOptions.ForbidPartnerStopper(CoopActor.Diamond));
        Assert(C1NoCircleStop.Status != CoopSolverStatus.Solved || C1NoCircleStop.OptimalActionCount > C1Solution.OptimalActionCount, "Circle partner-stopper counterfactual must worsen or remove solution");
        Assert(C1NoDiamondStop.Status != CoopSolverStatus.Solved || C1NoDiamondStop.OptimalActionCount > C1Solution.OptimalActionCount, "Diamond partner-stopper counterfactual must worsen or remove solution");
    }

    private static void CheckGeometryParity()
    {
        CoopRoomDefinition room = GeometryRoom();
        CoopState state = CoopRules.CreateInitialState(room);
        state.CirclePosition = new GridPoint(1, 1);
        GridPoint destination;
        int distance;
        bool stopped;
        string reason;
        Assert(!CoopRules.TrySlide(room, state, CoopActor.Circle, GameCommand.Up, out destination, out distance, out stopped, out reason), "boundary wall must reject zero slide");
        AssertEqual("blocked_zero", reason, "boundary rejection reason");
        state = CoopRules.CreateInitialState(room);
        Assert(CoopRules.TrySlide(room, state, CoopActor.Circle, GameCommand.Right, out destination, out distance, out stopped, out reason), "circle must slide toward partner");
        AssertEqual(new GridPoint(3, 2), destination, "partner must stop circle before overlap");
        AssertEqual(2, distance, "partner-stop distance");
        Assert(stopped, "partner stopper must be reported");

        CoopRoomDefinition goalPass = GeometryRoom();
        goalPass.CircleGoal = new GridPoint(2, 2);
        state = CoopRules.CreateInitialState(goalPass);
        Assert(CoopRules.TrySlide(goalPass, state, CoopActor.Circle, GameCommand.Right, out destination, out distance, out stopped, out reason), "goal line slide must be valid");
        AssertEqual(new GridPoint(3, 2), destination, "partner remains the stopper; goal itself is pass-through geometry");
    }

    private static void CheckOwnershipAndPass()
    {
        CoopRoomDefinition room = GeometryRoom();
        CoopSession session = CoopSession.Create(room);
        string before = CoopRules.StateFingerprint(room, session.State);
        CoopDispatchResult wrongSeat = session.Dispatch(CoopCommandFactory.Slide(CoopActor.Diamond, "wrong-seat", 0, GameCommand.Down));
        Assert(!wrongSeat.Accepted && wrongSeat.Reason == "wrong_active_actor", "other actor must not move on Circle turn");
        AssertEqual(before, CoopRules.StateFingerprint(room, session.State), "wrong seat must not mutate state");

        CoopDispatchResult passOne = session.Dispatch(CoopCommandFactory.Pass(CoopActor.Circle, "pass-circle", 0));
        Assert(passOne.Accepted, "active pass must be valid");
        AssertEqual(1, passOne.State.LogicalActionCount, "pass costs one team action");
        AssertEqual(CoopActor.Diamond, passOne.State.ActiveActor, "pass alternates actor");
        CoopDispatchResult passTwo = session.Dispatch(CoopCommandFactory.Pass(CoopActor.Diamond, "pass-diamond", 1));
        Assert(passTwo.Accepted, "second pass must be valid");
        AssertEqual(CoopRunStatus.Playing, passTwo.State.Status, "two passes must not win or fail");
        AssertEqual(CoopActor.Circle, passTwo.State.ActiveActor, "two passes return the turn without reset");

        CoopState soft = session.State;
        soft.CirclePosition = room.CircleGoal;
        soft.DiamondPosition = room.DiamondStart;
        soft.ActiveActor = CoopActor.Circle;
        Assert(!CoopRules.IsBothGoals(room, soft), "single endpoint cannot clear");
        Assert(CoopRules.TrySlide(room, soft, CoopActor.Circle, GameCommand.Left, out _, out _, out _, out _), "goal occupant must remain movable under soft goal rule");
    }

    private static void CheckIdempotencyAndUndo()
    {
        CoopRoomDefinition room = GeometryRoom();
        CoopSession session = CoopSession.Create(room);
        CoopDispatchResult first = session.Dispatch(CoopCommandFactory.Slide(CoopActor.Circle, "c0", 0, GameCommand.Right));
        Assert(first.Accepted, "first action accepted");
        CoopDispatchResult second = session.Dispatch(CoopCommandFactory.Pass(CoopActor.Diamond, "d1", 1));
        Assert(second.Accepted, "second action accepted");
        CoopDispatchResult duplicate = session.Dispatch(CoopCommandFactory.Slide(CoopActor.Circle, "c0", 0, GameCommand.Right));
        Assert(duplicate.Accepted && duplicate.Idempotent, "same command ID/payload must be idempotent");
        AssertEqual(session.State.AuthorityRevision, duplicate.State.AuthorityRevision, "delayed duplicate must expose current authoritative state");
        CoopDispatchResult conflict = session.Dispatch(CoopCommandFactory.Slide(CoopActor.Circle, "c0", 0, GameCommand.Down));
        Assert(!conflict.Accepted && conflict.Reason == "command_id_payload_conflict", "same ID with changed payload must reject");

        CoopDispatchResult request = session.Dispatch(CoopCommandFactory.RequestUndo(CoopActor.Circle, "undo-request", 2, "u1"));
        Assert(request.Accepted, "active actor may request undo");
        CoopDispatchResult approval = session.Dispatch(CoopCommandFactory.ResolveUndo(CoopActor.Diamond, "undo-approve", 3, "u1", true));
        Assert(approval.Accepted, "other actor may approve undo");
        AssertEqual(4L, approval.State.AuthorityRevision, "undo revision must grow instead of restoring old revision");
        AssertEqual(1, approval.State.LogicalActionCount, "undo restores logical action count");
        AssertEqual(CoopActor.Diamond, approval.State.ActiveActor, "undo restores actor before removed Diamond action");
        CoopDispatchResult stale = session.Dispatch(CoopCommandFactory.Pass(CoopActor.Diamond, "stale-pre-undo", 2));
        Assert(!stale.Accepted && stale.Reason == "stale_revision", "pre-undo delayed command must remain stale");
    }

    private static void CheckConsentAndRestart()
    {
        CoopRoomDefinition room = GeometryRoom();
        CoopSession session = CoopSession.Create(room);
        Assert(session.Dispatch(CoopCommandFactory.RequestRestart(CoopActor.Circle, "restart-request", 0, "r1")).Accepted, "restart request accepted");
        CoopDispatchResult invalidatingPass = session.Dispatch(CoopCommandFactory.Pass(CoopActor.Circle, "pass-invalidates", 1));
        Assert(invalidatingPass.Accepted && invalidatingPass.State.PendingConsent == null, "valid action must invalidate pending consent");
        CoopDispatchResult staleResolution = session.Dispatch(CoopCommandFactory.ResolveRestart(CoopActor.Diamond, "restart-late", 2, "r1", true));
        Assert(!staleResolution.Accepted && staleResolution.Reason == "consent_missing", "invalidated restart cannot resolve later");

        Assert(session.Dispatch(CoopCommandFactory.RequestRestart(CoopActor.Diamond, "restart-request-2", 2, "r2")).Accepted, "current actor may request restart");
        CoopDispatchResult restart = session.Dispatch(CoopCommandFactory.ResolveRestart(CoopActor.Circle, "restart-approve", 3, "r2", true));
        Assert(restart.Accepted, "other actor approves restart");
        AssertEqual(4L, restart.State.AuthorityRevision, "restart revision grows");
        AssertEqual(0, restart.State.LogicalActionCount, "restart resets logical count");
        AssertEqual(CoopActor.Circle, restart.State.ActiveActor, "restart returns initial actor");
    }

    private static void CheckSaveReplay()
    {
        if (C1 == null || C1Solution == null) CheckC1();
        CoopSession session = CoopSession.Create(C1);
        for (int index = 0; index < C1Solution.Commands.Length; index++) Assert(session.Dispatch(C1Solution.Commands[index]).Accepted, "C1 save setup command accepted");
        CoopSaveEnvelope envelope = CoopSaveCodec.Capture(session, "local-c1");
        CoopSession restored;
        string error;
        Assert(CoopSaveCodec.TryRestore(C1, envelope, out restored, out error), "save must restore: " + error);
        Assert(CoopRules.StatesEqual(session.State, restored.State), "restored state must equal saved state");
        envelope.StateFingerprint = "tampered";
        Assert(!CoopSaveCodec.TryRestore(C1, envelope, out restored, out error) && error == "state_fingerprint_mismatch", "tampered state fingerprint must reject");
    }

    private static CoopRoomDefinition GeometryRoom()
    {
        return new CoopRoomDefinition
        {
            Id = "geometry",
            Width = 8,
            Height = 8,
            Rows = new[] { "########", "#......#", "#......#", "#......#", "#......#", "#......#", "#......#", "########" },
            CircleStart = new GridPoint(1, 2),
            DiamondStart = new GridPoint(4, 2),
            CircleGoal = new GridPoint(6, 6),
            DiamondGoal = new GridPoint(1, 6),
            RulesVersion = "coop-rules-v1",
            ContentVersion = "geometry-v1"
        };
    }

    private static void Assert(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private static void AssertEqual<T>(T expected, T actual, string detail)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(detail + " expected=" + expected + " actual=" + actual);
    }

    [Serializable]
    private sealed class CheckRecord
    {
        public string Name;
        public bool Passed;
        public string Detail;
    }

    [Serializable]
    private sealed class CheckSummary
    {
        public string Suite;
        public int Passed;
        public int Failed;
        public CheckRecord[] Checks;
        public int C1OptimalActionCount;
        public int C1VisitedStates;
        public string C1ForbidCircleStopStatus;
        public int C1ForbidCircleStopCost;
        public string C1ForbidDiamondStopStatus;
        public int C1ForbidDiamondStopCost;
        public string C1RoomFingerprint;
        public string C1RulesVersion;
        public string C1ContentVersion;
        public CoopCommand[] C1Trace;
    }
}
