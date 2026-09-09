using System;
using System.Globalization;
using System.Reflection;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using Nectorial.SlideEscape.Record;
using Nectorial.SlideEscape.Unity.CoopOnline;
using Nectorial.SlideEscape.Unity.Record;
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

            bool hasMine;
            bool hasShared;
            string recordError;
            if (!CoopOnlineBootstrap.TryDeserializeRecordEnvelopeForCheck("{\"hasMine\":false,\"hasShared\":false,\"mine\":null,\"shared\":null}", out hasMine, out hasShared, out recordError) || hasMine || hasShared)
                throw new InvalidOperationException("Online empty record DTO became a record: " + recordError);
            if (CoopOnlineBootstrap.TryDeserializeRecordEnvelopeForCheck("{\"hasMine\":true,\"hasShared\":false,\"mine\":null,\"shared\":null}", out hasMine, out hasShared, out recordError) || recordError != "mine_record_invalid")
                throw new InvalidOperationException("Online empty mine DTO was accepted: " + recordError);
            var current = new RecordSummaryObservation { modeId = "coop-v1", definitionId = "coop-c1", rulesVersion = "coop-rules-v1", contentVersion = "coop-c1-v1", definitionFingerprint = "current", statusCode = "Cleared", effectiveActionCount = 1, logicalActionCount = 1 };
            var selectedOther = new RecordSummaryObservation { modeId = "coop-v1", definitionId = "coop-c2", rulesVersion = "coop-rules-v1", contentVersion = "coop-c2-v1", definitionFingerprint = "other", statusCode = "Cleared", effectiveActionCount = 1, logicalActionCount = 1 };
            if (RecordObservationGuard.SameIdentity(current, selectedOther)) throw new InvalidOperationException("Online record comparison accepted a different selected definition.");
            Debug.Log("COOP_ONLINE_JSON_PROBE case=record-empty-guard-and-definition-identity state=pass");

            CheckTransportFailurePreservesConfirmedState(room);
            CheckRecordResultDoesNotReplaceActiveRoom(room);
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

        private static void CheckRecordResultDoesNotReplaceActiveRoom(CoopRoomDefinition room)
        {
            GameObject probe = null;
            GameObject reloadProbe = null;
            string bestKey = "nectorial.record.best.v1.coop-v1." + room.Id + "." + CoopRules.RoomFingerprint(room);
            PreferenceSnapshot priorBest = CapturePreference(bestKey);
            try
            {
                PlayerPrefs.DeleteKey(bestKey);
                PlayerPrefs.Save();
                probe = new GameObject("CoopOnlineRecordProbe");
                CoopOnlineBootstrap bootstrap = probe.AddComponent<CoopOnlineBootstrap>();
                SetField(bootstrap, "_room", CoopRules.CloneRoom(room));
                SetField(bootstrap, "_activeDefinitionId", room.Id);
                SetField(bootstrap, "_selectedDefinitionId", room.Id);
                SetField(bootstrap, "_initialized", true);
                SetField(bootstrap, "_joined", true);
                SetField(bootstrap, "_roomReady", true);
                SetField(bootstrap, "_roomId", "online-record-probe");
                CoopSolverResult solution = CoopSolver.FindSolution(room, 500000, new CoopSolverOptions { AllowPass = false });
                if (solution.Status != CoopSolverStatus.Solved) throw new InvalidOperationException("Coop record probe solution was unavailable.");
                var directions = new char[solution.Commands.Length];
                for (int index = 0; index < directions.Length; index++) directions[index] = solution.Commands[index].Direction == GameCommand.Up ? 'U' : solution.Commands[index].Direction == GameCommand.Down ? 'D' : solution.Commands[index].Direction == GameCommand.Left ? 'L' : 'R';
                var capsule = new RecordCapsule
                {
                    SchemaVersion = RecordCapsuleRules.SchemaVersion,
                    ModeId = RecordCapsuleRules.CoopModeId,
                    DefinitionId = room.Id,
                    RulesVersion = room.RulesVersion,
                    ContentVersion = room.ContentVersion,
                    DefinitionFingerprint = CoopRules.RoomFingerprint(room),
                    InputSequence = new string(directions)
                };
                string encoded;
                string encodeError;
                if (!RecordCapsuleCodec.TryEncode(capsule, out encoded, out encodeError)) throw new InvalidOperationException("Coop record probe capsule failed: " + encodeError);
                bootstrap.OnOnlineResult("{\"ok\":true,\"op\":\"record\",\"capsule\":\"" + encoded + "\"}");
                if (!ReadField<bool>(bootstrap, "_hasMine")) throw new InvalidOperationException("Coop runtime did not accept its verified mine record.");
                string bestBeforeShared = ReadField<string>(bootstrap, "_mineCapsule");
                bootstrap.HandleOnlineCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + encoded + "\"}");
                if (!ReadField<bool>(bootstrap, "_hasShared")) throw new InvalidOperationException("Coop runtime did not accept its verified shared record.");
                if (ReadField<string>(bootstrap, "_mineCapsule") != bestBeforeShared) throw new InvalidOperationException("Coop shared import changed local best record.");
                bootstrap.HandleOnlineCommand("{\"kind\":\"Challenge\"}");
                if (ReadField<string>(bootstrap, "_activeDefinitionId") != room.Id || ReadField<string>(bootstrap, "_selectedDefinitionId") != room.Id || ReadField<string>(bootstrap, "_recordError") != "challenge_requires_new_room")
                    throw new InvalidOperationException("Coop shared record changed an active room or skipped the explicit new-room gate.");
                UnityEngine.Object.DestroyImmediate(probe);
                probe = null;
                reloadProbe = new GameObject("CoopOnlineRecordReloadProbe");
                CoopOnlineBootstrap reloaded = reloadProbe.AddComponent<CoopOnlineBootstrap>();
                if (!ReadField<bool>(reloaded, "_initialized"))
                {
                    MethodInfo awake = typeof(CoopOnlineBootstrap).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (awake == null) throw new InvalidOperationException("Coop online bootstrap Awake was unavailable.");
                    awake.Invoke(reloaded, null);
                }
                if (!ReadField<bool>(reloaded, "_hasMine") || ReadField<string>(reloaded, "_mineCapsule") != bestBeforeShared)
                    throw new InvalidOperationException("Coop runtime did not revalidate local best record after reload.");
                Debug.Log("COOP_ONLINE_JSON_PROBE case=record-mine-shared-active-room-preserved state=pass");
            }
            finally
            {
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
                if (reloadProbe != null) UnityEngine.Object.DestroyImmediate(reloadProbe);
                RestorePreference(bestKey, priorBest);
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

        private static PreferenceSnapshot CapturePreference(string key)
        {
            return new PreferenceSnapshot { Exists = PlayerPrefs.HasKey(key), Value = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null };
        }

        private static void RestorePreference(string key, PreferenceSnapshot snapshot)
        {
            if (snapshot.Exists) PlayerPrefs.SetString(key, snapshot.Value);
            else PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }

        private static string OnlineResultJson(CoopRoomDefinition room, string onlineRoomId, long revision, int actions)
        {
            return "{\"ok\":true,\"op\":\"state\",\"seat\":0,\"room\":{\"roomId\":\"" + onlineRoomId + "\",\"rulesVersion\":\"" + room.RulesVersion + "\",\"contentVersion\":\"" + room.ContentVersion + "\",\"roomFingerprint\":\"" + CoopRules.RoomFingerprint(room) + "\"},\"state\":" + StateJson(room, "null", revision, actions) + ",\"availability\":{\"status\":1,\"circleConnected\":true,\"diamondConnected\":true},\"expressions\":{\"expressionSequence\":0,\"events\":[]}}";
        }

        private static string StateJson(CoopRoomDefinition room, string pending, long revision, int actions = 0)
        {
            return "{\"roomId\":\"" + room.Id + "\",\"circlePosition\":{\"x\":" + room.CircleStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.CircleStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"diamondPosition\":{\"x\":" + room.DiamondStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.DiamondStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"activeActor\":0,\"authorityRevision\":" + revision.ToString(CultureInfo.InvariantCulture) + ",\"logicalActionCount\":" + actions.ToString(CultureInfo.InvariantCulture) + ",\"status\":0,\"pendingConsent\":" + pending + "}";
        }

        private sealed class PreferenceSnapshot { public bool Exists; public string Value; }
    }
}
