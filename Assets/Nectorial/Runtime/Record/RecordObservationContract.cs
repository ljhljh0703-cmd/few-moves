using System;
using Nectorial.SlideEscape.Record;

namespace Nectorial.SlideEscape.Unity.Record
{
    [Serializable]
    public sealed class RecordSummaryObservation
    {
        public string modeId;
        public string definitionId;
        public string rulesVersion;
        public string contentVersion;
        public string definitionFingerprint;
        public string statusCode;
        public int effectiveActionCount;
        public int logicalActionCount;
        public int hits;
        public int circleX;
        public int circleY;
        public int diamondX;
        public int diamondY;

        public static RecordSummaryObservation From(RecordVerification verification)
        {
            if (verification == null) return null;
            return new RecordSummaryObservation
            {
                modeId = verification.ModeId,
                definitionId = verification.DefinitionId,
                rulesVersion = verification.RulesVersion,
                contentVersion = verification.ContentVersion,
                definitionFingerprint = verification.DefinitionFingerprint,
                statusCode = verification.StatusCode,
                effectiveActionCount = verification.EffectiveActionCount,
                logicalActionCount = verification.LogicalActionCount,
                hits = verification.Hits,
                circleX = verification.CirclePosition.X,
                circleY = verification.CirclePosition.Y,
                diamondX = verification.DiamondPosition.X,
                diamondY = verification.DiamondPosition.Y
            };
        }
    }

    [Serializable]
    public sealed class RecordObservationEnvelope
    {
        public bool hasMine;
        public RecordSummaryObservation mine;
        public bool hasShared;
        public RecordSummaryObservation shared;
    }

    public static class RecordObservationGuard
    {
        public static bool IsUsable(bool present, RecordSummaryObservation summary)
        {
            return present && summary != null && !string.IsNullOrEmpty(summary.modeId) && !string.IsNullOrEmpty(summary.definitionId)
                && !string.IsNullOrEmpty(summary.rulesVersion) && !string.IsNullOrEmpty(summary.contentVersion)
                && !string.IsNullOrEmpty(summary.definitionFingerprint) && string.Equals(summary.statusCode, "Cleared", StringComparison.Ordinal)
                && summary.effectiveActionCount > 0 && summary.logicalActionCount >= 0;
        }

        public static bool SameIdentity(RecordSummaryObservation left, RecordSummaryObservation right)
        {
            return IsUsable(true, left) && IsUsable(true, right)
                && string.Equals(left.modeId, right.modeId, StringComparison.Ordinal)
                && string.Equals(left.definitionId, right.definitionId, StringComparison.Ordinal)
                && string.Equals(left.rulesVersion, right.rulesVersion, StringComparison.Ordinal)
                && string.Equals(left.contentVersion, right.contentVersion, StringComparison.Ordinal)
                && string.Equals(left.definitionFingerprint, right.definitionFingerprint, StringComparison.Ordinal);
        }
    }
}
