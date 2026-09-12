const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const template = readFileSync(path.join(root, "Assets/WebGLTemplates/Raid/index.html"), "utf8");
const boardView = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Raid/RaidBoardView.cs"), "utf8");
const bridge = readFileSync(path.join(root, "Assets/Plugins/WebGL/RaidState.jslib"), "utf8");
const build = readFileSync(path.join(root, "Assets/Editor/RaidBuild.cs"), "utf8");

assert.match(template, /data-raid-action="Slide" data-direction="Up"/);
assert.match(template, /id="record-dialog"/);
assert.match(template, /id="record-close"/);
assert.match(template, /min-height:48px/);
assert.match(template, /width:56px; height:56px/);
assert.match(template, /16×16 레이드 보드/);
const headScale = boardView.match(/new Vector2\(index == 0 \? ([0-9.]+)f : 0\.72f, index == 0 \? ([0-9.]+)f : 0\.72f\)/);
const headOutlineScale = boardView.match(/Snake Head Outline", WallRim, new Vector2\(([0-9.]+)f, ([0-9.]+)f\)/);
assert.ok(headScale && headScale[1] === headScale[2], "raid head fill remains a symmetric square");
assert.ok(headOutlineScale && headOutlineScale[1] === headOutlineScale[2], "raid head outline remains a symmetric square");
const headAxisWidth = Number(headScale[1]) * Math.SQRT2;
const headOutlineAxisWidth = Number(headOutlineScale[1]) * Math.SQRT2;
assert.ok(headAxisWidth <= 1, `raid head fill rotated bounds stay within one cell: ${headAxisWidth}`);
assert.ok(headOutlineAxisWidth <= 1, `raid head outline rotated bounds stay within one cell: ${headOutlineAxisWidth}`);
assert.ok(headOutlineAxisWidth > headAxisWidth, "raid head outline remains visible around the fill");
assert.match(template, /href="\.\.\/index\.html"/);
assert.doesNotMatch(template, /snake\.advance|enemy\.move|Math\.random|leaderboard|랭킹/);
assert.match(bridge, /NectorialRaidReportState/);
assert.doesNotMatch(bridge, /RaidRules|RaidSession|RaidSolver/);
assert.match(build, /RaidSerializationChecks\.Run\(\);/);

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

function makeElement(documentObject, id, dataset = {}) {
  return {
    id, dataset, hidden: false, disabled: false, open: false, textContent: "", innerHTML: "", value: "", style: {}, tagName: id.includes("fallback") ? "INPUT" : "BUTTON", listeners: {}, children: [],
    addEventListener(name, handler) { this.listeners[name] = handler; },
    setAttribute(name, value) { if (name === "open") this.open = true; this[name] = value; },
    removeAttribute(name) { if (name === "open") this.open = false; delete this[name]; },
    showModal() { this.open = true; }, close() { this.open = false; },
    focus() { documentObject.activeElement = this; }, select() { this.selected = true; },
    getBoundingClientRect() { return { width: 320, height: 320 }; }
  };
}

function summary(actions, hits, definitionId = "raid-01-v3", fingerprint = "raid-fp") {
  return { modeId: "raid-v1", definitionId, rulesVersion: "raid-rules-v1", contentVersion: "raid-content-v1", definitionFingerprint: fingerprint, statusCode: "Cleared", effectiveActionCount: actions, logicalActionCount: actions, hits };
}

function observation(status = "Playing", overrides = {}) {
  return Object.assign({
    initialized: true, inputEnabled: status === "Playing" || status === "Armed", transitioning: false, statusCode: status,
    actions: 2, hits: 0, shieldCharges: 1, magnetStepsRemaining: 0, slowStepsRemaining: 0, tailCount: 0, tailTarget: 3,
    activeDefinitionId: "raid-01-v3", selectedDefinitionId: "raid-01-v3", recordStatus: "idle", recordCapsule: "", hasMine: false, mine: {}, hasShared: false, shared: {}, sharedRecordRequestId: "", recordError: "", message: "실제 상태", stateFingerprint: "state-1"
  }, overrides);
}

function createHarness(hash = "#record=fm1.shared") {
  const documentObject = { activeElement: null, listeners: {}, loader: null };
  const ids = ["unity-canvas","feedback","turn-label","player-state","snake-state","tail-count","shield-count","magnet-count","slow-count","result-dialog","result-title","result-copy","result-record","result-restart","restart-top","save-button","record-dialog","record-top","record-note","record-get","record-share","record-challenge","record-fallback","record-close","mine-value","mine-meta","shared-value","shared-meta","record-compare"];
  const elements = Object.fromEntries(ids.map(id => [id, makeElement(documentObject, id)]));
  elements["result-dialog"].tagName = "DIALOG"; elements["record-dialog"].tagName = "DIALOG";
  const actions = [makeElement(documentObject,"up",{raidAction:"Slide",direction:"Up"}),makeElement(documentObject,"left",{raidAction:"Slide",direction:"Left"}),makeElement(documentObject,"right",{raidAction:"Slide",direction:"Right"}),makeElement(documentObject,"down",{raidAction:"Slide",direction:"Down"}),elements["save-button"],elements["result-restart"]];
  elements["save-button"].dataset = { raidAction:"Save" }; elements["result-restart"].dataset = { raidAction:"Restart" };
  const boardStage = makeElement(documentObject, "board-stage"); boardStage.tagName = "SECTION";
  documentObject.body = { appendChild(node) { documentObject.loader = node; } };
  documentObject.querySelector = selector => selector === ".board-stage" ? boardStage : selector === "dialog[open]" ? [elements["result-dialog"],elements["record-dialog"]].find(item => item.open) || null : selector.startsWith("#") ? elements[selector.slice(1)] : null;
  documentObject.querySelectorAll = selector => selector === "button[data-raid-action]" ? actions : [];
  documentObject.createElement = () => makeElement(documentObject, "loader");
  documentObject.addEventListener = (name, handler) => { documentObject.listeners[name] = handler; };
  const sent = [], unityReady = deferred(), clipboardCalls = [];
  const windowObject = { listeners:{}, addEventListener(name,handler){ this.listeners[name]=handler; } };
  const sandbox = { window:windowObject, document:documentObject, ResizeObserver:undefined, location:{ origin:"https://game.test", pathname:"/content/raid/index.html", search:"?invite=DROP", hash }, navigator:{ clipboard:{ writeText(value){ const item=deferred(); clipboardCalls.push({value,item}); return item.promise; } } }, setTimeout(){return 1;}, clearTimeout(){}, console, JSON, Math, Number, String,
    createUnityInstance(_canvas,_config,onProgress){ sandbox.progress=onProgress; return unityReady.promise; }
  };
  const script = template.slice(template.indexOf("<script>")+8, template.lastIndexOf("</script>"));
  vm.runInNewContext(script, sandbox, { filename:"Raid/index.html" });
  const instance = { SendMessage(name, method, payload){ sent.push({name,method,payload:JSON.parse(payload)}); } };
  return { sandbox, documentObject, elements, actions, sent, unityReady, instance, clipboardCalls };
}

async function settle() { await Promise.resolve(); await Promise.resolve(); await new Promise(resolve => setImmediate(resolve)); }

(async function run(){
  const h = createHarness();
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation()));
  assert.equal(h.sent.length, 0, "initial observation before Unity instance defers the hash import");
  h.documentObject.loader.onload();
  h.sandbox.progress(0.5);
  assert.equal(h.elements.feedback.textContent, "실제 상태", "loading progress cannot overwrite an observed game message");
  h.unityReady.resolve(h.instance); await settle();
  const firstRequestId = h.sent.at(-1).payload.requestId;
  assert.deepEqual(h.sent.at(-1).payload, { kind:"LoadSharedRecord", capsule:"fm1.shared", requestId:firstRequestId }, "hash reaches C# with a bounded correlation ID after instance and initialized observation");
  assert.match(firstRequestId,/^record-hash-\d+$/); assert.ok(firstRequestId.length<=96);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing",{sharedRecordRequestId:firstRequestId})));
  assert.equal(h.sent.filter(item => item.payload.kind === "LoadSharedRecord").length, 1, "one hash is imported exactly once");

  h.elements["record-top"].listeners.click();
  assert.equal(h.elements["record-dialog"].open, true, "record opens in its own dialog");
  assert.equal(h.elements["mine-value"].textContent, "기록 없음", "default objects are not shown as zero records");
  const exact = observation("Cleared", { actions:14, hits:3, recordStatus:"ready", recordCapsule:"fm1.mine", hasMine:true, mine:summary(11,2), hasShared:true, shared:summary(13,4), sharedRecordRequestId:firstRequestId, stateFingerprint:"clear-1" });
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(exact));
  assert.equal(h.elements["mine-value"].textContent, "11수");
  assert.equal(h.elements["shared-value"].textContent, "13수");
  assert.match(h.elements["record-compare"].textContent, /2수 적습니다/);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(Object.assign({}, exact, { shared:summary(9,1,"raid-other","other-fp") })));
  assert.equal(h.elements["record-compare"].textContent, "같은 판의 기록만 비교할 수 있습니다.", "different record identities are never compared");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(exact));
  assert.equal(h.elements["result-dialog"].open, false, "result does not cover an open record dialog");
  const beforeClose = h.sent.length;
  h.elements["record-close"].listeners.click();
  assert.equal(h.elements["record-dialog"].open, false);
  assert.equal(h.sent.length, beforeClose, "closing the record dialog never restarts the game");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(exact));
  assert.equal(h.elements["result-dialog"].open, false, "closing record does not auto-reopen the same result");

  h.elements["record-top"].listeners.click();
  h.elements["record-share"].listeners.click();
  assert.equal(h.clipboardCalls[0].value, "https://game.test/content/raid/index.html#record=fm1.mine", "record URL drops invitation/query data");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(Object.assign({}, exact, { recordCapsule:"fm1.new" })));
  h.clipboardCalls[0].item.resolve(); await settle();
  assert.notEqual(h.elements["record-note"].textContent, "기록 링크를 복사했습니다.", "stale clipboard completion is ignored");
  h.elements["record-share"].listeners.click(); h.clipboardCalls[1].item.reject(new Error("denied")); await settle();
  assert.equal(h.elements["record-fallback"].hidden, false);
  assert.equal(h.elements["record-fallback"].value, "https://game.test/content/raid/index.html#record=fm1.new");
  assert.match(h.elements["record-note"].textContent, /자동 복사가 되지 않았습니다/);
  h.elements["record-share"].listeners.click(); h.clipboardCalls[2].item.resolve(); await settle();
  assert.equal(h.elements["record-fallback"].hidden, false, "clipboard success also leaves a selectable link");
  assert.equal(h.elements["record-fallback"].value, "https://game.test/content/raid/index.html#record=fm1.new");
  assert.equal(h.elements["record-note"].textContent, "기록 링크를 복사했습니다.");

  const beforeKey = h.sent.length;
  h.documentObject.listeners.keydown({ key:"ArrowUp", repeat:false, ctrlKey:false, metaKey:false, altKey:false, preventDefault(){} });
  assert.equal(h.sent.length, beforeKey, "keyboard movement is blocked while a dialog is open");
  h.elements["record-close"].listeners.click();
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation()));
  h.documentObject.activeElement = null;
  h.documentObject.listeners.keydown({ key:"ArrowUp", repeat:false, ctrlKey:false, metaKey:false, altKey:false, preventDefault(){} });
  assert.equal(h.sent.at(-1).payload.kind, "Slide");

  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(exact)); h.elements["record-top"].listeners.click();
  h.elements["record-challenge"].listeners.click();
  assert.deepEqual(h.sent.slice(-2).map(item => item.payload.kind), ["Challenge","Restart"], "challenge requires the explicit challenge-and-restart button");
  const reverse = createHarness("#record=fm1.reverse"); reverse.documentObject.loader.onload(); reverse.unityReady.resolve(reverse.instance); await settle();
  assert.equal(reverse.sent.length, 0, "instance alone cannot import before an initialized observation");
  reverse.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation()));
  assert.equal(reverse.sent.at(-1).payload.kind,"LoadSharedRecord"); assert.equal(reverse.sent.at(-1).payload.capsule,"fm1.reverse"); assert.match(reverse.sent.at(-1).payload.requestId,/^record-hash-\d+$/);

  const route = createHarness("#record=fm1.A");
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation())); route.documentObject.loader.onload(); route.unityReady.resolve(route.instance); await settle();
  const requestA=route.sent.at(-1).payload.requestId;
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", {recordStatus:"ready",hasShared:true,shared:summary(13,2),sharedRecordRequestId:requestA})));
  route.elements["record-top"].listeners.click();
  assert.equal(route.elements["shared-value"].textContent,"13수");
  route.sandbox.location.hash="#record=fm1.B"; route.sandbox.window.listeners.hashchange();
  assert.equal(route.elements["shared-value"].textContent,"확인 중","new hash immediately hides the previous shared result");
  assert.equal(route.elements["record-compare"].textContent,"새 링크의 기록을 확인하고 있습니다.");
  const requestB=route.sent.at(-1).payload.requestId;assert.deepEqual(route.sent.at(-1).payload,{kind:"LoadSharedRecord",capsule:"fm1.B",requestId:requestB});assert.notEqual(requestB,requestA);
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", {actions:3,recordStatus:"ready",hasMine:true,mine:summary(7,0),hasShared:true,shared:summary(13,2),sharedRecordRequestId:requestA})));
  assert.equal(route.elements["shared-value"].textContent,"확인 중","late A success cannot release B");assert.equal(route.elements["mine-value"].textContent,"7수","late record observation still preserves current mine");assert.match(route.elements["turn-label"].innerHTML,/행동 3/);
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", {actions:4,recordStatus:"ready",hasShared:true,shared:summary(13,2),sharedRecordRequestId:""})));
  assert.equal(route.elements["shared-value"].textContent,"확인 중","ordinary observation cannot release B");
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", {recordStatus:"ready",hasShared:true,shared:summary(9,1),sharedRecordRequestId:requestB})));
  assert.equal(route.elements["shared-value"].textContent,"9수","the next C# observation replaces the pending state");
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing",{recordStatus:"invalid",recordError:"late_A",hasShared:false,shared:{},sharedRecordRequestId:requestA})));assert.equal(route.elements["shared-value"].textContent,"9수","late A error cannot replace accepted B");
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing",{recordStatus:"ready",hasShared:true,shared:summary(13,2),sharedRecordRequestId:""})));assert.equal(route.elements["shared-value"].textContent,"9수","empty legacy ID cannot replace accepted B");
  route.sandbox.window.listeners.hashchange();
  assert.equal(route.sent.filter(item=>item.payload.kind==="LoadSharedRecord").length,2,"the same hash is not imported twice");
  route.sandbox.location.hash="";route.sandbox.window.listeners.hashchange();assert.equal(route.sent.filter(item=>item.payload.kind==="LoadSharedRecord").length,2,"removing hash sends no command");assert.equal(route.elements["shared-value"].textContent,"9수");
  route.sandbox.location.hash="#record=fm1.A";route.sandbox.window.listeners.hashchange();const requestA2=route.sent.at(-1).payload.requestId;assert.notEqual(requestA2,requestA,"A after B receives a fresh request ID");route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing",{recordStatus:"ready",hasShared:true,shared:summary(13,2),sharedRecordRequestId:requestA2})));assert.equal(route.elements["shared-value"].textContent,"13수");
  route.sandbox.location.hash="#record=malformed"; route.sandbox.window.listeners.hashchange();
  assert.equal(route.elements["shared-value"].textContent,"확인 중","malformed candidate cannot inherit the previous valid record");
  const requestC=route.sent.at(-1).payload.requestId;route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", {recordStatus:"invalid",recordError:"stale_error",hasShared:false,shared:{},sharedRecordRequestId:requestA2})));assert.equal(route.elements["shared-value"].textContent,"확인 중","stale error cannot release C");
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", {recordStatus:"invalid",recordError:"record_invalid",hasShared:false,shared:{},sharedRecordRequestId:requestC})));
  assert.equal(route.elements["shared-value"].textContent,"기록 없음");
  route.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing",{recordStatus:"ready",hasShared:true,shared:summary(9,1),sharedRecordRequestId:requestB})));assert.equal(route.elements["shared-value"].textContent,"기록 없음","stale success cannot replace the accepted latest failure");
  assert.equal(route.sent.every(item=>item.payload.kind==="LoadSharedRecord"),true,"record navigation never sends save or restart commands");
  console.log("Raid UI contract checks passed");
})().catch(error => { console.error(error); process.exitCode=1; });
