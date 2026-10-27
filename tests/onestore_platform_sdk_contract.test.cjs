// Contract check of the ONE store bridge against the real ONE H5 SDK module (not a fake SDK).
// The SDK file is not vendored: pass its path in FEWMOVES_ONESTORE_SDK, or keep the fetched evidence copy.
// The pinned hash proves which official build was exercised. Native hosts are emulated (iOS message handler).
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const ROOT = path.resolve(__dirname, "..");
const SDK_SHA256 = "951e5a5ffb1fc20c467681bb9dfceebf3fcfc6f111765c2e93413658d4df7881"; // https://h5sdk.onestore.net/lib/v1.1.0/onestore-h5-sdk.min.js
const SDK_PATH = process.env.FEWMOVES_ONESTORE_SDK || path.join(ROOT, "Builds", "StoreFinish-2026-10-27", "platform", "official-docs", "sdk-v1.1.0.min.js");
const bridge = require(path.join(ROOT, "web", "platforms", "onestore", "fewmoves-onestore.js"));

if (!fs.existsSync(SDK_PATH)) {
  console.error("ONE SDK copy not found at " + SDK_PATH + " (set FEWMOVES_ONESTORE_SDK)");
  process.exit(1);
}
assert.equal(crypto.createHash("sha256").update(fs.readFileSync(SDK_PATH)).digest("hex"), SDK_SHA256, "exercised SDK must be the pinned official v1.1.0 build");
assert.equal(bridge.SDK_URL, "https://h5sdk.onestore.net/lib/v1.1.0/onestore-h5-sdk.min.js");

let scenario = 0;
async function settle(ms = 0) { for (let i = 0; i < 6; i++) await Promise.resolve(); await new Promise(r => setTimeout(r, ms)); }

function environment({ ios = false } = {}) {
  const docListeners = {}, winListeners = {};
  const doc = {
    readyState: "complete", visibilityState: "visible", referrer: "", openDialog: null,
    addEventListener(name, fn) { (docListeners[name] ||= []).push(fn); },
    getElementById(id) { return id === "unity-canvas" ? {} : null; },
    querySelector(sel) { return sel === "dialog[open]" && this.openDialog && this.openDialog.open ? this.openDialog : null; },
    fire(name, event) { (docListeners[name] || []).forEach(fn => fn(event || {})); }
  };
  const win = { addEventListener(name, fn) { (winListeners[name] ||= []).push(fn); }, fire(name) { (winListeners[name] || []).forEach(fn => fn({})); } };
  win.parent = win; win.self = win;
  const sent = [];
  if (ios) win.webkit = { messageHandlers: { webGameBridge: { postMessage(json) { sent.push(JSON.parse(json)); } } } };
  globalThis.window = win; globalThis.document = doc;
  const unity = { syncs: 0, paused: 0, resumed: 0 };
  const instance = { Module: { FS: { syncfs(populate, cb) { unity.syncs++; cb && cb(); } }, pauseMainLoop() { unity.paused++; }, resumeMainLoop() { unity.resumed++; } } };
  const sdkUrl = pathToFileURL(SDK_PATH).href + "?scenario=" + (++scenario); // fresh SDK singleton per scenario
  const handle = bridge.install(win, doc, () => import(sdkUrl));
  let resolveUnity;
  const loading = new Promise(r => { resolveUnity = r; });
  win.createUnityInstance = function (canvas, config, onProgress) { win.lastProgress = onProgress; return loading; };
  (docListeners["load"] || []).forEach(fn => fn({ target: { tagName: "SCRIPT", src: "Build/x.loader.js" } }));
  const receive = msg => win.__ONEstoreH5Receive(JSON.stringify(msg));
  return { win, doc, handle, sent, unity, instance, receive, resolveUnity: () => resolveUnity(instance) };
}

function dialog(id) {
  const d = { id, open: true, closer: { disabled: false, click() { d.open = false; } }, querySelector(sel) { return sel === 'button[id$="-close"]' ? this.closer : null; } };
  return d;
}

(async function run() {
  // 1. Ordinary browser: the SDK reports Platform Not Supported, and still maps visibilitychange/pagehide to
  //    pause/resume/exit. The bridge must stay inert there: no Unity loop or storage calls from page visibility.
  const web = environment();
  web.win.createUnityInstance("c", {}, () => {});
  web.resolveUnity();
  await web.handle.ready; await settle();
  assert.equal(web.handle.status().phase, "unsupported");
  assert.equal(web.handle.status().started, false, "no start call in a plain browser");
  web.doc.visibilityState = "hidden"; web.doc.fire("visibilitychange");
  web.doc.visibilityState = "visible"; web.doc.fire("visibilitychange");
  web.win.fire("pagehide");
  assert.deepEqual({ paused: web.unity.paused, resumed: web.unity.resumed, syncs: web.unity.syncs }, { paused: 0, resumed: 0, syncs: 0 },
    "plain browser tab switches and page hide never touch the Unity loop or storage");

  // 2. Emulated ONE app (iOS message handler): full lifecycle over the real SDK protocol.
  const app = environment({ ios: true });
  await settle();
  assert.equal(app.sent[0].action, "core.getConfig", "first native message is the getConfig handshake");
  app.win.createUnityInstance("c", {}, () => {});
  app.win.lastProgress(0.4);
  app.receive({ requestId: app.sent[0].requestId, success: true, data: { playerId: "p-1", locale: "ko-KR", ringerSilent: false, safeArea: { top: 0, bottom: 0, left: 0, right: 0 } } });
  await app.handle.ready; await settle();
  assert.equal(app.handle.status().phase, "ready");
  const progress = app.sent.filter(m => m.action === "core.loadingProgress").map(m => m.params.progress);
  assert.deepEqual(progress, [40], "queued Unity progress is reported after init");
  assert.equal(app.sent.some(m => m.action === "core.ready"), false, "loading screen stays until Unity is ready");
  app.win.lastProgress(0.9);
  app.resolveUnity();
  await settle();
  const ready = app.sent.find(m => m.action === "core.ready");
  assert.ok(ready && ready.requestId, "startGameAsync sends core.ready once Unity resolves");
  assert.deepEqual(app.sent.filter(m => m.action === "core.loadingProgress").map(m => m.params.progress), [40, 90, 100]);
  app.receive({ requestId: ready.requestId, success: true, data: {} });
  await settle();
  assert.equal(app.handle.status().started, true);
  assert.equal(app.handle.status().startError, "");

  app.receive({ event: "lifecycle.pause", data: { reason: "background" } });
  assert.deepEqual({ paused: app.unity.paused, syncs: app.unity.syncs }, { paused: 1, syncs: 1 }, "native pause stops the loop and starts the save flush");
  app.receive({ event: "lifecycle.resume", data: {} });
  assert.equal(app.unity.resumed, 1, "native resume restarts the loop");

  app.doc.openDialog = dialog("record-dialog");
  app.receive({ event: "lifecycle.backPressed", data: { seq: 1 } });
  app.doc.openDialog = dialog("result-dialog");
  app.receive({ event: "lifecycle.backPressed", data: { seq: 2 } });
  app.doc.openDialog = null;
  app.receive({ event: "lifecycle.backPressed", data: { seq: 3 } });
  assert.deepEqual(app.sent.filter(m => m.action === "core.backConsumed").map(m => m.params), [
    { seq: 1, consumed: true }, { seq: 2, consumed: false }, { seq: 3, consumed: false }
  ], "back is answered synchronously: ordinary dialog closed, result and no dialog left to the app");

  const before = app.unity.syncs;
  app.receive({ event: "lifecycle.destroy", data: {} });
  assert.equal(app.unity.syncs, before + 1, "destroy (exit) starts the save flush synchronously");
  await settle(450);
  assert.equal(app.sent.some(m => m.action === "core.quit"), false, "after destroy the SDK must not send quit (restart safety)");

  // 3. Native start failure is recorded, not thrown into the page.
  const failing = environment({ ios: true });
  await settle();
  failing.receive({ requestId: failing.sent[0].requestId, success: true, data: { playerId: "p" } });
  failing.win.createUnityInstance("c", {}, () => {});
  failing.resolveUnity();
  await failing.handle.ready; await settle();
  const readyReq = failing.sent.find(m => m.action === "core.ready");
  failing.receive({ requestId: readyReq.requestId, success: false, data: { reason: "internal_error" } });
  await settle();
  assert.equal(failing.handle.status().startError, "internal_error");

  // 4. Native pause while Unity is still loading (independent review round 1): the pause must be applied to the
  //    instance once it exists, while the loading screen is still dismissed; resume then restarts the loop.
  const early = environment({ ios: true });
  await settle();
  early.receive({ requestId: early.sent[0].requestId, success: true, data: { playerId: "p" } });
  await early.handle.ready;
  early.win.createUnityInstance("c", {}, () => {});
  early.receive({ event: "lifecycle.pause", data: { reason: "background" } });
  assert.equal(early.handle.status().paused, true);
  early.resolveUnity();
  await settle();
  assert.equal(early.unity.paused, 1, "a pause received during loading is applied when the Unity instance arrives");
  assert.equal(early.unity.syncs, 0, "nothing to flush before the instance exists");
  const earlyReady = early.sent.find(m => m.action === "core.ready");
  assert.ok(earlyReady, "the loading screen is still dismissed");
  early.receive({ requestId: earlyReady.requestId, success: true, data: {} });
  early.receive({ event: "lifecycle.resume", data: {} });
  assert.equal(early.unity.resumed, 1, "resume after a deferred pause restarts the loop");

  // 5. loader.js network error (independent review round 1): the page shows its own error text, so the native
  //    loading overlay must be removed. Errors from unrelated scripts do not count.
  const broken = environment({ ios: true });
  await settle();
  broken.receive({ requestId: broken.sent[0].requestId, success: true, data: { playerId: "p" } });
  await broken.handle.ready;
  broken.doc.fire("error", { target: { tagName: "SCRIPT", src: "Build/framework.js" } });
  broken.doc.fire("error", { target: { tagName: "IMG", src: "Build/x.loader.js" } });
  await settle();
  assert.equal(broken.sent.some(m => m.action === "core.ready"), false, "unrelated errors do not dismiss the loading screen");
  broken.doc.fire("error", { target: { tagName: "SCRIPT", src: "Build/x.loader.js" } });
  await settle();
  assert.equal(broken.handle.status().pageReady, true);
  assert.equal(broken.sent.filter(m => m.action === "core.ready").length, 1, "a loader script error dismisses the loading screen once");

  console.log("ONE store SDK contract checks passed (real SDK v1.1.0, emulated host)");
})().catch(error => { console.error(error); process.exitCode = 1; });
