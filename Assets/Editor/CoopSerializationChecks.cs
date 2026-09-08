using System;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using Nectorial.SlideEscape.Unity.Coop;
using UnityEditor;
using UnityEngine;

namespace Nectorial.Editor
{
    public static class CoopSerializationChecks
    {
        private const string RoomResource = "CoopRooms/coop-c1";

        [MenuItem("Few Moves/Coop/Run JSON serialization checks")]
        public static void Run()
        {
            TextAsset asset = Resources.Load<TextAsset>(RoomResource);
            if (asset == null) throw new InvalidOperationException("Missing coop room for serialization check: " + RoomResource);

            CoopRoomDefinition room = JsonUtility.FromJson<CoopRoomDefinition>(asset.text);
            string[] roomErrors = CoopRules.ValidateRoom(room);
            if (roomErrors.Length > 0) throw new InvalidOperationException("Coop room invalid: " + roomErrors[0]);

            CheckRoundTrip(room, "no-consent", CoopSession.Create(room), expectPending: false);

            CoopSession undo = CoopSession.Create(room);
            RequireAccepted(undo.Dispatch(CoopCommandFactory.Pass(CoopActor.Circle, "probe-pass", 0)), "probe-pass");
            RequireAccepted(undo.Dispatch(CoopCommandFactory.RequestUndo(CoopActor.Diamond, "probe-undo", 1, "probe-undo-request")), "probe-undo");
            CheckRoundTrip(room, "active-undo", undo, expectPending: true);

            CoopSession restart = CoopSession.Create(room);
            RequireAccepted(restart.Dispatch(CoopCommandFactory.RequestRestart(CoopActor.Circle, "probe-restart", 0, "probe-restart-request")), "probe-restart");
            CheckRoundTrip(room, "active-restart", restart, expectPending: true);

            CoopSaveEnvelope tampered = CoopSaveCodec.Capture(CoopSession.Create(room), "probe-tampered");
            tampered.State.PendingConsent = new CoopPendingConsent
            {
                RequestId = "tampered",
                Kind = (CoopConsentKind)99,
                Requester = (CoopActor)99,
                RequestedAtRevision = 0
            };
            string tamperedError;
            if (CoopSaveSerializationAdapter.TryNormalizePendingConsent(tampered, out tamperedError))
            {
                throw new InvalidOperationException("Tampered nonempty pending consent was accepted.");
            }
            Debug.Log("COOP_JSON_PROBE case=tampered-consent adapter=rejected error=" + tamperedError);
            Debug.Log("COOP_JSON_PROBE_RESULT pass=true");
        }

        private static void CheckRoundTrip(CoopRoomDefinition room, string label, CoopSession source, bool expectPending)
        {
            CoopSaveEnvelope captured = CoopSaveCodec.Capture(source, "probe-" + label);
            string json = JsonUtility.ToJson(captured);
            CoopSaveEnvelope decoded = JsonUtility.FromJson<CoopSaveEnvelope>(json);
            string rawPending = DescribePending(decoded != null && decoded.State != null ? decoded.State.PendingConsent : null);
            Debug.Log("COOP_JSON_PROBE case=" + label + " raw_pending=" + rawPending);

            string normalizeError;
            if (!CoopSaveSerializationAdapter.TryNormalizePendingConsent(decoded, out normalizeError))
            {
                throw new InvalidOperationException("Serialization normalization failed for " + label + ": " + normalizeError);
            }

            string normalizedPending = DescribePending(decoded.State.PendingConsent);
            if (expectPending != (decoded.State.PendingConsent != null))
            {
                throw new InvalidOperationException("Pending consent projection mismatch for " + label);
            }

            CoopSession restored;
            string restoreError;
            if (!CoopSaveCodec.TryRestore(room, decoded, out restored, out restoreError))
            {
                throw new InvalidOperationException("Save restore failed for " + label + ": " + restoreError);
            }
            if (!CoopRules.StatesEqual(source.State, restored.State) ||
                !string.Equals(CoopRules.StateFingerprint(room, source.State), CoopRules.StateFingerprint(room, restored.State), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("State projection changed for " + label);
            }

            Debug.Log("COOP_JSON_PROBE case=" + label + " normalized_pending=" + normalizedPending + " restore=pass state=" + CoopRules.StateFingerprint(room, restored.State));
        }

        private static void RequireAccepted(CoopDispatchResult result, string label)
        {
            if (result == null || !result.Accepted || result.Idempotent)
            {
                throw new InvalidOperationException("Probe command was not accepted: " + label);
            }
        }

        private static string DescribePending(CoopPendingConsent pending)
        {
            if (pending == null) return "null";
            if (string.IsNullOrEmpty(pending.RequestId) && pending.RequestedAtRevision == 0) return "empty-object";
            return pending.Kind + ":" + pending.Requester + ":" + pending.RequestId + ":" + pending.RequestedAtRevision;
        }
    }
}
