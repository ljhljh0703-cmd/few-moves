using System;
using System.Globalization;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using Nectorial.SlideEscape.Unity.CoopOnline;
using UnityEditor;
using UnityEngine;

namespace Nectorial.Editor
{
    public static class CoopOnlineSerializationChecks
    {
        private const string RoomResource = "CoopRooms/coop-c1";

        [MenuItem("Few Moves/Coop/Run online JSON serialization checks")]
        public static void Run()
        {
            TextAsset asset = Resources.Load<TextAsset>(RoomResource);
            if (asset == null) throw new InvalidOperationException("Missing coop room for online serialization check: " + RoomResource);
            CoopRoomDefinition room = JsonUtility.FromJson<CoopRoomDefinition>(asset.text);
            string[] roomErrors = CoopRules.ValidateRoom(room);
            if (roomErrors.Length > 0) throw new InvalidOperationException("Coop room invalid: " + roomErrors[0]);

            CoopState noPending;
            string noPendingError;
            if (!CoopOnlineBootstrap.TryDeserializeServerStateForCheck(room, StateJson(room, "null", 0), out noPending, out noPendingError) || noPending.PendingConsent != null)
            {
                throw new InvalidOperationException("Online null pending normalization failed: " + noPendingError);
            }
            Debug.Log("COOP_ONLINE_JSON_PROBE case=pending-null state=pass");

            CoopState active;
            string activeError;
            string activePending = "{\"requestId\":\"probe-active\",\"kind\":0,\"requester\":0,\"requestedAtRevision\":1}";
            if (!CoopOnlineBootstrap.TryDeserializeServerStateForCheck(room, StateJson(room, activePending, 1), out active, out activeError) || active.PendingConsent == null || !string.Equals(active.PendingConsent.RequestId, "probe-active", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Online active pending preservation failed: " + activeError);
            }
            Debug.Log("COOP_ONLINE_JSON_PROBE case=active-pending state=pass");

            CoopState malformed;
            string malformedError;
            string malformedPending = "{\"requestId\":\"\",\"kind\":1,\"requester\":0,\"requestedAtRevision\":0}";
            if (CoopOnlineBootstrap.TryDeserializeServerStateForCheck(room, StateJson(room, malformedPending, 1), out malformed, out malformedError))
            {
                throw new InvalidOperationException("Online malformed pending consent was accepted.");
            }
            Debug.Log("COOP_ONLINE_JSON_PROBE case=malformed-pending state=rejected error=" + malformedError);
            Debug.Log("COOP_ONLINE_JSON_PROBE_RESULT pass=true");
        }

        private static string StateJson(CoopRoomDefinition room, string pending, long revision)
        {
            return "{\"roomId\":\"" + room.Id + "\",\"circlePosition\":{\"x\":" + room.CircleStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.CircleStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"diamondPosition\":{\"x\":" + room.DiamondStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.DiamondStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"activeActor\":0,\"authorityRevision\":" + revision.ToString(CultureInfo.InvariantCulture) + ",\"logicalActionCount\":0,\"status\":0,\"pendingConsent\":" + pending + "}";
        }
    }
}
