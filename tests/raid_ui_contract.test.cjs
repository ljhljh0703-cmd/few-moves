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
assert.match(template, /id="menu-dialog"/);
assert.match(template, /id="help-dialog"/);
assert.match(template, /금빛 조각 3개를 모아요/);
assert.match(template, /모래시계: 표시된 칸만큼 내가 움직이는 동안 뱀이 멈춰요/, "hourglass is described as a stop measured in cells you slide");
assert.match(template, /<header class="topbar">\s*<span id="turn-label" class="moves">0번 이동<\/span>\s*<div class="top-actions"><button id="restart-top" class="quick-retry"/, "one HUD row: moves left, retry/help/menu grouped right");
assert.doesNotMatch(template, /quickbar|small-screen-actions|turn-label-small|restart-small|goal-help|<h1>/, "no duplicate title or second move/retry row");
assert.doesNotMatch(template, /data-raid-action="Pass"|data-raid-action="Load"|저장한 판 열기/);
assert.match(template, /min-height:48px/);
assert.match(template, /width:56px; height:56px/);
assert.match(template, /16×16 레이드 보드/);
const css = template.slice(template.indexOf("<style>")+7, template.indexOf("</style>"));
assert.match(css, /\.controls \{[^}]*grid-template-rows:24px 168px;[^}]*gap:16px;/, "portrait controls: fixed 24px effect slot, 16px gap, fixed 168px D-pad");
assert.match(css, /\.effect-row \{ grid-row:1;/);
assert.match(css, /\.dpad \{ grid-row:2;/, "the D-pad stays in its fixed row when the effect strip is hidden");
assert.match(css, /\.effect-row\[hidden\] \{ display:flex!important; visibility:hidden; \}/, "an empty effect strip keeps its slot so the board never resizes");
assert.match(template, /<strong id="tail-count">[^<]*<\/strong><p id="feedback" class="feedback">/, "feedback lives in the quest row, never over the board or the D-pad");
assert.doesNotMatch(css, /\.feedback \{[^}]*position:absolute/);
const landscapeCss = css.slice(css.indexOf("@media (orientation:landscape)"), css.indexOf("@media (prefers-reduced-motion:reduce)"));
assert.match(landscapeCss, /\.controls \{ grid-column:2; grid-row:1 \/ span 2;/, "landscape keeps the same controls in the side column");
function boardGlyph(name) {
  const start = boardView.indexOf(`private static readonly string[] ${name}`);
  assert.ok(start >= 0, `${name} exists`);
  const block = boardView.slice(start, boardView.indexOf("};", start));
  const rows = [...block.matchAll(/"([.OFHE]{9})"/g)].map(match => match[1]);
  assert.equal(rows.length, 9, `${name} has nine rows`);
  return rows;
}
const head = boardGlyph("SnakeHeadGlyph");
assert.equal(head.flatMap((row, y) => [...row].flatMap((pixel, x) => pixel === "E" ? [`${x},${y}`] : [])).join("|"), "6,3|6,5", "the head has two distinct forward-facing eyes");
assert.match(boardView, /Vector3 forward = head - neck;/);
assert.match(boardView, /Mathf\.Atan2\(forward\.y, forward\.x\)/);
const glyphPixelSize = Number(boardView.match(/GlyphPixelSize = ([0-9.]+)f/)[1]);
const maxHeadRadius = Math.max(...head.flatMap((row,y) => [...row].flatMap((pixel,x) => pixel === "." ? [] : [Math.hypot((Math.abs(x-4)+0.5)*glyphPixelSize,(Math.abs(y-4)+0.5)*glyphPixelSize)])));
assert.ok(maxHeadRadius < 0.5, `the rotating head and eyes stay inside one cell: ${maxHeadRadius}`);
assert.match(boardView, /state\.Status == RaidRunStatus\.Armed \|\| state\.Status == RaidRunStatus\.Cleared/, "gold player begins only after confirmed Armed");
for (const name of ["PlayerGlyph","TailGlyph","SnakeHeadGlyph","SnakeBodyGlyph","ShieldGlyph","MagnetGlyph","SlowGlyph"]) {
  const pattern = boardGlyph(name).join("/");
  assert.ok(template.includes(`data-glyph="${name}" data-pattern="${pattern}"`), `${name} help picture matches the actual board glyph`);
}
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
  const classes = new Set();
  return {
    id, dataset, hidden: false, disabled: false, open: false, textContent: "", innerHTML: "", value: "", style: {}, tagName: id.includes("fallback") ? "INPUT" : "BUTTON", listeners: {}, children: [], classList: { toggle(name, force) { if (force === undefined ? !classes.has(name) : force) classes.add(name); else classes.delete(name); return classes.has(name); }, contains(name) { return classes.has(name); } },
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
  const ids = ["unity-canvas","feedback","turn-label","tail-count","goal-panel","shield-count","magnet-count","slow-count","effect-row","shield-effect","magnet-effect","slow-effect","charge-0","charge-1","charge-2","charge-meter","result-dialog","result-title","result-copy","result-record","result-restart","restart-top","save-button","menu-top","menu-dialog","menu-close","help-top","help-dialog","help-close","record-dialog","record-top","record-note","record-get","record-share","record-challenge","record-fallback","record-close","mine-value","mine-meta","shared-value","shared-meta","record-compare"];
  const elements = Object.fromEntries(ids.map(id => [id, makeElement(documentObject, id)]));
  for (const id of ["result-dialog","record-dialog","menu-dialog","help-dialog"]) elements[id].tagName = "DIALOG";
  const actions = [makeElement(documentObject,"up",{raidAction:"Slide",direction:"Up"}),makeElement(documentObject,"left",{raidAction:"Slide",direction:"Left"}),makeElement(documentObject,"right",{raidAction:"Slide",direction:"Right"}),makeElement(documentObject,"down",{raidAction:"Slide",direction:"Down"}),elements["save-button"],elements["result-restart"]];
  elements["save-button"].dataset = { raidAction:"Save" }; elements["result-restart"].dataset = { raidAction:"Restart" };
  const boardStage = makeElement(documentObject, "board-stage"); boardStage.tagName = "SECTION";
  documentObject.body = { appendChild(node) { documentObject.loader = node; } };
  documentObject.querySelector = selector => selector === ".board-stage" ? boardStage : selector === ".charge-meter" ? elements["charge-meter"] : selector === "dialog[open]" ? [elements["result-dialog"],elements["record-dialog"],elements["menu-dialog"],elements["help-dialog"]].find(item => item.open) || null : selector.startsWith("#") ? elements[selector.slice(1)] : null;
  documentObject.querySelectorAll = selector => selector === "button[data-raid-action]" ? actions : selector === ".charge-pip" ? [elements["charge-0"],elements["charge-1"],elements["charge-2"]] : [];
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
  assert.equal(h.elements["tail-count"].textContent, "금빛 조각 3개를 모아요");
  assert.equal(h.elements["goal-panel"].classList.contains("is-armed"), false, "collecting phase is not styled as armed");
  assert.equal(h.elements["turn-label"].textContent, "2번 이동");
  assert.equal(h.elements["shield-count"].textContent, "1회");
  assert.equal(h.elements["shield-effect"].hidden, false, "shield capacity is visible when positive");
  assert.equal(h.elements["effect-row"].hidden, false);
  assert.equal(h.elements["magnet-effect"].hidden, true);
  assert.equal(h.elements["slow-effect"].hidden, true);
  assert.equal(h.elements["charge-0"].classList.contains("filled"), false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1 })));
  assert.equal(h.elements["tail-count"].textContent, "금빛 조각 2개를 더 모아요");
  assert.equal(h.elements["charge-0"].classList.contains("filled"), true);
  assert.equal(h.elements["charge-1"].classList.contains("filled"), false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:2 })));
  assert.equal(h.elements["tail-count"].textContent, "금빛 조각 1개를 더 모아요");
  assert.equal(h.elements["charge-1"].classList.contains("filled"), true);
  assert.equal(h.elements["charge-2"].classList.contains("filled"), false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { transitioning:true, tailCount:3 })));
  assert.equal(h.elements["tail-count"].textContent, "멈추면 힘이 생겨요", "count alone does not authorize the armed state");
  assert.equal(h.elements["goal-panel"].classList.contains("is-armed"), false, "mid-slide full count is not yet armed");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Armed", { tailCount:3 })));
  assert.equal(h.elements["tail-count"].textContent, "돌진! 뱀에 부딪혀요");
  assert.equal(h.elements["goal-panel"].classList.contains("is-armed"), true, "confirmed Armed state marks the quest row, not only by color");
  assert.equal(h.elements["charge-2"].classList.contains("filled"), true);
  assert.equal(h.elements["charge-2"].classList.contains("armed"), true);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Failed", { tailCount:2, shieldCharges:0 })));
  assert.equal(h.elements["tail-count"].textContent, "뱀에게 잡혔어요");
  assert.equal(h.elements["result-title"].textContent, "뱀에게 잡혔어요");
  assert.equal(h.elements["goal-panel"].classList.contains("is-armed"), false, "a failed run drops the armed marker");
  assert.equal(h.elements["result-dialog"].open, true);
  assert.equal(h.elements["result-record"].disabled, false);
  h.elements["restart-top"].listeners.click();
  assert.notEqual(h.sent.at(-1).payload.kind, "Restart", "quick retry behind result is blocked");
  h.elements["result-restart"].listeners.click();
  assert.equal(h.sent.at(-1).payload.kind, "Restart", "result retry explicitly resets the current arena");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:0, shieldCharges:1 })));
  assert.equal(h.elements["tail-count"].textContent, "금빛 조각 3개를 모아요", "restart redraws an empty charge");
  assert.equal(h.elements["charge-0"].classList.contains("filled"), false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, shieldCharges:1, magnetStepsRemaining:2, slowStepsRemaining:1 })));
  assert.equal(h.elements["tail-count"].textContent, "금빛 조각 2개를 더 모아요", "restored playing state redraws the confirmed charge");
  assert.equal(h.elements["shield-count"].textContent, "1회");
  assert.equal(h.elements["magnet-count"].textContent, "2칸", "magnet shows its remaining cells of player movement");
  assert.equal(h.elements["slow-count"].textContent, "1칸", "hourglass shows its remaining cells of player movement");
  assert.equal(h.elements["magnet-effect"].hidden, false);
  assert.equal(h.elements["slow-effect"].hidden, false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { shieldCharges:0 })));
  assert.equal(h.elements["effect-row"].hidden, true, "inactive effect strip is hidden (its CSS slot stays reserved)");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, shieldCharges:1, magnetStepsRemaining:2, slowStepsRemaining:1 })));

  h.elements["menu-top"].listeners.click();
  assert.equal(h.elements["menu-dialog"].open, true);
  const beforeMenuInput = h.sent.length;
  h.actions[0].listeners.click();
  h.documentObject.listeners.keydown({ key:"ArrowUp", repeat:false, ctrlKey:false, metaKey:false, altKey:false, preventDefault(){} });
  assert.equal(h.sent.length, beforeMenuInput, "menu blocks both gamepad clicks and keyboard movement");
  h.elements["save-button"].listeners.click();
  assert.equal(h.sent.at(-1).payload.kind, "Save", "menu keeps real save command");
  assert.equal(h.elements["menu-dialog"].open, false);
  h.elements["help-top"].listeners.click();
  assert.equal(h.elements["help-dialog"].open, true);
  const beforeHelpInput = h.sent.length;
  h.actions[1].listeners.click();
  assert.equal(h.sent.length, beforeHelpInput, "help blocks gamepad clicks");
  h.elements["help-close"].listeners.click();
  assert.equal(h.elements["help-dialog"].open, false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { transitioning:true })));
  const beforeTransitionInput = h.sent.length;
  h.actions[2].listeners.click();
  assert.equal(h.sent.length, beforeTransitionInput, "movement is blocked while a turn animates");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing")));

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
  assert.equal(route.elements["shared-value"].textContent,"확인 중","late A success cannot release B");assert.equal(route.elements["mine-value"].textContent,"7수","late record observation still preserves current mine");assert.equal(route.elements["turn-label"].textContent,"3번 이동","action count remains the sole turn label");
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
