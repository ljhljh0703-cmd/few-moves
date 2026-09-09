using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;

internal static class Program
{
    private const int BoardSize = 8;
    private const int SolverLimit = 100000;
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };

    private static int Main(string[] args)
    {
        string mode = Value(args, "--mode") ?? "search";
        string evidencePath = Value(args, "--evidence");
        string emitDir = Value(args, "--emit-dir");
        string output = mode == "search" ? Search(emitDir) : mode == "verify" ? Verify(Value(args, "--room-dir")) : throw new ArgumentException("Unknown mode: " + mode);
        Console.WriteLine(output);
        if (!string.IsNullOrEmpty(evidencePath)) File.WriteAllText(evidencePath, output + Environment.NewLine);
        return 0;
    }

    private static string Search(string emitDir)
    {
        var c2Candidates = new List<object>();
        var c3Candidates = new List<object>();
        var c2Diagnostics = new List<C2Diagnostic>();
        CoopRoomDefinition c2 = null;
        CoopRoomDefinition c3 = null;
        C2Measurement c2Measurement = null;
        C3Measurement c3Measurement = null;
        int c2Score = -1;

        foreach (CoopRoomDefinition candidate in Candidates())
        {
            if (c2Diagnostics.Count < 12 && TryProbeC2(candidate, out C2Diagnostic diagnostic) && diagnostic.Normal.Status == "Solved" && diagnostic.NormalStopperWitness.CircleStopped && diagnostic.NormalStopperWitness.DiamondStopped)
                c2Diagnostics.Add(diagnostic);
            if (TryMeasureC2(candidate, out C2Measurement measurement))
            {
                int score = ScoreC2(candidate, measurement);
                if (c2 == null || score > c2Score || score == c2Score && measurement.Normal.OptimalActionCount < c2Measurement.Normal.OptimalActionCount)
                {
                    c2 = candidate;
                    c2Measurement = measurement;
                    c2Score = score;
                }
            }
            if (TryMeasureC3(candidate, out C3Measurement c3Result) && (c3 == null || c3Result.Normal.OptimalActionCount < c3Measurement.Normal.OptimalActionCount))
            {
                c3 = candidate;
                c3Measurement = c3Result;
            }
        }

        var report = new AuthoringReport
        {
            RulesVersion = "coop-rules-v1",
            CandidateCount = Candidates().Count(),
            C2Diagnostics = c2Diagnostics,
            C2 = c2 == null ? null : new RoomReport { Room = c2, Measurement = c2Measurement },
            C3 = c3 == null ? null : new RoomReport { Room = c3, Measurement = c3Measurement }
        };
        if (!string.IsNullOrEmpty(emitDir)) EmitRoomsAndEvidence(emitDir, report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    private static bool TryMeasureC2(CoopRoomDefinition room, out C2Measurement measurement)
    {
        measurement = null;
        if (!TryProbeC2(room, out C2Diagnostic diagnostic)) return false;
        if (diagnostic.Normal.Status != "Solved" || diagnostic.Normal.Trace.Length == 0) return false;
        if (!diagnostic.NormalStopperWitness.CircleStopped || !diagnostic.NormalStopperWitness.DiamondStopped) return false;
        if (!IsConclusiveCounterfactual(diagnostic.ForbidCircleStop) || !IsConclusiveCounterfactual(diagnostic.ForbidDiamondStop)) return false;
        if (diagnostic.ForbidCircleStop.Status == "Solved" && diagnostic.ForbidCircleStop.OptimalActionCount <= diagnostic.Normal.OptimalActionCount) return false;
        if (diagnostic.ForbidDiamondStop.Status == "Solved" && diagnostic.ForbidDiamondStop.OptimalActionCount <= diagnostic.Normal.OptimalActionCount) return false;
        if (SameTrace(diagnostic.Normal.Trace, diagnostic.ForbidCircleStop.Trace) || SameTrace(diagnostic.Normal.Trace, diagnostic.ForbidDiamondStop.Trace)) return false;
        if (diagnostic.ForbidCircleStop.OptimalActionCount == diagnostic.Normal.OptimalActionCount && diagnostic.ForbidDiamondStop.OptimalActionCount == diagnostic.Normal.OptimalActionCount &&
            diagnostic.ForbidCircleStop.VisitedCount == diagnostic.Normal.VisitedCount && diagnostic.ForbidDiamondStop.VisitedCount == diagnostic.Normal.VisitedCount) return false;

        measurement = new C2Measurement
        {
            AllowPass = false,
            Normal = diagnostic.Normal,
            ForbidCircleStop = diagnostic.ForbidCircleStop,
            ForbidDiamondStop = diagnostic.ForbidDiamondStop,
            NormalStopperWitness = diagnostic.NormalStopperWitness
        };
        return true;
    }

    private static bool IsConclusiveCounterfactual(SolutionSummary result)
    {
        return result != null && (result.Status == "Solved" || result.Status == "Unsolvable");
    }

    private static bool TryProbeC2(CoopRoomDefinition room, out C2Diagnostic diagnostic)
    {
        diagnostic = null;
        if (CoopRules.ValidateRoom(room).Length != 0) return false;
        CoopSolverResult normal = SolveNoPass(room, CoopSolverOptions.Normal());
        if (normal.Status != CoopSolverStatus.Solved)
        {
            diagnostic = new C2Diagnostic { Room = room, Normal = Summarize(normal), ForbidCircleStop = EmptySummary(), ForbidDiamondStop = EmptySummary(), NormalStopperWitness = new StopperWitness() };
            return true;
        }
        StopperWitness normalWitness = Replay(room, normal.Commands);
        SolutionSummary normalSummary = Summarize(normal);
        normalSummary.TraceSteps = TraceStates(room, normal.Commands);
        if (!normalWitness.CircleStopped || !normalWitness.DiamondStopped)
        {
            diagnostic = new C2Diagnostic { Room = room, Normal = normalSummary, ForbidCircleStop = EmptySummary(), ForbidDiamondStop = EmptySummary(), NormalStopperWitness = normalWitness };
            return true;
        }
        CoopSolverResult noCircleStop = SolveNoPass(room, CoopSolverOptions.ForbidPartnerStopper(CoopActor.Circle));
        CoopSolverResult noDiamondStop = SolveNoPass(room, CoopSolverOptions.ForbidPartnerStopper(CoopActor.Diamond));
        diagnostic = new C2Diagnostic
        {
            Room = room,
            Normal = normalSummary,
            ForbidCircleStop = Summarize(noCircleStop),
            ForbidDiamondStop = Summarize(noDiamondStop),
            NormalStopperWitness = normalWitness
        };
        return true;
    }

    private static bool TryMeasureC3(CoopRoomDefinition room, out C3Measurement measurement)
    {
        measurement = null;
        if (CoopRules.ValidateRoom(room).Length != 0) return false;
        CoopSolverResult solution = SolveNoPass(room, CoopSolverOptions.Normal());
        if (solution.Status != CoopSolverStatus.Solved || solution.Commands.Length < 4) return false;
        C3Witness witness = FindC3Witness(room, solution.Commands);
        if (witness == null) return false;
        SolutionSummary summary = Summarize(solution);
        summary.TraceSteps = TraceStates(room, solution.Commands);
        measurement = new C3Measurement
        {
            AllowPass = false,
            Normal = summary,
            GoalLeaveHelpGoal = witness,
            Trace = TraceDirections(solution.Commands),
            StateTrace = TraceStates(room, solution.Commands)
        };
        return true;
    }

    private static int ScoreC2(CoopRoomDefinition room, C2Measurement measurement)
    {
        bool circleSolved = measurement.ForbidCircleStop.Status == "Solved";
        bool diamondSolved = measurement.ForbidDiamondStop.Status == "Solved";
        bool circleWorsened = circleSolved && measurement.ForbidCircleStop.OptimalActionCount > measurement.Normal.OptimalActionCount;
        bool diamondWorsened = diamondSolved && measurement.ForbidDiamondStop.OptimalActionCount > measurement.Normal.OptimalActionCount;
        int score = circleWorsened && diamondWorsened ? 30 : circleWorsened || diamondWorsened ? 20 : 10;
        for (int y = 1; y < room.Height - 1; y++) for (int x = 1; x < room.Width - 1; x++) if (room.Rows[y][x] == '#') return score + 5;
        return score;
    }

    private static string Verify(string roomDir)
    {
        if (string.IsNullOrEmpty(roomDir)) throw new ArgumentException("--room-dir is required for verify mode");
        var report = new AuthoringReport { RulesVersion = "coop-rules-v1", CandidateCount = 2, C2Diagnostics = new List<C2Diagnostic>() };
        foreach (string id in new[] { "coop-c2", "coop-c3" })
        {
            string path = Path.Combine(roomDir, id + ".json");
            if (!File.Exists(path)) throw new FileNotFoundException("Room JSON missing", path);
            CoopRoomDefinition room = JsonSerializer.Deserialize<CoopRoomDefinition>(File.ReadAllText(path), JsonOptions);
            if (room == null || CoopRules.ValidateRoom(room).Length != 0) throw new InvalidOperationException(id + " failed ValidateRoom");
            C2Measurement c2;
            if (id == "coop-c2")
            {
                if (!TryMeasureC2(room, out c2)) throw new InvalidOperationException("coop-c2 failed stopper counterfactual gate");
                report.C2 = new RoomReport { Room = room, Measurement = c2 };
            }
            else
            {
                C3Measurement c3;
                if (!TryMeasureC3(room, out c3)) throw new InvalidOperationException("coop-c3 failed goal-leave-help-goal gate");
                report.C3 = new RoomReport { Room = room, Measurement = c3 };
            }
        }
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    private static void EmitRoomsAndEvidence(string emitDir, AuthoringReport report)
    {
        Directory.CreateDirectory(emitDir);
        if (report.C2 != null)
        {
            report.C2.Room = WithMetadata(report.C2.Room, "coop-c2", "coop-c2-v1");
            File.WriteAllText(Path.Combine(emitDir, "coop-c2.json"), JsonSerializer.Serialize(report.C2.Room, JsonOptions));
        }
        if (report.C3 != null)
        {
            report.C3.Room = WithMetadata(report.C3.Room, "coop-c3", "coop-c3-v1");
            File.WriteAllText(Path.Combine(emitDir, "coop-c3.json"), JsonSerializer.Serialize(report.C3.Room, JsonOptions));
        }
    }

    private static CoopRoomDefinition WithMetadata(CoopRoomDefinition source, string id, string contentVersion)
    {
        CoopRoomDefinition room = CoopRules.CloneRoom(source);
        room.Id = id;
        room.ContentVersion = contentVersion;
        return room;
    }

    private static CoopSolverResult SolveNoPass(CoopRoomDefinition room, CoopSolverOptions options)
    {
        options.AllowPass = false;
        return CoopSolver.FindSolution(room, SolverLimit, options);
    }

    private static C3Witness FindC3Witness(CoopRoomDefinition room, CoopCommand[] commands)
    {
        CoopSession session = CoopSession.Create(room);
        CoopActor? firstGoalActor = null;
        int firstGoalIndex = -1;
        int leaveIndex = -1;
        int helpIndex = -1;
        int helpStartIndex = -1;
        int partnerStopIndex = -1;
        int returnIndex = -1;
        CoopState prior = session.State;
        for (int index = 0; index < commands.Length; index++)
        {
            CoopCommand command = commands[index];
            CoopDispatchResult result = session.Dispatch(command);
            if (!result.Accepted) return null;
            CoopState after = result.State;
            if (!firstGoalActor.HasValue)
            {
                if (CoopRules.Same(after.CirclePosition, room.CircleGoal)) { firstGoalActor = CoopActor.Circle; firstGoalIndex = index; }
                else if (CoopRules.Same(after.DiamondPosition, room.DiamondGoal)) { firstGoalActor = CoopActor.Diamond; firstGoalIndex = index; }
            }
            else if (leaveIndex < 0 && AtGoal(room, prior, firstGoalActor.Value) && !AtGoal(room, after, firstGoalActor.Value) && command.Seat == firstGoalActor.Value)
            {
                leaveIndex = index;
            }
            else if (leaveIndex >= 0 && helpIndex < 0 && command.Seat == firstGoalActor.Value && command.Kind == CoopCommandKind.Slide)
            {
                helpStartIndex = index;
                helpIndex = index;
            }
            else if (helpIndex >= 0 && partnerStopIndex < 0 && command.Seat != firstGoalActor.Value && command.Kind == CoopCommandKind.Slide)
            {
                for (int eventIndex = 0; eventIndex < result.Events.Length; eventIndex++)
                    if (result.Events[eventIndex].Type == "stopped_by_partner" && result.Events[eventIndex].Actor != firstGoalActor.Value && result.Events[eventIndex].Detail == firstGoalActor.Value.ToString()) partnerStopIndex = index;
            }
            else if (partnerStopIndex >= 0 && returnIndex < 0 && command.Seat == firstGoalActor.Value && AtGoal(room, after, firstGoalActor.Value))
            {
                returnIndex = index;
            }
            prior = after;
        }
        if (!firstGoalActor.HasValue || leaveIndex < 0 || helpIndex < 0 || partnerStopIndex < 0 || returnIndex < 0) return null;
        return new C3Witness
        {
            FirstGoalActor = firstGoalActor.Value.ToString(),
            FirstGoalAction = firstGoalIndex,
            LeaveAction = leaveIndex,
            HelpStartAction = helpStartIndex,
            HelpAction = helpIndex,
            PartnerStopAction = partnerStopIndex,
            ReturnGoalAction = returnIndex,
            HelpActor = commands[helpIndex].Seat.ToString()
        };
    }

    private static bool AtGoal(CoopRoomDefinition room, CoopState state, CoopActor actor)
    {
        return actor == CoopActor.Circle ? CoopRules.Same(state.CirclePosition, room.CircleGoal) : CoopRules.Same(state.DiamondPosition, room.DiamondGoal);
    }

    private static StopperWitness Replay(CoopRoomDefinition room, CoopCommand[] commands)
    {
        CoopSession session = CoopSession.Create(room);
        var witness = new StopperWitness();
        for (int index = 0; index < commands.Length; index++)
        {
            CoopDispatchResult result = session.Dispatch(commands[index]);
            if (!result.Accepted) continue;
            for (int eventIndex = 0; eventIndex < result.Events.Length; eventIndex++)
            {
                CoopEvent item = result.Events[eventIndex];
                if (!string.Equals(item.Type, "stopped_by_partner", StringComparison.Ordinal)) continue;
                if (item.Actor == CoopActor.Circle) { witness.CircleStopped = true; witness.CircleAction = index; }
                if (item.Actor == CoopActor.Diamond) { witness.DiamondStopped = true; witness.DiamondAction = index; }
            }
        }
        return witness;
    }

    private static bool SameTrace(string[] left, string[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
        return true;
    }

    private static SolutionSummary Summarize(CoopSolverResult result)
    {
        return new SolutionSummary
        {
            Status = result.Status.ToString(),
            OptimalActionCount = result.OptimalActionCount,
            VisitedCount = result.VisitedCount,
            Trace = TraceDirections(result.Commands),
            TraceSteps = new TraceStep[0]
        };
    }

    private static SolutionSummary EmptySummary()
    {
        return new SolutionSummary { Status = "not_run", OptimalActionCount = -1, VisitedCount = -1, Trace = new string[0] };
    }

    private static string[] TraceDirections(CoopCommand[] commands)
    {
        var trace = new string[commands.Length];
        for (int index = 0; index < commands.Length; index++) trace[index] = commands[index].Direction.ToString();
        return trace;
    }

    private static TraceStep[] TraceStates(CoopRoomDefinition room, CoopCommand[] commands)
    {
        CoopSession session = CoopSession.Create(room);
        var steps = new TraceStep[commands.Length];
        for (int index = 0; index < commands.Length; index++)
        {
            CoopCommand command = commands[index];
            CoopDispatchResult result = session.Dispatch(command);
            CoopState state = result.State;
            var events = new string[result.Events == null ? 0 : result.Events.Length];
            for (int eventIndex = 0; eventIndex < events.Length; eventIndex++)
                events[eventIndex] = result.Events[eventIndex].Type + ":" + result.Events[eventIndex].Actor + ":" + result.Events[eventIndex].Detail;
            steps[index] = new TraceStep
            {
                Index = index,
                Actor = command.Seat.ToString(),
                Direction = command.Direction.ToString(),
                CircleX = state.CirclePosition.X,
                CircleY = state.CirclePosition.Y,
                DiamondX = state.DiamondPosition.X,
                DiamondY = state.DiamondPosition.Y,
                CircleAtGoal = CoopRules.Same(state.CirclePosition, room.CircleGoal),
                DiamondAtGoal = CoopRules.Same(state.DiamondPosition, room.DiamondGoal),
                Events = events
            };
        }
        return steps;
    }

    private static IEnumerable<CoopRoomDefinition> Candidates()
    {
        string[][] patterns =
        {
            new[] { "########", "#......#", "#......#", "#......#", "#......#", "#......#", "#......#", "########" },
            new[] { "########", "#.....##", "#..#...#", "#....#.#", "#......#", "#..#...#", "##.##..#", "########" },
            new[] { "########", "#.....##", "#......#", "#..#...#", "#......#", "#...#..#", "##.....#", "########" },
            new[] { "########", "#......#", "#..##..#", "#......#", "#..##..#", "#......#", "#......#", "########" },
            new[] { "########", "#...#..#", "#......#", "##..#..#", "#......#", "#..#...#", "#......#", "########" },
            new[] { "########", "#......#", "#.#....#", "#....#.#", "#......#", "#.#....#", "#....#.#", "########" }
        };
        var positions = new List<GridPoint>();
        for (int y = 1; y < 7; y++) for (int x = 1; x < 7; x++) positions.Add(new GridPoint(x, y));
        int id = 0;
        yield return new CoopRoomDefinition
        {
            Id = "coop-c1-reference",
            Width = BoardSize,
            Height = BoardSize,
            Rows = new[] { "########", "#.....##", "#..#...#", "#....#.#", "#......#", "#..#...#", "##.##..#", "########" },
            CircleStart = new GridPoint(6, 6),
            DiamondStart = new GridPoint(5, 1),
            CircleGoal = new GridPoint(6, 3),
            DiamondGoal = new GridPoint(6, 4),
            RulesVersion = "coop-rules-v1",
            ContentVersion = "coop-c1-reference-v1"
        };
        for (int patternIndex = 0; patternIndex < patterns.Length; patternIndex++)
        {
            string[] rows = patterns[patternIndex];
            foreach (CoopRoomDefinition room in MakeCandidates(rows, positions, id, floorMultiplier: 100)) { yield return room; id++; }
        }
        for (int patternIndex = 0; patternIndex < 64; patternIndex++)
        {
            string[] rows = GeneratedRows(patternIndex + 1);
            foreach (CoopRoomDefinition room in MakeCandidates(rows, positions, id, floorMultiplier: 5)) { yield return room; id++; }
        }
    }

    private static IEnumerable<CoopRoomDefinition> MakeCandidates(string[] rows, List<GridPoint> positions, int idStart, int floorMultiplier)
    {
        var floor = positions.Where(point => rows[point.Y][point.X] == '.').ToList();
        if (floor.Count < 4) yield break;
        for (int seed = 0; seed < floor.Count * floorMultiplier; seed++)
        {
            GridPoint circleStart = floor[(seed * 5 + 1) % floor.Count];
            GridPoint diamondStart = floor[(seed * 7 + 9) % floor.Count];
            GridPoint circleGoal = floor[(seed * 11 + 17) % floor.Count];
            GridPoint diamondGoal = floor[(seed * 13 + 23) % floor.Count];
            if (Same(circleStart, diamondStart) || Same(circleGoal, diamondGoal) || Same(circleStart, circleGoal) || Same(diamondStart, diamondGoal)) continue;
            int id = idStart + seed;
            yield return new CoopRoomDefinition
            {
                Id = "coop-candidate-" + id.ToString(CultureInfo.InvariantCulture), Width = BoardSize, Height = BoardSize,
                Rows = (string[])rows.Clone(), CircleStart = circleStart, DiamondStart = diamondStart,
                CircleGoal = circleGoal, DiamondGoal = diamondGoal, RulesVersion = "coop-rules-v1",
                ContentVersion = "candidate-" + id.ToString(CultureInfo.InvariantCulture) + "-v1"
            };
        }
    }

    private static string[] GeneratedRows(int seed)
    {
        uint value = (uint)(seed * 747796405 + 2891336453u);
        var rows = new string[BoardSize];
        rows[0] = "########"; rows[BoardSize - 1] = "########";
        for (int y = 1; y < BoardSize - 1; y++)
        {
            var chars = new char[BoardSize]; chars[0] = '#'; chars[BoardSize - 1] = '#';
            for (int x = 1; x < BoardSize - 1; x++)
            {
                value = value * 1664525u + 1013904223u;
                chars[x] = value % 100u < 17u ? '#' : '.';
            }
            rows[y] = new string(chars);
        }
        return rows;
    }

    private static bool Same(GridPoint left, GridPoint right) { return left.X == right.X && left.Y == right.Y; }
    private static string Value(string[] args, string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }

    private sealed class AuthoringReport { public string RulesVersion; public int CandidateCount; public List<C2Diagnostic> C2Diagnostics; public RoomReport C2; public RoomReport C3; }
    private sealed class RoomReport { public CoopRoomDefinition Room; public object Measurement; }
    private sealed class C2Measurement { public bool AllowPass; public SolutionSummary Normal; public SolutionSummary ForbidCircleStop; public SolutionSummary ForbidDiamondStop; public StopperWitness NormalStopperWitness; }
    private sealed class C2Diagnostic { public CoopRoomDefinition Room; public SolutionSummary Normal; public SolutionSummary ForbidCircleStop; public SolutionSummary ForbidDiamondStop; public StopperWitness NormalStopperWitness; }
    private sealed class C3Measurement { public bool AllowPass; public SolutionSummary Normal; public C3Witness GoalLeaveHelpGoal; public string[] Trace; public TraceStep[] StateTrace; }
    private sealed class SolutionSummary { public string Status; public int OptimalActionCount; public int VisitedCount; public string[] Trace; public TraceStep[] TraceSteps; }
    private sealed class StopperWitness { public bool CircleStopped; public int CircleAction = -1; public bool DiamondStopped; public int DiamondAction = -1; }
    private sealed class C3Witness { public string FirstGoalActor; public int FirstGoalAction; public int LeaveAction; public int HelpStartAction; public int HelpAction; public int PartnerStopAction; public int ReturnGoalAction; public string HelpActor; }
    private sealed class TraceStep
    {
        public int Index;
        public string Actor;
        public string Direction;
        public int CircleX;
        public int CircleY;
        public int DiamondX;
        public int DiamondY;
        public bool CircleAtGoal;
        public bool DiamondAtGoal;
        public string[] Events;
    }
}
