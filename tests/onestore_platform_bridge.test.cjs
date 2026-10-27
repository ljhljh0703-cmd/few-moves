const assert = require("node:assert/strict");
const path = require("node:path");

const bridge = require(path.join(__dirname, "..", "web", "platforms", "onestore", "fewmoves-onestore.js"));

function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
async function settle() { for (let i = 0; i < 6; i++) await Promise.resolve(); await new Promise(r => setImmediate(r)); }

function fakeSdk(initResults) {
  const calls = [];
  const listeners = {};
  let back = null;
  const sdk = {
    calls, listeners,
    on(name, fn) { calls.push("on:" + name); listeners[name] = fn; },
    onBackPressed(fn) { calls.push("onBackPressed"); back = fn; },
    back() { return back(); },
    initializeAsync() { calls.push("initializeAsync"); const next = initResults.shift(); return next instanceof Error || (next && next.reason && !next.playerId) ? Promise.reject(next) : Promise.resolve(next); },
    setLoadingProgress(n) { calls.push("progress:" + n); },
    startGameAsync() { calls.push("startGameAsync"); return Promise.resolve(); }
  };
  return sdk;
}

function dialog(id, closeButton) {
  const d = { id, open: true, tagName: "DIALOG", closer: null, querySelector(sel) { return sel === 'button[id$="-close"]' ? this.closer : null; } };
  if (closeButton) d.closer = { id: id.replace("-dialog", "-close"), disabled: false, clicks: 0, click() { this.clicks++; d.open = false; } };
  return d;
}

function page({ unity = true, readyState = "complete", sdkModule, importThrows = false, initResults = [{ playerId: "p", locale: "ko-KR" }] } = {}) {
  const docListeners = {};
  const doc = {
    readyState,
    openDialog: null,
    addEventListener(name, fn, capture) { (docListeners[name + (capture ? ":capture" : "")] ||= []).push(fn); },
    getElementById(id) { return id === "unity-canvas" && unity ? {} : null; },
    querySelector(sel) { return sel === "dialog[open]" && this.openDialog && this.openDialog.open ? this.openDialog : null; }
  };
  const win = {};
  const sdk = fakeSdk(initResults);
  const timers = [];
  const imports = [];
  const importSdk = url => { imports.push(url); if (importThrows) throw new Error("offline"); return sdkModule === undefined ? Promise.resolve({ createSDK: () => sdk }) : sdkModule(); };
  const handle = bridge.install(win, doc, importSdk, { setTimeout: (fn, ms) => { timers.push(ms); fn(); } });
  return {
    win, doc, sdk, handle, timers, imports,
    fireLoaderLoad(src = "Build/abc.loader.js") { (docListeners["load:capture"] || []).forEach(fn => fn({ target: { tagName: "SCRIPT", src } })); },
    fireDomReady() { (docListeners.DOMContentLoaded || []).forEach(fn => fn()); }
  };
}

function unityLoader(outcome) {
  const d = deferred();
  const seen = [];
  function createUnityInstance(canvas, config, onProgress) { seen.push({ canvas, config, onProgress }); return d.promise; }
  return { createUnityInstance, seen, d, outcome };
}

(async function run() {
  assert.equal(bridge.SDK_URL, "https://h5sdk.onestore.net/lib/v1.1.0/onestore-h5-sdk.min.js", "SDK is pinned to the documented CDN version");

  // Happy path: listeners before initializeAsync, progress forwarded, one start only after the Unity instance resolves.
  const p = page();
  const loader = unityLoader();
  p.win.createUnityInstance = loader.createUnityInstance;
  p.fireLoaderLoad();
  assert.equal(p.win.createUnityInstance.fewMovesOneStore, true, "loader function is wrapped after the loader script runs");
  const pageProgress = [];
  const returned = p.win.createUnityInstance("canvas", { productName: "x" }, v => pageProgress.push(v));
  assert.equal(returned, loader.d.promise, "the page keeps the original Unity promise");
  await settle();
  assert.deepEqual(p.sdk.calls.slice(0, 5), ["on:pause", "on:resume", "on:exit", "onBackPressed", "initializeAsync"], "all listeners are registered before initializeAsync");
  loader.seen[0].onProgress(0.25); loader.seen[0].onProgress(0.2); loader.seen[0].onProgress(0.9);
  assert.deepEqual(pageProgress, [0.25, 0.2, 0.9], "the page still receives every Unity progress callback");
  assert.deepEqual(p.sdk.calls.filter(c => c.startsWith("progress:")), ["progress:25", "progress:90"], "progress is reported in percent and never goes backwards");
  assert.equal(p.sdk.calls.includes("startGameAsync"), false, "loading screen stays until Unity is ready");
  const instance = { Module: { FS: { syncs: 0, syncfs(populate, cb) { this.syncs++; assert.equal(populate, false); cb(); } }, paused: 0, resumed: 0, pauseMainLoop() { this.paused++; }, resumeMainLoop() { this.resumed++; } } };
  loader.d.resolve(instance);
  await settle();
  assert.deepEqual(p.sdk.calls.slice(-2), ["progress:100", "startGameAsync"], "ready reports 100 then starts once");
  assert.equal(p.handle.status().started, true);

  // pause / resume / exit.
  p.sdk.listeners.pause({ reason: "background" });
  assert.equal(instance.Module.FS.syncs, 1, "pause starts the save flush");
  assert.equal(instance.Module.paused, 1);
  p.sdk.listeners.resume({});
  p.sdk.listeners.resume({});
  assert.equal(instance.Module.resumed, 1, "resume only after a pause, once");
  p.sdk.listeners.exit({});
  assert.equal(instance.Module.FS.syncs, 2, "exit starts the save flush synchronously");

  // Back key: closes ordinary dialogs through their own close button; never results, clear or consent.
  assert.equal(p.sdk.back(), false, "no dialog: app shows its exit confirmation");
  p.doc.openDialog = dialog("record-dialog", true);
  assert.equal(p.sdk.back(), true, "record dialog is closed by its own close button");
  assert.equal(p.doc.openDialog.closer.clicks, 1);
  for (const id of ["result-dialog", "clear-dialog", "consent-dialog"]) {
    p.doc.openDialog = dialog(id, true);
    assert.equal(p.sdk.back(), false, id + " is not dismissed by back");
    assert.equal(p.doc.openDialog.closer.clicks, 0);
  }
  p.doc.openDialog = dialog("menu-dialog", false);
  assert.equal(p.sdk.back(), false, "a dialog without an explicit close button is not consumed");
  p.doc.openDialog = { id: "help-dialog", open: true, querySelector() { throw new Error("boom"); } };
  assert.equal(p.sdk.back(), false, "exceptions count as not consumed");

  // Unity ready before initialization finishes: start waits for init, still once.
  const early = page({ initResults: [] });
  const gate = deferred();
  early.sdk.initializeAsync = () => { early.sdk.calls.push("initializeAsync"); return gate.promise; };
  const l2 = unityLoader();
  early.win.createUnityInstance = l2.createUnityInstance;
  early.fireLoaderLoad();
  early.win.createUnityInstance("c", {});
  l2.seen[0].onProgress(0.5);
  l2.d.resolve({});
  await settle();
  assert.equal(early.sdk.calls.includes("startGameAsync"), false, "no start before initializeAsync resolves");
  gate.resolve({ playerId: "p" });
  await settle();
  assert.deepEqual(early.sdk.calls.slice(-3), ["progress:50", "progress:100", "startGameAsync"], "queued progress is sent after init, then start");

  // Ordinary browser: Platform Not Supported leaves the page untouched.
  const web = page({ initResults: [{ err: "Platform Not Supported" }] });
  const l3 = unityLoader();
  web.win.createUnityInstance = l3.createUnityInstance;
  web.fireLoaderLoad();
  web.win.createUnityInstance("c", {});
  l3.seen[0].onProgress(1);
  l3.d.resolve({});
  await settle();
  assert.equal(web.handle.status().phase, "unsupported");
  assert.equal(web.sdk.calls.some(c => c.startsWith("progress:") || c === "startGameAsync"), false, "no SDK calls after unsupported");

  // Init failure: one retry, then failed without start.
  const flaky = page({ initResults: [{ reason: "timeout" }, { playerId: "p" }] });
  await settle();
  assert.equal(flaky.timers.length, 1, "one delayed retry");
  assert.equal(flaky.handle.status().phase, "ready", "retry succeeds");
  const broken = page({ initResults: [{ reason: "timeout" }, { reason: "timeout" }] });
  await settle();
  assert.equal(broken.handle.status().phase, "failed");
  assert.equal(broken.handle.status().error, "timeout");
  assert.equal(broken.sdk.calls.filter(c => c === "initializeAsync").length, 2, "no endless retry");

  // Unity load failure still removes the native loading screen so the page error is visible.
  const bad = page();
  const l4 = unityLoader();
  bad.win.createUnityInstance = l4.createUnityInstance;
  bad.fireLoaderLoad();
  bad.win.createUnityInstance("c", {}).catch(() => {});
  l4.d.reject(new Error("wasm failed"));
  await settle();
  assert.equal(bad.sdk.calls.includes("startGameAsync"), true);

  // Portal (no Unity canvas): starts as soon as DOM is ready; loading DOM waits for DOMContentLoaded.
  const portal = page({ unity: false, readyState: "loading" });
  await settle();
  assert.equal(portal.sdk.calls.includes("startGameAsync"), false);
  portal.fireDomReady();
  await settle();
  assert.equal(portal.sdk.calls.includes("startGameAsync"), true, "portal starts after DOMContentLoaded");

  // Non-loader scripts and double wrapping are ignored.
  const other = page();
  const l5 = unityLoader();
  other.win.createUnityInstance = l5.createUnityInstance;
  other.fireLoaderLoad("Build/framework.js");
  assert.equal(other.win.createUnityInstance, l5.createUnityInstance, "only *.loader.js triggers wrapping");
  other.fireLoaderLoad(); const once = other.win.createUnityInstance; other.fireLoaderLoad();
  assert.equal(other.win.createUnityInstance, once, "wrapped once");

  // SDK unavailable (offline CDN / import error / bad module): page keeps working, bridge reports failed.
  for (const opts of [{ importThrows: true }, { sdkModule: () => Promise.reject(new Error("net")) }, { sdkModule: () => Promise.resolve({}) }]) {
    const off = page(opts);
    const l6 = unityLoader();
    off.win.createUnityInstance = l6.createUnityInstance;
    off.fireLoaderLoad();
    assert.equal(off.win.createUnityInstance("c", {}), l6.d.promise, "Unity still loads without the SDK");
    await settle();
    assert.equal(off.handle.status().phase, "failed");
  }

  console.log("ONE store platform bridge checks passed");
})().catch(error => { console.error(error); process.exitCode = 1; });
