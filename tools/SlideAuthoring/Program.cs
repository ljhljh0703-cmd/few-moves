using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nectorial.SlideEscape;

internal static class Program
{
    private const int DefaultAttemptBudget = 20000;
    private const int SolverBudget = 500000;
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };
    private static readonly GridPoint TargetStart = new GridPoint(1, 1);
    private static readonly GridPoint[] HelperStarts =
    {
        new GridPoint(6, 6), new GridPoint(5, 6), new GridPoint(6, 5),
        new GridPoint(4, 6), new GridPoint(6, 4), new GridPoint(1, 6)
    };
    private static readonly GridPoint[] Goals =
    {
        new GridPoint(3, 3), new GridPoint(4, 3), new GridPoint(5, 3),
        new GridPoint(3, 4), new GridPoint(4, 4), new GridPoint(5, 4),
        new GridPoint(3, 5), new GridPoint(4, 5), new GridPoint(5, 5)
    };
    private static readonly GridPoint[] WallPool =
    {
        new GridPoint(1, 4), new GridPoint(2, 5), new GridPoint(2, 6),
        new GridPoint(3, 3), new GridPoint(3, 5), new GridPoint(3, 6),
        new GridPoint(4, 2), new GridPoint(4, 5), new GridPoint(4, 6),
        new GridPoint(5, 1), new GridPoint(5, 3), new GridPoint(5, 5),
        new GridPoint(6, 2), new GridPoint(6, 4), new GridPoint(6, 6)
    };

    private static int Main(string[] args)
    {
        int attemptBudget = DefaultAttemptBudget;
        string mode = "reciprocal";
        if (args != null)
        {
            for (int index = 0; index + 1 < args.Length; index += 2)
            {
                if (string.Equals(args[index], "--attempt-budget", StringComparison.Ordinal)) attemptBudget = int.Parse(args[index + 1]);
                else if (string.Equals(args[index], "--mode", StringComparison.Ordinal)) mode = args[index + 1];
                else throw new ArgumentException("unknown_argument:" + args[index]);
            }
        }
        if (attemptBudget <= 0) throw new ArgumentOutOfRangeException("attemptBudget");

        List<RoomDefinition> protectedRooms = LoadProtectedRooms();
        SearchSummary summary = string.Equals(mode, "chain3", StringComparison.Ordinal)
            ? SearchChain3Template(attemptBudget, protectedRooms)
            : SearchReciprocalTemplate(attemptBudget, protectedRooms);
        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
        return summary.Candidates.Length > 0 ? 0 : 1;
    }

    private static SearchSummary SearchReciprocalTemplate(int attemptBudget, List<RoomDefinition> protectedRooms)
    {
        List<Candidate> candidates = new List<Candidate>();
        RejectionCounts rejections = new RejectionCounts();
        int attempts = 0;
        const int wallCount = 5;
        int masks = 1 << WallPool.Length;
        for (int helperIndex = 0; helperIndex < HelperStarts.Length && attempts < attemptBudget; helperIndex++)
        {
            GridPoint helperStart = HelperStarts[helperIndex];
            for (int goalIndex = 0; goalIndex < Goals.Length && attempts < attemptBudget; goalIndex++)
            {
                GridPoint goal = Goals[goalIndex];
                for (int mask = 0; mask < masks && attempts < attemptBudget; mask++)
                {
                    if (BitCount(mask) != wallCount) continue;
                    if (HasSelectedWall(mask, TargetStart) || HasSelectedWall(mask, helperStart) || HasSelectedWall(mask, goal)) continue;
                    attempts++;
                    RoomDefinition room = BuildRoom(mask, helperStart, goal);
                    Candidate candidate;
                    string rejection;
                    if (!TryEvaluate(room, protectedRooms, out candidate, out rejection))
                    {
                        rejections.Add(rejection);
                        continue;
                    }

                    candidates.Add(candidate);
                    if (candidates.Count == 8) goto Complete;
                }
            }
        }

    Complete:
        return new SearchSummary
        {
            Tool = "slide-authoring-reciprocal-v1",
            Randomness = "none",
            EnumerationOrder = "helper_start_index,goal_index,ascending_wall_mask with exactly five selected WallPool cells",
            AttemptBudget = attemptBudget,
            Attempts = attempts,
            Template = "helper must stop against target before a later target stop against that moved helper",
            Rejections = rejections,
            Candidates = candidates.ToArray()
        };
    }

    private static SearchSummary SearchChain3Template(int attemptBudget, List<RoomDefinition> protectedRooms)
    {
        GridPoint[] primaryStarts = { new GridPoint(1, 5), new GridPoint(4, 6), new GridPoint(5, 6), new GridPoint(6, 5) };
        GridPoint[] secondaryStarts = { new GridPoint(6, 6), new GridPoint(5, 6), new GridPoint(6, 5), new GridPoint(1, 6) };
        List<Candidate> candidates = new List<Candidate>();
        RejectionCounts rejections = new RejectionCounts();
        int attempts = 0;
        const int wallCount = 5;
        int masks = 1 << WallPool.Length;
        for (int firstIndex = 0; firstIndex < primaryStarts.Length && attempts < attemptBudget; firstIndex++)
        {
            for (int secondIndex = 0; secondIndex < secondaryStarts.Length && attempts < attemptBudget; secondIndex++)
            {
                if (primaryStarts[firstIndex].Equals(secondaryStarts[secondIndex])) continue;
                for (int goalIndex = 0; goalIndex < Goals.Length && attempts < attemptBudget; goalIndex++)
                {
                    for (int mask = 0; mask < masks && attempts < attemptBudget; mask++)
                    {
                        if (BitCount(mask) != wallCount) continue;
                        if (HasSelectedWall(mask, TargetStart) || HasSelectedWall(mask, primaryStarts[firstIndex]) ||
                            HasSelectedWall(mask, secondaryStarts[secondIndex]) || HasSelectedWall(mask, Goals[goalIndex])) continue;
                        attempts++;
                        RoomDefinition room = BuildRoom3(mask, primaryStarts[firstIndex], secondaryStarts[secondIndex], Goals[goalIndex]);
                        Candidate candidate;
                        string rejection;
                        if (!TryEvaluateChain3(room, out candidate, out rejection))
                        {
                            rejections.Add(rejection);
                            continue;
                        }

                        candidates.Add(candidate);
                        if (candidates.Count == 8) goto CompleteChain;
                    }
                }
            }
        }

    CompleteChain:
        return new SearchSummary
        {
            Tool = "slide-authoring-chain3-v1",
            Randomness = "none",
            EnumerationOrder = "primary_start_index,secondary_start_index,goal_index,ascending_wall_mask with exactly five selected WallPool cells",
            AttemptBudget = attemptBudget,
            Attempts = attempts,
            Template = "one helper must stop against the other helper before a later target stop against that moved helper",
            Rejections = rejections,
            Candidates = candidates.ToArray()
        };
    }

    private static bool TryEvaluateChain3(RoomDefinition room, out Candidate candidate, out string rejection)
    {
        candidate = null;
        string[] validation = GameEngine.ValidateRoom(room);
        if (validation.Length > 0) { rejection = "invalid_room"; return false; }
        SolverResult solved = Solver.FindSolution(room, SolverBudget);
        if (solved.Status != SolverStatus.Solved) { rejection = "not_solved:" + solved.Status; return false; }
        if (solved.Moves.Length < 8 || solved.Moves.Length > 12) { rejection = "move_band"; return false; }
        SolverResult targetOnly = Solver.FindSolution(room, SolverBudget, 1);
        if (targetOnly.Status != SolverStatus.Unsolvable) { rejection = "target_only:" + targetOnly.Status; return false; }
        if (CountInitialTargetChoices(room) < 2) { rejection = "initial_choices"; return false; }
        for (int helperIndex = 1; helperIndex < room.Pieces.Length; helperIndex++)
        {
            int mask = ((1 << room.Pieces.Length) - 1) & ~(1 << helperIndex);
            SolverResult frozen = Solver.FindSolution(room, SolverBudget, mask);
            if (frozen.Status == SolverStatus.LimitReached) { rejection = "freeze_limit"; return false; }
            if (frozen.Status != SolverStatus.Unsolvable && (frozen.Status != SolverStatus.Solved || frozen.Moves.Length < solved.Moves.Length + 2))
            {
                rejection = "freeze_value";
                return false;
            }
        }

        HelperInteraction[] interactions = FindHelperInteractions3(room, solved.Moves);
        TargetStop[] stops = FindTargetStops3(room, solved.Moves);
        if (!HasChainContribution(interactions, stops)) { rejection = "reciprocal_stop"; return false; }
        candidate = new Candidate
        {
            Rows = room.Rows,
            HelperStart = room.Pieces[1].Start,
            Helper2Start = room.Pieces[2].Start,
            Goal = room.Goal,
            MinimumMoves = solved.Moves.Length,
            Route = MoveNames(solved.Moves),
            TargetOnlyVisited = targetOnly.VisitedCount,
            Interactions = interactions,
            TargetStops = stops
        };
        rejection = null;
        return true;
    }

    private static bool TryEvaluate(RoomDefinition room, List<RoomDefinition> protectedRooms, out Candidate candidate, out string rejection)
    {
        candidate = null;
        string[] validation = GameEngine.ValidateRoom(room);
        if (validation.Length > 0) { rejection = "invalid_room"; return false; }
        SolverResult solved = Solver.FindSolution(room, SolverBudget);
        if (solved.Status != SolverStatus.Solved) { rejection = "not_solved:" + solved.Status; return false; }
        if (solved.Moves.Length < 6 || solved.Moves.Length > 9) { rejection = "move_band"; return false; }
        SolverResult targetOnly = Solver.FindSolution(room, SolverBudget, 1);
        if (targetOnly.Status != SolverStatus.Unsolvable) { rejection = "target_only:" + targetOnly.Status; return false; }
        if (CountInitialTargetChoices(room) < 2) { rejection = "initial_choices"; return false; }
        if (!HasHelperTargetSameHelper(solved.Moves)) { rejection = "helper_target_helper"; return false; }
        HelperInteraction[] interactions = FindHelperInteractions(room, solved.Moves);
        TargetStop[] stops = FindTargetStops(room, solved.Moves);
        if (!HasReciprocalContribution(interactions, stops)) { rejection = "reciprocal_stop"; return false; }
        if (MatchesD4Topology(room, protectedRooms)) { rejection = "d4_duplicate"; return false; }
        if (RecipeTransfers(room, protectedRooms)) { rejection = "recipe_transfer"; return false; }

        candidate = new Candidate
        {
            Rows = room.Rows,
            HelperStart = room.Pieces[1].Start,
            Goal = room.Goal,
            MinimumMoves = solved.Moves.Length,
            Route = MoveNames(solved.Moves),
            TargetOnlyVisited = targetOnly.VisitedCount,
            Interactions = interactions,
            TargetStops = stops
        };
        rejection = null;
        return true;
    }

    private static RoomDefinition BuildRoom(int mask, GridPoint helperStart, GridPoint goal)
    {
        char[,] tiles = new char[8, 8];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++) tiles[y, x] = x == 0 || x == 7 || y == 0 || y == 7 ? '#' : '.';
        for (int index = 0; index < WallPool.Length; index++)
            if ((mask & (1 << index)) != 0) tiles[WallPool[index].Y, WallPool[index].X] = '#';
        string[] rows = new string[8];
        for (int y = 0; y < 8; y++)
        {
            char[] row = new char[8];
            for (int x = 0; x < 8; x++) row[x] = tiles[y, x];
            rows[y] = new string(row);
        }

        return new RoomDefinition
        {
            Id = "authoring-reciprocal",
            Width = 8,
            Height = 8,
            Rows = rows,
            Pieces = new PieceDefinition[]
            {
                new PieceDefinition { Id = "target", Start = TargetStart },
                new PieceDefinition { Id = "helper-a", Start = helperStart }
            },
            TargetPieceIndex = 0,
            Goal = goal,
            ParMoves = 6
        };
    }

    private static RoomDefinition BuildRoom3(int mask, GridPoint firstHelperStart, GridPoint secondHelperStart, GridPoint goal)
    {
        RoomDefinition room = BuildRoom(mask, firstHelperStart, goal);
        room.Id = "authoring-chain3";
        room.Pieces = new PieceDefinition[]
        {
            new PieceDefinition { Id = "target", Start = TargetStart },
            new PieceDefinition { Id = "helper-a", Start = firstHelperStart },
            new PieceDefinition { Id = "helper-b", Start = secondHelperStart }
        };
        return room;
    }

    private static List<RoomDefinition> LoadProtectedRooms()
    {
        string directory = Path.GetFullPath("Assets/Nectorial/Resources/SlideRooms");
        List<RoomDefinition> rooms = new List<RoomDefinition>();
        for (int number = 1; number <= 6; number++)
        {
            string path = Path.Combine(directory, "room-" + (number < 10 ? "0" : "") + number + ".json");
            RoomDefinition room = JsonSerializer.Deserialize<RoomDefinition>(File.ReadAllText(path), JsonOptions);
            if (room != null) rooms.Add(room);
        }

        return rooms;
    }

    private static bool HasReciprocalContribution(HelperInteraction[] interactions, TargetStop[] stops)
    {
        for (int interactionIndex = 0; interactionIndex < interactions.Length; interactionIndex++)
        {
            HelperInteraction interaction = interactions[interactionIndex];
            for (int stopIndex = 0; stopIndex < stops.Length; stopIndex++)
            {
                TargetStop stop = stops[stopIndex];
                if (stop.MoveIndex > interaction.MoveIndex && stop.HelperIndex == interaction.MoverIndex) return true;
            }
        }

        return false;
    }

    private static bool HasChainContribution(HelperInteraction[] interactions, TargetStop[] stops)
    {
        for (int interactionIndex = 0; interactionIndex < interactions.Length; interactionIndex++)
        {
            HelperInteraction interaction = interactions[interactionIndex];
            for (int stopIndex = 0; stopIndex < stops.Length; stopIndex++)
            {
                TargetStop stop = stops[stopIndex];
                if (stop.MoveIndex > interaction.MoveIndex && stop.HelperIndex == interaction.MoverIndex) return true;
            }
        }

        return false;
    }

    private static HelperInteraction[] FindHelperInteractions(RoomDefinition room, GameMove[] moves)
    {
        List<HelperInteraction> interactions = new List<HelperInteraction>();
        GameState state = GameEngine.Create(room);
        for (int moveIndex = 0; moveIndex < moves.Length; moveIndex++)
        {
            GameMove move = moves[moveIndex];
            GameState before = state;
            StepResult result = GameEngine.Step(room, state, move);
            if (!result.Accepted) return new HelperInteraction[0];
            if (move.PieceIndex == 1)
            {
                GridPoint start = before.Positions[1];
                GridPoint destination = result.State.Positions[1];
                GridPoint stopper = Beyond(start, destination);
                if (stopper.Equals(before.Positions[0])) interactions.Add(new HelperInteraction { MoveIndex = moveIndex + 1, MoverIndex = 1, StopperIndex = 0, Destination = destination, Stopper = stopper });
            }

            state = result.State;
        }

        return interactions.ToArray();
    }

    private static TargetStop[] FindTargetStops(RoomDefinition room, GameMove[] moves)
    {
        List<TargetStop> stops = new List<TargetStop>();
        GameState state = GameEngine.Create(room);
        for (int moveIndex = 0; moveIndex < moves.Length; moveIndex++)
        {
            GameMove move = moves[moveIndex];
            GameState before = state;
            StepResult result = GameEngine.Step(room, state, move);
            if (!result.Accepted) return new TargetStop[0];
            if (move.PieceIndex == 0)
            {
                GridPoint stopper = Beyond(before.Positions[0], result.State.Positions[0]);
                if (stopper.Equals(before.Positions[1])) stops.Add(new TargetStop { MoveIndex = moveIndex + 1, HelperIndex = 1, Destination = result.State.Positions[0], Stopper = stopper });
            }

            state = result.State;
        }

        return stops.ToArray();
    }

    private static HelperInteraction[] FindHelperInteractions3(RoomDefinition room, GameMove[] moves)
    {
        List<HelperInteraction> interactions = new List<HelperInteraction>();
        GameState state = GameEngine.Create(room);
        for (int moveIndex = 0; moveIndex < moves.Length; moveIndex++)
        {
            GameMove move = moves[moveIndex];
            GameState before = state;
            StepResult result = GameEngine.Step(room, state, move);
            if (!result.Accepted) return new HelperInteraction[0];
            if (move.PieceIndex != 0)
            {
                GridPoint stopper = Beyond(before.Positions[move.PieceIndex], result.State.Positions[move.PieceIndex]);
                for (int pieceIndex = 1; pieceIndex < before.Positions.Length; pieceIndex++)
                {
                    if (pieceIndex != move.PieceIndex && stopper.Equals(before.Positions[pieceIndex]))
                    {
                        interactions.Add(new HelperInteraction
                        {
                            MoveIndex = moveIndex + 1,
                            MoverIndex = move.PieceIndex,
                            StopperIndex = pieceIndex,
                            Destination = result.State.Positions[move.PieceIndex],
                            Stopper = stopper
                        });
                    }
                }
            }

            state = result.State;
        }

        return interactions.ToArray();
    }

    private static TargetStop[] FindTargetStops3(RoomDefinition room, GameMove[] moves)
    {
        List<TargetStop> stops = new List<TargetStop>();
        GameState state = GameEngine.Create(room);
        for (int moveIndex = 0; moveIndex < moves.Length; moveIndex++)
        {
            GameMove move = moves[moveIndex];
            GameState before = state;
            StepResult result = GameEngine.Step(room, state, move);
            if (!result.Accepted) return new TargetStop[0];
            if (move.PieceIndex == 0)
            {
                GridPoint stopper = Beyond(before.Positions[0], result.State.Positions[0]);
                for (int helperIndex = 1; helperIndex < before.Positions.Length; helperIndex++)
                    if (stopper.Equals(before.Positions[helperIndex])) stops.Add(new TargetStop { MoveIndex = moveIndex + 1, HelperIndex = helperIndex, Destination = result.State.Positions[0], Stopper = stopper });
            }
            state = result.State;
        }

        return stops.ToArray();
    }

    private static GridPoint Beyond(GridPoint start, GridPoint destination)
    {
        int deltaX = destination.X == start.X ? 0 : (destination.X > start.X ? 1 : -1);
        int deltaY = destination.Y == start.Y ? 0 : (destination.Y > start.Y ? 1 : -1);
        return new GridPoint(destination.X + deltaX, destination.Y + deltaY);
    }

    private static bool HasHelperTargetSameHelper(GameMove[] moves)
    {
        for (int index = 0; index + 2 < moves.Length; index++)
            if (moves[index].PieceIndex == 1 && moves[index + 1].PieceIndex == 0 && moves[index + 2].PieceIndex == 1) return true;
        return false;
    }

    private static int CountInitialTargetChoices(RoomDefinition room)
    {
        GameState state = GameEngine.Create(room);
        int choices = 0;
        GameCommand[] directions = new GameCommand[] { GameCommand.Up, GameCommand.Right, GameCommand.Down, GameCommand.Left };
        for (int index = 0; index < directions.Length; index++)
            if (GameEngine.Step(room, state, Move(0, directions[index])).Accepted) choices++;
        return choices;
    }

    private static bool MatchesD4Topology(RoomDefinition candidate, List<RoomDefinition> protectedRooms)
    {
        string canonical = CanonicalTopology(candidate);
        for (int index = 0; index < protectedRooms.Count; index++)
            if (protectedRooms[index].Pieces.Length == candidate.Pieces.Length &&
                string.Equals(canonical, CanonicalTopology(protectedRooms[index]), StringComparison.Ordinal)) return true;
        return false;
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
                    for (int x = 0; x < 8; x++)
                    {
                        GridPoint transformed = TransformPoint(new GridPoint(x, y), reflect == 1, rotations);
                        tiles[transformed.Y, transformed.X] = room.Rows[y][x];
                    }
                string text = string.Empty;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++) text += tiles[y, x];
                    text += "/";
                }
                GridPoint target = TransformPoint(room.Pieces[0].Start, reflect == 1, rotations);
                GridPoint helper = TransformPoint(room.Pieces[1].Start, reflect == 1, rotations);
                GridPoint goal = TransformPoint(room.Goal, reflect == 1, rotations);
                text += PointKey(target) + PointKey(helper) + PointKey(goal);
                if (canonical == null || string.CompareOrdinal(text, canonical) < 0) canonical = text;
            }
        }
        return canonical;
    }

    private static bool RecipeTransfers(RoomDefinition candidate, List<RoomDefinition> protectedRooms)
    {
        foreach (RoomDefinition sourceRoom in protectedRooms)
        {
            if (sourceRoom.Pieces.Length != candidate.Pieces.Length) continue;
            SolverResult source = Solver.FindSolution(sourceRoom, SolverBudget);
            for (int reflect = 0; reflect < 2; reflect++)
                for (int rotations = 0; rotations < 4; rotations++)
                    if (ReplaysWithBlockedZero(candidate, TransformRecipe(source.Moves, reflect == 1, rotations))) return true;
        }
        return false;
    }

    private static GameMove[] TransformRecipe(GameMove[] moves, bool reflect, int rotations)
    {
        GameMove[] transformed = new GameMove[moves.Length];
        for (int index = 0; index < moves.Length; index++) transformed[index] = Move(moves[index].PieceIndex, TransformDirection(moves[index].Direction, reflect, rotations));
        return transformed;
    }

    private static bool ReplaysWithBlockedZero(RoomDefinition room, GameMove[] moves)
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
        for (int index = 0; index < rotations; index++) { int previousX = x; x = -y; y = previousX; }
        if (x == 0 && y == -1) return GameCommand.Up;
        if (x == 0 && y == 1) return GameCommand.Down;
        if (x == -1 && y == 0) return GameCommand.Left;
        return GameCommand.Right;
    }

    private static GridPoint TransformPoint(GridPoint point, bool reflect, int rotations)
    {
        GridPoint result = reflect ? new GridPoint(7 - point.X, point.Y) : point;
        for (int index = 0; index < rotations; index++) result = new GridPoint(7 - result.Y, result.X);
        return result;
    }

    private static bool HasSelectedWall(int mask, GridPoint point)
    {
        for (int index = 0; index < WallPool.Length; index++) if ((mask & (1 << index)) != 0 && WallPool[index].Equals(point)) return true;
        return false;
    }

    private static int BitCount(int value)
    {
        int count = 0;
        while (value != 0) { value &= value - 1; count++; }
        return count;
    }

    private static GameMove Move(int pieceIndex, GameCommand direction)
    {
        return new GameMove { PieceIndex = pieceIndex, Direction = direction };
    }

    private static string[] MoveNames(GameMove[] moves)
    {
        string[] names = new string[moves.Length];
        for (int index = 0; index < moves.Length; index++) names[index] = moves[index].PieceIndex.ToString() + ":" + moves[index].Direction.ToString();
        return names;
    }

    private static string PointKey(GridPoint point)
    {
        return point.X.ToString() + "," + point.Y.ToString() + ";";
    }

    private sealed class SearchSummary
    {
        public string Tool;
        public string Randomness;
        public string EnumerationOrder;
        public int AttemptBudget;
        public int Attempts;
        public string Template;
        public RejectionCounts Rejections;
        public Candidate[] Candidates;
    }

    private sealed class RejectionCounts
    {
        public int InvalidRoom;
        public int MoveBand;
        public int TargetOnly;
        public int InitialChoices;
        public int HelperTargetHelper;
        public int ReciprocalStop;
        public int D4Duplicate;
        public int RecipeTransfer;
        public int Other;

        public void Add(string reason)
        {
            if (reason == "invalid_room") InvalidRoom++;
            else if (reason == "move_band") MoveBand++;
            else if (reason.StartsWith("target_only", StringComparison.Ordinal)) TargetOnly++;
            else if (reason == "initial_choices") InitialChoices++;
            else if (reason == "helper_target_helper") HelperTargetHelper++;
            else if (reason == "reciprocal_stop") ReciprocalStop++;
            else if (reason == "d4_duplicate") D4Duplicate++;
            else if (reason == "recipe_transfer") RecipeTransfer++;
            else Other++;
        }
    }

    private sealed class Candidate
    {
        public string[] Rows;
        public GridPoint HelperStart;
        public GridPoint Helper2Start;
        public GridPoint Goal;
        public int MinimumMoves;
        public string[] Route;
        public int TargetOnlyVisited;
        public HelperInteraction[] Interactions;
        public TargetStop[] TargetStops;
    }

    private sealed class HelperInteraction
    {
        public int MoveIndex;
        public int MoverIndex;
        public int StopperIndex;
        public GridPoint Destination;
        public GridPoint Stopper;
    }

    private sealed class TargetStop
    {
        public int MoveIndex;
        public int HelperIndex;
        public GridPoint Destination;
        public GridPoint Stopper;
    }
}
