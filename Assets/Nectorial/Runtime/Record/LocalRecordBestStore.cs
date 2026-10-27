using System;
using Nectorial.SlideEscape.Record;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity.Record
{
    public static class LocalRecordBestStore
    {
        private const string KeyPrefix = "nectorial.record.best.v1";

        public static bool TryRead(RecordCapsule identity, out string capsule)
        {
            capsule = null;
            string key;
            if (!TryKey(identity, out key)) return false;
            try
            {
                if (!PlayerPrefs.HasKey(key)) return false;
                capsule = PlayerPrefs.GetString(key);
                return !string.IsNullOrEmpty(capsule);
            }
            catch (Exception)
            {
                capsule = null;
                return false;
            }
        }

        public static bool TryWrite(RecordCapsule identity, string capsule)
        {
            string key;
            if (!TryKey(identity, out key) || string.IsNullOrEmpty(capsule)) return false;
            try
            {
                PlayerPrefs.SetString(key, capsule);
                PlayerPrefs.Save();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool IsBetter(RecordSummaryObservation candidate, RecordSummaryObservation incumbent)
        {
            if (!RecordObservationGuard.IsUsable(true, candidate)) return false;
            if (!RecordObservationGuard.IsUsable(true, incumbent)) return true;
            if (!RecordObservationGuard.SameIdentity(candidate, incumbent)) return false;
            if (candidate.effectiveActionCount != incumbent.effectiveActionCount) return candidate.effectiveActionCount < incumbent.effectiveActionCount;
            if (string.Equals(candidate.modeId, RecordCapsuleRules.RaidModeId, StringComparison.Ordinal) && candidate.hits != incumbent.hits) return candidate.hits < incumbent.hits;
            return false;
        }

        private static bool TryKey(RecordCapsule identity, out string key)
        {
            key = null;
            if (identity == null || string.IsNullOrEmpty(identity.ModeId) || string.IsNullOrEmpty(identity.DefinitionId) || string.IsNullOrEmpty(identity.DefinitionFingerprint)) return false;
            key = KeyPrefix + "." + identity.ModeId + "." + identity.DefinitionId + "." + identity.DefinitionFingerprint;
            return key.Length <= 512;
        }
    }
}
