using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FewMoves.Coop.Server;
using Microsoft.AspNetCore.Builder;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;

internal static class Program
{
    private static readonly List<CheckRecord> Records = new List<CheckRecord>();
    private static readonly JsonSerializerOptions SummaryJson = new JsonSerializerOptions { IncludeFields = true };

    private static async Task<int> Main()
    {
        await Run("http_lifecycle_auth_static_and_secret_boundary", CheckLifecycleAuthStaticAndSecretBoundary);
        await Run("http_command_race_idempotency_stale_and_public_pass", CheckCommands);
        await Run("http_consent_expression_cooldown_and_reconnect", CheckConsentExpressionAndReconnect);
        await Run("http_atomic_persistence_restart_and_corrupt_fail_closed", CheckPersistenceRestartAndCorruption);
        await Run("http_body_general_room_and_ttl_limits", CheckLimits);

        int passed = 0;
        for (int index = 0; index < Records.Count; index++) if (Records[index].Passed) passed++;
        Console.WriteLine(JsonSerializer.Serialize(new CheckSummary
        {
            Suite = "few-moves-coop-server",
            Passed = passed,
            Failed = Records.Count - passed,
            Checks = Records.ToArray()
        }, SummaryJson));
        return passed == Records.Count ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task> check)
    {
        try
        {
            await check();
            Records.Add(new CheckRecord { Name = name, Passed = true, Detail = "ok" });
        }
        catch (Exception exception)
        {
            Records.Add(new CheckRecord { Name = name, Passed = false, Detail = exception.GetType().Name + ": " + SafeDetail(exception.Message) });
        }
    }

    private static async Task CheckLifecycleAuthStaticAndSecretBoundary()
    {
        await using (ServerHarness harness = await ServerHarness.StartAsync())
        {
            HttpResponse health = await harness.SendAsync(HttpMethod.Get, "/healthz", null, null);
            AssertStatus(health, 200, "health");
            Assert(health.Root.GetProperty("ok").GetBoolean(), "health must be successful");
            Assert(!health.Body.Contains(harness.StateRoot, StringComparison.Ordinal), "health must not expose private path");
            AssertEqual("no-store", health.CacheControl, "health cache boundary");

            HttpResponse index = await harness.SendAsync(HttpMethod.Get, "/", null, null);
            AssertStatus(index, 200, "public index");
            Assert(index.Body.Contains("few-moves-online-check", StringComparison.Ordinal), "public index must come from explicit root");
            AssertEqual("no-store", index.CacheControl, "static cache boundary");
            HttpResponse wasm = await harness.SendAsync(HttpMethod.Get, "/game.wasm", null, null);
            AssertStatus(wasm, 200, "wasm static asset");
            Assert(string.Equals(wasm.ContentType, "application/wasm", StringComparison.OrdinalIgnoreCase), "wasm MIME type must be application/wasm");
            HttpResponse missingDirectory = await harness.SendAsync(HttpMethod.Get, "/missing/", null, null);
            Assert(missingDirectory.StatusCode == 404, "static directory listing must be absent");

            SessionInfo session = await CreateAndJoin(harness);
            AssertPrivateStatePermissions(harness);
            HttpResponse inviteAsToken = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.InviteCode);
            AssertStatus(inviteAsToken, 401, "invite cannot read state");

            HttpResponse otherCreate = await harness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", CreateBody("create-other-abcdefghijklmnopqrstuvwxyz"), null);
            AssertStatus(otherCreate, 201, "second independent create");
            string otherToken = otherCreate.Root.GetProperty("seatToken").GetString();
            HttpResponse wrongRoom = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, otherToken);
            AssertStatus(wrongRoom, 403, "another room token cannot read this room");

            HttpResponse actorForgery = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId),
                "{\"commandId\":\"actor-forgery\",\"expectedRevision\":0,\"kind\":0,\"direction\":0,\"actor\":1}", session.CircleToken);
            AssertStatus(actorForgery, 400, "actor field is rejected");
            AssertEqual("actor_not_allowed", ErrorCode(actorForgery), "actor rejection code");

            string privateState = File.ReadAllText(harness.StateFilePath);
            Assert(!privateState.Contains(session.CircleToken, StringComparison.Ordinal), "private state must not retain circle bearer raw value");
            Assert(!privateState.Contains(session.DiamondToken, StringComparison.Ordinal), "private state must not retain diamond bearer raw value");
            Assert(!privateState.Contains(session.InviteCode, StringComparison.Ordinal), "private state must not retain invite raw value");
            HttpResponse privatePath = await harness.SendAsync(HttpMethod.Get, "/rooms.v1.json", null, null);
            Assert(privatePath.StatusCode == 404, "private state cannot be reached through static root");
        }
    }

    private static async Task CheckCommands()
    {
        await using (ServerHarness harness = await ServerHarness.StartAsync())
        {
            SessionInfo session = await CreateAndJoin(harness);
            for (int index = 0; index < 60; index++)
            {
                HttpResponse circlePoll = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.CircleToken);
                HttpResponse diamondPoll = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.DiamondToken);
                AssertStatus(circlePoll, 200, "circle normal poll budget");
                AssertStatus(diamondPoll, 200, "diamond normal poll budget");
            }
            CoopCommand first = NoPassTrace()[0];
            Task<HttpResponse> circleTask = harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("race-circle", 0, first.Kind, first.Direction), session.CircleToken);
            Task<HttpResponse> diamondTask = harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("race-diamond", 0, CoopCommandKind.Slide, GameCommand.Up), session.DiamondToken);
            HttpResponse[] raced = await Task.WhenAll(circleTask, diamondTask);
            int accepted = 0;
            for (int index = 0; index < raced.Length; index++) if (raced[index].Root.GetProperty("accepted").GetBoolean()) accepted++;
            AssertEqual(1, accepted, "only one simultaneous revision-zero command may be accepted");

            HttpResponse current = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.CircleToken);
            AssertStatus(current, 200, "state after race");
            AssertEqual(1L, current.Root.GetProperty("state").GetProperty("authorityRevision").GetInt64(), "race advances revision once");

            HttpResponse retry = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("race-circle", 0, first.Kind, first.Direction), session.CircleToken);
            AssertStatus(retry, 200, "exact accepted retry");
            Assert(retry.Root.GetProperty("accepted").GetBoolean(), "accepted retry remains accepted");
            Assert(retry.Root.GetProperty("idempotent").GetBoolean(), "exact retry is idempotent");
            AssertEqual(1L, retry.Root.GetProperty("state").GetProperty("authorityRevision").GetInt64(), "retry never rewinds or advances revision");

            HttpResponse conflict = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("race-circle", 0, first.Kind, GameCommand.Down), session.CircleToken);
            AssertStatus(conflict, 409, "changed payload collision");
            AssertEqual("command_id_payload_conflict", ErrorCode(conflict), "changed payload code");

            HttpResponse publicPass = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("pass-disabled", 1, CoopCommandKind.Pass, GameCommand.Up), session.DiamondToken);
            AssertStatus(publicPass, 422, "public pass is rejected");
            AssertEqual("pass_not_supported", ErrorCode(publicPass), "pass rejection code");
            AssertEqual(1L, publicPass.Root.GetProperty("state").GetProperty("authorityRevision").GetInt64(), "public pass never changes state");

            HttpResponse stale = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("stale-command", 0, CoopCommandKind.Slide, GameCommand.Up), session.DiamondToken);
            AssertStatus(stale, 409, "stale command rejected");
            AssertEqual("stale_revision", ErrorCode(stale), "stale reason retained");
        }
    }

    private static async Task CheckConsentExpressionAndReconnect()
    {
        await using (ServerHarness harness = await ServerHarness.StartAsync(delegate (ServerOptions options) { options.HeartbeatTimeout = TimeSpan.FromMilliseconds(120); }))
        {
            SessionInfo session = await CreateAndJoin(harness);
            HttpResponse request = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), ConsentBody("restart-request", 0, CoopCommandKind.RequestRestart, "restart-request-id-abcdefghijkl"), session.CircleToken);
            AssertStatus(request, 200, "restart request");
            AssertEqual(1L, request.Root.GetProperty("state").GetProperty("authorityRevision").GetInt64(), "request grows revision");

            HttpResponse selfApprove = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), ResolveBody("restart-self", 1, CoopCommandKind.ResolveRestart, "restart-request-id-abcdefghijkl", true), session.CircleToken);
            AssertStatus(selfApprove, 409, "requester cannot self-approve");
            AssertEqual("consent_same_seat", ErrorCode(selfApprove), "self-approval code");

            HttpResponse approve = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), ResolveBody("restart-other", 1, CoopCommandKind.ResolveRestart, "restart-request-id-abcdefghijkl", true), session.DiamondToken);
            AssertStatus(approve, 200, "other seat restart approval");
            AssertEqual(2L, approve.Root.GetProperty("state").GetProperty("authorityRevision").GetInt64(), "approval grows revision once");
            HttpResponse duplicateApproval = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), ResolveBody("restart-other", 1, CoopCommandKind.ResolveRestart, "restart-request-id-abcdefghijkl", true), session.DiamondToken);
            AssertStatus(duplicateApproval, 200, "duplicate approval");
            Assert(duplicateApproval.Root.GetProperty("idempotent").GetBoolean(), "duplicate approval stays idempotent");

            HttpResponse expression = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), ExpressionBody("expression-one", 2, 0), session.CircleToken);
            AssertStatus(expression, 200, "first expression");
            AssertEqual(1L, expression.Root.GetProperty("expressions").GetProperty("expressionSequence").GetInt64(), "expression gets one sequence");
            HttpResponse rapidExpression = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), ExpressionBody("expression-rapid", 2, 1), session.CircleToken);
            AssertStatus(rapidExpression, 429, "two-second expression cooldown");
            AssertEqual("expression_rate_limited", ErrorCode(rapidExpression), "expression cooldown code");
            HttpResponse duplicateExpression = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), ExpressionBody("expression-one", 2, 0), session.CircleToken);
            AssertStatus(duplicateExpression, 200, "duplicate expression response");
            Assert(duplicateExpression.Root.GetProperty("idempotent").GetBoolean(), "duplicate expression is idempotent");
            AssertEqual(1L, duplicateExpression.Root.GetProperty("expressions").GetProperty("expressionSequence").GetInt64(), "duplicate expression does not add sequence");

            HttpResponse peerState = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.DiamondToken);
            AssertStatus(peerState, 200, "peer expression state");
            AssertEqual(1, peerState.Root.GetProperty("expressions").GetProperty("events").GetArrayLength(), "peer gets one bounded expression event");
            AssertEqual(1, peerState.Root.GetProperty("seat").GetInt32(), "authoritative peer seat remains numeric diamond");

            HttpResponse heartbeat = await harness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms/" + session.RoomId + "/heartbeat", "{}", session.CircleToken);
            AssertStatus(heartbeat, 200, "explicit heartbeat");
            AssertEqual(0, heartbeat.Root.GetProperty("seat").GetInt32(), "heartbeat uses authenticated circle seat");

            await Task.Delay(180);
            HttpResponse paused = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), ConsentBody("paused-command", 2, CoopCommandKind.RequestRestart, "paused-request-id-abcdefghijk"), session.CircleToken);
            AssertStatus(paused, 409, "offline peer pauses command");
            AssertEqual("room_paused_offline", ErrorCode(paused), "pause code");
            HttpResponse circleResume = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.CircleToken);
            AssertStatus(circleResume, 200, "circle reconnect state");
            HttpResponse diamondResume = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.DiamondToken);
            AssertStatus(diamondResume, 200, "diamond reconnect state");
            AssertEqual(1, diamondResume.Root.GetProperty("availability").GetProperty("status").GetInt32(), "both heartbeats restore active availability");
        }
    }

    private static async Task CheckPersistenceRestartAndCorruption()
    {
        await using (ServerHarness harness = await ServerHarness.StartAsync())
        {
            SessionInfo session = await CreateAndJoin(harness);
            CoopCommand[] trace = NoPassTrace();
            HttpResponse first = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("persist-first", 0, trace[0].Kind, trace[0].Direction), session.CircleToken);
            AssertStatus(first, 200, "persistent first slide");
            byte[] diskBeforeFailure = File.ReadAllBytes(harness.StateFilePath);
            harness.Options.FailNextWritesForTests = 1;
            HttpResponse failed = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("persist-second", 1, trace[1].Kind, trace[1].Direction), session.DiamondToken);
            AssertStatus(failed, 503, "injected write failure");
            AssertEqual("persistence_failed", ErrorCode(failed), "write failure code");
            Assert(ByteArraysEqual(diskBeforeFailure, File.ReadAllBytes(harness.StateFilePath)), "write failure leaves durable state unchanged");
            HttpResponse afterFailure = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.DiamondToken);
            AssertStatus(afterFailure, 200, "state after write failure");
            AssertEqual(1L, afterFailure.Root.GetProperty("state").GetProperty("authorityRevision").GetInt64(), "write failure rolls memory back too");
            HttpResponse retry = await harness.SendAsync(HttpMethod.Post, CommandPath(session.RoomId), CommandBody("persist-second", 1, trace[1].Kind, trace[1].Direction), session.DiamondToken);
            AssertStatus(retry, 200, "same command after rolled-back failure can apply once");
            AssertEqual(2L, retry.Root.GetProperty("state").GetProperty("authorityRevision").GetInt64(), "replayed command advances exactly once");

            await harness.RestartAsync();
            HttpResponse firstAfterRestart = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.CircleToken);
            AssertStatus(firstAfterRestart, 200, "circle reauth after restart");
            HttpResponse secondAfterRestart = await harness.SendAsync(HttpMethod.Get, StatePath(session.RoomId), null, session.DiamondToken);
            AssertStatus(secondAfterRestart, 200, "diamond reauth after restart");
            AssertEqual(2L, secondAfterRestart.Root.GetProperty("state").GetProperty("authorityRevision").GetInt64(), "restart restores authoritative core state");

            await harness.StopAsync();
            File.WriteAllText(harness.StateFilePath, "{corrupt");
            bool corruptRejected = false;
            try { ServerApplication.Create(harness.Options); }
            catch (ServerStateCorruptException) { corruptRejected = true; }
            Assert(corruptRejected, "corrupt private state must fail closed instead of resetting");
        }
    }

    private static async Task CheckLimits()
    {
        await using (ServerHarness bodyHarness = await ServerHarness.StartAsync())
        {
            string oversized = "{" + new string('x', ServerOptions.DefaultRequestBodyBytes) + "}";
            HttpResponse response = await bodyHarness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", oversized, null);
            AssertStatus(response, 413, "request body limit");
            AssertEqual("request_too_large", ErrorCode(response), "body limit code");
        }

        await using (ServerHarness rateHarness = await ServerHarness.StartAsync(delegate (ServerOptions options) { options.MaxRequestsPerWindow = 2; }))
        {
            HttpResponse first = await rateHarness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", "{}", null);
            HttpResponse second = await rateHarness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", "{}", null);
            HttpResponse third = await rateHarness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", "{}", null);
            AssertStatus(first, 400, "first bounded API request");
            AssertStatus(second, 400, "second bounded API request");
            AssertStatus(third, 429, "general API request limit");
            AssertEqual("request_rate_limited", ErrorCode(third), "general request limit code");
        }

        await using (ServerHarness roomHarness = await ServerHarness.StartAsync(delegate (ServerOptions options) { options.MaxLiveRooms = 1; options.MaxStoredRooms = 1; options.RoomTtl = TimeSpan.FromMilliseconds(120); }))
        {
            HttpResponse created = await roomHarness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", CreateBody("create-ttl-abcdefghijklmnopqrstuvwxyz"), null);
            AssertStatus(created, 201, "ttl room create");
            HttpResponse capped = await roomHarness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", CreateBody("create-cap-abcdefghijklmnopqrstuvwxyz"), null);
            AssertStatus(capped, 429, "live room cap");
            string roomId = created.Root.GetProperty("roomId").GetString();
            string token = created.Root.GetProperty("seatToken").GetString();
            string invite = created.Root.GetProperty("inviteCode").GetString();
            await Task.Delay(180);
            HttpResponse expired = await roomHarness.SendAsync(HttpMethod.Get, StatePath(roomId), null, token);
            AssertStatus(expired, 410, "ttl expiry");
            AssertEqual("room_expired", ErrorCode(expired), "ttl code");
            HttpResponse expiredJoin = await roomHarness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms/join", JoinBody(invite, "join-expired-abcdefghijklmnopqrstuvwxyz"), null);
            AssertStatus(expiredJoin, 410, "expired invite cannot join");
            JsonElement ignored;
            Assert(!expiredJoin.Root.TryGetProperty("state", out ignored), "expired unauthenticated invite response must not contain state");
            HttpResponse recordCap = await roomHarness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", CreateBody("create-record-cap-abcdefghijklmnopqrstuvwxyz"), null);
            AssertStatus(recordCap, 429, "stored room cap bounds expired room history");
            AssertEqual("room_record_limit_reached", ErrorCode(recordCap), "stored room cap code");
        }

        var limiter = new ApiRequestLimiter(1, 1, TimeSpan.FromMilliseconds(20));
        DateTimeOffset at = DateTimeOffset.UtcNow;
        Assert(limiter.TryAllow("old-client", at), "first request bucket accepted");
        Assert(limiter.TryAllow("new-client", at.AddMilliseconds(30)), "expired request bucket is evicted for a new client");
    }

    private static async Task<SessionInfo> CreateAndJoin(ServerHarness harness)
    {
        const string createId = "create-primary-abcdefghijklmnopqrstuvwxyz";
        const string joinId = "join-primary-abcdefghijklmnopqrstuvwxyz";
        HttpResponse created = await harness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", CreateBody(createId), null);
        AssertStatus(created, 201, "create room");
        string roomId = created.Root.GetProperty("roomId").GetString();
        string circleToken = created.Root.GetProperty("seatToken").GetString();
        string inviteCode = created.Root.GetProperty("inviteCode").GetString();
        HttpResponse createRetry = await harness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms", CreateBody(createId), null);
        AssertStatus(createRetry, 200, "create retry");
        AssertEqual(roomId, createRetry.Root.GetProperty("roomId").GetString(), "create retry keeps room");
        AssertEqual(circleToken, createRetry.Root.GetProperty("seatToken").GetString(), "create retry keeps seat token");
        AssertEqual(inviteCode, createRetry.Root.GetProperty("inviteCode").GetString(), "create retry keeps invite code");
        AssertEqual("no-store", created.CacheControl, "create response cache boundary");
        AssertEqual("no-store", createRetry.CacheControl, "create retry cache boundary");

        HttpResponse joined = await harness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms/join", JoinBody(inviteCode, joinId), null);
        AssertStatus(joined, 201, "join room");
        string diamondToken = joined.Root.GetProperty("seatToken").GetString();
        HttpResponse joinRetry = await harness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms/join", JoinBody(inviteCode, joinId), null);
        AssertStatus(joinRetry, 200, "join retry");
        AssertEqual(diamondToken, joinRetry.Root.GetProperty("seatToken").GetString(), "join retry keeps seat token");
        AssertEqual("no-store", joined.CacheControl, "join response cache boundary");
        HttpResponse thirdJoin = await harness.SendAsync(HttpMethod.Post, "/api/coop/v1/rooms/join", JoinBody(inviteCode, "join-third-abcdefghijklmnopqrstuvwxyz"), null);
        AssertStatus(thirdJoin, 409, "third join blocked");
        AssertEqual("room_full", ErrorCode(thirdJoin), "third join code");
        return new SessionInfo { RoomId = roomId, CircleToken = circleToken, DiamondToken = diamondToken, InviteCode = inviteCode };
    }

    private static CoopCommand[] NoPassTrace()
    {
        string roomPath = FindRoomPath();
        CoopRoomDefinition room = JsonSerializer.Deserialize<CoopRoomDefinition>(File.ReadAllText(roomPath), new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true });
        CoopSolverResult result = CoopSolver.FindSolution(room, 500000, new CoopSolverOptions { AllowPass = false });
        AssertEqual(CoopSolverStatus.Solved, result.Status, "C1 no-pass solver status");
        AssertEqual(13, result.OptimalActionCount, "C1 measured no-pass optimum");
        return result.Commands;
    }

    private static string FindRoomPath()
    {
        DirectoryInfo current = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int index = 0; index < 12 && current != null; index++, current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, "Assets", "Nectorial", "Resources", "CoopRooms", "coop-c1.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("C1 room path unavailable for server checks.");
    }

    private static void AssertPrivateStatePermissions(ServerHarness harness)
    {
        if (OperatingSystem.IsWindows()) return;
        UnixFileMode file = File.GetUnixFileMode(harness.StateFilePath);
        UnixFileMode directory = File.GetUnixFileMode(harness.StateRoot);
        Assert((file & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0, "private state file must exclude group and other access");
        Assert((file & (UnixFileMode.UserRead | UnixFileMode.UserWrite)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "private state file must keep owner read/write");
        Assert((directory & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0, "private state directory must exclude group and other access");
        Assert((directory & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "private state directory must keep owner access");
    }

    private static string CreateBody(string createId) { return "{\"createRequestId\":\"" + createId + "\"}"; }
    private static string JoinBody(string inviteCode, string joinId) { return "{\"inviteCode\":\"" + inviteCode + "\",\"joinRequestId\":\"" + joinId + "\"}"; }
    private static string StatePath(string roomId) { return "/api/coop/v1/rooms/" + roomId + "/state"; }
    private static string CommandPath(string roomId) { return "/api/coop/v1/rooms/" + roomId + "/commands"; }
    private static string CommandBody(string commandId, long revision, CoopCommandKind kind, GameCommand direction)
    {
        return "{\"commandId\":\"" + commandId + "\",\"expectedRevision\":" + revision + ",\"kind\":" + (int)kind + ",\"direction\":" + (int)direction + "}";
    }
    private static string ConsentBody(string commandId, long revision, CoopCommandKind kind, string requestId)
    {
        return "{\"commandId\":\"" + commandId + "\",\"expectedRevision\":" + revision + ",\"kind\":" + (int)kind + ",\"requestId\":\"" + requestId + "\"}";
    }
    private static string ResolveBody(string commandId, long revision, CoopCommandKind kind, string requestId, bool approve)
    {
        return "{\"commandId\":\"" + commandId + "\",\"expectedRevision\":" + revision + ",\"kind\":" + (int)kind + ",\"requestId\":\"" + requestId + "\",\"approve\":" + (approve ? "true" : "false") + "}";
    }
    private static string ExpressionBody(string commandId, long revision, int expression)
    {
        return "{\"commandId\":\"" + commandId + "\",\"expectedRevision\":" + revision + ",\"kind\":6,\"expression\":" + expression + "}";
    }

    private static void AssertStatus(HttpResponse response, int expected, string label)
    {
        AssertEqual(expected, response.StatusCode, label + " status");
    }

    private static string ErrorCode(HttpResponse response)
    {
        JsonElement root = response.Root;
        JsonElement error;
        if (root.TryGetProperty("error", out error)) return error.GetProperty("code").GetString();
        return root.GetProperty("reason").GetString();
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
        return true;
    }

    private static string SafeDetail(string value)
    {
        if (string.IsNullOrEmpty(value)) return "failure";
        return value.Length <= 180 ? value.Replace('\n', ' ') : value.Substring(0, 180).Replace('\n', ' ');
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message);
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
    }

    private sealed class SessionInfo
    {
        public string RoomId;
        public string CircleToken;
        public string DiamondToken;
        public string InviteCode;
    }

    private sealed class HttpResponse
    {
        public int StatusCode;
        public string Body;
        public string ContentType;
        public string CacheControl;
        public JsonDocument Document;
        public JsonElement Root
        {
            get
            {
                if (Document == null) throw new InvalidOperationException("Response was not JSON.");
                return Document.RootElement;
            }
        }
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private WebApplication _app;
        private HttpClient _client;
        private readonly string _root;
        public ServerOptions Options;
        public string StateRoot;
        public string StateFilePath { get { return Path.Combine(StateRoot, "rooms.v1.json"); } }

        private ServerHarness(string root, ServerOptions options)
        {
            _root = root;
            Options = options;
            StateRoot = options.StateRoot;
        }

        public static async Task<ServerHarness> StartAsync(Action<ServerOptions> configure = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "few-moves-coop-serverchecks-" + Guid.NewGuid().ToString("N"));
            string publicRoot = Path.Combine(root, "public");
            string stateRoot = Path.Combine(root, "private-state");
            Directory.CreateDirectory(publicRoot);
            File.WriteAllText(Path.Combine(publicRoot, "index.html"), "<!doctype html><title>few-moves-online-check</title>");
            File.WriteAllBytes(Path.Combine(publicRoot, "game.wasm"), new byte[] { 0, 97, 115, 109 });
            var options = new ServerOptions
            {
                PublicRoot = publicRoot,
                StateRoot = stateRoot,
                RoomPath = FindRoomPath(),
                ListenUrl = "http://127.0.0.1:" + FindFreePort().ToString(),
                BuildId = "server-check"
            };
            if (configure != null) configure(options);
            options.Validate();
            var harness = new ServerHarness(root, options);
            await harness.StartApplicationAsync();
            return harness;
        }

        public async Task<HttpResponse> SendAsync(HttpMethod method, string path, string body, string bearer)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                if (bearer != null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                using (HttpResponseMessage response = await _client.SendAsync(request))
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    string contentType = response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType;
                    return new HttpResponse
                    {
                        StatusCode = (int)response.StatusCode,
                        Body = responseBody,
                        ContentType = contentType,
                        CacheControl = response.Headers.CacheControl == null ? null : response.Headers.CacheControl.ToString(),
                        Document = contentType != null && contentType.IndexOf("application/json", StringComparison.OrdinalIgnoreCase) >= 0 ? JsonDocument.Parse(responseBody) : null
                    };
                }
            }
        }

        public async Task RestartAsync()
        {
            await StopAsync();
            Options.ListenUrl = "http://127.0.0.1:" + FindFreePort().ToString();
            await StartApplicationAsync();
        }

        public async Task StopAsync()
        {
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private async Task StartApplicationAsync()
        {
            _app = ServerApplication.Create(Options);
            await _app.StartAsync();
            _client = new HttpClient { BaseAddress = new Uri(Options.ListenUrl, UriKind.Absolute) };
        }

        private static int FindFreePort()
        {
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }
    }
}
