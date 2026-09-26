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

            CheckNoSessionResumeAndMalformedAuthenticatedPayload(room);
            CheckTransportFailurePreservesConfirmedState(room);
            CheckRecordConsentRestartBoundary(room);
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

        private static void CheckNoSessionResumeAndMalformedAuthenticatedPayload(CoopRoomDefinition room)
        {
            GameObject probe = null;
            try
            {
                probe = new GameObject("CoopOnlineResumeProbe");
                CoopOnlineBootstrap bootstrap = probe.AddComponent<CoopOnlineBootstrap>();
                SetField(bootstrap, "_room", CoopRules.CloneRoom(room));
                SetField(bootstrap, "_initialized", true);
                bootstrap.OnOnlineResult("{\"ok\":true,\"op\":\"resume\",\"inviteCode\":\"\",\"seat\":-1}");
                if (ReadField<bool>(bootstrap, "_joined") || ReadField<bool>(bootstrap, "_roomReady") || ReadField<CoopState>(bootstrap, "_serverState") != null ||
                    ReadField<string>(bootstrap, "_message") != "방을 만들거나 초대 코드로 참여하세요" || ReadField<string>(bootstrap, "_inviteCode") != string.Empty)
                {
                    throw new InvalidOperationException("Online no-session resume did not return to the create/join lobby.");
                }
                Debug.Log("COOP_ONLINE_JSON_PROBE case=resume-no-session-empty-invite-lobby state=pass");

                bootstrap.OnOnlineResult("{\"ok\":true,\"op\":\"resume\",\"inviteCode\":\"INV-URL-PROBE\",\"seat\":-1}");
                if (ReadField<bool>(bootstrap, "_joined") || ReadField<bool>(bootstrap, "_roomReady") || ReadField<CoopState>(bootstrap, "_serverState") != null ||
                    ReadField<string>(bootstrap, "_inviteCode") != "INV-URL-PROBE" || ReadField<string>(bootstrap, "_message") != "초대 코드에 연결할 저장된 좌석이 없습니다")
                {
                    throw new InvalidOperationException("Online no-session invite resume did not retain the invitation for joining.");
                }
                Debug.Log("COOP_ONLINE_JSON_PROBE case=resume-no-session-invite-preserved state=pass");

                bootstrap.OnOnlineResult("{\"ok\":true,\"op\":\"joined\",\"seat\":1,\"room\":{\"roomId\":\"join-probe-room\",\"rulesVersion\":\"" + room.RulesVersion + "\",\"contentVersion\":\"" + room.ContentVersion + "\",\"roomFingerprint\":\"" + CoopRules.RoomFingerprint(room) + "\"}}");
                if (!ReadField<bool>(bootstrap, "_joined") || !ReadField<bool>(bootstrap, "_roomReady") || ReadField<int>(bootstrap, "_seatCode") != 1)
                {
                    throw new InvalidOperationException("Online join after preserved invite did not enter the authenticated room path.");
                }
                Debug.Log("COOP_ONLINE_JSON_PROBE case=resume-invite-then-join state=pass");

                bootstrap.OnOnlineResult("{\"ok\":true,\"op\":\"state\",\"seat\":0,\"room\":{},\"state\":{}}");
                if (!ReadField<bool>(bootstrap, "_transportLocked") || ReadField<string>(bootstrap, "_error") != "online_authenticated_payload_invalid" || ReadField<CoopState>(bootstrap, "_serverState") != null)
                {
                    throw new InvalidOperationException("Malformed authenticated payload did not fail closed.");
                }
                Debug.Log("COOP_ONLINE_JSON_PROBE case=malformed-authenticated-payload state=rejected");
            }
            finally
            {
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        private static void CheckRecordConsentRestartBoundary(CoopRoomDefinition room)
        {
            GameObject probe = null;
            string bestKey = "nectorial.record.best.v1.coop-v1." + room.Id + "." + CoopRules.RoomFingerprint(room);
            PreferenceSnapshot priorBest = CapturePreference(bestKey);
            try
            {
                CoopSolverResult solution = CoopSolver.FindSolution(room, 500000, new CoopSolverOptions { AllowPass = false });
                if (solution.Status != CoopSolverStatus.Solved) throw new InvalidOperationException("Next clear record probe solution was unavailable.");
                var directions = new char[solution.Commands.Length];
                for (int index = 0; index < directions.Length; index++) directions[index] = solution.Commands[index].Direction == GameCommand.Up ? 'U' : solution.Commands[index].Direction == GameCommand.Down ? 'D' : solution.Commands[index].Direction == GameCommand.Left ? 'L' : 'R';
                var capsule = new RecordCapsule
                {
                    SchemaVersion = RecordCapsuleRules.SchemaVersion, ModeId = RecordCapsuleRules.CoopModeId, DefinitionId = room.Id,
                    RulesVersion = room.RulesVersion, ContentVersion = room.ContentVersion, DefinitionFingerprint = CoopRules.RoomFingerprint(room), InputSequence = new string(directions)
                };
                string encoded;
                string encodeError;
                if (!RecordCapsuleCodec.TryEncode(capsule, out encoded, out encodeError)) throw new InvalidOperationException("Next clear record capsule failed: " + encodeError);
                PlayerPrefs.SetString(bestKey, encoded);
                PlayerPrefs.Save();
                probe = new GameObject("CoopOnlineRecordConsentProbe");
                CoopOnlineBootstrap bootstrap = probe.AddComponent<CoopOnlineBootstrap>();
                SetField(bootstrap, "_room", CoopRules.CloneRoom(room));
                SetField(bootstrap, "_activeDefinitionId", room.Id);
                SetField(bootstrap, "_selectedDefinitionId", room.Id);
                SetField(bootstrap, "_initialized", true);
                SetField(bootstrap, "_joined", true);
                SetField(bootstrap, "_roomReady", true);
                SetField(bootstrap, "_roomId", "online-record-consent-probe");
                SetField(bootstrap, "_pendingRecordRequestId", "record-old");
                SetField(bootstrap, "_pendingRecordAutomatic", true);
                SetField(bootstrap, "_pendingAutoRecordFingerprint", "clear-old");
                SetField(bootstrap, "_recordStatus", "loading");
                RecordSummaryObservation retained = new RecordSummaryObservation
                {
                    modeId = "coop-v1", definitionId = room.Id, rulesVersion = room.RulesVersion, contentVersion = room.ContentVersion,
                    definitionFingerprint = CoopRules.RoomFingerprint(room), statusCode = "Cleared", effectiveActionCount = 8, logicalActionCount = 8
                };
                SetField(bootstrap, "_hasMine", true);
                SetField(bootstrap, "_mine", retained);
                SetField(bootstrap, "_hasShared", true);
                SetField(bootstrap, "_shared", retained);

                bootstrap.OnOnlineResult(AuthenticatedResultJson(room, "online-record-consent-probe", ClearedPendingRestartStateJson(room)));
                if (ReadField<string>(bootstrap, "_pendingRecordRequestId") != string.Empty || ReadField<string>(bootstrap, "_recordStatus") != "idle" || ReadField<string>(bootstrap, "_recordError") != string.Empty)
                    throw new InvalidOperationException("Consent-pending clear did not skip the automatic record request.");
                Debug.Log("COOP_ONLINE_JSON_PROBE case=record-consent-pending-skip state=pass");

                bootstrap.OnOnlineResult(AuthenticatedResultJson(room, "online-record-consent-probe", RestartedStateJson(room)));
                if (ReadField<CoopState>(bootstrap, "_serverState").LogicalActionCount != 0 || !ReadField<bool>(bootstrap, "_hasMine") || !ReadField<bool>(bootstrap, "_hasShared"))
                    throw new InvalidOperationException("Approved restart did not preserve records and reset the active run.");

                bootstrap.OnOnlineResult("{\"ok\":false,\"op\":\"record\",\"recordRequestId\":\"record-old\",\"error\":{\"code\":\"record_consent_pending\"}}");
                if (ReadField<string>(bootstrap, "_recordError") != string.Empty || ReadField<string>(bootstrap, "_recordStatus") != "idle" || !ReadField<bool>(bootstrap, "_hasMine") || !ReadField<bool>(bootstrap, "_hasShared"))
                    throw new InvalidOperationException("Late old record callback attached to the restarted run.");
                Debug.Log("COOP_ONLINE_JSON_PROBE case=record-late-callback-ignored state=pass");

                bootstrap.OnOnlineResult(AuthenticatedResultJson(room, "online-record-consent-probe", ClearedStateJson(room)));
                SetField(bootstrap, "_pendingRecordRequestId", "record-next");
                SetField(bootstrap, "_pendingRecordAutomatic", true);
                SetField(bootstrap, "_pendingAutoRecordFingerprint", "clear-next");
                SetField(bootstrap, "_pendingRecordStateFingerprint", CoopRules.StateFingerprint(room, ReadField<CoopState>(bootstrap, "_serverState")));
                bootstrap.OnOnlineResult("{\"ok\":true,\"op\":\"record\",\"recordRequestId\":\"record-next\",\"capsule\":\"" + encoded + "\"}");
                if (ReadField<string>(bootstrap, "_lastAutoRecordFingerprint") != "clear-next" || ReadField<string>(bootstrap, "_recordStatus") != "ready" || ReadField<string>(bootstrap, "_pendingRecordRequestId") != string.Empty)
                    throw new InvalidOperationException("Next clear record capture did not complete after restart.");
                Debug.Log("COOP_ONLINE_JSON_PROBE case=record-next-clear-capture state=pass");
            }
            finally
            {
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
                RestorePreference(bestKey, priorBest);
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
                CoopState clearedState = new CoopState
                {
                    RoomId = room.Id,
                    CirclePosition = room.CircleGoal,
                    DiamondPosition = room.DiamondGoal,
                    ActiveActor = CoopActor.Circle,
                    AuthorityRevision = 20,
                    LogicalActionCount = solution.Commands.Length,
                    Status = CoopRunStatus.Cleared,
                    PendingConsent = null
                };
                SetField(bootstrap, "_serverState", clearedState);
                SetField(bootstrap, "_pendingRecordRequestId", "record-probe-1");
                SetField(bootstrap, "_pendingRecordStateFingerprint", CoopRules.StateFingerprint(room, clearedState));
                bootstrap.OnOnlineResult("{\"ok\":true,\"op\":\"record\",\"recordRequestId\":\"record-probe-1\",\"capsule\":\"" + encoded + "\"}");
                if (!ReadField<bool>(bootstrap, "_hasMine")) throw new InvalidOperationException("Coop runtime did not accept its verified mine record.");
                string bestBeforeShared = ReadField<string>(bootstrap, "_mineCapsule");
                bootstrap.HandleOnlineCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + encoded + "\",\"requestId\":\"shared-a\"}");
                if (!ReadField<bool>(bootstrap, "_hasShared")) throw new InvalidOperationException("Coop runtime did not accept its verified shared record.");
                if (ReadField<string>(bootstrap, "_mineCapsule") != bestBeforeShared) throw new InvalidOperationException("Coop shared import changed local best record.");
                string delayedAObservation = ReadField<string>(bootstrap, "_lastObservationJsonForCheck");
                bootstrap.HandleOnlineCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + encoded + "\",\"requestId\":\"shared-b\"}");
                AssertSharedRecordRequestId(delayedAObservation, "shared-a", "Coop delayed A observation lost its request id.");
                AssertSharedRecordRequestId(ReadField<string>(bootstrap, "_lastObservationJsonForCheck"), "shared-b", "Coop B success observation did not carry B request id.");
                bootstrap.HandleOnlineCommand("{\"kind\":\"Challenge\"}");
                if (ReadField<string>(bootstrap, "_activeDefinitionId") != room.Id || ReadField<string>(bootstrap, "_selectedDefinitionId") != room.Id || ReadField<string>(bootstrap, "_recordError") != "challenge_requires_new_room")
                    throw new InvalidOperationException("Coop shared record changed an active room or skipped the explicit new-room gate.");
                AssertSharedRecordRequestId(ReadField<string>(bootstrap, "_lastObservationJsonForCheck"), "shared-b", "Coop subsequent observation did not preserve B request id.");
                bootstrap.HandleOnlineCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"fm1.invalid\",\"requestId\":\"shared-b\"}");
                AssertSharedRecordRequestId(ReadField<string>(bootstrap, "_lastObservationJsonForCheck"), "shared-b", "Coop B failure observation did not carry B request id.");
                bootstrap.HandleOnlineCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + encoded + "\"}");
                if (!ReadField<bool>(bootstrap, "_hasShared")) throw new InvalidOperationException("Coop legacy shared record input no longer validates.");
                AssertSharedRecordRequestId(ReadField<string>(bootstrap, "_lastObservationJsonForCheck"), string.Empty, "Coop legacy shared record input did not expose an empty request id.");
                bootstrap.HandleOnlineCommand("{\"kind\":\"LoadSharedRecord\",\"capsule\":\"" + encoded + "\",\"requestId\":\"" + new string('x', 97) + "\"}");
                AssertSharedRecordRequestId(ReadField<string>(bootstrap, "_lastObservationJsonForCheck"), string.Empty, "Coop oversized shared record request id was not bounded.");
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
                Debug.Log("COOP_ONLINE_JSON_PROBE case=record-mine-shared-request-correlation-active-room-preserved state=pass");
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

        private static void AssertSharedRecordRequestId(string json, string expected, string failure)
        {
            SharedRecordRequestObservation observation = JsonUtility.FromJson<SharedRecordRequestObservation>(json);
            if (observation == null || !string.Equals(observation.sharedRecordRequestId, expected, StringComparison.Ordinal)) throw new InvalidOperationException(failure);
        }

        private static string OnlineResultJson(CoopRoomDefinition room, string onlineRoomId, long revision, int actions)
        {
            return AuthenticatedResultJson(room, onlineRoomId, StateJson(room, "null", revision, actions));
        }

        private static string AuthenticatedResultJson(CoopRoomDefinition room, string onlineRoomId, string state)
        {
            return "{\"ok\":true,\"op\":\"state\",\"seat\":0,\"room\":{\"roomId\":\"" + onlineRoomId + "\",\"rulesVersion\":\"" + room.RulesVersion + "\",\"contentVersion\":\"" + room.ContentVersion + "\",\"roomFingerprint\":\"" + CoopRules.RoomFingerprint(room) + "\"},\"state\":" + state + ",\"availability\":{\"status\":1,\"circleConnected\":true,\"diamondConnected\":true},\"expressions\":{\"expressionSequence\":0,\"events\":[]}}";
        }

        private static string ClearedPendingRestartStateJson(CoopRoomDefinition room)
        {
            return "{\"roomId\":\"" + room.Id + "\",\"circlePosition\":{\"x\":" + room.CircleGoal.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.CircleGoal.Y.ToString(CultureInfo.InvariantCulture) + "},\"diamondPosition\":{\"x\":" + room.DiamondGoal.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.DiamondGoal.Y.ToString(CultureInfo.InvariantCulture) + "},\"activeActor\":0,\"authorityRevision\":9,\"logicalActionCount\":8,\"status\":1,\"pendingConsent\":{\"requestId\":\"restart-pending\",\"kind\":1,\"requester\":0,\"requestedAtRevision\":9}}";
        }

        private static string RestartedStateJson(CoopRoomDefinition room)
        {
            return "{\"roomId\":\"" + room.Id + "\",\"circlePosition\":{\"x\":" + room.CircleStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.CircleStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"diamondPosition\":{\"x\":" + room.DiamondStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.DiamondStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"activeActor\":0,\"authorityRevision\":10,\"logicalActionCount\":0,\"status\":0,\"pendingConsent\":null}";
        }

        private static string ClearedStateJson(CoopRoomDefinition room)
        {
            return "{\"roomId\":\"" + room.Id + "\",\"circlePosition\":{\"x\":" + room.CircleGoal.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.CircleGoal.Y.ToString(CultureInfo.InvariantCulture) + "},\"diamondPosition\":{\"x\":" + room.DiamondGoal.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.DiamondGoal.Y.ToString(CultureInfo.InvariantCulture) + "},\"activeActor\":0,\"authorityRevision\":20,\"logicalActionCount\":8,\"status\":1,\"pendingConsent\":null}";
        }

        private static string StateJson(CoopRoomDefinition room, string pending, long revision, int actions = 0)
        {
            return "{\"roomId\":\"" + room.Id + "\",\"circlePosition\":{\"x\":" + room.CircleStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.CircleStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"diamondPosition\":{\"x\":" + room.DiamondStart.X.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + room.DiamondStart.Y.ToString(CultureInfo.InvariantCulture) + "},\"activeActor\":0,\"authorityRevision\":" + revision.ToString(CultureInfo.InvariantCulture) + ",\"logicalActionCount\":" + actions.ToString(CultureInfo.InvariantCulture) + ",\"status\":0,\"pendingConsent\":" + pending + "}";
        }

        [Serializable]
        private sealed class SharedRecordRequestObservation { public string sharedRecordRequestId; }

        private sealed class PreferenceSnapshot { public bool Exists; public string Value; }
    }
}
