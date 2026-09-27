const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const template = readFileSync(path.join(root, "Assets/WebGLTemplates/Raid/index.html"), "utf8");
const boardView = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Raid/RaidBoardView.cs"), "utf8");
const bridge = readFileSync(path.join(root, "Assets/Plugins/WebGL/RaidState.jslib"), "utf8");
const build = readFileSync(path.join(root, "Assets/Editor/RaidBuild.cs"), "utf8");
const raidBootstrap = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Raid/RaidBootstrap.cs"), "utf8");

assert.match(template, /data-raid-action="Slide" data-direction="Up"/);
assert.match(template, /id="record-dialog"/);
assert.match(template, /id="record-close"/);
assert.doesNotMatch(template, /id="record-get"|GetRecord|현재 기록 확인/, "no manual current-record step: the verified best is shared directly");
assert.match(template, /id="menu-dialog"/);
assert.match(template, /id="help-dialog"/);
assert.match(template, /목표: 조각 3개를 모아 뱀에게 돌진/, "the goal stays readable to screen readers");
assert.doesNotMatch(template, /금빛 조각|다시 도전해|↻/, "no quest sentence names, retry prompt or in-play retry glyph");
assert.doesNotMatch(template, /SlowGlyph|slow-effect|slow-count|모래시계|effect-chip/, "the active raid shows no helper items at all: no hourglass help, chip or readout");
assert.match(template, /<div id="effect-row" class="effect-row" hidden aria-hidden="true"><\/div>/, "an empty, hidden spacer keeps the fixed effect row");
assert.match(template, /<header class="topbar">\s*<span id="turn-label" class="moves">0번 이동<\/span>\s*<div class="top-actions"><button id="help-top"/, "one HUD row: moves left, help/menu right");
assert.doesNotMatch(template, /restart-top|quick-retry/, "no in-play retry button, style or listener");
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
assert.match(template, /<span id="tail-count" class="sr-only">[^<]*<\/span><p id="feedback" class="feedback is-alert">/, "spoken goal and alert line live in the quest row, never over the board or the D-pad");
assert.match(css, /\.sr-only,\.feedback:not\(\.is-alert\) \{ position:absolute;/, "ordinary feedback is screen-reader only; alerts stay visible");
assert.match(template, /<div class="picto-hud" aria-hidden="true"><div class="charge-meter">(<span class="charge-pip"><svg[^]*?<\/svg><\/span>){3}<\/div><span class="picto-player">/, "one pictogram line: three fragment slots, then the player");
assert.match(css, /\.charge-pip:not\(\.filled\) \.slot-f,\.charge-pip:not\(\.filled\) \.slot-h \{ display:none; \}/, "empty slots are the fragment outline only, not just a colour change");
assert.match(template, /<g class="picto-powered">[^]*?(<rect [^>]*\/>){8}<\/g><\/g>/, "armed player pictogram carries the eight board corner marks");
assert.match(css, /\.goal-panel\.is-armed \.picto-arrow \.arrow-line \{ stroke-dasharray:none; \}/, "charge arrow turns solid only when armed");
const resultDialogMarkup = template.match(/<dialog id="result-dialog"[^]*?<\/dialog>/)[0];
assert.match(resultDialogMarkup, /<div class="dialog-actions"><button id="result-restart" class="primary" type="button" data-raid-action="Restart">한 판 더<\/button><\/div>/, "result keeps one bottom choice");
assert.doesNotMatch(resultDialogMarkup, /index\.html/, "other-mode link left the result dialog");
assert.match(resultDialogMarkup, /id="result-record"/, "record/share icon stays in the result head");
assert.match(template.match(/<dialog id="menu-dialog"[^]*?<\/dialog>/)[0], /href="\.\.\/index\.html"/, "other modes stay reachable from the menu");
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
assert.doesNotMatch(template, /ShieldGlyph|MagnetGlyph|shield-effect|magnet-effect|shield-count|magnet-count|보호막|자석/, "current raid shows no shield or magnet: no help entry, chip or state readout");
for (const name of ["PlayerGlyph","TailGlyph","SnakeHeadGlyph","SnakeBodyGlyph"]) {
  const pattern = boardGlyph(name).join("/");
  assert.ok(template.includes(`data-glyph="${name}" data-pattern="${pattern}"`), `${name} help picture matches the actual board glyph`);
}
assert.match(template, /href="\.\.\/index\.html"/);
assert.doesNotMatch(template, /snake\.advance|enemy\.move|Math\.random|leaderboard|랭킹/);
assert.match(bridge, /NectorialRaidReportState/);
assert.doesNotMatch(bridge, /RaidRules|RaidSession|RaidSolver/);
assert.match(build, /RaidSerializationChecks\.Run\(\);/);
// Cell cues: pooled, inside one cell, emitted only while an action plays (never by Render or restore), cleared on cancel.
assert.match(boardView, /private const int CuePoolSize = 6;/);
const renderBody = boardView.slice(boardView.indexOf("public void Render(RaidArenaDefinition arena, RaidState state)"), boardView.indexOf("public bool BeginAction("));
assert.doesNotMatch(renderBody, /EmitCuesThrough|PlayCue/, "Render and restore never emit cues");
assert.match(boardView, /public void AdvanceAction[\s\S]*?EmitCuesThrough\(frameProgress >= 0\.999f \? frameIndex : frameIndex - 1\);/, "cues fire once a frame completes");
assert.match(boardView, /public void CompleteAction[\s\S]*?EmitCuesThrough\(_activeFrames\.Length - 1\);/, "skipped frames still cue once at completion");
assert.match(boardView, /for \(; _nextCueFrame <= lastFrame && _nextCueFrame < _activeFrames\.Length; _nextCueFrame\+\+\)/, "each frame cues at most once per action");
assert.match(boardView, /public void CancelAction\(\)[\s\S]*?ClearEffects\(\);/, "cancel clears cues");
assert.match(boardView, /float half = 0\.45f \* scale;/, "cue ring stays inside its cell");
assert.doesNotMatch(boardView.slice(boardView.indexOf("private void PlayCue")), /Camera|orthographicSize/, "cues never move or zoom the camera");
assert.match(raidBootstrap, /float duration = _reducedMotion \? 0f : PresentationDuration\(/, "reduced motion makes slides instant (presentation only)");
assert.match(raidBootstrap, /public void SetReducedMotion\(string value\)/);
assert.match(raidBootstrap, /_board\.ClearEffects\(\); _board\.Render\(_arena, _session\.State\);/, "restart clears cues");

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

function createHarness(hash = "#record=fm1.shared", options = {}) {
  const documentObject = { activeElement: null, listeners: {}, loader: null };
  const ids = ["unity-canvas","storage-retry","feedback","turn-label","tail-count","goal-panel","effect-row","charge-0","charge-1","charge-2","charge-meter","result-dialog","result-title","result-turn","result-copy","result-record","result-restart","save-button","menu-top","menu-dialog","menu-close","help-top","help-dialog","help-close","record-dialog","record-top","record-note","record-get","record-share","record-challenge","record-fallback","record-close","mine-value","mine-meta","shared-value","shared-meta","record-compare"];
  const elements = Object.fromEntries(ids.map(id => [id, makeElement(documentObject, id)]));
  for (const id of ["result-dialog","record-dialog","menu-dialog","help-dialog"]) elements[id].tagName = "DIALOG";
  elements["effect-row"].hidden = true; // mirrors the markup's hidden attribute on the empty spacer
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
  if (options.matchMedia) windowObject.matchMedia = options.matchMedia;
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
  assert.equal(h.elements["tail-count"].textContent, "조각 0/3 모음, 다 모으면 뱀에게 돌진");
  assert.equal(h.elements["goal-panel"].classList.contains("is-armed"), false, "collecting phase is not styled as armed");
  assert.equal(h.elements.feedback.classList.contains("is-alert"), false, "ordinary game feedback is not shown as a visible alert");
  assert.equal(h.elements["turn-label"].textContent, "2번 이동");
  assert.equal(h.elements["effect-row"].hidden, true, "a legacy shieldCharges field shows nothing in the current raid");
  assert.equal(h.elements["charge-0"].classList.contains("filled"), false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1 })));
  assert.equal(h.elements["tail-count"].textContent, "조각 1/3 모음, 다 모으면 뱀에게 돌진");
  assert.equal(h.elements["charge-0"].classList.contains("filled"), true);
  assert.equal(h.elements["charge-1"].classList.contains("filled"), false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:2 })));
  assert.equal(h.elements["tail-count"].textContent, "조각 2/3 모음, 다 모으면 뱀에게 돌진");
  assert.equal(h.elements["charge-1"].classList.contains("filled"), true);
  assert.equal(h.elements["charge-2"].classList.contains("filled"), false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { transitioning:true, tailCount:3 })));
  assert.equal(h.elements["tail-count"].textContent, "조각 3개 모두 모음, 멈추면 돌진 준비", "count alone does not authorize the armed state");
  assert.equal(h.elements["goal-panel"].classList.contains("is-powered"), false, "mid-slide full count does not power the player picture");
  assert.equal(h.elements["goal-panel"].classList.contains("is-armed"), false, "mid-slide full count is not yet armed");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Armed", { tailCount:3 })));
  assert.equal(h.elements["tail-count"].textContent, "돌진 준비됨: 뱀 머리나 몸통에 부딪히면 잡아요");
  assert.equal(h.elements["goal-panel"].classList.contains("is-powered"), true, "Armed powers the player picture");
  assert.equal(h.elements["goal-panel"].classList.contains("is-armed"), true, "confirmed Armed state marks the quest row, not only by color");
  assert.equal(h.elements["charge-2"].classList.contains("filled"), true);
  assert.equal(h.elements["charge-2"].classList.contains("armed"), true);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Failed", { tailCount:2, shieldCharges:0 })));
  assert.equal(h.elements["tail-count"].textContent, "뱀에게 잡혔어요");
  assert.equal(h.elements["result-title"].textContent, "뱀에게 잡혔어요");
  assert.equal(h.elements["goal-panel"].classList.contains("is-armed"), false, "a failed run drops the armed marker");
  assert.equal(h.elements["result-turn"].textContent, "2수", "a failed result shows the move count large");
  assert.equal(h.elements["result-copy"].textContent, "", "a failed result has no retry prompt and no zero-hit noise");
  assert.equal(h.elements["result-dialog"].open, true);
  assert.equal(h.elements["result-record"].disabled, false);
  h.elements["result-restart"].listeners.click();
  assert.equal(h.sent.at(-1).payload.kind, "Restart", "result retry explicitly resets the current arena");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:0, shieldCharges:1 })));
  assert.equal(h.elements["tail-count"].textContent, "조각 0/3 모음, 다 모으면 뱀에게 돌진", "restart redraws an empty charge");
  assert.equal(h.elements["charge-0"].classList.contains("filled"), false);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, shieldCharges:1, magnetStepsRemaining:2, slowStepsRemaining:1 })));
  assert.equal(h.elements["tail-count"].textContent, "조각 1/3 모음, 다 모으면 뱀에게 돌진", "restored playing state redraws the confirmed charge");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, saveStatus:"failed", saveError:"webgl_persist_timeout", message:"저장하지 못했습니다" })));
  assert.equal(h.elements.feedback.textContent, "저장하지 못했습니다");
  assert.equal(h.elements.feedback.classList.contains("is-alert"), true, "a save failure stays visible");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, saveStatus:"failed", saveError:"raid_save_write:IOException", message:"이동했습니다" })));
  assert.equal(h.elements.feedback.textContent, "저장하지 못했습니다", "a save failure never shows the stale move message as its alert");
  assert.equal(h.elements.feedback.classList.contains("is-alert"), true);
  assert.doesNotMatch(h.elements.feedback.textContent, /IOException|raid_save_write/, "the raw exception code is not the user-facing alert");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, saveStatus:"failed", saveError:"raid_save_write:IOException", message:"" })));
  assert.equal(h.elements.feedback.textContent, "저장하지 못했습니다", "a save failure with an empty message still shows the alert text");
  assert.equal(h.elements.feedback.classList.contains("is-alert"), true, "an empty message cannot hide a save failure");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, error:"raid_state_invalid", message:"이동했습니다" })));
  assert.equal(h.elements.feedback.textContent, "게임 상태를 확인할 수 없습니다", "a state error never shows the stale move message or the raw code");
  assert.equal(h.elements.feedback.classList.contains("is-alert"), true);
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, error:"raid_state_invalid", message:"" })));
  assert.equal(h.elements.feedback.textContent, "게임 상태를 확인할 수 없습니다", "a state error with an empty message still shows the alert text");
  assert.equal(h.elements.feedback.classList.contains("is-alert"), true, "an empty message cannot hide a state error");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, inputEnabled:false, message:"저장된 레이드를 불러오지 못했습니다" })));
  assert.equal(h.elements.feedback.classList.contains("is-alert"), true, "blocked input (restore failure) stays visible");
  h.sandbox.window.__nectorialRaid.receiveState("{not json");
  assert.equal(h.elements.feedback.textContent, "레이드 상태를 확인할 수 없습니다.");
  assert.equal(h.elements.feedback.classList.contains("is-alert"), true, "an unreadable state report stays visible");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { tailCount:1, shieldCharges:1, magnetStepsRemaining:2, slowStepsRemaining:1 })));
  assert.equal(h.elements["effect-row"].hidden, true, "legacy shield/magnet/slow fields never show anything in the item-free raid");
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
  const exact = observation("Cleared", { actions:14, hits:3, recordStatus:"ready", recordCapsule:"fm1.mine", mineCapsule:"fm1.mine", hasMine:true, mine:summary(11,2), hasShared:true, shared:summary(13,4), sharedRecordRequestId:firstRequestId, stateFingerprint:"clear-1" });
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
  assert.equal(h.elements["result-dialog"].open, true, "closing the record returns a finished run to its result and 한 판 더");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(exact));
  assert.equal(h.elements["result-dialog"].open, true, "a repeated identical state keeps one result open");
  h.elements["result-dialog"].close();

  h.elements["record-top"].listeners.click();
  h.elements["record-share"].listeners.click();
  assert.equal(h.clipboardCalls[0].value, "https://game.test/content/raid/index.html#record=fm1.mine", "record URL drops invitation/query data");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(Object.assign({}, exact, { recordCapsule:"fm1.new", mineCapsule:"fm1.new" })));
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
  h.elements["record-dialog"].listeners.close();
  assert.equal(h.elements["result-dialog"].open, false, "a late record close after Challenge never flashes the old result");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(exact));
  assert.equal(h.elements["result-dialog"].open, false, "the old result stays hidden until the restarted state arrives");
  h.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing")));
  assert.equal(h.elements["result-dialog"].open, false);

  const flow = createHarness(""); flow.documentObject.loader.onload(); flow.unityReady.resolve(flow.instance); await settle();
  flow.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Cleared", { actions:12, hits:0, tailCount:3, stateFingerprint:"flow-clear" })));
  assert.equal(flow.elements["result-dialog"].open, true, "a clear opens the result");
  flow.elements["result-record"].listeners.click();
  assert.equal(flow.elements["record-dialog"].open, true);
  assert.equal(flow.elements["result-dialog"].open, false, "the record replaces the result while open");
  flow.elements["record-close"].listeners.click();
  assert.equal(flow.elements["result-dialog"].open, true, "clear → record → close returns to the result");
  flow.elements["result-restart"].listeners.click();
  assert.equal(flow.sent.at(-1).payload.kind, "Restart", "한 판 더 restarts after returning from the record");
  flow.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Failed", { actions:2, hits:1, tailCount:0, stateFingerprint:"flow-fail" })));
  assert.equal(flow.elements["result-dialog"].open, true, "a failure opens the result");
  flow.elements["result-record"].listeners.click();
  flow.elements["record-dialog"].close(); flow.elements["record-dialog"].listeners.close();
  assert.equal(flow.elements["result-dialog"].open, true, "fail → record → Escape returns to the result");
  flow.elements["result-restart"].listeners.click();
  assert.equal(flow.sent.at(-1).payload.kind, "Restart");
  flow.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { actions:0, stateFingerprint:"flow-play" })));
  flow.elements["record-top"].listeners.click();
  flow.elements["record-close"].listeners.click();
  assert.equal(flow.elements["result-dialog"].open, false, "closing the record during play opens no result");

  // OS reduced motion reaches the raid presentation once, and again only when it changes.
  const motionQuery = { matches:true, listeners:{}, addEventListener(name, handler){ this.listeners[name] = handler; } };
  const calm = createHarness("", { matchMedia: () => motionQuery }); calm.documentObject.loader.onload(); calm.unityReady.resolve(calm.instance); await settle();
  const motionMessages = () => calm.sent.filter(item => item.method === "SetReducedMotion");
  assert.deepEqual(motionMessages().map(item => item.payload), [true], "reduce-motion preference is sent when the instance is ready");
  motionQuery.listeners.change();
  assert.equal(motionMessages().length, 1, "an unchanged preference is not resent");
  motionQuery.matches = false; motionQuery.listeners.change();
  assert.deepEqual(motionMessages().map(item => item.payload), [true, false], "turning reduce-motion off is forwarded");

  // Best-record sharing after a reload: a verified best exists, but there is no current-run capsule.
  const best = createHarness(""); best.documentObject.loader.onload(); best.unityReady.resolve(best.instance); await settle();
  best.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { actions:0, hasMine:true, mine:summary(12,0), mineCapsule:"fm1.best", recordStatus:"idle", recordCapsule:"", stateFingerprint:"reloaded" })));
  best.elements["record-top"].listeners.click();
  assert.equal(best.elements["record-share"].disabled, false, "the verified best is shareable right after a reload and during a new run");
  assert.equal(best.elements["mine-meta"].textContent, "", "a zero-hit best shows no hit count");
  best.elements["record-share"].listeners.click();
  assert.equal(best.clipboardCalls.at(-1).value, "https://game.test/content/raid/index.html#record=fm1.best", "without native share the best link is copied");
  best.clipboardCalls.at(-1).item.resolve(); await settle();
  const shareCalls = [];
  best.sandbox.navigator.share = payload => { const item = deferred(); shareCalls.push({ payload, item }); return item.promise; };
  best.elements["record-fallback"].hidden = true;
  const copiesBeforeNative = best.clipboardCalls.length, noteBeforeCancel = best.elements["record-note"].textContent;
  best.elements["record-share"].listeners.click();
  assert.equal(shareCalls.length, 1, "native share is tried first when available");
  assert.equal(shareCalls[0].payload.url, "https://game.test/content/raid/index.html#record=fm1.best");
  const cancel = new Error("cancel"); cancel.name = "AbortError"; shareCalls[0].item.reject(cancel); await settle();
  assert.equal(best.clipboardCalls.length, copiesBeforeNative, "a cancelled share does not copy");
  assert.equal(best.elements["record-fallback"].hidden, true, "a cancelled share stays silent");
  assert.equal(best.elements["record-note"].textContent, noteBeforeCancel, "a cancelled share changes no message");
  best.elements["record-share"].listeners.click(); shareCalls[1].item.reject(new Error("blocked")); await settle();
  assert.equal(best.clipboardCalls.length, copiesBeforeNative + 1, "a failed native share falls back to copying the link");
  best.clipboardCalls.at(-1).item.reject(new Error("denied")); await settle();
  assert.equal(best.elements["record-fallback"].hidden, false, "and still leaves a selectable link");
  best.elements["record-share"].listeners.click(); shareCalls[2].item.resolve(); await settle();
  assert.equal(best.elements["record-note"].textContent, "기록을 공유했습니다.");
  best.elements["record-share"].listeners.click();
  best.elements["record-share"].listeners.click();
  assert.equal(shareCalls.length, 4, "a second tap while a share is pending does not start another share");
  const copiesBeforeStaleClose = best.clipboardCalls.length;
  best.elements["record-close"].listeners.click();
  shareCalls[3].item.reject(new Error("late failure")); await settle();
  assert.equal(best.clipboardCalls.length, copiesBeforeStaleClose, "a failure arriving after the dialog closed never copies");
  best.elements["record-top"].listeners.click();
  best.elements["record-share"].listeners.click();
  assert.equal(shareCalls.length, 5, "closing the dialog releases the pending share");
  best.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { actions:0, hasMine:true, mine:summary(11,0), mineCapsule:"fm1.newer", recordStatus:"idle", recordCapsule:"", stateFingerprint:"reloaded-2" })));
  shareCalls[4].item.reject(new Error("late failure")); await settle();
  assert.equal(best.clipboardCalls.length, copiesBeforeStaleClose, "a failure for a replaced best record never copies");
  best.sandbox.navigator.share = () => { const error = new Error("cancel"); error.name = "AbortError"; throw error; };
  best.elements["record-fallback"].hidden = true;
  const noteBeforeSyncCancel = best.elements["record-note"].textContent;
  best.elements["record-share"].listeners.click(); await settle();
  assert.equal(best.clipboardCalls.length, copiesBeforeStaleClose, "a synchronous cancel never copies");
  assert.equal(best.elements["record-fallback"].hidden, true, "a synchronous cancel stays silent");
  assert.equal(best.elements["record-note"].textContent, noteBeforeSyncCancel, "a synchronous cancel changes no message");
  best.elements["record-close"].listeners.click();

  // Result comparison is shown only for the live clear it was computed for.
  best.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Cleared", { actions:10, hits:0, tailCount:3, hasMine:true, mine:summary(10,0), mineCapsule:"fm1.best2", clearComparison:"improved", clearComparisonDelta:2, clearComparisonFingerprint:"clear-improved", stateFingerprint:"clear-improved" })));
  assert.equal(best.elements["result-turn"].textContent, "10수", "the result shows the move count large");
  assert.equal(best.elements["result-copy"].textContent, "신기록 · 2수 단축", "an improved clear reports the real improvement");
  best.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Cleared", { actions:10, hits:0, tailCount:3, hasMine:true, mine:summary(10,0), mineCapsule:"fm1.best2", clearComparison:"", clearComparisonDelta:0, clearComparisonFingerprint:"", stateFingerprint:"restored-clear" })));
  assert.equal(best.elements["result-copy"].textContent, "", "a restored clear makes no new-best claim");
  best.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Cleared", { actions:12, hits:0, tailCount:3, hasMine:true, mine:summary(10,0), mineCapsule:"fm1.best2", clearComparison:"improved", clearComparisonDelta:2, clearComparisonFingerprint:"clear-improved", stateFingerprint:"a-different-clear" })));
  assert.equal(best.elements["result-copy"].textContent, "", "a comparison for another state is never shown");
  best.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Cleared", { actions:12, hits:0, tailCount:3, hasMine:true, mine:summary(10,0), mineCapsule:"fm1.best2", clearComparison:"slower", clearComparisonDelta:2, clearComparisonFingerprint:"slow-clear", stateFingerprint:"slow-clear" })));
  assert.equal(best.elements["result-copy"].textContent, "최고보다 2수 많아요", "a slower clear is reported truthfully, not as a new best");
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
  // Phase 3 storage: retry appears only on an actual Toss startup failure, is one-shot, and manual save is
  // never shown as saved while a native write is still pending; a UI timeout shows its own message.
  assert.match(template, /<button id="storage-retry" class="storage-retry" type="button" hidden>저장소 다시 확인<\/button>/);
  // Failed-startup observation can arrive before createUnityInstance resolves; a tap then cannot dispatch and
  // must leave retry available, and the first tap after the instance is ready sends exactly one retry.
  const early = createHarness(""); early.documentObject.loader.onload();
  early.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { initialized:false, inputEnabled:false, startupRetryEnabled:true, saveStatus:"failed", saveError:"startup_timeout", message:"토스 저장소를 확인하지 못했습니다. 다시 확인해 주세요" })));
  assert.equal(early.elements["storage-retry"].hidden, false, "pre-ready startup failure shows retry");
  early.elements["storage-retry"].listeners.click();
  assert.equal(early.elements["storage-retry"].hidden, false, "undispatched retry stays available");
  early.unityReady.resolve(early.instance); await settle();
  early.elements["storage-retry"].listeners.click();
  assert.equal(early.sent.filter(item => item.payload.kind === "RetryStorage").length, 1, "retry dispatches once the instance is ready");
  assert.equal(early.elements["storage-retry"].hidden, true);
  const store = createHarness(""); store.documentObject.loader.onload(); store.unityReady.resolve(store.instance); await settle();
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { initialized:false, inputEnabled:false, saveStatus:"pending", message:"토스 저장소를 확인하고 있습니다" })));
  assert.equal(store.elements["storage-retry"].hidden, true, "retry stays hidden while startup is still pending");
  store.elements["storage-retry"].listeners.click();
  assert.equal(store.sent.some(item => item.payload.kind === "RetryStorage"), false, "hidden retry sends nothing");
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { initialized:false, inputEnabled:false, startupRetryEnabled:true, saveStatus:"failed", saveError:"startup_timeout", message:"토스 저장소를 확인하지 못했습니다. 다시 확인해 주세요" })));
  assert.equal(store.elements["storage-retry"].hidden, false, "startup failure exposes retry");
  assert.equal(store.elements.feedback.textContent, "토스 저장소를 확인하지 못했습니다. 다시 확인해 주세요", "startup failure is not reported as a save failure");
  assert.equal(store.actions.every(button => button.disabled), true, "no move, save or restart while storage startup failed");
  store.elements["storage-retry"].listeners.click();
  assert.deepEqual(store.sent.filter(item => item.payload.kind === "RetryStorage").map(item => item.payload), [{ kind:"RetryStorage" }]);
  assert.equal(store.elements["storage-retry"].hidden, true, "retry hides immediately after one tap");
  store.elements["storage-retry"].listeners.click();
  assert.equal(store.sent.filter(item => item.payload.kind === "RetryStorage").length, 1, "double tap sends one retry");
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { initialized:false, inputEnabled:false, startupRetryEnabled:true, saveStatus:"failed", saveError:"startup_timeout", message:"토스 저장소를 확인하지 못했습니다. 다시 확인해 주세요" })));
  assert.equal(store.elements["storage-retry"].hidden, true, "a repeated failure state before Unity reports the retry keeps it one-shot");
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { initialized:false, inputEnabled:false, saveStatus:"pending", message:"토스 저장소를 다시 확인하고 있습니다" })));
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { initialized:false, inputEnabled:false, startupRetryEnabled:true, saveStatus:"failed", saveError:"raid_toss_payload_version", message:"토스 저장소를 확인하지 못했습니다. 다시 확인해 주세요" })));
  assert.equal(store.elements["storage-retry"].hidden, false, "a new failure after the retry started offers retry again");
  store.elements["storage-retry"].listeners.click();
  assert.equal(store.sent.filter(item => item.payload.kind === "RetryStorage").length, 2);
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { saveStatus:"pending", savePending:true, message:"저장하고 있습니다" })));
  assert.equal(store.elements["storage-retry"].hidden, true, "retry is not a permanent control once storage is ready");
  assert.equal(store.elements["save-button"].disabled, true, "save cannot overlap a pending manual write");
  assert.equal(store.elements.feedback.textContent, "저장하고 있습니다", "pending manual save is not shown as saved");
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { saveStatus:"failed", saveError:"storage_ui_timeout", message:"저장이 아직 끝나지 않았습니다. 진행은 계속할 수 있습니다" })));
  assert.equal(store.elements.feedback.textContent, "저장이 아직 끝나지 않았습니다. 진행은 계속할 수 있습니다", "UI timeout is visible as not finished");
  assert.equal(store.elements["save-button"].disabled, false, "timeout releases the save button");
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { saveStatus:"failed", saveError:"storage_api_error", message:"저장하지 못했습니다. 다시 저장할 수 있습니다" })));
  assert.equal(store.elements.feedback.textContent, "저장하지 못했습니다", "real write failure keeps the existing failure text");
  store.sandbox.window.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { saveStatus:"saved", message:"저장했습니다" })));
  assert.equal(store.elements.feedback.textContent, "저장했습니다");
  assert.equal(store.sent.some(item => item.payload.kind === "Save"), false, "state updates never send save commands");
  console.log("Raid UI contract checks passed");
})().catch(error => { console.error(error); process.exitCode=1; });
