using System;
using Nectorial.SlideEscape.Unity;

internal static class Program
{
    private static int Main()
    {
        int failed = 0;
        failed += Run("documented_null_is_missing", delegate
        {
            Assert(TossPlatformPolicy.IsMissingStorageValue(null), "C# null must initialize a new scoped profile");
        });
        failed += Run("pinned_relay_null_string_is_missing", delegate
        {
            Assert(TossPlatformPolicy.IsMissingStorageValue("null"), "pinned jslib relay literal must initialize a new scoped profile");
        });
        failed += Run("empty_and_case_variant_are_not_missing", delegate
        {
            Assert(!TossPlatformPolicy.IsMissingStorageValue(string.Empty), "empty string is a malformed existing payload, not a missing key");
            Assert(!TossPlatformPolicy.IsMissingStorageValue("NULL"), "missing sentinel matching must stay exact");
        });
        failed += Run("existing_blob_is_not_missing", delegate
        {
            Assert(!TossPlatformPolicy.IsMissingStorageValue("{\"schemaVersion\":2}"), "an existing JSON blob must reach restore validation");
        });
        failed += Run("scope_key_is_versioned_and_requires_identity", delegate
        {
            const string anonymousHash = "opaque-platform-hash";
            string key = TossPlatformPolicy.BuildScopedStorageKey(anonymousHash);
            AssertEqual(TossPlatformPolicy.StorageKeyPrefix + anonymousHash, key, "scope key must bind this profile to the versioned Toss namespace");
            AssertThrows(delegate { TossPlatformPolicy.BuildScopedStorageKey(null); }, "null identity must not create a shared fallback key");
            AssertThrows(delegate { TossPlatformPolicy.BuildScopedStorageKey(" "); }, "blank identity must not create a shared fallback key");
        });
        failed += Run("solo_key_bytes_unchanged", delegate
        {
            AssertEqual("nectorial-turn-escape.toss.v1.opaque-platform-hash", TossPlatformPolicy.BuildScopedStorageKey("opaque-platform-hash"), "solo key bytes must stay exactly as released");
            AssertEqual("nectorial-turn-escape.toss.v1.", TossPlatformPolicy.StorageKeyPrefix, "solo prefix must stay exactly as released");
        });
        failed += Run("raid_key_is_scoped_by_user_arena_and_fingerprint", CheckRaidStorageKey);
        failed += Run("raid_payload_round_trip_and_fail_closed_parse", CheckRaidPayloadCodec);
        failed += Run("raid_clear_final_payload_wins_over_inflight_and_stale", CheckRaidWriteOrdering);
        failed += Run("queued_manual_superseded_by_auto_is_released", CheckSupersededQueuedManual);
        failed += Run("queued_manual_superseded_then_newest_fails_is_released", CheckSupersededQueuedManualFailure);
        failed += Run("solo_manual_behind_earlier_write_stays_pending", CheckManualStaysPendingBehindEarlierWrite);
        failed += Run("delayed_timeout_then_retry_keeps_latest_persisted_payload", CheckSerialWritePolicy);
        failed += Run("native_save_ui_timeout_and_status_coordinator", CheckNativeSaveCoordinator);

        Console.WriteLine("Toss platform policy checks: " + (failed == 0 ? "passed" : "failed=" + failed));
        return failed == 0 ? 0 : 1;
    }

    private static int Run(string name, Action check)
    {
        try
        {
            check();
            Console.WriteLine("PASS " + name);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
            return 1;
        }
    }

    private static void CheckSerialWritePolicy()
    {
        var writer = new TossSerialWritePolicy();
        TossWriteRequest requestToStart;
        Assert(writer.Enqueue(new TossWriteRequest(1, "before-timeout", false), out requestToStart), "first write must start immediately");
        AssertEqual(1, requestToStart.Id, "first active request");

        Assert(!writer.Enqueue(new TossWriteRequest(2, "retry-after-timeout", true), out requestToStart), "timeout UI must not start a concurrent retry");
        AssertEqual(0, requestToStart.Id, "second request remains queued until the original platform operation completes");
        Assert(!writer.Enqueue(new TossWriteRequest(3, "start-over-latest", true), out requestToStart), "newer explicit recovery replaces only the queued checkpoint");
        AssertEqual(0, requestToStart.Id, "the delayed first operation remains the only in-flight write");

        TossWriteRequest completed;
        TossWriteRequest nextToStart;
        Assert(!writer.Complete(2, out completed, out nextToStart), "a completion for a request that never started must be ignored");
        Assert(writer.Complete(1, out completed, out nextToStart), "late completion releases the serial slot");
        AssertEqual(1, completed.Id, "first payload persists before any later write begins");
        AssertEqual(3, nextToStart.Id, "the newest queued payload is the only next write");
        AssertEqual("start-over-latest", nextToStart.Payload, "older queued retry cannot overwrite the latest recovery payload");
        Assert(writer.Complete(3, out completed, out nextToStart), "latest write completes");
        AssertEqual(3, completed.Id, "latest payload is the final issued write");
        AssertEqual(0, nextToStart.Id, "queue drains after final completion");
    }

    private static void CheckNativeSaveCoordinator()
    {
        var coordinator = new TossNativeSaveCoordinator();
        TossWriteRequest requestToStart;
        TossNativeSaveCompletion completion;
        TossWriteRequest nextToStart;

        Assert(coordinator.Queue(new TossWriteRequest(10, "manual-saved", true), out requestToStart), "manual save starts when idle");
        AssertEqual(10, requestToStart.Id, "manual request is active");
        Assert(coordinator.ManualPending, "manual save disables input while its UI deadline is active");
        AssertEqual("pending", coordinator.Status, "manual save starts pending");
        Assert(coordinator.Complete(10, true, null, out completion, out nextToStart), "manual success completes");
        Assert(completion.AffectsCurrentStatus, "completed manual save is current");
        AssertEqual("saved", coordinator.Status, "only completed current payload becomes saved");

        Assert(coordinator.Queue(new TossWriteRequest(11, "move-after-save", false), out requestToStart), "first autosave after manual success starts");
        AssertEqual("changed", coordinator.Status, "new movement clears the prior saved claim while its checkpoint is pending");
        Assert(coordinator.Complete(11, false, "storage_api_error", out completion, out nextToStart), "autosave failure completes");
        Assert(completion.AffectsCurrentStatus, "failed autosave is current");
        AssertEqual("failed", coordinator.Status, "current durable checkpoint failure cannot retain saved status");

        var delayed = new TossNativeSaveCoordinator();
        Assert(delayed.Queue(new TossWriteRequest(20, "old-manual", true), out requestToStart), "first delayed manual write starts");
        Assert(delayed.TimeoutManual(20), "UI timeout releases input without completing the native write");
        Assert(!delayed.ManualPending, "UI timeout no longer blocks interaction");
        AssertEqual("failed", delayed.Status, "timeout is visible as failure");
        Assert(!delayed.Queue(new TossWriteRequest(21, "newest-checkpoint", false), out requestToStart), "timeout does not free the active native write slot");
        AssertEqual(0, requestToStart.Id, "new checkpoint waits behind the unresolved native operation");
        AssertEqual("changed", delayed.Status, "new movement clears timeout failure while latest checkpoint is pending");
        Assert(delayed.Complete(20, true, null, out completion, out nextToStart), "late old callback completes its own write");
        Assert(!completion.AffectsCurrentStatus, "late old callback cannot mark the newer checkpoint saved");
        AssertEqual("changed", delayed.Status, "late old callback leaves newest checkpoint pending");
        AssertEqual(21, nextToStart.Id, "only newest queued checkpoint starts after the old completion");
        Assert(delayed.Complete(21, true, null, out completion, out nextToStart), "newest checkpoint completes");
        Assert(completion.AffectsCurrentStatus, "newest completion controls saved status");
        AssertEqual("saved", delayed.Status, "latest durable checkpoint alone restores saved status");
    }

    private const string ArenaId = "raid-01-v5";
    private const string ArenaFingerprint = "3215b96aed7249f73ecc11b7e4f8b9f34df6e404dd7a1c4c43e12ddf2cddfe80";

    private static void CheckRaidStorageKey()
    {
        string key = TossPlatformPolicy.BuildRaidStorageKey("user-a", ArenaId, ArenaFingerprint);
        AssertEqual("nectorial-raid.toss.v1." + ArenaId + "." + ArenaFingerprint + ".user-a", key, "raid key names prefix, arena, fingerprint and user");
        Assert(!key.StartsWith(TossPlatformPolicy.StorageKeyPrefix, StringComparison.Ordinal), "raid key must never live under the solo prefix");
        Assert(key != TossPlatformPolicy.BuildRaidStorageKey("user-b", ArenaId, ArenaFingerprint), "another anonymous user gets another key");
        Assert(key != TossPlatformPolicy.BuildRaidStorageKey("user-a", "raid-01-v4", ArenaFingerprint), "another arena id gets another key");
        Assert(key != TossPlatformPolicy.BuildRaidStorageKey("user-a", ArenaId, "a0a60b8e"), "another fingerprint gets another key");
        AssertThrows(delegate { TossPlatformPolicy.BuildRaidStorageKey(" ", ArenaId, ArenaFingerprint); }, "blank identity must not create a shared raid key");
        AssertThrows(delegate { TossPlatformPolicy.BuildRaidStorageKey("user-a", "raid.v5", ArenaFingerprint); }, "dotted arena id would make keys ambiguous");
        AssertThrows(delegate { TossPlatformPolicy.BuildRaidStorageKey("user-a", ArenaId, string.Empty); }, "missing fingerprint must not create a key");
    }

    private static void CheckRaidPayloadCodec()
    {
        const string progress = "{\"SchemaVersion\":1,\"State\":{\"Actions\":3}}";
        const string best = "fm1.best-capsule";
        string payload = TossPlatformPolicy.FormatRaidPayload(ArenaId, ArenaFingerprint, best, progress);
        string parsedBest;
        string parsedProgress;
        string error;
        Assert(TossPlatformPolicy.TryParseRaidPayload(payload, ArenaId, ArenaFingerprint, out parsedBest, out parsedProgress, out error), "own payload must parse: " + error);
        AssertEqual(best, parsedBest, "best capsule survives in the same payload");
        AssertEqual(progress, parsedProgress, "progress survives in the same payload");

        string noBest = TossPlatformPolicy.FormatRaidPayload(ArenaId, ArenaFingerprint, null, progress);
        Assert(TossPlatformPolicy.TryParseRaidPayload(noBest, ArenaId, ArenaFingerprint, out parsedBest, out parsedProgress, out error), "a run without a best still parses");
        AssertEqual(string.Empty, parsedBest, "missing best is empty, not invented");

        ExpectParseError(string.Empty, "raid_toss_payload_empty");
        ExpectParseError(progress, "raid_toss_payload_version");
        ExpectParseError(payload.Replace("fm-raid-toss.v1", "fm-raid-toss.v2"), "raid_toss_payload_version");
        ExpectParseError(TossPlatformPolicy.FormatRaidPayload("raid-01-v4", ArenaFingerprint, best, progress), "raid_toss_payload_arena");
        ExpectParseError(TossPlatformPolicy.FormatRaidPayload(ArenaId, "a0a60b8e", best, progress), "raid_toss_payload_fingerprint");
        ExpectParseError(payload.Substring(0, payload.Length - 2), "raid_toss_payload_progress");
        ExpectParseError(payload + "\n{}", "raid_toss_payload_progress");
        ExpectParseError("fm-raid-toss.v1\n" + ArenaId + "\n" + ArenaFingerprint + "\n" + best, "raid_toss_payload_version");
        AssertThrows(delegate { TossPlatformPolicy.FormatRaidPayload(ArenaId, ArenaFingerprint, "a\nb", progress); }, "multi-line best must not be written");
        AssertThrows(delegate { TossPlatformPolicy.FormatRaidPayload(ArenaId, ArenaFingerprint, best, "not json"); }, "non-object progress must not be written");
    }

    private static void ExpectParseError(string payload, string expected)
    {
        string best;
        string progress;
        string error;
        Assert(!TossPlatformPolicy.TryParseRaidPayload(payload, ArenaId, ArenaFingerprint, out best, out progress, out error), "corrupt payload must not parse: " + expected);
        AssertEqual(expected, error, "parse error code");
        Assert(best == null && progress == null, "failed parse must not expose partial data");
    }

    // Mirrors RaidBootstrap's use of the shared coordinator (mocked native completions, not a device run):
    // manual save in flight, then a clear autosave carrying the new best; the clear payload must be the last one
    // written, stale completions must not change status, and only the newest completion reports "saved".
    private static void CheckRaidWriteOrdering()
    {
        var coordinator = new TossNativeSaveCoordinator();
        var nativeOrder = new System.Collections.Generic.List<string>();
        TossWriteRequest start;
        Assert(coordinator.Queue(new TossWriteRequest(1, "manual-progress", true), out start), "first write starts");
        nativeOrder.Add(start.Payload);
        Assert(!coordinator.Queue(new TossWriteRequest(2, "move-progress", false), out start), "second write waits, no overlap");
        Assert(!coordinator.Queue(new TossWriteRequest(3, "clear-progress+best", false), out start), "clear write waits, no overlap");
        AssertEqual("pending", coordinator.Status, "manual save is pending, not saved");
        Assert(coordinator.TimeoutManual(1), "UI timeout releases the waiting manual save");
        AssertEqual("failed", coordinator.Status, "timeout is visible");
        AssertEqual("storage_ui_timeout", coordinator.Error, "timeout code");

        TossNativeSaveCompletion completion;
        TossWriteRequest next;
        Assert(!coordinator.Complete(3, true, null, out completion, out next), "a completion for a write that is not active is ignored");
        Assert(coordinator.Complete(1, true, null, out completion, out next), "late native completion of the manual write is accepted");
        Assert(!completion.AffectsCurrentStatus, "late manual completion cannot report saved over newer progress");
        AssertEqual("failed", coordinator.Status, "stale success leaves the visible timeout");
        AssertEqual(3, next.Id, "only the newest queued payload is kept; the intermediate move is superseded");
        nativeOrder.Add(next.Payload);
        Assert(coordinator.Complete(3, true, null, out completion, out next), "clear write completes");
        Assert(completion.AffectsCurrentStatus, "newest write decides status");
        AssertEqual("saved", coordinator.Status, "saved only after the newest native write succeeded");
        AssertEqual(0, next.Id, "writer drains");
        AssertEqual("clear-progress+best", nativeOrder[nativeOrder.Count - 1], "final stored payload holds the clear and best");
        Assert(!coordinator.Complete(1, false, "storage_api_error", out completion, out next), "duplicate stale failure cannot regress saved");
        AssertEqual("saved", coordinator.Status, "status stays saved");
    }

    // Round 1 review R2: auto1 active -> manual2 queued -> auto3 replaces manual2 in the writer -> complete1 ->
    // complete3. The manual request's content landed in payload 3, so it must no longer be pending, and a later
    // auto4 must read as changed (not saved) until it completes.
    private static void CheckSupersededQueuedManual()
    {
        var coordinator = QueueSupersededManual();
        TossNativeSaveCompletion completion;
        TossWriteRequest next;
        Assert(coordinator.Complete(3, true, null, out completion, out next), "newest write completes");
        Assert(completion.AffectsCurrentStatus, "newest write decides status");
        Assert(!coordinator.ManualPending, "a manual request superseded by a completed newer write is no longer pending");
        AssertEqual(0, coordinator.PendingManualRequestId, "no stale pending manual id");
        AssertEqual("saved", coordinator.Status, "newest write succeeded");

        TossWriteRequest start;
        Assert(coordinator.Queue(new TossWriteRequest(4, "auto-4", false), out start), "auto4 starts");
        AssertEqual("changed", coordinator.Status, "auto4 in flight must not read as saved");
        Assert(!coordinator.TimeoutManual(2), "a late timeout for the superseded manual request is ignored");
        AssertEqual("changed", coordinator.Status, "ignored timeout leaves status");
        Assert(coordinator.Complete(4, true, null, out completion, out next), "auto4 completes");
        AssertEqual("saved", coordinator.Status, "saved only after auto4 succeeded");
    }

    private static void CheckSupersededQueuedManualFailure()
    {
        var coordinator = QueueSupersededManual();
        TossNativeSaveCompletion completion;
        TossWriteRequest next;
        Assert(coordinator.Complete(3, false, "storage_api_error", out completion, out next), "newest write completes with failure");
        Assert(!coordinator.ManualPending, "a failed newer write still ends the superseded manual wait");
        AssertEqual("failed", coordinator.Status, "failure is visible");
        AssertEqual("storage_api_error", coordinator.Error, "failure code");
    }

    private static TossNativeSaveCoordinator QueueSupersededManual()
    {
        var coordinator = new TossNativeSaveCoordinator();
        TossWriteRequest start;
        Assert(coordinator.Queue(new TossWriteRequest(1, "auto-1", false), out start), "auto1 starts");
        Assert(!coordinator.Queue(new TossWriteRequest(2, "manual-2", true), out start), "manual2 waits");
        AssertEqual("pending", coordinator.Status, "manual2 pending");
        Assert(!coordinator.Queue(new TossWriteRequest(3, "auto-3", false), out start), "auto3 replaces manual2 in the queue");
        TossNativeSaveCompletion completion;
        TossWriteRequest next;
        Assert(coordinator.Complete(1, true, null, out completion, out next), "auto1 completes");
        Assert(!completion.AffectsCurrentStatus, "auto1 is superseded");
        AssertEqual(3, next.Id, "auto3 starts next; manual2 is gone from the writer");
        return coordinator;
    }

    // Solo never queues an autosave while a manual save is pending; completing an earlier write must keep the
    // manual save pending exactly as before.
    private static void CheckManualStaysPendingBehindEarlierWrite()
    {
        var coordinator = new TossNativeSaveCoordinator();
        TossWriteRequest start;
        coordinator.Queue(new TossWriteRequest(1, "auto-1", false), out start);
        coordinator.Queue(new TossWriteRequest(2, "manual-2", true), out start);
        TossNativeSaveCompletion completion;
        TossWriteRequest next;
        Assert(coordinator.Complete(1, true, null, out completion, out next), "earlier write completes");
        Assert(coordinator.ManualPending, "manual save behind an earlier write stays pending");
        AssertEqual(2, coordinator.PendingManualRequestId, "pending manual id unchanged");
        AssertEqual("pending", coordinator.Status, "status stays pending");
        Assert(coordinator.Complete(2, true, null, out completion, out next), "manual completes");
        Assert(!coordinator.ManualPending, "manual completion releases");
        AssertEqual("saved", coordinator.Status, "manual saved");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message + " expected=" + expected + " actual=" + actual);
        }
    }

    private static void AssertEqual(int expected, int actual, string message)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(message + " expected=" + expected + " actual=" + actual);
        }
    }

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
