mergeInto(LibraryManager.library, {
  $NectorialCoopPersistenceBridge: {
    queuedRequestIds: [],
    active: false,
    manualSyncInFlight: false,
    timeoutMilliseconds: 8000,
    pollMilliseconds: 16,

    report: function (requestId, result) {
      try {
        SendMessage("CoopBootstrap", "OnWebGLPersistenceResult", String(requestId) + "|" + result);
      } catch (error) {
        if (typeof console !== "undefined" && console.warn) {
          console.warn("Coop persistence callback could not be delivered.");
        }
      }
    },

    unityMount: function () {
      if (typeof Module === "undefined" || !Module.__unityIdbfsMount) return null;
      return Module.__unityIdbfsMount.mount || null;
    },

    runNext: function () {
      var bridge = NectorialCoopPersistenceBridge;
      if (bridge.active || bridge.queuedRequestIds.length === 0) return;

      bridge.active = true;
      var requestId = bridge.queuedRequestIds.shift();
      var completed = false;
      var timeoutHandle = setTimeout(function () { finish("timeout"); }, bridge.timeoutMilliseconds);

      function finish(result) {
        if (completed) return;
        completed = true;
        clearTimeout(timeoutHandle);
        bridge.active = false;
        bridge.report(requestId, result);
        bridge.runNext();
      }

      function waitForAutomaticPersistence() {
        if (completed) return;
        if (bridge.manualSyncInFlight) {
          setTimeout(waitForAutomaticPersistence, bridge.pollMilliseconds);
          return;
        }

        var mount = bridge.unityMount();
        if (!mount) {
          finish("unavailable_mount");
          return;
        }
        if (typeof FS === "undefined" || !FS || typeof FS.syncfs !== "function") {
          finish("unavailable_fs");
          return;
        }
        if (mount.idbPersistState) {
          setTimeout(waitForAutomaticPersistence, bridge.pollMilliseconds);
          return;
        }

        bridge.manualSyncInFlight = true;
        try {
          FS.syncfs(false, function (error) {
            bridge.manualSyncInFlight = false;
            if (completed) {
              bridge.runNext();
              return;
            }
            finish(error ? "sync_error" : "ok");
          });
        } catch (error) {
          bridge.manualSyncInFlight = false;
          finish("sync_throw");
        }
      }

      waitForAutomaticPersistence();
    },

    request: function (requestId) {
      NectorialCoopPersistenceBridge.queuedRequestIds.push(requestId);
      NectorialCoopPersistenceBridge.runNext();
    }
  },

  NectorialCoopReportState: function (jsonPointer) {
    var json = UTF8ToString(jsonPointer);
    if (typeof window !== "undefined" && window.__nectorialCoop && window.__nectorialCoop.receiveState) {
      window.__nectorialCoop.receiveState(json);
    }
  },
  NectorialCoopPersistSave__deps: ["$NectorialCoopPersistenceBridge"],
  NectorialCoopPersistSave__sig: "vi",
  NectorialCoopPersistSave: function (requestId) {
    NectorialCoopPersistenceBridge.request(requestId);
  }
});
