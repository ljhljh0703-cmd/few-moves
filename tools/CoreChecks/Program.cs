using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nectorial.TurnEscape;

internal static class Program
{
    private const string ContentVersion = "content-v2";
    private const int ProductionRoomMaxVisitedStates = 65536;
    private const int ExpectedProductionRoomCount = 12;
    private static readonly int[] ExpectedDoorCounts = { 0, 0, 0, 1, 1, 0, 0, 1, 1, 2, 1, 2 };
    private static readonly int[] ExpectedSwitchCounts = { 0, 0, 0, 1, 1, 0, 0, 1, 1, 2, 1, 2 };
    private static readonly int[] ExpectedGuardCounts = { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2 };
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    private static readonly List<CheckRecord> Records = new List<CheckRecord>();
    private static readonly string DefaultRoomsDirectory = Path.Combine(AppContext.BaseDirectory, "Rooms");
    private static GameCommand[] room01Solution = new GameCommand[0];
    private static string selectedRoomsDirectory;
    private static CatalogEvaluation productionCatalog;

    private static int Main(string[] args)
    {
        string roomsArgumentError;
        if (TrySelectRoomsDirectory(args, out selectedRoomsDirectory, out roomsArgumentError))
        {
            Run("production_catalog_and_progression", CheckProductionRoomCatalog);
        }
        else
        {
            selectedRoomsDirectory = DefaultRoomsDirectory;
            productionCatalog = FailedCatalog(selectedRoomsDirectory, roomsArgumentError);
            Records.Add(new CheckRecord { Name = "production_catalog_and_progression", Passed = false, Detail = roomsArgumentError });
        }

        Run("room01_solution_replay", CheckRoom01SolutionReplay);
        Run("impossible_room_detection", CheckImpossibleRoom);
        Run("solver_budget_not_success", CheckSolverBudget);
        Run("initial_state_room_validation", CheckInitialStateRoomValidation);
        Run("invalid_command_wall_no_turn", CheckInvalidCommandAndWall);
        Run("door_switch_transition", CheckDoorAndSwitch);
        Run("guard_prediction_motion_capture", CheckGuardPredictionMotionAndCapture);
        Run("vacated_cell_and_swap_survive", CheckVacatedCellAndSwapSurvive);
        Run("simultaneous_collision_priority_and_all_guards", CheckSimultaneousCollisionPriorityAndAllGuards);
        Run("terminal_state_ignores_commands", CheckTerminalStates);
        Run("step_clones_input_state", CheckStepClonesInput);
        Run("fingerprint_future_state_and_content", CheckFingerprints);
        Run("deterministic_replay", CheckDeterministicReplay);
        Run("save_json_round_trip_and_rejections", CheckSaveRoundTripAndRejections);
        Run("save_terminal_state_validation", CheckSaveTerminalStateValidation);
        Run("malformed_room_validation", CheckMalformedRooms);
        Run("catalog_negative_fixtures", CheckCatalogNegativeFixtures);

        int passed = 0;
        for (int i = 0; i < Records.Count; i++)
        {
            if (Records[i].Passed)
            {
                passed++;
            }
        }

        CheckSummary summary = new CheckSummary
        {
            Suite = "nectorial-turn-escape-core",
            Passed = passed,
            Failed = Records.Count - passed,
            Checks = Records.ToArray(),
            Room01SolutionCommands = CommandNames(room01Solution),
            NormalInputSequence = NormalInputSequence(room01Solution),
            CatalogDirectory = productionCatalog == null ? selectedRoomsDirectory : productionCatalog.Directory,
            CatalogPassed = productionCatalog != null && productionCatalog.Passed,
            CatalogErrors = productionCatalog == null ? new string[] { "catalog_not_evaluated" } : productionCatalog.Errors,
            ProductionRooms = productionCatalog == null ? new CatalogRoomResult[0] : productionCatalog.Rooms
        };
        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
        return summary.Failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action check)
    {
        try
        {
            check();
            Records.Add(new CheckRecord { Name = name, Passed = true, Detail = "ok" });
        }
        catch (Exception exception)
        {
            Records.Add(new CheckRecord
            {
                Name = name,
                Passed = false,
                Detail = exception.GetType().Name + ": " + SingleLine(exception.Message)
            });
        }
    }

    private static void CheckRoom01SolutionReplay()
    {
        RoomDefinition room = LoadRoom01();
        AssertNoRoomErrors(room);

        SolverResult solution = Solver.FindSolution(room, 4096);
        AssertEqual(SolverStatus.Solved, solution.Status, "room-01 must have a real solver solution");
        Assert(solution.Commands.Length > 0, "room-01 solution must include commands");
        Assert(solution.VisitedCount > 0, "solver must report visited states");

        GameState state = GameEngine.Create(room);
        for (int i = 0; i < solution.Commands.Length; i++)
        {
            StepResult result = GameEngine.Step(room, state, solution.Commands[i]);
            Assert(result.Accepted, "solver command must be accepted at index " + i);
            state = result.State;
        }

        AssertEqual(RunStatus.Cleared, state.Status, "replaying the reported solution must clear room-01");
        AssertEqual(solution.Commands.Length, state.Turn, "every reported command must advance one turn");
        room01Solution = CopyCommands(solution.Commands);
    }

    private static void CheckImpossibleRoom()
    {
        RoomDefinition room = BuildImpossibleRoom();
        AssertNoRoomErrors(room);

        SolverResult result = Solver.FindSolution(room, 128);
        AssertEqual(SolverStatus.Unsolvable, result.Status, "isolated exit must be unsolvable");
        AssertEqual(0, result.Commands.Length, "unsolvable result must not report a command path");
    }

    private static void CheckSolverBudget()
    {
        RoomDefinition room = LoadRoom01();
        SolverResult result = Solver.FindSolution(room, 1);
        AssertEqual(SolverStatus.LimitReached, result.Status, "one visited state cannot prove room-01 solved");
        Assert(result.Status != SolverStatus.Solved, "budget exhaustion must not be success");
        AssertEqual(0, result.Commands.Length, "limited result must not return an unverified path");
    }

    private static void CheckInitialStateRoomValidation()
    {
        RoomDefinition sameStartAndExit = BuildDoorOnlyRoom();
        sameStartAndExit.Exit = sameStartAndExit.Start;
        Assert(Contains(GameEngine.ValidateRoom(sameStartAndExit), "start_equals_exit"), "start and exit must be different cells");
        AssertEqual(SolverStatus.InvalidRoom, Solver.FindSolution(sameStartAndExit, 16).Status, "invalid start/exit room must not reach state creation in solver");

        RoomDefinition closedDoorAtStart = BuildDoorOnlyRoom();
        closedDoorAtStart.Doors[0].Position = closedDoorAtStart.Start;
        Assert(Contains(GameEngine.ValidateRoom(closedDoorAtStart), "door_on_start:0"), "initially closed door cannot occupy player start");
        AssertEqual(SolverStatus.InvalidRoom, Solver.FindSolution(closedDoorAtStart, 16).Status, "invalid door-on-start room must not reach state creation in solver");
    }

    private static void CheckInvalidCommandAndWall()
    {
        RoomDefinition room = LoadRoom01();
        GameState state = GameEngine.Create(room);
        string before = GameEngine.Fingerprint(room, state, true);

        StepResult invalid = GameEngine.Step(room, state, (GameCommand)99);
        Assert(!invalid.Accepted, "unknown enum value must be rejected");
        AssertEqual("invalid_command", invalid.Reason, "unknown enum reason");
        AssertEqual(before, GameEngine.Fingerprint(room, state, true), "invalid command must not mutate input state");
        AssertEqual(before, GameEngine.Fingerprint(room, invalid.State, true), "invalid command result must preserve state");

        StepResult wall = GameEngine.Step(room, state, GameCommand.Up);
        Assert(!wall.Accepted, "wall move must be rejected");
        AssertEqual("blocked_wall", wall.Reason, "wall reason");
        AssertEqual(0, state.Turn, "wall move must not advance input turn");
        AssertEqual(0, wall.State.Turn, "wall move result must not advance turn");
        AssertEqual(before, GameEngine.Fingerprint(room, state, true), "wall move must not mutate input state");
    }

    private static void CheckDoorAndSwitch()
    {
        RoomDefinition doorOnly = BuildDoorOnlyRoom();
        AssertNoRoomErrors(doorOnly);
        GameState closedState = GameEngine.Create(doorOnly);
        StepResult blocked = GameEngine.Step(doorOnly, closedState, GameCommand.Right);
        Assert(!blocked.Accepted, "closed door must block movement");
        AssertEqual("blocked_closed_door", blocked.Reason, "closed door reason");
        AssertEqual(0, blocked.State.Turn, "closed door must not consume a turn");

        RoomDefinition room = BuildSwitchDoorRoom();
        AssertNoRoomErrors(room);
        GameState state = GameEngine.Create(room);
        StepResult switchEntry = GameEngine.Step(room, state, GameCommand.Right);
        Assert(switchEntry.Accepted, "switch tile must be enterable");
        AssertEqual("door-a", switchEntry.State.OpenDoorIds[0], "switch must permanently open its linked door");
        AssertEqual(1, switchEntry.State.OpenDoorIds.Length, "switch must not add duplicate door state");

        StepResult doorEntry = GameEngine.Step(room, switchEntry.State, GameCommand.Right);
        Assert(doorEntry.Accepted, "opened door must be enterable");
        AssertEqual(new GridPoint(3, 1), doorEntry.State.Player, "player must enter opened door cell");
        AssertEqual(RunStatus.Playing, doorEntry.State.Status, "door tile is not the exit in this fixture");
    }

    private static void CheckGuardPredictionMotionAndCapture()
    {
        RoomDefinition room = BuildMotionGuardRoom();
        AssertNoRoomErrors(room);
        GameState state = GameEngine.Create(room);

        GridPoint[] prediction = GameEngine.NextGuardPositions(room, state);
        AssertEqual(1, prediction.Length, "fixture must expose one next guard position");
        AssertEqual(new GridPoint(3, 1), prediction[0], "prediction must use guard route next index");

        StepResult waited = GameEngine.Step(room, state, GameCommand.Wait);
        Assert(waited.Accepted, "wait must be accepted");
        AssertEqual(1, waited.State.Turn, "wait must consume one turn");
        AssertEqual(1, waited.State.GuardIndices[0], "wait must advance guard once");
        GridPoint[] afterWaitPrediction = GameEngine.NextGuardPositions(room, waited.State);
        AssertEqual(new GridPoint(4, 1), afterWaitPrediction[0], "next intent must match the next Step transition");

        StepResult moveOne = GameEngine.Step(room, waited.State, GameCommand.Right);
        Assert(moveOne.Accepted, "first move toward guard must be accepted");
        AssertEqual(0, moveOne.State.GuardIndices[0], "guard must cycle to its authored first index");

        StepResult capture = GameEngine.Step(room, moveOne.State, GameCommand.Right);
        Assert(capture.Accepted, "move into future guard location is a valid command");
        AssertEqual(RunStatus.Captured, capture.State.Status, "guard landing on player must capture after player movement");
        AssertEqual(1, capture.State.GuardIndices[0], "capturing guard must have advanced exactly once");
        AssertEqual(0, GameEngine.NextGuardPositions(room, capture.State).Length, "terminal state has no next guard intent");
    }

    private static void CheckVacatedCellAndSwapSurvive()
    {
        RoomDefinition room = BuildImmediateGuardRoom();
        AssertNoRoomErrors(room);
        GameState state = GameEngine.Create(room);
        StepResult vacatedCell = GameEngine.Step(room, state, GameCommand.Right);

        Assert(vacatedCell.Accepted, "moving into a guard's current position is an accepted movement command");
        AssertEqual(RunStatus.Playing, vacatedCell.State.Status, "entering a guard's vacated cell must survive");
        AssertEqual(1, vacatedCell.State.GuardIndices[0], "the guard must advance before collision resolution");

        RoomDefinition swapRoom = BuildSwapGuardRoom();
        AssertNoRoomErrors(swapRoom);
        StepResult swap = GameEngine.Step(swapRoom, GameEngine.Create(swapRoom), GameCommand.Right);
        Assert(swap.Accepted, "swap movement must be accepted");
        AssertEqual(RunStatus.Playing, swap.State.Status, "player and guard swapping cells must survive");
        AssertEqual(new GridPoint(2, 1), swap.State.Player, "swap must preserve the player destination");
        AssertEqual(1, swap.State.GuardIndices[0], "swap guard must advance exactly once");
    }

    private static void CheckSimultaneousCollisionPriorityAndAllGuards()
    {
        RoomDefinition room = BuildExitTimingRoom();
        AssertNoRoomErrors(room);
        GameState state = GameEngine.Create(room);
        StepResult exitCollision = GameEngine.Step(room, state, GameCommand.Right);

        Assert(exitCollision.Accepted, "exit movement must be accepted");
        AssertEqual(RunStatus.Captured, exitCollision.State.Status, "same-destination collision must take priority over exit clear");
        AssertEqual(1, exitCollision.State.GuardIndices[0], "a colliding guard must advance before terminal resolution");
        Assert(Contains(exitCollision.Events, "captured_by_guard:guard-a"), "collision event must identify the arriving guard");

        RoomDefinition waitRoom = BuildWaitCaptureRoom();
        AssertNoRoomErrors(waitRoom);
        StepResult waitedCapture = GameEngine.Step(waitRoom, GameEngine.Create(waitRoom), GameCommand.Wait);
        Assert(waitedCapture.Accepted, "wait must be an accepted simultaneous action");
        AssertEqual(RunStatus.Captured, waitedCapture.State.Status, "waiting under an arriving guard must capture");
        AssertEqual(1, waitedCapture.State.GuardIndices[0], "wait capture must advance its guard exactly once");

        RoomDefinition allGuardsRoom = BuildAllGuardsAdvanceRoom();
        AssertNoRoomErrors(allGuardsRoom);
        StepResult allAdvanced = GameEngine.Step(allGuardsRoom, GameEngine.Create(allGuardsRoom), GameCommand.Right);
        AssertEqual(RunStatus.Captured, allAdvanced.State.Status, "fixture must capture on the first guard destination");
        AssertEqual(1, allAdvanced.State.GuardIndices[0], "first guard must advance exactly once");
        AssertEqual(1, allAdvanced.State.GuardIndices[1], "every other guard must also advance on a captured action");
    }

    private static void CheckTerminalStates()
    {
        RoomDefinition clearedRoom = BuildExitTimingRoom();
        GameState cleared = GameEngine.Step(clearedRoom, GameEngine.Create(clearedRoom), GameCommand.Right).State;
        string clearedHash = GameEngine.Fingerprint(clearedRoom, cleared, true);
        StepResult clearedAttempt = GameEngine.Step(clearedRoom, cleared, GameCommand.Wait);
        Assert(!clearedAttempt.Accepted, "cleared room must ignore later movement");
        AssertEqual("terminal_state", clearedAttempt.Reason, "cleared terminal reason");
        AssertEqual(clearedHash, GameEngine.Fingerprint(clearedRoom, clearedAttempt.State, true), "cleared terminal state must be unchanged");

        RoomDefinition capturedRoom = BuildWaitCaptureRoom();
        GameState captured = GameEngine.Step(capturedRoom, GameEngine.Create(capturedRoom), GameCommand.Wait).State;
        string capturedHash = GameEngine.Fingerprint(capturedRoom, captured, true);
        StepResult capturedAttempt = GameEngine.Step(capturedRoom, captured, GameCommand.Left);
        Assert(!capturedAttempt.Accepted, "captured room must ignore later movement");
        AssertEqual("terminal_state", capturedAttempt.Reason, "captured terminal reason");
        AssertEqual(capturedHash, GameEngine.Fingerprint(capturedRoom, capturedAttempt.State, true), "captured terminal state must be unchanged");

        GameState restarted = GameEngine.Create(capturedRoom);
        AssertEqual(RunStatus.Playing, restarted.Status, "creating the current room must restart it in playing state");
        AssertEqual(0, restarted.Turn, "restart must reset the turn counter");
        AssertEqual(new GridPoint(2, 1), restarted.Player, "restart must return player to room start");
        AssertEqual(0, restarted.OpenDoorIds.Length, "restart must clear room device state");
        AssertEqual(0, restarted.GuardIndices[0], "restart must restore the authored guard phase");
    }

    private static void CheckStepClonesInput()
    {
        RoomDefinition guardRoom = BuildMotionGuardRoom();
        GameState guardState = GameEngine.Create(guardRoom);
        int[] guardArray = guardState.GuardIndices;
        StepResult advanced = GameEngine.Step(guardRoom, guardState, GameCommand.Wait);
        AssertEqual(0, guardState.GuardIndices[0], "input guard phase must not be mutated");
        AssertEqual(1, advanced.State.GuardIndices[0], "output guard phase must advance");
        Assert(!object.ReferenceEquals(guardArray, advanced.State.GuardIndices), "output must own a cloned guard array");

        RoomDefinition switchRoom = BuildSwitchDoorRoom();
        GameState switchState = GameEngine.Create(switchRoom);
        string[] openDoorArray = switchState.OpenDoorIds;
        StepResult switched = GameEngine.Step(switchRoom, switchState, GameCommand.Right);
        AssertEqual(0, switchState.OpenDoorIds.Length, "input open-door state must not be mutated");
        AssertEqual(1, switched.State.OpenDoorIds.Length, "output must record opened door state");
        Assert(!object.ReferenceEquals(openDoorArray, switched.State.OpenDoorIds), "output must own a cloned open-door array");
    }

    private static void CheckFingerprints()
    {
        RoomDefinition switchRoom = BuildSwitchDoorRoom();
        GameState beforeSwitch = GameEngine.Create(switchRoom);
        GameState afterSwitch = GameEngine.Step(switchRoom, beforeSwitch, GameCommand.Right).State;
        AssertNotEqual(GameEngine.Fingerprint(switchRoom, beforeSwitch, false), GameEngine.Fingerprint(switchRoom, afterSwitch, false), "opening a door changes future state fingerprint");

        RoomDefinition guardRoom = BuildMotionGuardRoom();
        GameState beforeGuardMove = GameEngine.Create(guardRoom);
        GameState afterGuardMove = GameEngine.Step(guardRoom, beforeGuardMove, GameCommand.Wait).State;
        AssertNotEqual(GameEngine.Fingerprint(guardRoom, beforeGuardMove, false), GameEngine.Fingerprint(guardRoom, afterGuardMove, false), "guard phase changes future state fingerprint");

        RoomDefinition changedContent = BuildMotionGuardRoom();
        changedContent.Rows[1] = "#.#...#";
        AssertNoRoomErrors(changedContent);
        AssertNotEqual(GameEngine.Fingerprint(guardRoom, GameEngine.Create(guardRoom), false), GameEngine.Fingerprint(changedContent, GameEngine.Create(changedContent), false), "room content changes fingerprint");
    }

    private static void CheckDeterministicReplay()
    {
        RoomDefinition room = LoadRoom01();
        SolverResult solution = Solver.FindSolution(room, 4096);
        AssertEqual(SolverStatus.Solved, solution.Status, "determinism check requires a solution");

        string[] first = ReplayFingerprints(room, solution.Commands);
        string[] second = ReplayFingerprints(room, solution.Commands);
        AssertEqual(first.Length, second.Length, "replays must record same number of states");
        for (int i = 0; i < first.Length; i++)
        {
            AssertEqual(first[i], second[i], "same replay must produce same fingerprint at index " + i);
        }
    }

    private static void CheckSaveRoundTripAndRejections()
    {
        RoomDefinition room = LoadRoom01();
        GameState state = GameEngine.Create(room);
        state = GameEngine.Step(room, state, GameCommand.Right).State;
        state = GameEngine.Step(room, state, GameCommand.Right).State;
        SaveEnvelope captured = SaveCodec.Capture(room, state, ContentVersion);
        string json = JsonSerializer.Serialize(captured, JsonOptions);
        SaveEnvelope parsed = JsonSerializer.Deserialize<SaveEnvelope>(json, JsonOptions);
        Assert(parsed != null, "JSON adapter must deserialize an envelope");

        GameState restored;
        string error;
        Assert(SaveCodec.TryRestore(room, parsed, ContentVersion, out restored, out error), "JSON round trip must restore: " + error);
        AssertEqual(GameEngine.Fingerprint(room, state, true), GameEngine.Fingerprint(room, restored, true), "restored state must exactly match captured state");
        Assert(!object.ReferenceEquals(parsed.State.OpenDoorIds, restored.OpenDoorIds), "restore must clone serialized state arrays");

        AssertRejectedRestore(room, CloneEnvelope(parsed), "content-v1", "content_version_mismatch");
        SaveEnvelope wrongSchema = CloneEnvelope(parsed);
        wrongSchema.SchemaVersion = 2;
        AssertRejectedRestore(room, wrongSchema, ContentVersion, "schema_version_mismatch");
        SaveEnvelope wrongGame = CloneEnvelope(parsed);
        wrongGame.GameId = "wrong-game";
        AssertRejectedRestore(room, wrongGame, ContentVersion, "game_id_mismatch");
        SaveEnvelope wrongHash = CloneEnvelope(parsed);
        wrongHash.RoomHash = "0000";
        AssertRejectedRestore(room, wrongHash, ContentVersion, "room_hash_mismatch");

        SaveEnvelope badPosition = CloneEnvelope(parsed);
        badPosition.State.Player = new GridPoint(-1, 1);
        SaveEnvelope parsedBadPosition = JsonSerializer.Deserialize<SaveEnvelope>(JsonSerializer.Serialize(badPosition, JsonOptions), JsonOptions);
        Assert(parsedBadPosition != null, "corrupt JSON fixture must deserialize before codec validation");
        AssertRejectedRestore(room, parsedBadPosition, ContentVersion, "state_invalid:state_player_out_of_bounds");
        SaveEnvelope unknownDoor = CloneEnvelope(parsed);
        unknownDoor.State.OpenDoorIds = new string[] { "unknown-door" };
        AssertRejectedRestore(room, unknownDoor, ContentVersion, "state_invalid:state_open_door_unknown:unknown-door");
        SaveEnvelope badGuardState = CloneEnvelope(parsed);
        badGuardState.State.GuardIndices = new int[] { 0 };
        AssertRejectedRestore(room, badGuardState, ContentVersion, "state_invalid:state_guard_count_mismatch");

        RoomDefinition changedRoom = LoadRoom01();
        changedRoom.Rows[1] = "#.#....#";
        AssertNoRoomErrors(changedRoom);
        AssertRejectedRestore(changedRoom, CloneEnvelope(parsed), ContentVersion, "room_hash_mismatch");
    }

    private static void CheckSaveTerminalStateValidation()
    {
        RoomDefinition clearRoom = BuildExitTimingRoom();
        clearRoom.Guards[0].StartIndex = 1;
        GameState cleared = GameEngine.Step(clearRoom, GameEngine.Create(clearRoom), GameCommand.Right).State;
        AssertEqual(RunStatus.Cleared, cleared.Status, "exit must clear when the guard vacates the exit destination");
        SaveEnvelope clearSave = SaveCodec.Capture(clearRoom, cleared, ContentVersion);

        GameState restored;
        string error;
        Assert(SaveCodec.TryRestore(clearRoom, clearSave, ContentVersion, out restored, out error), "normal cleared state must restore: " + error);
        AssertEqual(RunStatus.Cleared, restored.Status, "normal cleared state must remain cleared after restore");

        SaveEnvelope clearedOnGuard = CloneEnvelope(clearSave);
        clearedOnGuard.State.GuardIndices[0] = 1;
        AssertRejectedRestore(clearRoom, clearedOnGuard, ContentVersion, "state_invalid:state_cleared_on_guard");

        RoomDefinition captureOnExitRoom = BuildExitTimingRoom();
        AssertNoRoomErrors(captureOnExitRoom);
        GameState capturedOnExit = GameEngine.Step(captureOnExitRoom, GameEngine.Create(captureOnExitRoom), GameCommand.Right).State;
        AssertEqual(RunStatus.Captured, capturedOnExit.Status, "arriving guard on exit must capture before clear");
        AssertEqual(captureOnExitRoom.Exit, capturedOnExit.Player, "captured-on-exit fixture must retain the exit position");
        SaveEnvelope capturedOnExitSave = SaveCodec.Capture(captureOnExitRoom, capturedOnExit, ContentVersion);
        Assert(SaveCodec.TryRestore(captureOnExitRoom, capturedOnExitSave, ContentVersion, out restored, out error), "captured-on-exit state must remain valid: " + error);
        AssertEqual(RunStatus.Captured, restored.Status, "captured-on-exit restore must preserve capture state");
    }

    private static void CheckMalformedRooms()
    {
        RoomDefinition badRows = BuildDoorOnlyRoom();
        badRows.Rows = new string[] { "#####" };
        Assert(Contains(GameEngine.ValidateRoom(badRows), "room_row_count_mismatch"), "row count mismatch must be explicit");

        RoomDefinition badTile = BuildDoorOnlyRoom();
        badTile.Rows[1] = "#.x.#";
        Assert(Contains(GameEngine.ValidateRoom(badTile), "room_tile_invalid:2,1"), "unknown tile must be explicit");

        RoomDefinition badSwitch = BuildSwitchDoorRoom();
        badSwitch.Switches[0].DoorIds = new string[] { "missing-door" };
        Assert(Contains(GameEngine.ValidateRoom(badSwitch), "switch_door_unknown:switch-a:missing-door"), "unknown switch reference must be explicit");

        RoomDefinition badRoute = BuildMotionGuardRoom();
        badRoute.Guards[0].Patrol = new GridPoint[] { new GridPoint(3, 1), new GridPoint(5, 1) };
        Assert(Contains(GameEngine.ValidateRoom(badRoute), "guard_patrol_not_cardinal_cycle:0:0"), "diagonal or skipped patrol route must fail");

        RoomDefinition guardOnDoor = BuildDoorOnlyRoom();
        guardOnDoor.Guards = new Guard[]
        {
            new Guard
            {
                Id = "guard-a",
                Patrol = new GridPoint[] { new GridPoint(2, 1) },
                StartIndex = 0
            }
        };
        Assert(Contains(GameEngine.ValidateRoom(guardOnDoor), "guard_patrol_on_door:0:0"), "guard patrol on a door cell must fail");

        RoomDefinition duplicateDoorPosition = BuildDoorOnlyRoom();
        duplicateDoorPosition.Doors = new Door[]
        {
            duplicateDoorPosition.Doors[0],
            new Door { Id = "door-b", Position = new GridPoint(2, 1) }
        };
        Assert(Contains(GameEngine.ValidateRoom(duplicateDoorPosition), "door_position_duplicate:1"), "distinct doors cannot share one grid position");

        SolverResult invalid = Solver.FindSolution(badSwitch, 16);
        AssertEqual(SolverStatus.InvalidRoom, invalid.Status, "solver must report invalid content separately from unsolvable content");
    }

    private static void AssertRejectedRestore(RoomDefinition room, SaveEnvelope envelope, string contentVersion, string expectedError)
    {
        GameState restored;
        string error;
        bool restoredSuccessfully = SaveCodec.TryRestore(room, envelope, contentVersion, out restored, out error);
        Assert(!restoredSuccessfully, "corrupt or incompatible save must not restore");
        Assert(restored == null, "rejected save must not return a state");
        AssertEqual(expectedError, error, "recoverable save error");
    }

    private static string[] ReplayFingerprints(RoomDefinition room, GameCommand[] commands)
    {
        GameState state = GameEngine.Create(room);
        string[] fingerprints = new string[commands.Length + 1];
        fingerprints[0] = GameEngine.Fingerprint(room, state, true);
        for (int i = 0; i < commands.Length; i++)
        {
            StepResult result = GameEngine.Step(room, state, commands[i]);
            Assert(result.Accepted, "replay command must be accepted");
            state = result.State;
            fingerprints[i + 1] = GameEngine.Fingerprint(room, state, true);
        }

        AssertEqual(RunStatus.Cleared, state.Status, "replay must end in a cleared state");
        return fingerprints;
    }

    private static void CheckProductionRoomCatalog()
    {
        productionCatalog = EvaluateCatalog(selectedRoomsDirectory);
        Assert(productionCatalog.Passed, "production room catalog failed: " + string.Join(",", productionCatalog.Errors));
        AssertEqual(ExpectedProductionRoomCount, productionCatalog.Rooms.Length, "production catalog must contain exactly twelve rounds");

        int previousAdvancedLength = 0;
        for (int index = 0; index < productionCatalog.Rooms.Length; index++)
        {
            int number = index + 1;
            string suffix = number < 10 ? "0" + number.ToString() : number.ToString();
            string expectedId = "room-" + suffix;
            string expectedFile = expectedId + ".json";
            CatalogRoomResult catalogRoom = productionCatalog.Rooms[index];
            AssertEqual(expectedFile, catalogRoom.SourceFile, "production room source order");
            AssertEqual(expectedId, catalogRoom.RoomId, "production room ID order");
            AssertEqual(SolverStatus.Solved.ToString(), catalogRoom.Result, "production room must solve through Step");
            Assert(catalogRoom.Replayed, "production room solution must replay through Step");

            RoomDefinition room = LoadProductionRoom(catalogRoom.SourceFile);
            AssertEqual(ExpectedDoorCounts[index], room.Doors.Length, "door count for " + expectedId);
            AssertEqual(ExpectedSwitchCounts[index], room.Switches.Length, "switch count for " + expectedId);
            AssertEqual(ExpectedGuardCounts[index], room.Guards.Length, "guard count for " + expectedId);

            int length = catalogRoom.Commands.Length;
            if (index < 8)
            {
                Assert(length >= 3 && length <= 12, "tutorial solution length must stay within 3-12 for " + expectedId);
            }
            else
            {
                if (index > 8)
                {
                    Assert(previousAdvancedLength < length, "rounds 09-12 minimum solution lengths must strictly increase");
                }

                previousAdvancedLength = length;
            }

            if (index == 6)
            {
                Assert(Contains(catalogRoom.Commands, "Wait"), "room-07 must teach a safe action-based wait");
                AssertRoom07SimultaneousWitness(room);
            }
        }
    }

    private static void CheckCatalogNegativeFixtures()
    {
        string root = Path.Combine(Path.GetTempPath(), "nectorial-corechecks-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            CatalogEvaluation missing = EvaluateCatalog(Path.Combine(root, "missing"));
            Assert(!missing.Passed, "missing catalog directory must fail");
            Assert(Contains(missing.Errors, "rooms_directory_missing"), "missing catalog error must be explicit");

            string emptyDirectory = Path.Combine(root, "empty");
            Directory.CreateDirectory(emptyDirectory);
            CatalogEvaluation empty = EvaluateCatalog(emptyDirectory);
            Assert(!empty.Passed, "empty catalog directory must fail");
            Assert(Contains(empty.Errors, "rooms_catalog_empty"), "empty catalog error must be explicit");

            string validDirectory = Path.Combine(root, "valid");
            Directory.CreateDirectory(validDirectory);
            WriteCatalogRoom(validDirectory, "a.json", BuildCatalogRoom("catalog-a"));
            WriteCatalogRoom(validDirectory, "b.json", BuildCatalogRoom("catalog-b"));
            CatalogEvaluation valid = EvaluateCatalog(validDirectory);
            Assert(valid.Passed, "distinct valid catalog rooms must all pass");
            AssertEqual(2, valid.Rooms.Length, "valid catalog must enumerate every JSON room");
            Assert(valid.Rooms[0].Replayed && valid.Rooms[1].Replayed, "every valid catalog room must replay through Step");

            string duplicateDirectory = Path.Combine(root, "duplicate");
            Directory.CreateDirectory(duplicateDirectory);
            WriteCatalogRoom(duplicateDirectory, "a.json", BuildCatalogRoom("duplicate-room"));
            WriteCatalogRoom(duplicateDirectory, "b.json", BuildCatalogRoom("duplicate-room"));
            CatalogEvaluation duplicate = EvaluateCatalog(duplicateDirectory);
            Assert(!duplicate.Passed, "duplicate room IDs must fail catalog selection");
            Assert(Contains(duplicate.Errors, "duplicate_room_id:duplicate-room"), "duplicate ID error must be explicit");
            AssertEqual(2, duplicate.Rooms.Length, "duplicate catalog must enumerate every JSON room");
            AssertEqual(SolverStatus.Solved.ToString(), duplicate.Rooms[0].Result, "duplicate fixture rooms must still be solved through Step");
            AssertEqual(SolverStatus.Solved.ToString(), duplicate.Rooms[1].Result, "duplicate fixture rooms must still be solved through Step");

            string unsolvableDirectory = Path.Combine(root, "unsolvable");
            Directory.CreateDirectory(unsolvableDirectory);
            WriteCatalogRoom(unsolvableDirectory, "impossible.json", BuildImpossibleRoom());
            CatalogEvaluation unsolvable = EvaluateCatalog(unsolvableDirectory);
            Assert(!unsolvable.Passed, "unsolvable catalog room must fail selection");
            AssertEqual(SolverStatus.Unsolvable.ToString(), unsolvable.Rooms[0].Result, "unsolvable result must remain distinct from invalid content");
            AssertEqual(0, unsolvable.Rooms[0].Commands.Length, "unsolvable catalog room must not report a command path");

            string malformedDirectory = Path.Combine(root, "malformed");
            Directory.CreateDirectory(malformedDirectory);
            File.WriteAllText(Path.Combine(malformedDirectory, "broken.json"), "{");
            CatalogEvaluation malformed = EvaluateCatalog(malformedDirectory);
            Assert(!malformed.Passed, "malformed catalog JSON must fail");
            AssertEqual("MalformedJson", malformed.Rooms[0].Result, "malformed JSON must be reported per room");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static bool TrySelectRoomsDirectory(string[] args, out string directory, out string error)
    {
        directory = DefaultRoomsDirectory;
        error = null;
        if (args == null || args.Length == 0)
        {
            return true;
        }

        if (args.Length != 2 || !string.Equals(args[0], "--rooms-directory", StringComparison.Ordinal))
        {
            error = "rooms_directory_argument_invalid";
            return false;
        }

        if (string.IsNullOrWhiteSpace(args[1]))
        {
            error = "rooms_directory_argument_missing";
            return false;
        }

        try
        {
            directory = Path.GetFullPath(args[1]);
            return true;
        }
        catch (Exception)
        {
            error = "rooms_directory_argument_invalid";
            return false;
        }
    }

    private static CatalogEvaluation EvaluateCatalog(string roomsDirectory)
    {
        CatalogEvaluation evaluation = new CatalogEvaluation
        {
            Directory = roomsDirectory,
            Passed = false,
            Errors = new string[0],
            Rooms = new CatalogRoomResult[0]
        };

        if (string.IsNullOrWhiteSpace(roomsDirectory) || !Directory.Exists(roomsDirectory))
        {
            AddCatalogError(evaluation, "rooms_directory_missing");
            return evaluation;
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(roomsDirectory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            AddCatalogError(evaluation, "rooms_directory_unreadable");
            return evaluation;
        }

        Array.Sort(paths, StringComparer.Ordinal);
        if (paths.Length == 0)
        {
            AddCatalogError(evaluation, "rooms_catalog_empty");
            return evaluation;
        }

        List<CatalogRoomResult> rooms = new List<CatalogRoomResult>();
        Dictionary<string, int> firstRoomIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            CatalogRoomResult roomResult = new CatalogRoomResult
            {
                SourceFile = Path.GetFileName(path),
                RoomId = null,
                Result = null,
                Commands = new string[0],
                VisitedCount = 0,
                Replayed = false,
                Errors = new string[0]
            };
            rooms.Add(roomResult);

            RoomDefinition room;
            try
            {
                room = JsonSerializer.Deserialize<RoomDefinition>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception)
            {
                roomResult.Result = "MalformedJson";
                AddRoomError(roomResult, "json_parse_failed");
                AddCatalogError(evaluation, "room_malformed:" + roomResult.SourceFile);
                continue;
            }

            if (room == null)
            {
                roomResult.Result = "MalformedRoom";
                AddRoomError(roomResult, "room_deserialized_null");
                AddCatalogError(evaluation, "room_malformed:" + roomResult.SourceFile);
                continue;
            }

            roomResult.RoomId = room.Id;
            if (!string.IsNullOrEmpty(room.Id))
            {
                int firstIndex;
                if (firstRoomIndexById.TryGetValue(room.Id, out firstIndex))
                {
                    string duplicateError = "duplicate_room_id:" + room.Id;
                    AddRoomError(rooms[firstIndex], duplicateError);
                    AddRoomError(roomResult, duplicateError);
                    AddCatalogError(evaluation, duplicateError);
                }
                else
                {
                    firstRoomIndexById.Add(room.Id, rooms.Count - 1);
                }
            }

            string[] validationErrors;
            try
            {
                validationErrors = GameEngine.ValidateRoom(room);
            }
            catch (Exception)
            {
                roomResult.Result = "ValidationError";
                AddRoomError(roomResult, "room_validation_exception");
                AddCatalogError(evaluation, "room_invalid:" + roomResult.SourceFile);
                continue;
            }

            if (validationErrors.Length > 0)
            {
                roomResult.Result = SolverStatus.InvalidRoom.ToString();
                AddRoomErrors(roomResult, validationErrors);
                AddCatalogError(evaluation, "room_invalid:" + roomResult.SourceFile);
                continue;
            }

            SolverResult solution;
            try
            {
                solution = Solver.FindSolution(room, ProductionRoomMaxVisitedStates);
            }
            catch (Exception)
            {
                roomResult.Result = "SolverError";
                AddRoomError(roomResult, "solver_exception");
                AddCatalogError(evaluation, "room_solver_error:" + roomResult.SourceFile);
                continue;
            }

            roomResult.Result = solution.Status.ToString();
            roomResult.Commands = CommandNames(solution.Commands);
            roomResult.VisitedCount = solution.VisitedCount;
            if (solution.Status != SolverStatus.Solved)
            {
                AddRoomError(roomResult, "room_not_solved:" + solution.Status);
                AddCatalogError(evaluation, "room_not_solved:" + roomResult.SourceFile + ":" + solution.Status);
                continue;
            }

            string replayError;
            roomResult.Replayed = ReplaysToClear(room, solution.Commands, out replayError);
            if (!roomResult.Replayed)
            {
                AddRoomError(roomResult, replayError);
                AddCatalogError(evaluation, "room_replay_failed:" + roomResult.SourceFile);
            }
        }

        evaluation.Rooms = rooms.ToArray();
        evaluation.Passed = evaluation.Errors.Length == 0;
        return evaluation;
    }

    private static CatalogEvaluation FailedCatalog(string roomsDirectory, string error)
    {
        return new CatalogEvaluation
        {
            Directory = roomsDirectory,
            Passed = false,
            Errors = new string[] { error },
            Rooms = new CatalogRoomResult[0]
        };
    }

    private static bool ReplaysToClear(RoomDefinition room, GameCommand[] commands, out string error)
    {
        error = null;
        try
        {
            GameState state = GameEngine.Create(room);
            for (int i = 0; i < commands.Length; i++)
            {
                StepResult step = GameEngine.Step(room, state, commands[i]);
                if (!step.Accepted)
                {
                    error = "replay_command_rejected:" + i;
                    return false;
                }

                state = step.State;
            }

            if (state.Status != RunStatus.Cleared)
            {
                error = "replay_not_cleared";
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            error = "replay_exception";
            return false;
        }
    }

    private static void AddCatalogError(CatalogEvaluation evaluation, string error)
    {
        evaluation.Errors = AppendString(evaluation.Errors, error);
    }

    private static void AddRoomError(CatalogRoomResult room, string error)
    {
        room.Errors = AppendString(room.Errors, error);
    }

    private static void AddRoomErrors(CatalogRoomResult room, string[] errors)
    {
        for (int i = 0; i < errors.Length; i++)
        {
            AddRoomError(room, errors[i]);
        }
    }

    private static string[] AppendString(string[] values, string value)
    {
        int length = values == null ? 0 : values.Length;
        string[] appended = new string[length + 1];
        if (length > 0)
        {
            Array.Copy(values, appended, length);
        }

        appended[length] = value;
        return appended;
    }

    private static void WriteCatalogRoom(string directory, string fileName, RoomDefinition room)
    {
        File.WriteAllText(Path.Combine(directory, fileName), JsonSerializer.Serialize(room, JsonOptions));
    }

    private static RoomDefinition LoadRoom01()
    {
        string path = Path.Combine(DefaultRoomsDirectory, "room-01.json");
        Assert(File.Exists(path), "room-01 data file must be copied into the default room catalog");
        RoomDefinition room = JsonSerializer.Deserialize<RoomDefinition>(File.ReadAllText(path), JsonOptions);
        Assert(room != null, "room-01 JSON must deserialize");
        return room;
    }

    private static RoomDefinition LoadProductionRoom(string sourceFile)
    {
        string path = Path.Combine(selectedRoomsDirectory, sourceFile);
        Assert(File.Exists(path), "production room data file must exist: " + sourceFile);
        RoomDefinition room = JsonSerializer.Deserialize<RoomDefinition>(File.ReadAllText(path), JsonOptions);
        Assert(room != null, "production room JSON must deserialize: " + sourceFile);
        return room;
    }

    private static void AssertRoom07SimultaneousWitness(RoomDefinition room)
    {
        GameState initial = GameEngine.Create(room);
        AssertEqual(new GridPoint(1, 1), initial.Player, "room-07 witness start position");
        AssertEqual(0, initial.GuardIndices[0], "room-07 witness guard phase");

        StepResult firstRight = GameEngine.Step(room, initial, GameCommand.Right);
        AssertEqual(RunStatus.Playing, firstRight.State.Status, "room-07 witness first move must survive");
        AssertEqual(new GridPoint(2, 1), firstRight.State.Player, "room-07 witness first player position");
        AssertEqual(1, firstRight.State.GuardIndices[0], "room-07 witness first guard phase");

        StepResult wait = GameEngine.Step(room, firstRight.State, GameCommand.Wait);
        AssertEqual(RunStatus.Playing, wait.State.Status, "room-07 wait must be safe at the announced phase");
        AssertEqual(new GridPoint(2, 1), wait.State.Player, "room-07 wait must keep the player position");
        AssertEqual(0, wait.State.GuardIndices[0], "room-07 wait must advance the guard");

        StepResult vacatedCell = GameEngine.Step(room, wait.State, GameCommand.Right);
        AssertEqual(RunStatus.Playing, vacatedCell.State.Status, "room-07 vacated-cell move must survive");
        AssertEqual(new GridPoint(3, 1), vacatedCell.State.Player, "room-07 vacated-cell player position");
        AssertEqual(1, vacatedCell.State.GuardIndices[0], "room-07 vacated-cell guard phase");

        StepResult sameDestination = GameEngine.Step(room, firstRight.State, GameCommand.Right);
        AssertEqual(RunStatus.Captured, sameDestination.State.Status, "room-07 same-destination move must capture");
        AssertEqual(new GridPoint(3, 1), sameDestination.State.Player, "room-07 same-destination player position");
        AssertEqual(0, sameDestination.State.GuardIndices[0], "room-07 same-destination guard phase");
    }

    private static RoomDefinition BuildCatalogRoom(string id)
    {
        return new RoomDefinition
        {
            Id = id,
            Width = 5,
            Height = 3,
            Rows = new string[]
            {
                "#####",
                "#...#",
                "#####"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(3, 1),
            Doors = new Door[0],
            Switches = new Switch[0],
            Guards = new Guard[0]
        };
    }

    private static RoomDefinition BuildImpossibleRoom()
    {
        return new RoomDefinition
        {
            Id = "impossible",
            Width = 5,
            Height = 5,
            Rows = new string[]
            {
                "#####",
                "#.###",
                "#####",
                "###.#",
                "#####"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(3, 3),
            Doors = new Door[0],
            Switches = new Switch[0],
            Guards = new Guard[0]
        };
    }

    private static RoomDefinition BuildDoorOnlyRoom()
    {
        return new RoomDefinition
        {
            Id = "door-only",
            Width = 5,
            Height = 3,
            Rows = new string[]
            {
                "#####",
                "#...#",
                "#####"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(3, 1),
            Doors = new Door[]
            {
                new Door { Id = "door-a", Position = new GridPoint(2, 1) }
            },
            Switches = new Switch[0],
            Guards = new Guard[0]
        };
    }

    private static RoomDefinition BuildSwitchDoorRoom()
    {
        return new RoomDefinition
        {
            Id = "switch-door",
            Width = 7,
            Height = 3,
            Rows = new string[]
            {
                "#######",
                "#.....#",
                "#######"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(5, 1),
            Doors = new Door[]
            {
                new Door { Id = "door-a", Position = new GridPoint(3, 1) }
            },
            Switches = new Switch[]
            {
                new Switch { Id = "switch-a", Position = new GridPoint(2, 1), DoorIds = new string[] { "door-a" } }
            },
            Guards = new Guard[0]
        };
    }

    private static RoomDefinition BuildMotionGuardRoom()
    {
        return new RoomDefinition
        {
            Id = "motion-guard",
            Width = 7,
            Height = 3,
            Rows = new string[]
            {
                "#######",
                "#.....#",
                "#######"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(5, 1),
            Doors = new Door[0],
            Switches = new Switch[0],
            Guards = new Guard[]
            {
                new Guard
                {
                    Id = "guard-a",
                    Patrol = new GridPoint[] { new GridPoint(4, 1), new GridPoint(3, 1) },
                    StartIndex = 0
                }
            }
        };
    }

    private static RoomDefinition BuildImmediateGuardRoom()
    {
        return new RoomDefinition
        {
            Id = "immediate-guard",
            Width = 7,
            Height = 3,
            Rows = new string[]
            {
                "#######",
                "#.....#",
                "#######"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(5, 1),
            Doors = new Door[0],
            Switches = new Switch[0],
            Guards = new Guard[]
            {
                new Guard
                {
                    Id = "guard-a",
                    Patrol = new GridPoint[] { new GridPoint(2, 1), new GridPoint(3, 1) },
                    StartIndex = 0
                }
            }
        };
    }

    private static RoomDefinition BuildSwapGuardRoom()
    {
        return new RoomDefinition
        {
            Id = "swap-guard",
            Width = 7,
            Height = 3,
            Rows = new string[]
            {
                "#######",
                "#.....#",
                "#######"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(5, 1),
            Doors = new Door[0],
            Switches = new Switch[0],
            Guards = new Guard[]
            {
                new Guard
                {
                    Id = "guard-a",
                    Patrol = new GridPoint[] { new GridPoint(2, 1), new GridPoint(1, 1) },
                    StartIndex = 0
                }
            }
        };
    }

    private static RoomDefinition BuildWaitCaptureRoom()
    {
        return new RoomDefinition
        {
            Id = "wait-capture",
            Width = 7,
            Height = 3,
            Rows = new string[]
            {
                "#######",
                "#.....#",
                "#######"
            },
            Start = new GridPoint(2, 1),
            Exit = new GridPoint(5, 1),
            Doors = new Door[0],
            Switches = new Switch[0],
            Guards = new Guard[]
            {
                new Guard
                {
                    Id = "guard-a",
                    Patrol = new GridPoint[] { new GridPoint(3, 1), new GridPoint(2, 1) },
                    StartIndex = 0
                }
            }
        };
    }

    private static RoomDefinition BuildAllGuardsAdvanceRoom()
    {
        return new RoomDefinition
        {
            Id = "all-guards-advance",
            Width = 7,
            Height = 3,
            Rows = new string[]
            {
                "#######",
                "#.....#",
                "#######"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(5, 1),
            Doors = new Door[0],
            Switches = new Switch[0],
            Guards = new Guard[]
            {
                new Guard
                {
                    Id = "guard-a",
                    Patrol = new GridPoint[] { new GridPoint(3, 1), new GridPoint(2, 1) },
                    StartIndex = 0
                },
                new Guard
                {
                    Id = "guard-b",
                    Patrol = new GridPoint[] { new GridPoint(5, 1), new GridPoint(4, 1) },
                    StartIndex = 0
                }
            }
        };
    }

    private static RoomDefinition BuildExitTimingRoom()
    {
        return new RoomDefinition
        {
            Id = "exit-timing",
            Width = 5,
            Height = 3,
            Rows = new string[]
            {
                "#####",
                "#...#",
                "#####"
            },
            Start = new GridPoint(1, 1),
            Exit = new GridPoint(2, 1),
            Doors = new Door[0],
            Switches = new Switch[0],
            Guards = new Guard[]
            {
                new Guard
                {
                    Id = "guard-a",
                    Patrol = new GridPoint[] { new GridPoint(3, 1), new GridPoint(2, 1) },
                    StartIndex = 0
                }
            }
        };
    }

    private static SaveEnvelope CloneEnvelope(SaveEnvelope source)
    {
        return new SaveEnvelope
        {
            SchemaVersion = source.SchemaVersion,
            GameId = source.GameId,
            ContentVersion = source.ContentVersion,
            RoomHash = source.RoomHash,
            State = CloneState(source.State)
        };
    }

    private static GameState CloneState(GameState source)
    {
        string[] doors = new string[source.OpenDoorIds.Length];
        Array.Copy(source.OpenDoorIds, doors, doors.Length);
        int[] guards = new int[source.GuardIndices.Length];
        Array.Copy(source.GuardIndices, guards, guards.Length);
        return new GameState
        {
            RoomId = source.RoomId,
            Player = source.Player,
            Turn = source.Turn,
            Status = source.Status,
            OpenDoorIds = doors,
            GuardIndices = guards
        };
    }

    private static void AssertNoRoomErrors(RoomDefinition room)
    {
        string[] errors = GameEngine.ValidateRoom(room);
        AssertEqual(0, errors.Length, "room must validate: " + string.Join(",", errors));
    }

    private static bool Contains(string[] values, string value)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static GameCommand[] CopyCommands(GameCommand[] source)
    {
        GameCommand[] copy = new GameCommand[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    private static string[] CommandNames(GameCommand[] commands)
    {
        string[] names = new string[commands.Length];
        for (int i = 0; i < commands.Length; i++)
        {
            names[i] = commands[i].ToString();
        }

        return names;
    }

    private static string NormalInputSequence(GameCommand[] commands)
    {
        string[] names = CommandNames(commands);
        return string.Join(" ", names);
    }

    private static string SingleLine(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message + " (expected " + expected + ", actual " + actual + ")");
        }
    }

    private static void AssertNotEqual(string left, string right, string message)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message + " (both " + left + ")");
        }
    }

    private sealed class CheckRecord
    {
        public string Name;
        public bool Passed;
        public string Detail;
    }

    private sealed class CatalogRoomResult
    {
        public string SourceFile;
        public string RoomId;
        public string Result;
        public string[] Commands;
        public int VisitedCount;
        public bool Replayed;
        public string[] Errors;
    }

    private sealed class CatalogEvaluation
    {
        public string Directory;
        public bool Passed;
        public string[] Errors;
        public CatalogRoomResult[] Rooms;
    }

    private sealed class CheckSummary
    {
        public string Suite;
        public int Passed;
        public int Failed;
        public CheckRecord[] Checks;
        public string[] Room01SolutionCommands;
        public string NormalInputSequence;
        public string CatalogDirectory;
        public bool CatalogPassed;
        public string[] CatalogErrors;
        public CatalogRoomResult[] ProductionRooms;
    }
}
