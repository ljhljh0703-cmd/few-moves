using System;
using System.IO;

namespace FewMoves.Coop.Server
{
    public sealed class ServerOptions
    {
        public const int DefaultRequestBodyBytes = 4096;

        public string PublicRoot;
        public string StateRoot;
        public string RoomPath;
        public string RoomCatalogRoot;
        public string ListenUrl;
        public string BuildId;
        public string Version = "coop-http-v1";
        public TimeSpan RoomTtl = TimeSpan.FromHours(24);
        public TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);
        public TimeSpan RequestWindow = TimeSpan.FromMinutes(1);
        public TimeSpan CommandWindow = TimeSpan.FromMinutes(1);
        public TimeSpan ExpressionWindow = TimeSpan.FromSeconds(10);
        public TimeSpan ExpressionCooldown = TimeSpan.FromSeconds(2);
        public int MaxLiveRooms = 64;
        public int MaxRequestBodyBytes = DefaultRequestBodyBytes;
        public int MaxRequestBuckets = 256;
        public int MaxRequestsPerWindow = 480;
        public int MaxCommandLedgerRecords = 2048;
        public int MaxStoredRooms = 128;
        public int MaxAuditEvents = 128;
        public int MaxExpressionEvents = 32;
        public int MaxReturnedCoreEvents = 8;
        public int MaxCommandsPerSeatPerWindow = 30;
        public int MaxExpressionsPerSeatPerWindow = 4;
        public int FailNextWritesForTests;

        public static ServerOptions FromEnvironment(string[] args)
        {
            var options = new ServerOptions();
            options.PublicRoot = RequiredPath("FEW_MOVES_WEB_ROOT");
            options.StateRoot = RequiredPath("FEW_MOVES_STATE_ROOT");
            options.RoomPath = Environment.GetEnvironmentVariable("FEW_MOVES_ROOM_PATH");
            if (string.IsNullOrEmpty(options.RoomPath)) options.RoomPath = Path.Combine(AppContext.BaseDirectory, "CoopRooms", "coop-c1.json");
            options.RoomCatalogRoot = Environment.GetEnvironmentVariable("FEW_MOVES_ROOM_CATALOG_ROOT");
            if (string.IsNullOrEmpty(options.RoomCatalogRoot)) options.RoomCatalogRoot = Path.GetDirectoryName(options.RoomPath);
            options.ListenUrl = Environment.GetEnvironmentVariable("FEW_MOVES_LISTEN_URL");
            if (string.IsNullOrEmpty(options.ListenUrl)) options.ListenUrl = "http://127.0.0.1:5088";
            options.BuildId = BoundedEnvironment("FEW_MOVES_SERVER_BUILD_ID", 128);
            options.Validate();
            return options;
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(PublicRoot)) throw new InvalidOperationException("FEW_MOVES_WEB_ROOT is required.");
            if (string.IsNullOrWhiteSpace(StateRoot)) throw new InvalidOperationException("FEW_MOVES_STATE_ROOT is required.");
            if (string.IsNullOrWhiteSpace(RoomPath)) throw new InvalidOperationException("Room path is required.");
            if (string.IsNullOrWhiteSpace(RoomCatalogRoot)) throw new InvalidOperationException("Room catalog root is required.");
            if (string.IsNullOrWhiteSpace(ListenUrl)) throw new InvalidOperationException("Listen URL is required.");

            PublicRoot = Path.GetFullPath(PublicRoot);
            StateRoot = Path.GetFullPath(StateRoot);
            RoomPath = Path.GetFullPath(RoomPath);
            RoomCatalogRoot = Path.GetFullPath(RoomCatalogRoot);
            if (!Directory.Exists(PublicRoot)) throw new DirectoryNotFoundException("Configured public root is unavailable.");
            if (!File.Exists(Path.Combine(PublicRoot, "index.html"))) throw new FileNotFoundException("Configured public root has no index.html.");
            if (!ContainsWasm(PublicRoot)) throw new FileNotFoundException("Configured public root has no uncompressed wasm asset.");
            if (!File.Exists(RoomPath)) throw new FileNotFoundException("Configured co-op room is unavailable.");
            if (!Directory.Exists(RoomCatalogRoot)) throw new DirectoryNotFoundException("Configured co-op room catalog is unavailable.");
            if (PathsOverlap(PublicRoot, StateRoot)) throw new InvalidOperationException("Public and private state roots must not overlap.");
            if (RoomTtl <= TimeSpan.Zero || HeartbeatTimeout <= TimeSpan.Zero || RequestWindow <= TimeSpan.Zero || CommandWindow <= TimeSpan.Zero || ExpressionWindow <= TimeSpan.Zero || ExpressionCooldown <= TimeSpan.Zero)
                throw new InvalidOperationException("Server durations must be positive.");
            if (MaxLiveRooms < 1 || MaxStoredRooms < MaxLiveRooms || MaxRequestBodyBytes < 128 || MaxRequestBuckets < 1 || MaxRequestsPerWindow < 1 || MaxCommandLedgerRecords < 1 || MaxAuditEvents < 1 || MaxExpressionEvents < 1 || MaxReturnedCoreEvents < 1 || MaxCommandsPerSeatPerWindow < 1 || MaxExpressionsPerSeatPerWindow < 1)
                throw new InvalidOperationException("Server limits must be positive.");
        }

        private static string RequiredPath(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(name + " is required.");
            return value;
        }

        private static string BoundedEnvironment(string name, int maximumLength)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) return null;
            if (value.Length > maximumLength) throw new InvalidOperationException(name + " is too long.");
            return value;
        }

        private static bool PathsOverlap(string first, string second)
        {
            string left = WithSeparator(first);
            string right = WithSeparator(second);
            return left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal);
        }

        private static string WithSeparator(string path)
        {
            char separator = Path.DirectorySeparatorChar;
            return path.EndsWith(separator.ToString(), StringComparison.Ordinal) ? path : path + separator;
        }

        private static bool ContainsWasm(string root)
        {
            using (System.Collections.Generic.IEnumerator<string> files = Directory.EnumerateFiles(root, "*.wasm", SearchOption.AllDirectories).GetEnumerator()) return files.MoveNext();
        }
    }
}
