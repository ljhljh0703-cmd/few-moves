using System;

namespace Nectorial.SlideEscape.Unity
{
    internal static class TossPlatformPolicy
    {
        internal const string StorageKeyPrefix = "nectorial-turn-escape.toss.v1.";

        // The pinned SDK documents null as the missing-key result. Its current
        // JavaScript/C# relay materializes that same value as the literal string
        // "null", so both forms mean a new scoped profile. Empty is not missing.
        internal static bool IsMissingStorageValue(string value)
        {
            return value == null || string.Equals(value, "null", StringComparison.Ordinal);
        }

        internal static string BuildScopedStorageKey(string anonymousHash)
        {
            if (string.IsNullOrWhiteSpace(anonymousHash))
            {
                throw new ArgumentException("A non-empty anonymous hash is required.", nameof(anonymousHash));
            }

            return StorageKeyPrefix + anonymousHash;
        }

        // Raid progress lives under its own namespace, never under the solo prefix above. The arena id and
        // fingerprint come first (dot-free token characters) so a key names exactly one user, arena and content.
        internal const string RaidStorageKeyPrefix = "nectorial-raid.toss.v1.";
        internal const string RaidPayloadHeader = "fm-raid-toss.v1";

        internal static bool IsValidRaidScope(string arenaId, string arenaFingerprint)
        {
            return IsToken(arenaId) && IsToken(arenaFingerprint);
        }

        internal static string BuildRaidStorageKey(string anonymousHash, string arenaId, string arenaFingerprint)
        {
            if (string.IsNullOrWhiteSpace(anonymousHash))
            {
                throw new ArgumentException("A non-empty anonymous hash is required.", nameof(anonymousHash));
            }

            if (!IsValidRaidScope(arenaId, arenaFingerprint))
            {
                throw new ArgumentException("A token arena id and fingerprint are required.", nameof(arenaId));
            }

            return RaidStorageKeyPrefix + arenaId + "." + arenaFingerprint + "." + anonymousHash;
        }

        // One stored string holds both the progress envelope and the verified best capsule, so a single
        // StorageSetItem replaces them together. Lines: header, arena id, fingerprint, best capsule (may be
        // empty), progress JSON. Callers still verify both parts against the live arena on restore.
        internal static string FormatRaidPayload(string arenaId, string arenaFingerprint, string bestCapsule, string progressJson)
        {
            if (!IsValidRaidScope(arenaId, arenaFingerprint))
            {
                throw new ArgumentException("A token arena id and fingerprint are required.", nameof(arenaId));
            }

            string best = bestCapsule ?? string.Empty;
            if (HasLineBreak(best)) throw new ArgumentException("The best capsule must be one line.", nameof(bestCapsule));
            if (!IsProgressJson(progressJson)) throw new ArgumentException("Progress must be one JSON object line.", nameof(progressJson));
            return RaidPayloadHeader + "\n" + arenaId + "\n" + arenaFingerprint + "\n" + best + "\n" + progressJson;
        }

        internal static bool TryParseRaidPayload(string payload, string arenaId, string arenaFingerprint,
            out string bestCapsule, out string progressJson, out string error)
        {
            bestCapsule = null;
            progressJson = null;
            error = null;
            if (string.IsNullOrEmpty(payload)) { error = "raid_toss_payload_empty"; return false; }

            string[] parts = payload.Split(new[] { '\n' }, 5);
            if (parts.Length != 5 || !string.Equals(parts[0], RaidPayloadHeader, StringComparison.Ordinal))
            {
                error = "raid_toss_payload_version";
                return false;
            }

            if (!string.Equals(parts[1], arenaId, StringComparison.Ordinal)) { error = "raid_toss_payload_arena"; return false; }
            if (!string.Equals(parts[2], arenaFingerprint, StringComparison.Ordinal)) { error = "raid_toss_payload_fingerprint"; return false; }
            if (parts[3].IndexOf('\r') >= 0) { error = "raid_toss_payload_best"; return false; }
            if (!IsProgressJson(parts[4])) { error = "raid_toss_payload_progress"; return false; }

            bestCapsule = parts[3];
            progressJson = parts[4];
            return true;
        }

        private static bool IsProgressJson(string value)
        {
            return !string.IsNullOrEmpty(value) && value[0] == '{' && value[value.Length - 1] == '}' && !HasLineBreak(value);
        }

        private static bool HasLineBreak(string value)
        {
            return value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0;
        }

        private static bool IsToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }

            return true;
        }
    }
}
