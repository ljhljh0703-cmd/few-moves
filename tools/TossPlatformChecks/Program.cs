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
