using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Raid;

internal static class Program
{
    private static readonly List<CheckRecord> Records = new List<CheckRecord>();
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };
    private static RaidArenaDefinition Arena;
    private static RaidSolverResult WinningSolution;

    private static int Main()
    {
        Run("raid_arena_solver_trace_and_all_items", CheckWinningTrace);
        Run("default_raid_v2_witnesses_and_content_identity", CheckDefaultV2Witnesses);
        Run("microstep_vacated_tail_and_collision_before_pickup", CheckCollisionSemantics);
        Run("shield_recovery_keeps_snake_phase", CheckShieldRecovery);
        Run("slow_and_magnet_timing", CheckBuffTiming);
        Run("armed_only_after_alive_action_and_later_contact", CheckArmedTiming);
        Run("zero_move_clone_restart_and_alias_isolation", CheckStateIsolationAndRestart);
        Run("duplicate_id_replay_and_save_round_trip", CheckDuplicateReplayAndSave);
        Run("save_requires_complete_attempt_transcript", CheckSaveTranscriptIntegrity);

        int passed = 0;
        for (int index = 0; index < Records.Count; index++) if (Records[index].Passed) passed++;
        Console.WriteLine(JsonSerializer.Serialize(new CheckSummary
        {
            Suite = "few-moves-raid-core",
            Passed = passed,
            Failed = Records.Count - passed,
            Checks = Records.ToArray(),
            ArenaFingerprint = Arena == null ? null : RaidRules.ArenaFingerprint(Arena),
            WinningTrace = WinningSolution == null ? new RaidMove[0] : WinningSolution.Moves,
            WinningCost = WinningSolution == null ? -1 : WinningSolution.OptimalActionCount,
            WinningVisited = WinningSolution == null ? -1 : WinningSolution.VisitedCount
        }, JsonOptions));
        return passed == Records.Count ? 0 : 1;
    }

    private static void Run(string name, Action action)
    {
        try { action(); Records.Add(new CheckRecord { Name = name, Passed = true, Detail = "ok" }); }
        catch (Exception exception) { Records.Add(new CheckRecord { Name = name, Passed = false, Detail = exception.GetType().Name + ": " + exception.Message.Replace('\n', ' ') }); }
    }

    private static void CheckWinningTrace()
    {
        Arena = LoadArena();
        AssertEqual(0, RaidRules.ValidateArena(Arena).Length, "raid-01 must validate");
        WinningSolution = RaidSolver.FindSolution(Arena, 200000);
        AssertEqual(RaidSolverStatus.Solved, WinningSolution.Status, "initial arena must solve");
        AssertEqual(5, WinningSolution.OptimalActionCount, "authored initial trace must be measured at five actions");
        AssertEqual(WinningSolution.OptimalActionCount, WinningSolution.Moves.Length, "reported optimum matches trace length");

        RaidSession session = RaidSession.Create(Arena);
        var itemKinds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < WinningSolution.Moves.Length; index++)
        {
            RaidDispatchResult result = session.Dispatch(WinningSolution.Moves[index]);
            Assert(result.Accepted && !result.Idempotent, "solver action accepted: " + index.ToString());
            for (int eventIndex = 0; eventIndex < result.Events.Length; eventIndex++)
            {
                RaidEvent item = result.Events[eventIndex];
                if (item.Type == "item_collected") itemKinds.Add(item.Detail.Split(':')[0]);
            }
        }
        AssertEqual(RaidRunStatus.Cleared, session.State.Status, "winning trace clears from initial state");
        Assert(itemKinds.Contains("Shield") && itemKinds.Contains("Magnet") && itemKinds.Contains("Slow"), "winning trace must collect each distinct item kind");
    }

    private static void CheckDefaultV2Witnesses()
    {
        RaidArenaDefinition arena = LoadArena("raid-01-v2.json");
        AssertEqual(RaidContent.DefaultArenaId, arena.Id, "default Raid content ID is v2");
        AssertEqual("9b1d5ed6c3cdda15d42956570fee57de4c629718a0d45b49f701470c287430da", RaidRules.ArenaFingerprint(arena), "default Raid v2 fingerprint is exact");
        RaidSolverResult shortest = RaidSolver.FindSolution(arena, 200000);
        AssertEqual(RaidSolverStatus.Solved, shortest.Status, "v2 shortest solver status");
        AssertEqual(5, shortest.OptimalActionCount, "v2 shortest cost");
        AssertEqual("RDULR", Directions(shortest.Moves), "v2 shortest trace");
        RaidState shortState = Replay(arena, "RDULR");
        AssertEqual(RaidRunStatus.Cleared, shortState.Status, "v2 short trace clears");
        AssertEqual(1, shortState.Hits, "v2 short trace uses one hit");
        RaidState noHitState = Replay(arena, "DURDLRU");
        AssertEqual(RaidRunStatus.Cleared, noHitState.Status, "v2 no-hit trace clears");
        AssertEqual(0, noHitState.Hits, "v2 no-hit trace has no hits");
        AssertEqual(3, noHitState.CollectedItemIds.Length, "v2 no-hit trace collects three items");
        RaidState danger = Replay(arena, "DRUU");
        AssertEqual(RaidRunStatus.Failed, danger.Status, "v2 danger trace fails");
    }

    private static void CheckCollisionSemantics()
    {
        RaidArenaDefinition collisionArena = RaidRules.CloneArena(EnsureArena());
        collisionArena.TailFragments[0].Position = new GridPoint(6, 5);
        AssertEqual(0, RaidRules.ValidateArena(collisionArena).Length, "collision pickup arena must validate");
        RaidState collisionState = RaidRules.CreateInitialState(collisionArena);
        collisionState.PlayerPosition = new GridPoint(6, 6);
        collisionState.SnakeHeadIndex = 5;
        collisionState.ShieldCharges = 0;
        RaidDispatchResult collision = RaidRules.Step(collisionArena, collisionState, GameCommand.Up);
        Assert(collision.Accepted, "collision path is a valid action");
        AssertEqual(RaidRunStatus.Failed, collision.State.Status, "unshielded collision fails");
        AssertEqual(RaidCollisionKind.SharedDestination, collision.Frames[0].Collision, "shared destination has explicit precedence");
        Assert(!Contains(collision.State.CollectedTailIds, "tail-a"), "collision-cell tail is not collected");

        RaidArenaDefinition vacatedArena = RaidRules.CloneArena(EnsureArena());
        RaidState vacated = RaidRules.CreateInitialState(vacatedArena);
        vacated.PlayerPosition = new GridPoint(3, 4);
        vacated.SnakeHeadIndex = 5;
        vacated.ShieldCharges = 0;
        RaidDispatchResult vacatedResult = RaidRules.Step(vacatedArena, vacated, GameCommand.Right);
        Assert(vacatedResult.Accepted, "vacated-tail path is valid");
        AssertEqual(new GridPoint(4, 4), vacatedResult.Frames[0].AttemptedPlayerAfter, "first microstep enters the vacated tail cell");
        AssertEqual(RaidCollisionKind.None, vacatedResult.Frames[0].Collision, "vacated SnakeBefore tail cell is safe without edge swap");
    }

    private static void CheckShieldRecovery()
    {
        RaidArenaDefinition arena = RaidRules.CloneArena(EnsureArena());
        RaidState state = RaidRules.CreateInitialState(arena);
        state.PlayerPosition = new GridPoint(6, 6);
        state.SnakeHeadIndex = 5;
        state.ShieldCharges = 1;
        RaidDispatchResult result = RaidRules.Step(arena, state, GameCommand.Up);
        Assert(result.Accepted && result.ActionCancelled, "shield collision ends the valid action");
        AssertEqual(RaidFrameOutcome.Shielded, result.Frames[0].Outcome, "shield resolves one collision");
        AssertEqual(6, result.State.SnakeHeadIndex, "shield recovery retains advanced snake phase");
        AssertEqual(new GridPoint(6, 6), result.State.PlayerPosition, "prior safe path location is preferred for recovery");
        AssertEqual(0, result.State.ShieldCharges, "shield is consumed exactly once");
        AssertEqual(1, result.State.Hits, "shield collision records hit count");
        Assert(!Contains(RaidRules.SnakeBody(arena, result.State.SnakeHeadIndex), result.State.PlayerPosition), "recovery is safe against SnakeAfter");
    }

    private static void CheckBuffTiming()
    {
        RaidSession session = RaidSession.Create(EnsureArena());
        RaidDispatchResult right = session.Dispatch(WinningSolution.Moves[0]);
        Assert(right.Accepted, "first action picks slow");
        AssertEqual(4, right.State.SlowStepsRemaining, "slow picked now affects following microsteps only");
        RaidDispatchResult left = session.Dispatch(WinningSolution.Moves[1]);
        AssertEqual(5, left.Frames.Length, "left slide contains five microsteps");
        for (int index = 0; index < 4; index++) Assert(!left.Frames[index].SnakeMoved, "existing slow delays microstep " + index.ToString());
        Assert(left.Frames[4].SnakeMoved, "snake resumes after configured slow duration");
        RaidDispatchResult down = session.Dispatch(WinningSolution.Moves[2]);
        RaidDispatchResult magnet = session.Dispatch(WinningSolution.Moves[3]);
        Assert(down.Accepted && magnet.Accepted, "pre-magnet actions accepted");
        Assert(ContainsEvent(magnet.Events, "tail_magnet_collected", "tail-b"), "magnet is actually used to absorb a nearby tail");
        Assert(Contains(magnet.State.CollectedTailIds, "tail-b"), "magnet tail persists in state");
    }

    private static void CheckArmedTiming()
    {
        RaidSession session = RaidSession.Create(EnsureArena());
        for (int index = 0; index < 4; index++) Assert(session.Dispatch(WinningSolution.Moves[index]).Accepted, "pre-armed trace accepted");
        AssertEqual(RaidRunStatus.Armed, session.State.Status, "last tail arms only after action end");
        RaidDispatchResult armingAction = session.Dispatch(WinningSolution.Moves[3]);
        Assert(armingAction.Idempotent, "replaying arming command is idempotent and cannot replay Armed animation");
        RaidSession replay = RaidSession.Create(EnsureArena());
        RaidDispatchResult fourth = null;
        for (int index = 0; index < 4; index++) fourth = replay.Dispatch(WinningSolution.Moves[index]);
        for (int index = 0; index < fourth.Frames.Length; index++) Assert(fourth.Frames[index].Outcome != RaidFrameOutcome.Cleared, "arming action has no same-action clear frame");
        RaidDispatchResult clear = replay.Dispatch(WinningSolution.Moves[4]);
        AssertEqual(RaidRunStatus.Cleared, clear.State.Status, "next valid body contact clears Armed raid");
        bool clearContact = false;
        for (int index = 0; index < clear.Frames.Length; index++) if (clear.Frames[index].Collision != RaidCollisionKind.None && clear.Frames[index].Outcome == RaidFrameOutcome.Cleared) clearContact = true;
        Assert(clearContact, "armed body contact wins before ordinary damage");
    }

    private static void CheckStateIsolationAndRestart()
    {
        RaidArenaDefinition arena = EnsureArena();
        RaidSession session = RaidSession.Create(arena);
        RaidState initial = session.State;
        RaidDispatchResult zero = session.Dispatch(new RaidMove { CommandId = "blocked", Direction = GameCommand.Up });
        Assert(!zero.Accepted && zero.Reason == "blocked_zero", "blocked zero remains a no-effect action");
        AssertEqual(RaidRules.StateFingerprint(arena, initial), RaidRules.StateFingerprint(arena, zero.State), "blocked zero keeps state unchanged");
        RaidArenaDefinition exposedArena = session.Arena;
        exposedArena.Rows[1] = "########";
        Assert(!string.Equals(session.Arena.Rows[1], "########", StringComparison.Ordinal), "arena getter does not expose active array aliases");
        RaidState exposedState = session.State;
        exposedState.CollectedTailIds = new[] { "tail-a" };
        AssertEqual(0, session.State.CollectedTailIds.Length, "state getter does not expose collection aliases");
        Assert(session.Dispatch(WinningSolution.Moves[0]).Accepted, "action before restart accepted");
        RaidDispatchResult restarted = session.Restart();
        Assert(restarted.Accepted && restarted.Reason == "restarted", "restart result is explicit");
        Assert(RaidRules.StatesEqual(initial, restarted.State), "restart returns the full initial state");
    }

    private static void CheckDuplicateReplayAndSave()
    {
        RaidSession session = RaidSession.Create(EnsureArena());
        RaidMove blocked = new RaidMove { CommandId = "replay-blocked", Direction = GameCommand.Up };
        RaidDispatchResult firstBlocked = session.Dispatch(blocked);
        RaidDispatchResult duplicateBlocked = session.Dispatch(new RaidMove { CommandId = "replay-blocked", Direction = GameCommand.Up });
        RaidDispatchResult conflict = session.Dispatch(new RaidMove { CommandId = "replay-blocked", Direction = GameCommand.Right });
        Assert(!firstBlocked.Accepted && !firstBlocked.Idempotent, "first blocked attempt is rejected");
        Assert(!duplicateBlocked.Accepted && duplicateBlocked.Idempotent, "same rejected ID is idempotent");
        Assert(!conflict.Accepted && conflict.Reason == "command_id_payload_conflict", "changed ID payload conflicts");
        for (int index = 0; index < WinningSolution.Moves.Length; index++) Assert(session.Dispatch(WinningSolution.Moves[index]).Accepted, "winning replay action accepted");
        RaidReplay replay = session.ExportReplay();
        RaidSession replayed;
        string replayError;
        Assert(RaidReplayCodec.TryReplay(EnsureArena(), replay, out replayed, out replayError), "attempt replay restores: " + replayError);
        Assert(RaidRules.StatesEqual(session.State, replayed.State), "attempt replay reaches identical state");
        RaidSaveEnvelope envelope = RaidSaveCodec.Capture(session);
        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        RaidSaveEnvelope parsed = JsonSerializer.Deserialize<RaidSaveEnvelope>(json, JsonOptions);
        RaidSession restored;
        string restoreError;
        Assert(RaidSaveCodec.TryRestore(EnsureArena(), parsed, out restored, out restoreError), "save restore succeeds: " + restoreError);
        Assert(RaidRules.StatesEqual(session.State, restored.State), "save restore reaches identical state");
        Assert(!object.ReferenceEquals(parsed.State.CollectedTailIds, restored.State.CollectedTailIds), "restored state owns arrays");
        RaidDispatchResult restoredConflict = restored.Dispatch(new RaidMove { CommandId = "replay-blocked", Direction = GameCommand.Right });
        Assert(!restoredConflict.Accepted && restoredConflict.Reason == "command_id_payload_conflict", "restored rejected command ID remains a payload conflict");
    }

    private static void CheckSaveTranscriptIntegrity()
    {
        RaidSession session = RaidSession.Create(EnsureArena());
        Assert(!session.Dispatch(new RaidMove { CommandId = "rejected-ledger-id", Direction = GameCommand.Up }).Accepted, "rejected ledger setup must reject");
        Assert(session.Dispatch(new RaidMove { CommandId = "accepted-ledger-id", Direction = GameCommand.Right }).Accepted, "accepted ledger setup must accept");

        RaidSaveEnvelope missingAttempts = RaidSaveCodec.Capture(session);
        missingAttempts.Replay.Attempts = null;
        RaidSession restored;
        string error;
        Assert(!RaidSaveCodec.TryRestore(EnsureArena(), missingAttempts, out restored, out error) && error == "attempt_transcript_missing", "missing attempt transcript must reject current save schema");

        RaidSaveEnvelope tamperedProjection = RaidSaveCodec.Capture(session);
        tamperedProjection.Replay.Moves[0] = new RaidMove { CommandId = "accepted-ledger-id", Direction = GameCommand.Left };
        Assert(!RaidSaveCodec.TryRestore(EnsureArena(), tamperedProjection, out restored, out error) && error.StartsWith("accepted_moves_projection_mismatch", StringComparison.Ordinal), "tampered accepted move projection must reject restore");

        RaidSaveEnvelope tamperedAttemptCount = RaidSaveCodec.Capture(session);
        tamperedAttemptCount.Replay.AttemptCount = 0;
        Assert(!RaidSaveCodec.TryRestore(EnsureArena(), tamperedAttemptCount, out restored, out error) && error == "attempt_count_mismatch", "attempt count must bind the complete rejected-command ledger");
    }

    private static RaidArenaDefinition EnsureArena()
    {
        if (Arena == null) Arena = LoadArena();
        if (WinningSolution == null) WinningSolution = RaidSolver.FindSolution(Arena, 200000);
        return RaidRules.CloneArena(Arena);
    }

    private static RaidArenaDefinition LoadArena()
    {
        return LoadArena("raid-01.json");
    }

    private static RaidArenaDefinition LoadArena(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "RaidArenas", fileName);
        RaidArenaDefinition arena = JsonSerializer.Deserialize<RaidArenaDefinition>(File.ReadAllText(path), JsonOptions);
        if (arena == null) throw new InvalidOperationException("Raid arena JSON did not deserialize.");
        return arena;
    }

    private static RaidState Replay(RaidArenaDefinition arena, string trace)
    {
        RaidState state = RaidRules.CreateInitialState(arena);
        for (int index = 0; index < trace.Length; index++)
        {
            GameCommand direction = trace[index] == 'U' ? GameCommand.Up : trace[index] == 'D' ? GameCommand.Down : trace[index] == 'L' ? GameCommand.Left : GameCommand.Right;
            RaidDispatchResult result = RaidRules.Step(arena, state, direction);
            Assert(result.Accepted, "v2 replay action accepted: " + index.ToString());
            state = result.State;
        }
        return state;
    }

    private static string Directions(RaidMove[] moves)
    {
        var values = new char[moves.Length];
        for (int index = 0; index < moves.Length; index++) values[index] = moves[index].Direction == GameCommand.Up ? 'U' : moves[index].Direction == GameCommand.Down ? 'D' : moves[index].Direction == GameCommand.Left ? 'L' : 'R';
        return new string(values);
    }

    private static bool Contains(string[] values, string value)
    {
        if (values == null) return false;
        for (int index = 0; index < values.Length; index++) if (string.Equals(values[index], value, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool Contains(GridPoint[] values, GridPoint value)
    {
        for (int index = 0; index < values.Length; index++) if (values[index].Equals(value)) return true;
        return false;
    }

    private static bool ContainsEvent(RaidEvent[] events, string type, string detail)
    {
        for (int index = 0; index < events.Length; index++) if (events[index].Type == type && events[index].Detail == detail) return true;
        return false;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message + " expected=" + expected + " actual=" + actual);
    }

    private sealed class CheckRecord { public string Name; public bool Passed; public string Detail; }
    private sealed class CheckSummary { public string Suite; public int Passed; public int Failed; public CheckRecord[] Checks; public string ArenaFingerprint; public RaidMove[] WinningTrace; public int WinningCost; public int WinningVisited; }
}
