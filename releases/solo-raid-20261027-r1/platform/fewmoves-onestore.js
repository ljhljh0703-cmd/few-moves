/*
 * Few Moves — ONE store H5 Game SDK bridge (classic script, first in <head>).
 *
 * Injected into already-built, verified pages by tools/prepare_onestore_web.py; the approved templates and
 * Unity builds are unchanged. Lifecycle per ONE docs (collected 2026-09-26, SDK v1.1.0):
 * createSDK -> on(pause/resume/exit) -> initializeAsync -> setLoadingProgress -> startGameAsync.
 * In an ordinary browser initializeAsync reports "Platform Not Supported"; the bridge then ignores the SDK's
 * browser-derived pause/resume/exit and stays inert.
 */
(function (root, factory) {
  "use strict";
  var api = factory();
  if (typeof module === "object" && module && module.exports) {
    module.exports = api;
  } else if (root && root.document) {
    api.install(root, root.document, function (url) { return import(url); });
  }
}(typeof window !== "undefined" ? window : this, function () {
  "use strict";

  var SDK_URL = "https://h5sdk.onestore.net/lib/v1.1.0/onestore-h5-sdk.min.js";
  var INIT_RETRY_DELAY_MS = 1000;
  // Terminal results and pending consent are never dismissed by the Android back key.
  var BACK_NEVER_CONSUMES = { "result-dialog": true, "clear-dialog": true, "consent-dialog": true };

  function install(win, doc, importSdk, options) {
    options = options || {};
    var state = {
      phase: "loading-sdk",   // loading-sdk | initializing | ready | unsupported | failed
      error: "",
      sdk: null,
      pageReady: false,
      started: false,
      startError: "",
      progress: -1,
      instance: null,
      paused: false,
      retried: false
    };
    var setTimer = options.setTimeout || (win.setTimeout ? win.setTimeout.bind(win) : setTimeout);

    function reason(error) {
      return error && typeof error.reason === "string" && error.reason ? error.reason : "unknown";
    }

    function clampPercent(fraction) {
      var value = Math.round(Number(fraction) * 100);
      if (!isFinite(value)) return 0;
      return value < 0 ? 0 : (value > 100 ? 100 : value);
    }

    function reportProgress(fraction) {
      var percent = clampPercent(fraction);
      if (percent <= state.progress) return;
      state.progress = percent;
      if (state.phase === "ready" && !state.started) state.sdk.setLoadingProgress(percent);
    }

    function startIfReady() {
      if (state.phase !== "ready" || !state.pageReady || state.started) return;
      state.started = true;
      if (state.progress < 100) {
        state.progress = 100;
        state.sdk.setLoadingProgress(100);
      }
      state.sdk.startGameAsync().catch(function (error) { state.startError = reason(error); });
    }

    // Unity keeps its own IndexedDB flush asynchronous; start it now so the last autosave is not lost.
    function flushSaves() {
      try {
        var module = state.instance && state.instance.Module;
        var fs = module && module.FS;
        if (fs && typeof fs.syncfs === "function") fs.syncfs(false, function () {});
      } catch (error) {}
    }

    function callLoop(name) {
      try {
        var module = state.instance && state.instance.Module;
        if (module && typeof module[name] === "function") module[name]();
      } catch (error) {}
    }

    // In an ordinary browser the SDK still maps visibilitychange/pagehide to pause/resume/exit
    // (wireBrowserLifecycle in SDK v1.1.0). Only the native app's events may touch Unity.
    function nativeHost() {
      return state.phase !== "unsupported";
    }

    function onPause() {
      if (!nativeHost()) return;
      state.paused = true;
      flushSaves();
      callLoop("pauseMainLoop");
    }

    function onResume() {
      if (!nativeHost() || !state.paused) return;
      state.paused = false;
      callLoop("resumeMainLoop");
    }

    function onExit() {
      if (!nativeHost()) return;
      flushSaves();
    }

    // Synchronous and exception-safe: the app waits at most 200 ms and treats anything but true as not consumed.
    function onBackPressed() {
      try {
        var open = doc.querySelector("dialog[open]");
        if (!open || BACK_NEVER_CONSUMES[open.id]) return false;
        var closer = open.querySelector('button[id$="-close"]');
        if (!closer || closer.disabled) return false;
        closer.click();
        return open.open !== true;
      } catch (error) {
        return false;
      }
    }

    function initialize() {
      state.phase = "initializing";
      return state.sdk.initializeAsync().then(function (info) {
        if (!info || info.err) {
          state.phase = "unsupported";
          state.error = info && info.err ? String(info.err) : "no_info";
          return;
        }
        state.phase = "ready";
        if (state.progress >= 0) state.sdk.setLoadingProgress(state.progress);
        startIfReady();
      }, function (error) {
        state.error = reason(error);
        if (state.retried) {
          state.phase = "failed";
          return;
        }
        state.retried = true;
        return new Promise(function (resolve) { setTimer(resolve, INIT_RETRY_DELAY_MS); }).then(initialize);
      });
    }

    function markPageReady() {
      if (state.pageReady) return;
      state.pageReady = true;
      startIfReady();
    }

    function wrapUnity(original) {
      function wrapped(canvas, config, onProgress) {
        var promise = original.call(this, canvas, config, function (value) {
          reportProgress(value);
          if (typeof onProgress === "function") onProgress(value);
        });
        if (promise && typeof promise.then === "function") {
          // A failed load still removes the native loading screen so the page's own error text is visible.
          promise.then(function (instance) {
            state.instance = instance;
            // A native pause that arrived while Unity was loading takes effect now; resume restarts the loop.
            if (state.paused) callLoop("pauseMainLoop");
            markPageReady();
          }, markPageReady);
        } else {
          markPageReady();
        }
        return promise;
      }
      wrapped.fewMovesOneStore = true;
      return wrapped;
    }

    function isLoaderScript(event) {
      var target = event && event.target;
      return !!target && String(target.tagName).toUpperCase() === "SCRIPT" && /\.loader\.js(?:[?#]|$)/.test(String(target.src || ""));
    }

    // loader.js declares createUnityInstance globally; wrap it after the loader runs and before the page's onload calls it.
    doc.addEventListener("load", function (event) {
      if (!isLoaderScript(event)) return;
      var current = win.createUnityInstance;
      if (typeof current === "function" && !current.fewMovesOneStore) win.createUnityInstance = wrapUnity(current);
    }, true);

    // If loader.js itself fails to load, the page shows its own error text; remove the native loading screen too.
    doc.addEventListener("error", function (event) {
      if (isLoaderScript(event)) markPageReady();
    }, true);

    // Pages without a Unity canvas (the portal) are ready as soon as their DOM is.
    function markDomReady() {
      if (!doc.getElementById("unity-canvas")) markPageReady();
    }
    if (doc.readyState === "loading") doc.addEventListener("DOMContentLoaded", markDomReady);
    else markDomReady();

    var loaded;
    try {
      loaded = Promise.resolve(importSdk(SDK_URL));
    } catch (error) {
      loaded = Promise.reject(error);
    }
    var done = loaded.then(function (sdkModule) {
      if (!sdkModule || typeof sdkModule.createSDK !== "function") throw { reason: "sdk_module_invalid" };
      var sdk = sdkModule.createSDK();
      state.sdk = sdk;
      sdk.on("pause", onPause);
      sdk.on("resume", onResume);
      sdk.on("exit", onExit);
      if (typeof sdk.onBackPressed === "function") sdk.onBackPressed(onBackPressed);
      return initialize();
    }).catch(function (error) {
      if (state.phase === "loading-sdk") {
        state.phase = "failed";
        state.error = error && error.reason ? error.reason : "sdk_import_failed";
      }
    });

    var handle = {
      status: function () {
        return { phase: state.phase, error: state.error, pageReady: state.pageReady, started: state.started, startError: state.startError, progress: state.progress, paused: state.paused };
      },
      ready: done,
      onBackPressed: onBackPressed
    };
    win.__fewMovesOneStore = handle;
    return handle;
  }

  return { install: install, SDK_URL: SDK_URL };
}));
