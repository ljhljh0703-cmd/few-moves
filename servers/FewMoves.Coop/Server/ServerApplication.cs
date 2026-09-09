using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Nectorial.SlideEscape.Coop;

namespace FewMoves.Coop.Server
{
    public static class ServerApplication
    {
        private static readonly string[] CreateFields = { "createRequestId", "definitionId" };
        private static readonly string[] JoinFields = { "inviteCode", "joinRequestId" };
        private static readonly string[] HeartbeatFields = new string[0];
        private static readonly string[] CommandFields = { "commandId", "expectedRevision", "kind", "direction", "requestId", "approve", "expression" };

        public static WebApplication Create(ServerOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            options.Validate();
            var store = new RoomStore(options);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.WebHost.UseUrls(options.ListenUrl);
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(new ApiRequestLimiter(options.MaxRequestBuckets, options.MaxRequestsPerWindow, options.RequestWindow));
            WebApplication app = builder.Build();

            app.Use(async delegate (HttpContext context, RequestDelegate next)
            {
                if (context.Request.Path.StartsWithSegments("/api/coop/v1"))
                {
                    ApiRequestLimiter limiter = context.RequestServices.GetRequiredService<ApiRequestLimiter>();
                    string connectionKey = context.Connection.RemoteIpAddress == null ? "unknown" : context.Connection.RemoteIpAddress.ToString();
                    if (!limiter.TryAllow(connectionKey, DateTimeOffset.UtcNow))
                    {
                        await WriteJson(context, 429, new ErrorView { Ok = false, Error = new ApiError { Code = "request_rate_limited" } });
                        return;
                    }
                }
                await next(context);
            });

            ConfigureStaticFiles(app, options);
            app.MapGet("/healthz", async context =>
            {
                await Execute(context, Json(200, new HealthView { Version = options.Version, BuildId = options.BuildId, PublicRootConfigured = true }));
            });
            app.MapGet("/", async context =>
            {
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await Execute(context, Results.File(Path.Combine(options.PublicRoot, "index.html"), "text/html; charset=utf-8", enableRangeProcessing: false));
            });
            app.MapGet("/api/coop/v1/definitions", async context =>
            {
                RoomStore roomStore = context.RequestServices.GetRequiredService<RoomStore>();
                await Execute(context, Json(200, new DefinitionsView { Definitions = roomStore.Definitions() }));
            });

            app.MapPost("/api/coop/v1/rooms", async (HttpContext context) =>
            {
                RoomStore roomStore = context.RequestServices.GetRequiredService<RoomStore>();
                ParsedRequest<CreateRoomRequest> parsed = await ReadJsonAsync<CreateRoomRequest>(context.Request, CreateFields, options.MaxRequestBodyBytes);
                if (!parsed.Ok)
                {
                    await Execute(context, Json(parsed.StatusCode, new ErrorView { Ok = false, Error = new ApiError { Code = parsed.ErrorCode } }));
                    return;
                }
                StoreResult result = roomStore.Create(parsed.Value, DateTimeOffset.UtcNow);
                if (!result.Ok)
                {
                    await Execute(context, ToError(result));
                    return;
                }
                await Execute(context, Json(result.StatusCode, new CreateRoomView
                {
                    RoomId = result.RoomId,
                    InviteCode = result.InviteCode,
                    Seat = result.Seat,
                    SeatToken = result.SeatToken,
                    Room = result.Snapshot.Room
                }));
            });

            app.MapPost("/api/coop/v1/rooms/join", async (HttpContext context) =>
            {
                RoomStore roomStore = context.RequestServices.GetRequiredService<RoomStore>();
                ParsedRequest<JoinRoomRequest> parsed = await ReadJsonAsync<JoinRoomRequest>(context.Request, JoinFields, options.MaxRequestBodyBytes);
                if (!parsed.Ok)
                {
                    await Execute(context, Json(parsed.StatusCode, new ErrorView { Ok = false, Error = new ApiError { Code = parsed.ErrorCode } }));
                    return;
                }
                StoreResult result = roomStore.Join(parsed.Value, DateTimeOffset.UtcNow);
                if (!result.Ok)
                {
                    await Execute(context, ToError(result));
                    return;
                }
                await Execute(context, Json(result.StatusCode, new JoinRoomView
                {
                    RoomId = result.RoomId,
                    Seat = result.Seat,
                    SeatToken = result.SeatToken,
                    Room = result.Snapshot.Room
                }));
            });

            app.MapGet("/api/coop/v1/rooms/{roomId}/state", async context =>
            {
                string roomId = Convert.ToString(context.Request.RouteValues["roomId"]);
                RoomStore roomStore = context.RequestServices.GetRequiredService<RoomStore>();
                StoreResult result = roomStore.GetState(roomId, ReadBearer(context.Request), DateTimeOffset.UtcNow);
                await Execute(context, ToStateOrError(result));
            });

            app.MapPost("/api/coop/v1/rooms/{roomId}/heartbeat", async (HttpContext context) =>
            {
                string roomId = Convert.ToString(context.Request.RouteValues["roomId"]);
                RoomStore roomStore = context.RequestServices.GetRequiredService<RoomStore>();
                ParsedRequest<object> parsed = await ReadJsonAsync<object>(context.Request, HeartbeatFields, options.MaxRequestBodyBytes);
                if (!parsed.Ok)
                {
                    await Execute(context, Json(parsed.StatusCode, new ErrorView { Ok = false, Error = new ApiError { Code = parsed.ErrorCode } }));
                    return;
                }
                StoreResult result = roomStore.Heartbeat(roomId, ReadBearer(context.Request), DateTimeOffset.UtcNow);
                await Execute(context, ToStateOrError(result));
            });

            app.MapPost("/api/coop/v1/rooms/{roomId}/commands", async (HttpContext context) =>
            {
                string roomId = Convert.ToString(context.Request.RouteValues["roomId"]);
                RoomStore roomStore = context.RequestServices.GetRequiredService<RoomStore>();
                ParsedRequest<CommandRequest> parsed = await ReadJsonAsync<CommandRequest>(context.Request, CommandFields, options.MaxRequestBodyBytes);
                if (!parsed.Ok)
                {
                    await Execute(context, Json(parsed.StatusCode, new ErrorView { Ok = false, Error = new ApiError { Code = parsed.ErrorCode } }));
                    return;
                }
                SetCommandPresence(parsed.Value, parsed.Fields);
                if (string.IsNullOrEmpty(parsed.Value.CommandId) || !parsed.Value.HasExpectedRevision || !parsed.Value.HasKind)
                {
                    await Execute(context, Json(400, new ErrorView { Ok = false, Error = new ApiError { Code = "command_fields_missing" } }));
                    return;
                }
                StoreResult result = roomStore.Dispatch(roomId, ReadBearer(context.Request), parsed.Value, DateTimeOffset.UtcNow);
                await Execute(context, ToCommandOrError(result));
            });

            app.MapPost("/api/coop/v1/rooms/{roomId}/record", async (HttpContext context) =>
            {
                string roomId = Convert.ToString(context.Request.RouteValues["roomId"]);
                ParsedRequest<object> parsed = await ReadJsonAsync<object>(context.Request, HeartbeatFields, options.MaxRequestBodyBytes);
                if (!parsed.Ok)
                {
                    await Execute(context, Json(parsed.StatusCode, new RecordView { Ok = false, Error = new ApiError { Code = parsed.ErrorCode } }));
                    return;
                }
                RoomStore roomStore = context.RequestServices.GetRequiredService<RoomStore>();
                StoreResult result = roomStore.CreateRecord(roomId, ReadBearer(context.Request), DateTimeOffset.UtcNow);
                await Execute(context, ToRecord(result));
            });

            return app;
        }

        private static void ConfigureStaticFiles(WebApplication app, ServerOptions options)
        {
            var contentTypes = new FileExtensionContentTypeProvider();
            contentTypes.Mappings[".wasm"] = "application/wasm";
            contentTypes.Mappings[".data"] = "application/octet-stream";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(options.PublicRoot),
                ContentTypeProvider = contentTypes,
                ServeUnknownFileTypes = false,
                OnPrepareResponse = delegate (StaticFileResponseContext context)
                {
                    context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Context.Response.Headers["Cache-Control"] = "no-store";
                }
            });
        }

        private static IResult ToStateOrError(StoreResult result)
        {
            if (!result.Ok) return ToError(result);
            StateSnapshot snapshot = result.Snapshot;
            return Json(result.StatusCode, new StateView
            {
                Seat = snapshot.Seat,
                Room = snapshot.Room,
                State = snapshot.State,
                Availability = snapshot.Availability,
                Expressions = snapshot.Expressions
            });
        }

        private static IResult ToCommandOrError(StoreResult result)
        {
            if (result.Snapshot == null) return ToError(result);
            StateSnapshot snapshot = result.Snapshot;
            string code = result.ErrorCode;
            if (string.IsNullOrEmpty(code) && !result.Accepted) code = result.Reason;
            return Json(result.StatusCode, new CommandView
            {
                Ok = result.Accepted,
                Seat = snapshot.Seat,
                Room = snapshot.Room,
                State = snapshot.State,
                Availability = snapshot.Availability,
                Expressions = snapshot.Expressions,
                Accepted = result.Accepted,
                Idempotent = result.Idempotent,
                Reason = result.Reason ?? result.ErrorCode,
                Events = result.Events ?? new CoopEvent[0],
                Error = result.Accepted ? null : new ApiError { Code = code }
            });
        }

        private static IResult ToRecord(StoreResult result)
        {
            return Json(result.StatusCode, new RecordView
            {
                Ok = result.Ok,
                Capsule = result.Ok ? result.Capsule : null,
                Verification = result.Verification,
                Error = result.Ok ? null : new ApiError { Code = result.ErrorCode }
            });
        }

        private static IResult ToError(StoreResult result)
        {
            StateSnapshot snapshot = result.Snapshot;
            return Json(result.StatusCode, new ErrorView
            {
                Ok = false,
                Error = new ApiError { Code = result.ErrorCode },
                Seat = snapshot == null ? (CoopActor?)null : snapshot.Seat,
                Room = snapshot == null ? null : snapshot.Room,
                State = snapshot == null ? null : snapshot.State,
                Availability = snapshot == null ? null : snapshot.Availability,
                Expressions = snapshot == null ? null : snapshot.Expressions
            });
        }

        private static IResult Json(int statusCode, object value)
        {
            return new NoStoreJsonResult(statusCode, value);
        }

        private static Task Execute(HttpContext context, IResult result)
        {
            return result.ExecuteAsync(context);
        }

        private static async Task WriteJson(HttpContext context, int statusCode, object value)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            await JsonSerializer.SerializeAsync(context.Response.Body, value, value.GetType(), WireJson.Options, context.RequestAborted);
        }

        private static string ReadBearer(HttpRequest request)
        {
            string header = request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (header == null || !header.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string token = header.Substring(prefix.Length);
            return string.IsNullOrWhiteSpace(token) || token.IndexOf(' ') >= 0 ? null : token;
        }

        private static void SetCommandPresence(CommandRequest command, HashSet<string> fields)
        {
            command.HasExpectedRevision = fields.Contains("expectedRevision");
            command.HasKind = fields.Contains("kind");
            command.HasDirection = fields.Contains("direction");
            command.HasRequestId = fields.Contains("requestId");
            command.HasApprove = fields.Contains("approve");
            command.HasExpression = fields.Contains("expression");
        }

        private static async Task<ParsedRequest<T>> ReadJsonAsync<T>(HttpRequest request, string[] allowedFields, int maximumBytes)
        {
            if (request.ContentLength.HasValue && request.ContentLength.Value > maximumBytes) return ParsedRequest<T>.Failure(413, "request_too_large");
            var bytes = new List<byte>();
            var buffer = new byte[1024];
            while (true)
            {
                int read = await request.Body.ReadAsync(buffer, 0, buffer.Length, request.HttpContext.RequestAborted);
                if (read == 0) break;
                if (bytes.Count + read > maximumBytes) return ParsedRequest<T>.Failure(413, "request_too_large");
                for (int index = 0; index < read; index++) bytes.Add(buffer[index]);
            }
            if (bytes.Count == 0) return ParsedRequest<T>.Failure(400, "request_body_invalid");
            try
            {
                using (JsonDocument document = JsonDocument.Parse(bytes.ToArray()))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return ParsedRequest<T>.Failure(400, "request_body_invalid");
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var fields = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (!seen.Add(property.Name)) return ParsedRequest<T>.Failure(400, "request_field_duplicate");
                        if (string.Equals(property.Name, "actor", StringComparison.Ordinal)) return ParsedRequest<T>.Failure(400, "actor_not_allowed");
                        if (!Contains(allowedFields, property.Name)) return ParsedRequest<T>.Failure(400, "request_field_unknown");
                        fields.Add(property.Name);
                    }
                    T value = JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), WireJson.Options);
                    if (value == null) return ParsedRequest<T>.Failure(400, "request_body_invalid");
                    return ParsedRequest<T>.Success(value, fields);
                }
            }
            catch (JsonException)
            {
                return ParsedRequest<T>.Failure(400, "request_body_invalid");
            }
        }

        private static bool Contains(string[] values, string target)
        {
            for (int index = 0; index < values.Length; index++) if (string.Equals(values[index], target, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    internal sealed class NoStoreJsonResult : IResult
    {
        private readonly int _statusCode;
        private readonly object _value;

        public NoStoreJsonResult(int statusCode, object value)
        {
            _statusCode = statusCode;
            _value = value;
        }

        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = _statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await JsonSerializer.SerializeAsync(context.Response.Body, _value, _value.GetType(), WireJson.Options, context.RequestAborted);
        }
    }

    internal sealed class ParsedRequest<T>
    {
        public bool Ok;
        public int StatusCode;
        public string ErrorCode;
        public T Value;
        public HashSet<string> Fields;

        public static ParsedRequest<T> Success(T value, HashSet<string> fields)
        {
            return new ParsedRequest<T> { Ok = true, StatusCode = 200, Value = value, Fields = fields };
        }

        public static ParsedRequest<T> Failure(int statusCode, string errorCode)
        {
            return new ParsedRequest<T> { Ok = false, StatusCode = statusCode, ErrorCode = errorCode, Fields = new HashSet<string>(StringComparer.Ordinal) };
        }
    }

    public sealed class ApiRequestLimiter
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Queue<DateTimeOffset>> _buckets = new Dictionary<string, Queue<DateTimeOffset>>(StringComparer.Ordinal);
        private readonly int _maximumBuckets;
        private readonly int _maximumRequests;
        private readonly TimeSpan _window;

        public ApiRequestLimiter(int maximumBuckets, int maximumRequests, TimeSpan window)
        {
            _maximumBuckets = maximumBuckets;
            _maximumRequests = maximumRequests;
            _window = window;
        }

        public bool TryAllow(string key, DateTimeOffset now)
        {
            lock (_gate)
            {
                DateTimeOffset cutoff = now - _window;
                var staleKeys = new List<string>();
                foreach (KeyValuePair<string, Queue<DateTimeOffset>> pair in _buckets)
                {
                    Queue<DateTimeOffset> existing = pair.Value;
                    while (existing.Count > 0 && existing.Peek() <= cutoff) existing.Dequeue();
                    if (existing.Count == 0) staleKeys.Add(pair.Key);
                }
                for (int index = 0; index < staleKeys.Count; index++) _buckets.Remove(staleKeys[index]);
                Queue<DateTimeOffset> bucket;
                if (!_buckets.TryGetValue(key, out bucket))
                {
                    if (_buckets.Count >= _maximumBuckets) return false;
                    bucket = new Queue<DateTimeOffset>();
                    _buckets.Add(key, bucket);
                }
                while (bucket.Count > 0 && bucket.Peek() <= cutoff) bucket.Dequeue();
                if (bucket.Count >= _maximumRequests) return false;
                bucket.Enqueue(now);
                return true;
            }
        }
    }
}
