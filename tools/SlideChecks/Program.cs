using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nectorial.SlideEscape;

internal static class Program
{
    private const string ContentVersion = "slide-v3";
    private const int ProductionRoomMaxVisitedStates = 500000;
    private const int ExpectedProductionRoomCount = 12;
    private static readonly List<CheckRecord> Records = new List<CheckRecord>();
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };
    private static readonly List<LevelProbe> Catalog = new List<LevelProbe>();
    private static readonly string DefaultRoomsDirectory = Path.Combine(AppContext.BaseDirectory, "SlideRooms");

    private static int Main(string[] args)
    {
        Run("slide_until_stopper", CheckSlideUntilStopper);
        Run("zero_invalid_and_terminal_moves", CheckRejectedMoves);
        Run("helper_stop_goal_and_overshoot", CheckGoalRules);
        Run("packed_solver_replay_and_limit", CheckSolver);
        Run("shared_slide_primitive_and_v3_surface", CheckSharedSlidePrimitiveAndV3Surface);
        Run("state_immutability", CheckStateImmutability);
        Run("save_transcript_round_trip_and_rejection", CheckSaveTranscript);
        string roomsDirectory;
        string argumentError;
        if (TrySelectRoomsDirectory(args, out roomsDirectory, out argumentError))
        {
            Run("production_slide_catalog_and_quality", delegate { CheckProductionCatalog(roomsDirectory); });
            Run("d4_topology_variance", delegate { CheckD4TopologyVariance(roomsDirectory); });
            Run("representative_recipe_transfer_variance", delegate { CheckRepresentativeRecipeTransferVariance(roomsDirectory); });
        }
        else
        {
            Records.Add(new CheckRecord { Name = "production_slide_catalog_and_quality", Passed = false, Detail = argumentError });
            Records.Add(new CheckRecord { Name = "d4_topology_variance", Passed = false, Detail = argumentError });
            Records.Add(new CheckRecord { Name = "representative_recipe_transfer_variance", Passed = false, Detail = argumentError });
        }

        int passed = 0;
        for (int index = 0; index < Records.Count; index++) if (Records[index].Passed) passed++;
        Console.WriteLine(JsonSerializer.Serialize(new CheckSummary
        {
            Suite = "nectorial-slide-core",
            Passed = passed,
            Failed = Records.Count - passed,
            Checks = Records.ToArray(),
            Catalog = Catalog.ToArray()
        }, JsonOptions));
        return passed == Records.Count ? 0 : 1;
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
            Records.Add(new CheckRecord { Name = name, Passed = false, Detail = exception.GetType().Name + ": " + exception.Message.Replace('\n', ' ') });
        }
    }

    private static void CheckSlideUntilStopper()
    {
        RoomDefinition room = BuildOpenRoom();
        GameState state = GameEngine.Create(room);
        StepResult slide = GameEngine.Step(room, state, Move(0, GameCommand.Right));
        Assert(slide.Accepted, "open direction must slide");
        AssertEqual(new GridPoint(6, 1), slide.State.Positions[0], "target must stop before the boundary wall");
        AssertEqual(1, slide.State.Turn, "one long slide costs one move");
        Assert(Contains(slide.Events, "slid:target:5"), "slide event must preserve actual distance");
    }

    private static void CheckRejectedMoves()
    {
        RoomDefinition room = BuildOpenRoom();
        GameState state = GameEngine.Create(room);
        string before = GameEngine.Fingerprint(room, state, true);
        StepResult zero = GameEngine.Step(room, state, Move(0, GameCommand.Up));
        Assert(!zero.Accepted, "wall-adjacent move must reject");
        AssertEqual("blocked_zero", zero.Reason, "zero-distance reason");
        AssertEqual(before, GameEngine.Fingerprint(room, zero.State, true), "zero slide must preserve state");

        StepResult invalidDirection = GameEngine.Step(room, state, Move(0, (GameCommand)99));
        Assert(!invalidDirection.Accepted, "unknown command must reject");
        AssertEqual("invalid_direction", invalidDirection.Reason, "unknown command reason");
        StepResult invalidPiece = GameEngine.Step(room, state, Move(4, GameCommand.Right));
        Assert(!invalidPiece.Accepted, "unknown piece must reject");
        AssertEqual("invalid_piece_index", invalidPiece.Reason, "unknown piece reason");
    }

    private static void CheckGoalRules()
    {
        RoomDefinition stopper = BuildHelperStopRoom();
        GameState state = GameEngine.Create(stopper);
        state = RequireAccepted(stopper, state, Move(0, GameCommand.Right));
        StepResult targetGoal = GameEngine.Step(stopper, state, Move(0, GameCommand.Down));
        Assert(targetGoal.Accepted, "target must slide toward helper");
        AssertEqual(new GridPoint(4, 2), targetGoal.State.Positions[0], "helper must stop target on interior goal");
        AssertEqual(RunStatus.Cleared, targetGoal.State.Status, "only target resting on goal clears");

        GameState helperGoalState = GameEngine.Create(stopper);
        helperGoalState = RequireAccepted(stopper, helperGoalState, Move(0, GameCommand.Right));
        StepResult helperOnGoal = GameEngine.Step(stopper, helperGoalState, Move(1, GameCommand.Up));
        Assert(helperOnGoal.Accepted, "helper may enter goal cell");
        AssertEqual(new GridPoint(4, 2), helperOnGoal.State.Positions[1], "helper may rest on goal");
        AssertEqual(RunStatus.Playing, helperOnGoal.State.Status, "helper on goal must not clear");

        RoomDefinition overshoot = BuildOvershootRoom();
        GameState overshootState = RequireAccepted(overshoot, GameEngine.Create(overshoot), Move(0, GameCommand.Right));
        StepResult passedGoal = GameEngine.Step(overshoot, overshootState, Move(0, GameCommand.Down));
        Assert(passedGoal.Accepted, "target may pass through the goal line");
        AssertEqual(new GridPoint(4, 6), passedGoal.State.Positions[0], "target must continue past an unstopped goal");
        AssertEqual(RunStatus.Playing, passedGoal.State.Status, "passing the goal must not clear");
    }

    private static void CheckSolver()
    {
        RoomDefinition room = BuildHelperStopRoom();
        SolverResult result = Solver.FindSolution(room, 1024);
        AssertEqual(SolverStatus.Solved, result.Status, "solver must find a real route");
        AssertEqual(2, result.Moves.Length, "fixture has a two-slide optimum");
        AssertReplaysToClear(room, result.Moves, "solver route");

        SolverResult limited = Solver.FindSolution(room, 1);
        AssertEqual(SolverStatus.LimitReached, limited.Status, "search cap must not report success");
        AssertEqual(0, limited.Moves.Length, "limited search must not return a route");
    }

    private static void CheckSharedSlidePrimitiveAndV3Surface()
    {
        AssertEqual(4, Enum.GetValues(typeof(GameCommand)).Length, "v3 command surface must exclude Wait");
        AssertEqual(2, Enum.GetValues(typeof(RunStatus)).Length, "v3 run status surface must exclude capture");
        AssertEqual(2, SaveCodec.CurrentSchemaVersion, "v3 save schema version");

        RoomDefinition room = BuildHelperStopRoom();
        Queue<GameState> queue = new Queue<GameState>();
        HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
        GameState initial = GameEngine.Create(room);
        queue.Enqueue(initial);
        visited.Add(StateKey(initial));
        GameCommand[] directions = new GameCommand[] { GameCommand.Up, GameCommand.Right, GameCommand.Down, GameCommand.Left };
        while (queue.Count > 0)
        {
            GameState state = queue.Dequeue();
            ulong packed = GameEngine.PackPositions(state.Positions);
            for (int pieceIndex = 0; pieceIndex < room.Pieces.Length; pieceIndex++)
            {
                for (int directionIndex = 0; directionIndex < directions.Length; directionIndex++)
                {
                    int destinationCell;
                    int distance;
                    string error;
                    bool primitive = GameEngine.TryGetSlideDestination(room, packed, room.Pieces.Length, pieceIndex, directions[directionIndex],
                        out destinationCell, out distance, out error);
                    Assert(primitive, "shared primitive must accept a valid cardinal direction");
                    StepResult step = GameEngine.Step(room, state, Move(pieceIndex, directions[directionIndex]));
                    if (distance == 0)
                    {
                        Assert(!step.Accepted, "zero primitive distance must reject in public Step");
                        AssertEqual("blocked_zero", step.Reason, "zero primitive rejection reason");
                        continue;
                    }

                    Assert(step.Accepted, "positive primitive distance must accept in public Step");
                    AssertEqual(GameEngine.FromCell(destinationCell), step.State.Positions[pieceIndex], "shared primitive destination must equal public Step destination");
                    if (step.State.Status == RunStatus.Playing && visited.Add(StateKey(step.State))) queue.Enqueue(step.State);
                }
            }
        }
    }

    private static void CheckStateImmutability()
    {
        RoomDefinition room = BuildHelperStopRoom();
        GameState before = GameEngine.Create(room);
        GridPoint[] positions = before.Positions;
        StepResult result = GameEngine.Step(room, before, Move(0, GameCommand.Right));
        AssertEqual(new GridPoint(1, 1), before.Positions[0], "input target position must not mutate");
        Assert(!object.ReferenceEquals(positions, result.State.Positions), "output state must own its positions array");
    }

    private static void CheckSaveTranscript()
    {
        RoomDefinition room = BuildHelperStopRoom();
        SolverResult solution = Solver.FindSolution(room, 1024);
        GameState state = Replay(room, solution.Moves);
        SaveEnvelope envelope = SaveCodec.Capture(room, state, ContentVersion, solution.Moves, 1);
        string json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { IncludeFields = true });
        SaveEnvelope parsed = JsonSerializer.Deserialize<SaveEnvelope>(json, new JsonSerializerOptions { IncludeFields = true });
        Assert(parsed != null, "save JSON must deserialize");

        GameState restored;
        GameMove[] moves;
        int selected;
        string error;
        Assert(SaveCodec.TryRestore(room, parsed, ContentVersion, out restored, out moves, out selected, out error), "save must restore: " + error);
        AssertEqual(GameEngine.Fingerprint(room, state, true), GameEngine.Fingerprint(room, restored, true), "restored state must match");
        AssertEqual(1, selected, "selection must round trip");
        Assert(!object.ReferenceEquals(parsed.Moves, moves), "restored history must be cloned");

        AssertRejectedRestore(room, CloneEnvelope(parsed), "slide-v2", "content_version_mismatch");
        SaveEnvelope badSelection = CloneEnvelope(parsed);
        badSelection.SelectedPieceIndex = 9;
        AssertRejectedRestore(room, badSelection, ContentVersion, "selected_piece_index_invalid");
        SaveEnvelope badTranscript = CloneEnvelope(parsed);
        badTranscript.Moves[0].Direction = GameCommand.Up;
        AssertRejectedRestore(room, badTranscript, ContentVersion, "transcript_move_rejected:0:blocked_zero");
    }

    private static bool TrySelectRoomsDirectory(string[] args, out string directory, out string error)
    {
        directory = DefaultRoomsDirectory;
        error = null;
        if (args == null || args.Length == 0) return true;
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

        directory = Path.GetFullPath(args[1]);
        return true;
    }

    private static void CheckProductionCatalog(string directory)
    {
        Assert(Directory.Exists(directory), "slide rooms directory must exist");
        string[] paths = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.Ordinal);
        AssertEqual(ExpectedProductionRoomCount, paths.Length, "slide rooms catalog must contain twelve rooms");
        bool hasEarlyInteriorGoalStopper = false;
        bool hasLateInterHelperTargetDependency = false;
        for (int index = 0; index < paths.Length; index++)
        {
            int number = index + 1;
            string suffix = number < 10 ? "0" + number.ToString() : number.ToString();
            string expectedFile = "room-" + suffix + ".json";
            string expectedId = "slide-" + suffix;
            AssertEqual(expectedFile, Path.GetFileName(paths[index]), "production source order");
            RoomDefinition room = JsonSerializer.Deserialize<RoomDefinition>(File.ReadAllText(paths[index]), JsonOptions);
            Assert(room != null, "room JSON must deserialize: " + Path.GetFileName(paths[index]));
            AssertEqual(expectedId, room.Id, "production room ID order");
            string[] errors = GameEngine.ValidateRoom(room);
            AssertEqual(0, errors.Length, "room validation: " + Path.GetFileName(paths[index]) + " " + string.Join(",", errors));
            SolverResult result = Solver.FindSolution(room, ProductionRoomMaxVisitedStates);
            LevelProbe probe = new LevelProbe
            {
                SourceFile = Path.GetFileName(paths[index]),
                Id = room.Id,
                Status = result.Status.ToString(),
                Moves = MoveNames(result.Moves),
                VisitedCount = result.VisitedCount,
                InitialTargetChoices = CountInitialTargetChoices(room),
                ParMoves = room.ParMoves,
                HasHelperTargetSameHelper = HasHelperTargetSameHelper(result.Moves),
                HelperStops = FindHelperStops(room, result.Moves),
                HelperInteractions = FindHelperInteractions(room, result.Moves),
                Overshoot = FindGoalOvershootWitness(room, 4096)
            };
            probe.HelperStopMissing = room.Pieces.Length > 1 && probe.HelperStops.Length == 0;
            probe.DistinctHelperStopDestinations = CountDistinctHelperStopDestinations(probe.HelperStops);
            probe.HasInterHelperTargetDependency = HasInterHelperTargetDependency(probe.HelperInteractions, probe.HelperStops);
            for (int stopIndex = 0; stopIndex < probe.HelperStops.Length; stopIndex++)
            {
                HelperStopWitness stop = probe.HelperStops[stopIndex];
                if (index < 4 && stop.IsGoal && stop.HelperMovedBefore && IsInterior(room.Goal)) hasEarlyInteriorGoalStopper = true;
            }
            if (room.Pieces.Length > 1)
            {
                SolverResult targetOnly = Solver.FindSolution(room, ProductionRoomMaxVisitedStates, 1);
                probe.TargetOnlyStatus = targetOnly.Status.ToString();
                probe.TargetOnlyMoves = targetOnly.Moves.Length;
                probe.TargetOnlyVisitedCount = targetOnly.VisitedCount;
                probe.HelperFreezeStatuses = new string[room.Pieces.Length - 1];
                probe.HelperFreezeMoves = new int[room.Pieces.Length - 1];
                probe.HelperFreezeVisitedCounts = new int[room.Pieces.Length - 1];
                int allPiecesMask = (1 << room.Pieces.Length) - 1;
                for (int pieceIndex = 1; pieceIndex < room.Pieces.Length; pieceIndex++)
                {
                    SolverResult frozen = Solver.FindSolution(room, ProductionRoomMaxVisitedStates, allPiecesMask & ~(1 << pieceIndex));
                    probe.HelperFreezeStatuses[pieceIndex - 1] = frozen.Status.ToString();
                    probe.HelperFreezeMoves[pieceIndex - 1] = frozen.Moves.Length;
                    probe.HelperFreezeVisitedCounts[pieceIndex - 1] = frozen.VisitedCount;
                }
            }
            Catalog.Add(probe);
            AssertEqual(SolverStatus.Solved, result.Status, "room must solve: " + room.Id);
            AssertReplaysToClear(room, result.Moves, room.Id);
            AssertEqual(room.ParMoves, result.Moves.Length, "ParMoves must match proven optimum: " + room.Id);
            Assert(result.Moves.Length > 1, "one-slide clear is forbidden: " + room.Id);
            Assert(probe.InitialTargetChoices >= 2, "target must have at least two legal initial destinations: " + room.Id);
            AssertSolutionBand(index, result.Moves.Length, room.Id);
            if (index >= 2)
            {
                AssertEqual(SolverStatus.Unsolvable.ToString(), probe.TargetOnlyStatus, "target-only frozen helper search must be unsolvable: " + room.Id);
            }
            if (index >= 4 && index <= 7)
            {
                Assert(probe.HasHelperTargetSameHelper, "round 05-08 shortest route must contain helper-target-same-helper sequence: " + room.Id);
            }
            if (index >= 8)
            {
                AssertLateHelperValue(room, result, probe);
                if ((index == 9 || index == 11) && probe.HasInterHelperTargetDependency) hasLateInterHelperTargetDependency = true;
            }
        }

        Assert(hasEarlyInteriorGoalStopper, "an early room must visibly place a moved helper as the interior goal stopper");
        Assert(hasLateInterHelperTargetDependency, "room-10 or room-12 must use an inter-helper stop that later contributes to a target stop");
    }

    private static void AssertSolutionBand(int index, int moves, string roomId)
    {
        int min;
        int max;
        if (index < 2) { min = 3; max = 5; }
        else if (index < 4) { min = 4; max = 6; }
        else if (index < 8) { min = 6; max = 9; }
        else { min = 8; max = 12; }
        Assert(moves >= min && moves <= max, "shortest route is outside its authored band for " + roomId);
    }

    private static void CheckD4TopologyVariance(string directory)
    {
        string[] paths = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.Ordinal);
        AssertEqual(ExpectedProductionRoomCount, paths.Length, "D4 check requires twelve production rooms");
        Dictionary<string, string> firstRoomByCanonicalTopology = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < paths.Length; index++)
        {
            RoomDefinition room = JsonSerializer.Deserialize<RoomDefinition>(File.ReadAllText(paths[index]), JsonOptions);
            Assert(room != null, "D4 room JSON must deserialize");
            string canonical = CanonicalTopology(room);
            string firstRoom;
            if (firstRoomByCanonicalTopology.TryGetValue(canonical, out firstRoom))
            {
                throw new InvalidOperationException("d4_topology_duplicate:" + firstRoom + ":" + room.Id);
            }

            firstRoomByCanonicalTopology.Add(canonical, room.Id);
        }
    }

    private static string CanonicalTopology(RoomDefinition room)
    {
        string canonical = null;
        for (int reflect = 0; reflect < 2; reflect++)
        {
            for (int rotations = 0; rotations < 4; rotations++)
            {
                char[,] tiles = new char[8, 8];
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        GridPoint transformed = TransformD4(new GridPoint(x, y), reflect == 1, rotations);
                        tiles[transformed.Y, transformed.X] = room.Rows[y][x];
                    }
                }

                string topology = string.Empty;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++) topology += tiles[y, x];
                    topology += "/";
                }

                GridPoint target = TransformD4(room.Pieces[room.TargetPieceIndex].Start, reflect == 1, rotations);
                GridPoint goal = TransformD4(room.Goal, reflect == 1, rotations);
                GridPoint[] helpers = new GridPoint[room.Pieces.Length - 1];
                int helperWriteIndex = 0;
                for (int pieceIndex = 0; pieceIndex < room.Pieces.Length; pieceIndex++)
                {
                    if (pieceIndex == room.TargetPieceIndex) continue;
                    helpers[helperWriteIndex++] = TransformD4(room.Pieces[pieceIndex].Start, reflect == 1, rotations);
                }

                Array.Sort(helpers, ComparePoints);
                topology += "T" + PointKey(target) + "G" + PointKey(goal) + "H";
                for (int helperIndex = 0; helperIndex < helpers.Length; helperIndex++) topology += PointKey(helpers[helperIndex]);
                if (canonical == null || string.CompareOrdinal(topology, canonical) < 0) canonical = topology;
            }
        }

        return canonical;
    }

    private static GridPoint TransformD4(GridPoint source, bool reflect, int rotations)
    {
        GridPoint point = reflect ? new GridPoint(7 - source.X, source.Y) : source;
        for (int index = 0; index < rotations; index++) point = new GridPoint(7 - point.Y, point.X);
        return point;
    }

    private static int ComparePoints(GridPoint left, GridPoint right)
    {
        int byY = left.Y.CompareTo(right.Y);
        return byY != 0 ? byY : left.X.CompareTo(right.X);
    }

    private static void CheckRepresentativeRecipeTransferVariance(string directory)
    {
        RoomDefinition[] rooms = LoadOrderedRooms(directory);
        CheckRecipeBand(rooms, 4, 8);
        CheckRecipeBand(rooms, 8, 12);
    }

    private static void CheckRecipeBand(RoomDefinition[] rooms, int startInclusive, int endExclusive)
    {
        for (int sourceIndex = startInclusive; sourceIndex < endExclusive; sourceIndex++)
        {
            SolverResult source = Solver.FindSolution(rooms[sourceIndex], ProductionRoomMaxVisitedStates);
            AssertEqual(SolverStatus.Solved, source.Status, "recipe source must solve");
            for (int targetIndex = startInclusive; targetIndex < endExclusive; targetIndex++)
            {
                if (sourceIndex == targetIndex) continue;
                for (int reflect = 0; reflect < 2; reflect++)
                {
                    for (int rotations = 0; rotations < 4; rotations++)
                    {
                        int helperSwapVariants = rooms[sourceIndex].Pieces.Length == 3 ? 2 : 1;
                        for (int swapVariant = 0; swapVariant < helperSwapVariants; swapVariant++)
                        {
                            GameMove[] transformed = TransformRecipe(source.Moves, reflect == 1, rotations, swapVariant == 1);
                            if (ReplaysToClear(rooms[targetIndex], transformed))
                            {
                                throw new InvalidOperationException("recipe_transfer:" + rooms[sourceIndex].Id + ":" + rooms[targetIndex].Id +
                                    ":reflect=" + reflect + ":rotations=" + rotations + ":helperSwap=" + swapVariant);
                            }
                        }
                    }
                }
            }
        }
    }

    private static RoomDefinition[] LoadOrderedRooms(string directory)
    {
        string[] paths = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.Ordinal);
        AssertEqual(ExpectedProductionRoomCount, paths.Length, "recipe gate requires twelve rooms");
        RoomDefinition[] rooms = new RoomDefinition[paths.Length];
        for (int index = 0; index < paths.Length; index++)
        {
            rooms[index] = JsonSerializer.Deserialize<RoomDefinition>(File.ReadAllText(paths[index]), JsonOptions);
            Assert(rooms[index] != null, "recipe room JSON must deserialize");
        }

        return rooms;
    }

    private static GameMove[] TransformRecipe(GameMove[] moves, bool reflect, int rotations, bool helperSwap)
    {
        GameMove[] transformed = new GameMove[moves.Length];
        for (int index = 0; index < moves.Length; index++)
        {
            GameMove source = moves[index];
            int pieceIndex = source.PieceIndex;
            if (helperSwap && pieceIndex == 1) pieceIndex = 2;
            else if (helperSwap && pieceIndex == 2) pieceIndex = 1;
            transformed[index] = Move(pieceIndex, TransformDirection(source.Direction, reflect, rotations));
        }

        return transformed;
    }

    private static GameCommand TransformDirection(GameCommand direction, bool reflect, int rotations)
    {
        int x;
        int y;
        switch (direction)
        {
            case GameCommand.Up: x = 0; y = -1; break;
            case GameCommand.Down: x = 0; y = 1; break;
            case GameCommand.Left: x = -1; y = 0; break;
            case GameCommand.Right: x = 1; y = 0; break;
            default: throw new ArgumentOutOfRangeException("direction");
        }

        if (reflect) x = -x;
        for (int index = 0; index < rotations; index++)
        {
            int previousX = x;
            x = -y;
            y = previousX;
        }

        if (x == 0 && y == -1) return GameCommand.Up;
        if (x == 0 && y == 1) return GameCommand.Down;
        if (x == -1 && y == 0) return GameCommand.Left;
        if (x == 1 && y == 0) return GameCommand.Right;
        throw new InvalidOperationException("direction_transform_invalid");
    }

    private static bool ReplaysToClear(RoomDefinition room, GameMove[] moves)
    {
        GameState state = GameEngine.Create(room);
        for (int index = 0; index < moves.Length; index++)
        {
            StepResult result = GameEngine.Step(room, state, moves[index]);
            if (!result.Accepted)
            {
                if (string.Equals(result.Reason, "blocked_zero", StringComparison.Ordinal)) continue;
                return false;
            }
            state = result.State;
            if (state.Status == RunStatus.Cleared) return true;
        }

        return false;
    }

    private static string PointKey(GridPoint point)
    {
        return point.X.ToString() + "," + point.Y.ToString() + ";";
    }

    private static bool HasHelperTargetSameHelper(GameMove[] moves)
    {
        for (int index = 0; index + 2 < moves.Length; index++)
        {
            if (moves[index].PieceIndex != 0 && moves[index + 1].PieceIndex == 0 && moves[index + 2].PieceIndex == moves[index].PieceIndex)
                return true;
        }

        return false;
    }

    private static void AssertLateHelperValue(RoomDefinition room, SolverResult full, LevelProbe probe)
    {
        for (int helperIndex = 0; helperIndex < probe.HelperFreezeStatuses.Length; helperIndex++)
        {
            string status = probe.HelperFreezeStatuses[helperIndex];
            int moves = probe.HelperFreezeMoves[helperIndex];
            bool passes = string.Equals(status, SolverStatus.Unsolvable.ToString(), StringComparison.Ordinal) ||
                (string.Equals(status, SolverStatus.Solved.ToString(), StringComparison.Ordinal) && moves >= full.Moves.Length + 2);
            Assert(passes, "late helper must be essential or improve shortest route by at least two slides: " + room.Id + ":" + (helperIndex + 1));
        }
    }

    private static RoomDefinition BuildOpenRoom()
    {
        return Room("open", Rows("########", "#......#", "#......#", "#......#", "#......#", "#......#", "#......#", "########"),
            new PieceDefinition[] { Piece("target", 1, 1) }, 4, 4, 1);
    }

    private static RoomDefinition BuildHelperStopRoom()
    {
        return Room("helper-stop", Rows("########", "#....#.#", "#......#", "#......#", "#......#", "#......#", "#......#", "########"),
            new PieceDefinition[] { Piece("target", 1, 1), Piece("helper", 4, 3) }, 4, 2, 2);
    }

    private static RoomDefinition BuildOvershootRoom()
    {
        return Room("overshoot", Rows("########", "#....#.#", "#......#", "#......#", "#......#", "#......#", "#......#", "########"),
            new PieceDefinition[] { Piece("target", 1, 1) }, 4, 3, 2);
    }

    private static RoomDefinition Room(string id, string[] rows, PieceDefinition[] pieces, int goalX, int goalY, int parMoves)
    {
        return new RoomDefinition { Id = id, Width = 8, Height = 8, Rows = rows, Pieces = pieces, TargetPieceIndex = 0, Goal = new GridPoint(goalX, goalY), ParMoves = parMoves };
    }

    private static PieceDefinition Piece(string id, int x, int y)
    {
        return new PieceDefinition { Id = id, Start = new GridPoint(x, y) };
    }

    private static string[] Rows(params string[] rows)
    {
        return rows;
    }

    private static GameMove Move(int pieceIndex, GameCommand direction)
    {
        return new GameMove { PieceIndex = pieceIndex, Direction = direction };
    }

    private static GameState RequireAccepted(RoomDefinition room, GameState state, GameMove move)
    {
        StepResult result = GameEngine.Step(room, state, move);
        Assert(result.Accepted, "fixture move must be accepted: " + result.Reason);
        return result.State;
    }

    private static GameState Replay(RoomDefinition room, GameMove[] moves)
    {
        GameState state = GameEngine.Create(room);
        for (int index = 0; index < moves.Length; index++) state = RequireAccepted(room, state, moves[index]);
        return state;
    }

    private static void AssertReplaysToClear(RoomDefinition room, GameMove[] moves, string label)
    {
        GameState state = Replay(room, moves);
        AssertEqual(RunStatus.Cleared, state.Status, label + " must clear");
        AssertEqual(moves.Length, state.Turn, label + " turn count");
    }

    private static void AssertRejectedRestore(RoomDefinition room, SaveEnvelope envelope, string contentVersion, string expectedError)
    {
        GameState state;
        GameMove[] moves;
        int selected;
        string error;
        bool restored = SaveCodec.TryRestore(room, envelope, contentVersion, out state, out moves, out selected, out error);
        Assert(!restored, "invalid save must reject");
        Assert(state == null && moves == null && selected == -1, "rejected save must not return partial state");
        AssertEqual(expectedError, error, "save rejection reason");
    }

    private static SaveEnvelope CloneEnvelope(SaveEnvelope source)
    {
        GameMove[] moves = new GameMove[source.Moves.Length];
        for (int index = 0; index < moves.Length; index++) moves[index] = Move(source.Moves[index].PieceIndex, source.Moves[index].Direction);
        GridPoint[] positions = new GridPoint[source.State.Positions.Length];
        Array.Copy(source.State.Positions, positions, positions.Length);
        return new SaveEnvelope
        {
            SchemaVersion = source.SchemaVersion,
            GameId = source.GameId,
            ContentVersion = source.ContentVersion,
            RoomHash = source.RoomHash,
            State = new GameState { RoomId = source.State.RoomId, Positions = positions, Turn = source.State.Turn, Status = source.State.Status },
            Moves = moves,
            SelectedPieceIndex = source.SelectedPieceIndex
        };
    }

    private static bool Contains(string[] values, string value)
    {
        for (int index = 0; index < values.Length; index++) if (string.Equals(values[index], value, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string[] MoveNames(GameMove[] moves)
    {
        string[] names = new string[moves.Length];
        for (int index = 0; index < moves.Length; index++) names[index] = moves[index].PieceIndex.ToString() + ":" + moves[index].Direction.ToString();
        return names;
    }

    private static int CountInitialTargetChoices(RoomDefinition room)
    {
        GameState state = GameEngine.Create(room);
        int count = 0;
        GameCommand[] directions = new GameCommand[] { GameCommand.Up, GameCommand.Right, GameCommand.Down, GameCommand.Left };
        for (int index = 0; index < directions.Length; index++)
            if (GameEngine.Step(room, state, Move(room.TargetPieceIndex, directions[index])).Accepted) count++;
        return count;
    }

    private static HelperStopWitness[] FindHelperStops(RoomDefinition room, GameMove[] moves)
    {
        List<HelperStopWitness> stops = new List<HelperStopWitness>();
        bool[] helperMoved = new bool[room.Pieces.Length];
        GameState state = GameEngine.Create(room);
        for (int moveIndex = 0; moveIndex < moves.Length; moveIndex++)
        {
            GameMove move = moves[moveIndex];
            GameState before = state;
            StepResult result = GameEngine.Step(room, before, move);
            Assert(result.Accepted, "quality replay move must be accepted");
            if (move.PieceIndex == room.TargetPieceIndex)
            {
                GridPoint start = before.Positions[move.PieceIndex];
                GridPoint destination = result.State.Positions[move.PieceIndex];
                int deltaX = destination.X == start.X ? 0 : (destination.X > start.X ? 1 : -1);
                int deltaY = destination.Y == start.Y ? 0 : (destination.Y > start.Y ? 1 : -1);
                GridPoint stopperPoint = new GridPoint(destination.X + deltaX, destination.Y + deltaY);
                for (int pieceIndex = 1; pieceIndex < before.Positions.Length; pieceIndex++)
                {
                    if (before.Positions[pieceIndex].Equals(stopperPoint))
                    {
                        stops.Add(new HelperStopWitness
                        {
                            MoveIndex = moveIndex + 1,
                            HelperIndex = pieceIndex,
                            Before = start,
                            Destination = destination,
                            Stopper = stopperPoint,
                            IsGoal = destination.Equals(room.Goal),
                            HelperMovedBefore = helperMoved[pieceIndex]
                        });
                    }
                }
            }

            if (move.PieceIndex != room.TargetPieceIndex) helperMoved[move.PieceIndex] = true;
            state = result.State;
        }

        return stops.ToArray();
    }

    private static HelperInteractionWitness[] FindHelperInteractions(RoomDefinition room, GameMove[] moves)
    {
        List<HelperInteractionWitness> interactions = new List<HelperInteractionWitness>();
        GameState state = GameEngine.Create(room);
        for (int moveIndex = 0; moveIndex < moves.Length; moveIndex++)
        {
            GameMove move = moves[moveIndex];
            GameState before = state;
            StepResult result = GameEngine.Step(room, before, move);
            Assert(result.Accepted, "quality replay move must be accepted");
            if (move.PieceIndex != room.TargetPieceIndex)
            {
                GridPoint start = before.Positions[move.PieceIndex];
                GridPoint destination = result.State.Positions[move.PieceIndex];
                int deltaX = destination.X == start.X ? 0 : (destination.X > start.X ? 1 : -1);
                int deltaY = destination.Y == start.Y ? 0 : (destination.Y > start.Y ? 1 : -1);
                GridPoint stopperPoint = new GridPoint(destination.X + deltaX, destination.Y + deltaY);
                for (int pieceIndex = 1; pieceIndex < before.Positions.Length; pieceIndex++)
                {
                    if (pieceIndex != move.PieceIndex && before.Positions[pieceIndex].Equals(stopperPoint))
                    {
                        interactions.Add(new HelperInteractionWitness
                        {
                            MoveIndex = moveIndex + 1,
                            MoverIndex = move.PieceIndex,
                            StopperHelperIndex = pieceIndex,
                            Before = start,
                            Destination = destination,
                            Stopper = stopperPoint
                        });
                    }
                }
            }

            state = result.State;
        }

        return interactions.ToArray();
    }

    private static bool HasInterHelperTargetDependency(HelperInteractionWitness[] interactions, HelperStopWitness[] targetStops)
    {
        for (int interactionIndex = 0; interactionIndex < interactions.Length; interactionIndex++)
        {
            HelperInteractionWitness interaction = interactions[interactionIndex];
            for (int stopIndex = 0; stopIndex < targetStops.Length; stopIndex++)
            {
                HelperStopWitness targetStop = targetStops[stopIndex];
                if (targetStop.MoveIndex > interaction.MoveIndex && targetStop.HelperIndex == interaction.MoverIndex && targetStop.HelperMovedBefore)
                    return true;
            }
        }

        return false;
    }

    private static int CountDistinctHelperStopDestinations(HelperStopWitness[] stops)
    {
        HashSet<string> destinations = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < stops.Length; index++)
        {
            HelperStopWitness stop = stops[index];
            destinations.Add(stop.Destination.X.ToString() + "," + stop.Destination.Y.ToString());
        }

        return destinations.Count;
    }

    private static GoalOvershootWitness FindGoalOvershootWitness(RoomDefinition room, int maxVisitedStates)
    {
        List<TraceNode> nodes = new List<TraceNode>();
        HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
        Queue<int> queue = new Queue<int>();
        GameState initial = GameEngine.Create(room);
        nodes.Add(new TraceNode(initial, -1, null));
        visited.Add(StateKey(initial));
        queue.Enqueue(0);

        GameCommand[] directions = new GameCommand[] { GameCommand.Up, GameCommand.Right, GameCommand.Down, GameCommand.Left };
        while (queue.Count > 0 && visited.Count < maxVisitedStates)
        {
            int nodeIndex = queue.Dequeue();
            TraceNode node = nodes[nodeIndex];
            for (int pieceIndex = 0; pieceIndex < room.Pieces.Length; pieceIndex++)
            {
                for (int directionIndex = 0; directionIndex < directions.Length; directionIndex++)
                {
                    GameMove move = Move(pieceIndex, directions[directionIndex]);
                    StepResult step = GameEngine.Step(room, node.State, move);
                    if (!step.Accepted) continue;
                    if (pieceIndex == room.TargetPieceIndex && PassesGoal(node.State.Positions[pieceIndex], step.State.Positions[pieceIndex], room.Goal))
                    {
                        return new GoalOvershootWitness
                        {
                            Moves = MoveNames(AppendTrace(nodes, nodeIndex, move)),
                            Before = node.State.Positions[pieceIndex],
                            Destination = step.State.Positions[pieceIndex],
                            Goal = room.Goal
                        };
                    }

                    if (step.State.Status != RunStatus.Playing) continue;
                    string key = StateKey(step.State);
                    if (visited.Add(key))
                    {
                        nodes.Add(new TraceNode(step.State, nodeIndex, move));
                        queue.Enqueue(nodes.Count - 1);
                    }
                }
            }
        }

        return null;
    }

    private static bool PassesGoal(GridPoint before, GridPoint destination, GridPoint goal)
    {
        if (before.X == destination.X && goal.X == before.X)
            return goal.Y > Math.Min(before.Y, destination.Y) && goal.Y < Math.Max(before.Y, destination.Y);
        if (before.Y == destination.Y && goal.Y == before.Y)
            return goal.X > Math.Min(before.X, destination.X) && goal.X < Math.Max(before.X, destination.X);
        return false;
    }

    private static bool IsInterior(GridPoint point)
    {
        return point.X > 0 && point.X < 7 && point.Y > 0 && point.Y < 7;
    }

    private static GameMove[] AppendTrace(List<TraceNode> nodes, int nodeIndex, GameMove finalMove)
    {
        List<GameMove> reversed = new List<GameMove>();
        reversed.Add(finalMove);
        int current = nodeIndex;
        while (current >= 0)
        {
            TraceNode node = nodes[current];
            if (node.Move != null) reversed.Add(Move(node.Move.PieceIndex, node.Move.Direction));
            current = node.ParentIndex;
        }

        reversed.Reverse();
        return reversed.ToArray();
    }

    private static string StateKey(GameState state)
    {
        string key = state.Status.ToString();
        for (int index = 0; index < state.Positions.Length; index++)
            key += ";" + state.Positions[index].X.ToString() + "," + state.Positions[index].Y.ToString();
        return key;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message + " (expected " + expected + ", actual " + actual + ")");
    }

    private sealed class CheckRecord
    {
        public string Name;
        public bool Passed;
        public string Detail;
    }

    private sealed class CheckSummary
    {
        public string Suite;
        public int Passed;
        public int Failed;
        public CheckRecord[] Checks;
        public LevelProbe[] Catalog;
    }

    private sealed class LevelProbe
    {
        public string SourceFile;
        public string Id;
        public string Status;
        public string[] Moves;
        public int VisitedCount;
        public int ParMoves;
        public int InitialTargetChoices;
        public string TargetOnlyStatus;
        public int TargetOnlyMoves;
        public int TargetOnlyVisitedCount;
        public string[] HelperFreezeStatuses;
        public int[] HelperFreezeMoves;
        public int[] HelperFreezeVisitedCounts;
        public bool HasHelperTargetSameHelper;
        public HelperStopWitness[] HelperStops;
        public HelperInteractionWitness[] HelperInteractions;
        public int DistinctHelperStopDestinations;
        public bool HelperStopMissing;
        public bool HasInterHelperTargetDependency;
        public GoalOvershootWitness Overshoot;
    }

    private sealed class HelperStopWitness
    {
        public int MoveIndex;
        public int HelperIndex;
        public GridPoint Before;
        public GridPoint Destination;
        public GridPoint Stopper;
        public bool IsGoal;
        public bool HelperMovedBefore;
    }

    private sealed class GoalOvershootWitness
    {
        public string[] Moves;
        public GridPoint Before;
        public GridPoint Destination;
        public GridPoint Goal;
    }

    private sealed class HelperInteractionWitness
    {
        public int MoveIndex;
        public int MoverIndex;
        public int StopperHelperIndex;
        public GridPoint Before;
        public GridPoint Destination;
        public GridPoint Stopper;
    }

    private sealed class TraceNode
    {
        public readonly GameState State;
        public readonly int ParentIndex;
        public readonly GameMove Move;

        public TraceNode(GameState state, int parentIndex, GameMove move)
        {
            State = state;
            ParentIndex = parentIndex;
            Move = move;
        }
    }
}
