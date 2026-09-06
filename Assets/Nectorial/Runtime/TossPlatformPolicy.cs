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
    }
}
