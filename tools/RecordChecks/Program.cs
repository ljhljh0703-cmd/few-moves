using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using Nectorial.SlideEscape.Raid;
using Nectorial.SlideEscape.Record;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };
    private static readonly List<CheckRecord> Records = new List<CheckRecord>();
    private static CoopRoomDefinition C1;
    private static RaidArenaDefinition Raid;

    private static int Main()
    {
        Run("capsule_codec_bounds_and_canonical_round_trip", CheckCodec);
        Run("raid_capsule_replays_actual_clear_only", CheckRaidCapsule);
        Run("default_raid_v2_capsule_replays_no_hit_clear", CheckDefaultRaidV2Capsule);
        Run("default_raid_v5_capsule_identity_separate_from_v4", CheckDefaultRaidV5Capsule);
        Run("legacy_raid_v4_capsule_identity_separate_from_v3", CheckLegacyRaidV4Capsule);
        Run("coop_capsule_replays_no_pass_clear_only", CheckCoopCapsule);
        Run("coop_effective_stack_undo_restart_and_erased_pass", CheckEffectiveStack);

        int passed = 0;
        for (int index = 0; index < Records.Count; index++) if (Records[index].Passed) passed++;
        Console.WriteLine(JsonSerializer.Serialize(new CheckSummary { Suite = "few-moves-record-core", Passed = passed, Failed = Records.Count - passed, Checks = Records.ToArray() }, JsonOptions));
        return passed == Records.Count ? 0 : 1;
    }

    private static void Run(string name, Action check)
    {
        try { check(); Records.Add(new CheckRecord { Name = name, Passed = true, Detail = "ok" }); }
        catch (Exception exception) { Records.Add(new CheckRecord { Name = name, Passed = false, Detail = exception.GetType().Name + ": " + exception.Message }); }
    }

    private static void CheckCodec()
    {
        RecordCapsule capsule = CoopCapsule("UDLR");
        string encoded;
        string error;
        Assert(RecordCapsuleCodec.TryEncode(capsule, out encoded, out error), "canonical capsule encodes: " + error);
        Assert(encoded.StartsWith("fm1.", StringComparison.Ordinal), "capsule uses fm1 prefix");
        RecordCapsule decoded;
        Assert(RecordCapsuleCodec.TryDecode(encoded, out decoded, out error), "canonical capsule decodes: " + error);
        string reencoded;
        Assert(RecordCapsuleCodec.TryEncode(decoded, out reencoded, out error) && encoded == reencoded, "capsule encoding is canonical");
        Assert(!RecordCapsuleCodec.TryDecode(encoded + "=", out decoded, out error), "padded capsule rejects");
        capsule.InputSequence = new string('U', RecordCapsuleRules.MaximumInputCharacters + 1);
        Assert(!RecordCapsuleCodec.TryEncode(capsule, out encoded, out error) && error == "capsule_input_length_invalid", "oversized input rejects before encoding");
        capsule.InputSequence = "UX";
        Assert(!RecordCapsuleCodec.TryEncode(capsule, out encoded, out error) && error.StartsWith("capsule_input_invalid", StringComparison.Ordinal), "non-UDLR input rejects");
    }

    private static void CheckRaidCapsule()
    {
        RaidArenaDefinition arena = EnsureRaid();
        RaidSolverResult solution = RaidSolver.FindSolution(arena, 200000);
        Assert(solution.Status == RaidSolverStatus.Solved, "raid solution exists");
        RecordCapsule capsule = new RecordCapsule
        {
            SchemaVersion = RecordCapsuleRules.SchemaVersion,
            ModeId = RecordCapsuleRules.RaidModeId,
            DefinitionId = arena.Id,
            RulesVersion = arena.RulesVersion,
            ContentVersion = arena.ContentVersion,
            DefinitionFingerprint = RaidRules.ArenaFingerprint(arena),
            InputSequence = Directions(solution.Moves)
        };
        RecordVerification verification;
        Assert(RecordCapsuleVerifier.TryVerifyRaid(arena, capsule, out verification), "raid capsule validates: " + verification.ErrorCode);
        Assert(verification.StatusCode == RaidRunStatus.Cleared.ToString() && verification.Hits >= 0, "raid verification derives terminal state");
        capsule.InputSequence += "U";
        Assert(!RecordCapsuleVerifier.TryVerifyRaid(arena, capsule, out verification) && verification.ErrorCode.StartsWith("input_after_cleared", StringComparison.Ordinal), "post-clear raid input rejects");
        capsule.InputSequence = Directions(solution.Moves);
        capsule.DefinitionFingerprint = "wrong";
        Assert(!RecordCapsuleVerifier.TryVerifyRaid(arena, capsule, out verification) && verification.ErrorCode == "definition_identity_mismatch", "raid fingerprint mismatch rejects");
    }

    private static void CheckDefaultRaidV2Capsule()
    {
        RaidArenaDefinition arena = Load<RaidArenaDefinition>("RaidArenas", "raid-01-v2.json");
        var capsule = new RecordCapsule
        {
            SchemaVersion = RecordCapsuleRules.SchemaVersion,
            ModeId = RecordCapsuleRules.RaidModeId,
            DefinitionId = arena.Id,
            RulesVersion = arena.RulesVersion,
            ContentVersion = arena.ContentVersion,
            DefinitionFingerprint = RaidRules.ArenaFingerprint(arena),
            InputSequence = "DURDLRU"
        };
        RecordVerification verification;
        Assert(RecordCapsuleVerifier.TryVerifyRaid(arena, capsule, out verification), "v2 no-hit record capsule validates: " + verification.ErrorCode);
        Assert(verification.Hits == 0 && verification.EffectiveActionCount == 7 && verification.StatusCode == RaidRunStatus.Cleared.ToString(), "v2 record derives no-hit clear outcome");
    }

    // v5 (no helper items) is the default; its records verify on their own identity and never cross with v4.
    private static void CheckDefaultRaidV5Capsule()
    {
        RaidArenaDefinition v5 = Load<RaidArenaDefinition>("RaidArenas", "raid-01-v5.json");
        RaidArenaDefinition v4 = Load<RaidArenaDefinition>("RaidArenas", "raid-01-v4.json");
        Assert(RaidContent.DefaultArenaId == v5.Id, "v5 is the default raid arena");
        RecordCapsule capsule = RaidCapsule(v5, "DURDULDRURLU");
        RecordVerification verification;
        Assert(RecordCapsuleVerifier.TryVerifyRaid(v5, capsule, out verification), "v5 record capsule validates: " + verification.ErrorCode);
        Assert(verification.Hits == 0 && verification.EffectiveActionCount == 12 && verification.StatusCode == RaidRunStatus.Cleared.ToString(), "v5 record derives the witness clear");
        Assert(!RecordCapsuleVerifier.TryVerifyRaid(v5, RaidCapsule(v4, "DURDULDRURLU"), out verification) && verification.ErrorCode == "definition_identity_mismatch", "a v4 record cannot verify against v5");
        Assert(!RecordCapsuleVerifier.TryVerifyRaid(v4, capsule, out verification) && verification.ErrorCode == "definition_identity_mismatch", "a v5 record cannot verify against v4");
    }

    // v4 (Slow only) records keep verifying on their own identity; v3 and v4 records are not interchangeable.
    private static void CheckLegacyRaidV4Capsule()
    {
        RaidArenaDefinition v4 = Load<RaidArenaDefinition>("RaidArenas", "raid-01-v4.json");
        RaidArenaDefinition v3 = Load<RaidArenaDefinition>("RaidArenas", "raid-01-v3.json");
        RecordCapsule capsule = RaidCapsule(v4, "DURDULDRURLU");
        RecordVerification verification;
        Assert(RecordCapsuleVerifier.TryVerifyRaid(v4, capsule, out verification), "v4 record capsule validates: " + verification.ErrorCode);
        Assert(verification.Hits == 0 && verification.EffectiveActionCount == 12 && verification.StatusCode == RaidRunStatus.Cleared.ToString(), "v4 record derives the no-hit twelve-action clear");
        RecordCapsule v3Capsule = RaidCapsule(v3, "DURDULDRURLU");
        Assert(!RecordCapsuleVerifier.TryVerifyRaid(v4, v3Capsule, out verification) && verification.ErrorCode == "definition_identity_mismatch", "a v3 record cannot verify against v4");
        Assert(!RecordCapsuleVerifier.TryVerifyRaid(v3, capsule, out verification) && verification.ErrorCode == "definition_identity_mismatch", "a v4 record cannot verify against v3");
        Assert(RaidRules.ArenaFingerprint(v4) != RaidRules.ArenaFingerprint(v3), "v3 and v4 fingerprints differ");
    }

    private static RecordCapsule RaidCapsule(RaidArenaDefinition arena, string input)
    {
        return new RecordCapsule
        {
            SchemaVersion = RecordCapsuleRules.SchemaVersion,
            ModeId = RecordCapsuleRules.RaidModeId,
            DefinitionId = arena.Id,
            RulesVersion = arena.RulesVersion,
            ContentVersion = arena.ContentVersion,
            DefinitionFingerprint = RaidRules.ArenaFingerprint(arena),
            InputSequence = input
        };
    }

    private static void CheckCoopCapsule()
    {
        CoopRoomDefinition room = EnsureC1();
        CoopSolverResult solution = NoPassSolution(room);
        RecordCapsule capsule = CoopCapsule(Directions(solution.Commands));
        RecordVerification verification;
        Assert(RecordCapsuleVerifier.TryVerifyCoop(room, capsule, out verification), "co-op capsule validates: " + verification.ErrorCode);
        Assert(verification.StatusCode == CoopRunStatus.Cleared.ToString() && verification.LogicalActionCount == solution.Commands.Length, "co-op verification derives clear and count");
        capsule.InputSequence += "U";
        Assert(!RecordCapsuleVerifier.TryVerifyCoop(room, capsule, out verification) && verification.ErrorCode.StartsWith("input_after_cleared", StringComparison.Ordinal), "post-clear co-op input rejects");
    }

    private static void CheckEffectiveStack()
    {
        CoopRoomDefinition room = EnsureC1();
        CoopSolverResult solution = NoPassSolution(room);

        CoopSession undoRestart = CoopSession.Create(room);
        DispatchSlide(undoRestart, solution.Commands[0].Direction, "undo-seed");
        CoopState undoRequestState = undoRestart.State;
        CoopDispatchResult undoRequest = undoRestart.Dispatch(CoopCommandFactory.RequestUndo(undoRequestState.ActiveActor, "undo-request", undoRequestState.AuthorityRevision, "undo-1"));
        Assert(undoRequest.Accepted, "undo request accepted");
        Assert(undoRestart.Dispatch(CoopCommandFactory.ResolveUndo(CoopRules.Opponent(undoRequestState.ActiveActor), "undo-approve", undoRequest.State.AuthorityRevision, "undo-1", true)).Accepted, "undo approval accepted");
        DispatchSlide(undoRestart, solution.Commands[0].Direction, "restart-seed");
        CoopState restartRequestState = undoRestart.State;
        CoopDispatchResult restartRequest = undoRestart.Dispatch(CoopCommandFactory.RequestRestart(restartRequestState.ActiveActor, "restart-request", restartRequestState.AuthorityRevision, "restart-1"));
        Assert(restartRequest.Accepted, "restart request accepted");
        Assert(undoRestart.Dispatch(CoopCommandFactory.ResolveRestart(CoopRules.Opponent(restartRequestState.ActiveActor), "restart-approve", restartRequest.State.AuthorityRevision, "restart-1", true)).Accepted, "restart approval accepted");
        DispatchSolution(undoRestart, solution.Commands, "final");
        Assert(undoRestart.State.Status == CoopRunStatus.Cleared, "undo/restart session clears");
        RecordCapsule capsule;
        RecordVerification verification;
        string error;
        Assert(CoopEffectiveStack.TryCreateCompletedCapsule(room, undoRestart.ExportReplay(), undoRestart.State, out capsule, out verification, out error), "undo/restart effective stack validates: " + error);
        Assert(capsule.InputSequence == Directions(solution.Commands), "undo and restart remove superseded actions");

        CoopSession erasedPass = CoopSession.Create(room);
        CoopState passState = erasedPass.State;
        Assert(erasedPass.Dispatch(CoopCommandFactory.Pass(passState.ActiveActor, "legacy-pass", passState.AuthorityRevision)).Accepted, "legacy pass setup accepted");
        CoopState passUndoState = erasedPass.State;
        CoopDispatchResult passUndoRequest = erasedPass.Dispatch(CoopCommandFactory.RequestUndo(passUndoState.ActiveActor, "legacy-undo-request", passUndoState.AuthorityRevision, "legacy-undo"));
        Assert(passUndoRequest.Accepted, "legacy pass undo requested");
        Assert(erasedPass.Dispatch(CoopCommandFactory.ResolveUndo(CoopRules.Opponent(passUndoState.ActiveActor), "legacy-undo-approve", passUndoRequest.State.AuthorityRevision, "legacy-undo", true)).Accepted, "legacy pass undo approved");
        DispatchSolution(erasedPass, solution.Commands, "legacy-final");
        Assert(CoopEffectiveStack.TryCreateCompletedCapsule(room, erasedPass.ExportReplay(), erasedPass.State, out capsule, out verification, out error), "erased legacy pass does not poison a record: " + error);
        Assert(capsule.InputSequence == Directions(solution.Commands), "erased pass is absent from final capsule");
    }

    private static void DispatchSolution(CoopSession session, CoopCommand[] commands, string prefix)
    {
        for (int index = 0; index < commands.Length; index++) DispatchSlide(session, commands[index].Direction, prefix + "-" + index.ToString());
    }

    private static void DispatchSlide(CoopSession session, GameCommand direction, string commandId)
    {
        CoopState state = session.State;
        CoopDispatchResult result = session.Dispatch(CoopCommandFactory.Slide(state.ActiveActor, commandId, state.AuthorityRevision, direction));
        Assert(result.Accepted && !result.Idempotent, "slide accepted: " + commandId + ":" + result.Reason);
    }

    private static CoopSolverResult NoPassSolution(CoopRoomDefinition room)
    {
        CoopSolverResult solution = CoopSolver.FindSolution(room, 500000, new CoopSolverOptions { AllowPass = false });
        Assert(solution.Status == CoopSolverStatus.Solved, "no-pass C1 solution exists");
        return solution;
    }

    private static RecordCapsule CoopCapsule(string input)
    {
        CoopRoomDefinition room = EnsureC1();
        return new RecordCapsule
        {
            SchemaVersion = RecordCapsuleRules.SchemaVersion,
            ModeId = RecordCapsuleRules.CoopModeId,
            DefinitionId = room.Id,
            RulesVersion = room.RulesVersion,
            ContentVersion = room.ContentVersion,
            DefinitionFingerprint = CoopRules.RoomFingerprint(room),
            InputSequence = input
        };
    }

    private static string Directions(RaidMove[] moves)
    {
        var values = new char[moves.Length];
        for (int index = 0; index < moves.Length; index++) values[index] = Direction(moves[index].Direction);
        return new string(values);
    }

    private static string Directions(CoopCommand[] commands)
    {
        var values = new char[commands.Length];
        for (int index = 0; index < commands.Length; index++) values[index] = Direction(commands[index].Direction);
        return new string(values);
    }

    private static char Direction(GameCommand direction)
    {
        if (direction == GameCommand.Up) return 'U';
        if (direction == GameCommand.Down) return 'D';
        if (direction == GameCommand.Left) return 'L';
        if (direction == GameCommand.Right) return 'R';
        throw new InvalidOperationException("Unexpected direction.");
    }

    private static CoopRoomDefinition EnsureC1()
    {
        if (C1 == null) C1 = Load<CoopRoomDefinition>("CoopRooms", "coop-c1.json");
        return CoopRules.CloneRoom(C1);
    }

    private static RaidArenaDefinition EnsureRaid()
    {
        if (Raid == null) Raid = Load<RaidArenaDefinition>("RaidArenas", "raid-01.json");
        return RaidRules.CloneArena(Raid);
    }

    private static T Load<T>(string directory, string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, directory, file);
        T value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        if (value == null) throw new InvalidOperationException("Fixture did not deserialize: " + file);
        return value;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CheckRecord { public string Name; public bool Passed; public string Detail; }
    private sealed class CheckSummary { public string Suite; public int Passed; public int Failed; public CheckRecord[] Checks; }
}
