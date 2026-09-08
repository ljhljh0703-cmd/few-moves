using System;
using System.Globalization;
using System.Reflection;
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

            CheckTransportFailurePreservesConfirmedState(room);
            Debug.Log("COOP_ONLINE_JSON_PROBE_RESULT pass=true");
        }

        private static void CheckTransportFailurePreservesConfirmedState(CoopRoomDefinition room)
        {
            GameObject probe = null;
            try
            {
                probe = new GameObject("CoopOnlineSerializationProbe");
                CoopOnlineBootstrap bootstrap = probe.AddComponent<CoopOnlineBootstrap>();
                SetField(bootstrap, "_room", CoopRules.CloneRoom(room));
                SetField(bootstrap, "_initialized", true);
                string onlineRoomId = "7b3c5a3d8c2f4e65a2b5d7089134c6de";
                string confirmed = OnlineResultJson(room, onlineRoomId, 2, 2);
                bootstrap.OnOnlineResult(confirmed);

                CoopState before = ReadField<CoopState>(bootstrap, "_serverState");
                if (before == null) throw new InvalidOperationException("Confirmed online probe state was not applied.");
                string fingerprint = CoopRules.StateFingerprint(room, before);
                bootstrap.OnOnlineResult("{\"ok\":false,\"op\":\"state\",\"error\":{\"code\":\"network_unavailable\"}}");

                CoopState offline = ReadField<CoopState>(bootstrap, "_serverState");
                if (offline == null || !string.Equals(fingerprint, CoopRules.StateFingerprint(room, offline), StringComparison.Ordinal) ||
                    ReadField<long>(bootstrap, "_authorityRevision") != 2 || ReadField<int>(bootstrap, "_logicalActionCount") != 2 || !ReadField<bool>(bootstrap, "_transportLocked"))
                {
                    throw new InvalidOperationException("Online transport failure did not preserve confirmed state while locking input.");
                }
                Debug.Log("COOP_ONLINE_JSON_PROBE case=error-without-state preserve=pass input=locked");

                bootstrap.OnOnlineResult(confirmed);
                if (ReadField<bool>(bootstrap, "_transportLocked")) throw new InvalidOperationException("Recovered authenticated state did not unlock input.");
                Debug.Log("COOP_ONLINE_JSON_PROBE case=recovered-auth-state unlock=pass");
            }
            finally
            {
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        private static T ReadField<T>(CoopOnlineBootstrap bootstrap, string name)
        {
            FieldInfo field = typeof(CoopOnlineBootstrap).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing online probe field: " + name);
            return (T)field.GetValue(bootstrap);
        }

        private static void SetField<T>(CoopOnlineBootstrap bootstrap, string name, T value)
        {
            FieldInfo field = typeof(CoopOnlineBootstrap).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing online probe field: " + name);
            field.SetValue(bootstrap, value);
        }

        private static string OnlineResultJson(CoopRoomDefinition room, string onlineRoomId, long revision, int actions)
        {
            return "{\"ok\":true,\"op\":\"state\",\"seat\":0,\"room\":{\"roomId\":\"" + onlineRoomId + "\",\"rulesVersion\":\"" + room.RulesVersion + "\",\"contentVersion\":\"" + room.ContentVersion + "\",\"roomFingerprint\":\"" + CoopRules.RoomFingerprint(room) + "\"},\"state\":" + StateJson(room, "null", revision, actions) + ",\"availability\":{\"status\":1,\"circleConnected\":true,\"diamondConnected\":true},\"expressions\":{\"expressionSequence\":0,\"events\":[]}}";
        }

        private static string StateJson(CoopRoomDefinition room, string pending, long revision, int actions = 0)
        {
            return "{\"roomId\":\"" + room.Id + "\",\"circlePosition\":{\"x\":" + room.CircleStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.CircleStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"diamondPosition\":{\"x\":" + room.DiamondStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.DiamondStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"activeActor\":0,\"authorityRevision\":" + revision.ToString(CultureInfo.InvariantCulture) + ",\"logicalActionCount\":" + actions.ToString(CultureInfo.InvariantCulture) + ",\"status\":0,\"pendingConsent\":" + pending + "}";
        }
    }
}
