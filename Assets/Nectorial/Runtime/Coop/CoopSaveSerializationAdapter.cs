using System;
using Nectorial.SlideEscape.Coop;

namespace Nectorial.SlideEscape.Unity.Coop
{
    public static class CoopSaveSerializationAdapter
    {
        public static bool TryNormalizePendingConsent(CoopSaveEnvelope envelope, out string error)
        {
            error = null;
            if (envelope == null || envelope.State == null) return true;

            CoopPendingConsent pending = envelope.State.PendingConsent;
            if (pending == null) return true;
            if (IsCanonicalEmptyPending(pending))
            {
                envelope.State.PendingConsent = null;
                return true;
            }

            if (string.IsNullOrEmpty(pending.RequestId) ||
                !IsKnownConsentKind(pending.Kind) ||
                !IsKnownActor(pending.Requester) ||
                pending.RequestedAtRevision < 1)
            {
                error = "save_pending_consent_invalid";
                return false;
            }

            return true;
        }

        private static bool IsCanonicalEmptyPending(CoopPendingConsent pending)
        {
            return string.IsNullOrEmpty(pending.RequestId) &&
                pending.Kind == default(CoopConsentKind) &&
                pending.Requester == default(CoopActor) &&
                pending.RequestedAtRevision == 0;
        }

        private static bool IsKnownConsentKind(CoopConsentKind kind)
        {
            return kind == CoopConsentKind.Undo || kind == CoopConsentKind.Restart;
        }

        private static bool IsKnownActor(CoopActor actor)
        {
            return actor == CoopActor.Circle || actor == CoopActor.Diamond;
        }
    }
}
