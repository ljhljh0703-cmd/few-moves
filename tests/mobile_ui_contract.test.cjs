const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const repoRoot = path.resolve(__dirname, "..");
const template = readFileSync(repoRoot + "/Assets/WebGLTemplates/TurnEscape/index.html", "utf8");
const bootstrap = readFileSync(repoRoot + "/Assets/Nectorial/Runtime/GameBootstrap.cs", "utf8");
const board = readFileSync(repoRoot + "/Assets/Nectorial/Runtime/GameBoardView.cs", "utf8");
const runtimeAssembly = readFileSync(repoRoot + "/Assets/Nectorial/Runtime/Nectorial.Runtime.asmdef", "utf8");
const webGLKeyboardCapture = readFileSync(repoRoot + "/Assets/WebGLKeyboardCapture.cs", "utf8");
const linker = readFileSync(repoRoot + "/Assets/link.xml", "utf8");
const runtime = template.slice(template.indexOf("<script>") + 8, template.indexOf("    var buildUrl"));

function classList() {
  const values = new Set();
  return {
    toggle(name, force) {
      if (force === undefined) {
        if (values.has(name)) values.delete(name);
        else values.add(name);
      } else if (force) values.add(name);
      else values.delete(name);
      return values.has(name);
    },
    contains(name) { return values.has(name); }
  };
}

function element(options = {}) {
  const handlers = {};
  const tag = options.tag || "";
  return {
    dataset: options.dataset || {},
    attributes: {},
    disabled: false,
    hidden: false,
    open: false,
    textContent: "",
    value: "",
    style: {},
    classList: classList(),
    handlers,
    addEventListener(type, handler) { handlers[type] = handler; },
    dispatch(type, event) { return handlers[type] && handlers[type](event); },
    closest(selector) {
      const selectors = selector.split(",").map((value) => value.trim());
      if (tag === "contenteditable") return selectors.includes("[contenteditable]") ? this : null;
      return selectors.includes(tag) ? this : null;
    },
    showModal() { this.open = true; },
    close() { this.open = false; if (handlers.close) handlers.close(); },
    setAttribute(name, value) { this.attributes[name] = String(value); },
    removeAttribute() {},
    focus() {},
    select() {}
  };
}

function createHarness() {
  const actionButtons = ["Up", "Down", "Left", "Right", "Restart", "Restart", "Next"].map((action) =>
    element({ dataset: { gameAction: action }, tag: "button" }));
  const pieceButtons = [0, 1, 2].map((index) => element({ dataset: { pieceIndex: String(index) }, tag: "button" }));
  const elements = {
    "#unity-canvas": element(),
    "#menu-toggle": element({ tag: "button" }),
    "#help-toggle": element({ tag: "button" }),
    "#help-dialog": element({ tag: "dialog" }),
    "#help-close": element({ tag: "button" }),
    "#menu-dialog": element({ tag: "dialog" }),
    "#menu-main": element(),
    "#menu-confirmation": element(),
    "#menu-continue": element({ tag: "button" }),
    "#menu-save": element({ tag: "button" }),
    "#menu-start-over": element({ tag: "button" }),
    "#menu-start-over-confirm": element({ tag: "button" }),
    "#menu-start-over-cancel": element({ tag: "button" }),
    "#menu-room": element(),
    "#menu-save-status": element(),
    "#result-dialog": element({ tag: "dialog" }),
    "#result-menu": element({ tag: "button" }),
    "#result-title": element(),
    "#result-turn": element(),
    "#result-par": element(),
    "#result-actions": element(),
    "#restart-top": actionButtons.find((button) => button.dataset.gameAction === "Restart"),
    "#result-retry": actionButtons.filter((button) => button.dataset.gameAction === "Restart").at(-1),
    "#result-next": actionButtons.find((button) => button.dataset.gameAction === "Next"),
    "#result-share": element({ tag: "button" }),
    "#share-dialog": element({ tag: "dialog" }),
    "#share-text": element({ tag: "textarea" }),
    "#share-copy": element({ tag: "button" }),
    "#share-native": element({ tag: "button" }),
    "#share-close": element({ tag: "button" }),
    "#share-panel-feedback": element(),
    "#feedback": element(),
    "#play-controls": element(),
    "#piece-row": element(),
    "#turn-runline": element()
  };
  const documentHandlers = {};
  const navigator = {};
  const sandbox = {
    console,
    navigator,
    CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init.detail; } },
    document: {
      querySelector(selector) { return elements[selector]; },
      querySelectorAll(selector) {
        if (selector === "button[data-game-action]") return actionButtons;
        if (selector === "button[data-piece-index]") return pieceButtons;
        return [];
      },
      addEventListener(type, handler) { documentHandlers[type] = handler; }
    },
    window: { dispatchEvent() {} }
  };
  vm.runInNewContext(runtime, sandbox, { filename: "mobile-ui-runtime.js" });
  const sent = [];
  sandbox.unityInstance = { SendMessage: (receiver, method, action) => sent.push({ receiver, method, action }) };
  return { actionButtons, pieceButtons, documentHandlers, elements, navigator, sandbox, sent };
}

function state(extra = {}) {
  return {
    initialized: true,
    inputEnabled: true,
    restartEnabled: true,
    nextEnabled: false,
    manualSaveEnabled: true,
    startOverEnabled: true,
    savePending: false,
    saveStatus: "idle",
    saveError: "",
    saveRequestId: 0,
    room: "room-01",
    roomNumber: 1,
    roomCount: 3,
    turn: 0,
    status: "풀이 중",
    statusCode: "Playing",
    message: "",
    error: "",
    transitioning: false,
    selectedPieceIndex: 0,
    pieceCount: 3,
    parMoves: 5,
    fingerprint: "playing-0",
    ...extra
  };
}

function clearedState(extra = {}) {
  return state({
    inputEnabled: false,
    nextEnabled: true,
    turn: 9,
    status: "클리어",
    statusCode: "Cleared",
    message: "출구에 도착했습니다",
    fingerprint: "clear-9",
    ...extra
  });
}

function flush() {
  return new Promise((resolve) => setImmediate(resolve));
}

function keyboard(harness, code, key, target, repeat = false, modifiers = {}) {
  let prevented = false;
  harness.documentHandlers.keydown({
    code,
    key,
    target,
    repeat,
    ctrlKey: !!modifiers.ctrlKey,
    metaKey: !!modifiers.metaKey,
    altKey: !!modifiers.altKey,
    preventDefault() { prevented = true; }
  });
  return prevented;
}

(async () => {
  assert.doesNotMatch(bootstrap, /WebGLInput\.captureAllKeyboardInput/);
  assert.match(webGLKeyboardCapture, /Type\.GetType\("UnityEngine\.WebGLInput, UnityEngine\.WebGLModule", false\)/);
  assert.match(webGLKeyboardCapture, /if \(webGLInputType == null\)/);
  assert.match(webGLKeyboardCapture, /captureProperty\.PropertyType != typeof\(bool\)/);
  assert.match(webGLKeyboardCapture, /!captureProperty\.CanRead \|\| !captureProperty\.CanWrite/);
  assert.match(webGLKeyboardCapture, /captureProperty\.SetValue\(null, false\)/);
  assert.match(webGLKeyboardCapture, /captureProperty\.GetValue\(null\)/);
  assert.match(webGLKeyboardCapture, /if \(!\(configuredValue is bool\) \|\| \(bool\)configuredValue\)/);
  assert.match(linker, /assembly fullname="UnityEngine\.WebGLModule"/);
  assert.match(linker, /type fullname="UnityEngine\.WebGLInput" preserve="all"/);
  assert.match(bootstrap, /private void Update\(\)\s*\{\s*#if UNITY_WEBGL && !UNITY_EDITOR\s*return;/);
  assert.match(bootstrap, /new Color32\(0xf0, 0xec, 0xe2, 0xff\)/);
  assert.match(bootstrap, /ContentVersion = "slide-v3"/);
  assert.match(bootstrap, /SaveKey = "nectorial-turn-escape\.save\.v3"/);
  assert.match(bootstrap, /RoomResources = "SlideRooms"/);
  assert.match(bootstrap, /rooms\[0\]\.Id, "slide-01"/);
  assert.match(bootstrap, /using Nectorial\.SlideEscape/);
  assert.match(board, /namespace Nectorial\.SlideEscape\.Unity/);
  assert.match(runtimeAssembly, /"Nectorial\.SlideCore"/);
  assert.match(bootstrap, /new GameMove \{ PieceIndex = _selectedPieceIndex, Direction = command \}/);
  assert.match(bootstrap, /SaveCodec\.Capture\(_room, _state, ContentVersion, _moves\.ToArray\(\), _selectedPieceIndex\)/);
  assert.match(bootstrap, /SaveCodec\.TryRestore\(savedRoom, envelope, ContentVersion, out restored, out restoredMoves, out restoredSelection, out error\)/);
  assert.match(bootstrap, /private void RequestManualSave\(\)/);
  assert.match(bootstrap, /public void OnWebGLPersistenceResult\(string payload\)/);
  assert.match(bootstrap, /if \(!_manualSavePending \|\| requestId != _pendingSaveRequestId\)/);
  assert.match(bootstrap, /NectorialPersistSave\(requestId\)/);
  assert.match(bootstrap, /private bool _restoreBlocked;/);
  assert.match(bootstrap, /BlockOnRestoreError\("save_room_unknown"/);
  assert.match(bootstrap, /inputEnabled = _initialized && !_restoreBlocked && !_manualSavePending && !_transitioning/);
  assert.match(bootstrap, /_roomIndex = 0;[\s\S]*?_moves\.Clear\(\);[\s\S]*?RequestManualSave\(\)/);
  assert.doesNotMatch(bootstrap, /PlayerPrefs\.DeleteAll\(|PlayerPrefs\.DeleteKey\(/);
  assert.match(bootstrap, /private void Undo\(\)/);
  assert.match(bootstrap, /_moves\.RemoveAt\(_moves\.Count - 1\)/);
  assert.match(bootstrap, /TryReplayMoves/);
  assert.doesNotMatch(bootstrap, /save\.v2|content-v2|GameCommand\.Wait|RunStatus\.Captured/);
  assert.match(bootstrap, /public bool transitioning;/);
  assert.match(bootstrap, /MOTION_OBSERVATION phase=/);
  assert.match(bootstrap, /selected=" \+ _transitionMove\.PieceIndex/);
  assert.match(bootstrap, /StartCoroutine\(CompleteTransition/);
  assert.match(bootstrap, /if \(epoch != _transitionEpoch\) yield break;/);
  assert.match(bootstrap, /private void CancelTransition\(\)[\s\S]*?_transitionEpoch\+\+[\s\S]*?StopCoroutine\(_transitionRoutine\)/);
  assert.match(bootstrap, /SetReducedMotion/);
  assert.match(board, /public bool BeginTransition\(RoomDefinition room, GameState before, GameState after, int selectedPieceIndex\)/);
  assert.match(board, /public void AdvanceTransition\(/);
  assert.match(board, /public void CompleteTransition\(/);
  assert.match(board, /public void CancelTransition\(/);
  assert.match(board, /EnsurePieceActors/);
  assert.match(board, /_selectionMarkers/);
  assert.doesNotMatch(board.match(/public void AdvanceTransition[\s\S]*?public void CompleteTransition/)[0], /new GameObject/);
  assert.doesNotMatch(board.match(/public void Render[\s\S]*?public bool BeginTransition/)[0], /ClearDynamicTiles/);
  assert.match(template, /--paper:#eee7d8; --surface:#faf5e9; --ink:#293238/);
  assert.match(template, /grid-template-rows:32px minmax\(0,1fr\) 234px/);
  assert.match(template, /\.play-controls \{[^}]*height:234px;[^}]*grid-template-rows:48px 168px;[^}]*gap:18px;/, "selector row and pad are clearly separated");
  assert.match(template, /\.direction-console \{ grid-row:2; position:relative; width:180px; height:168px/);
  assert.match(template, /\.direction-button \{ position:absolute; z-index:2; width:56px; height:56px/);
  assert.match(template, /\.dpad-base::before \{ left:62px; top:0; width:56px; height:164px; \} \.dpad-base::after \{ left:0; top:56px; width:180px; height:52px; \}/);
  assert.match(template, /\.dpad-base \{[^}]*drop-shadow\(0 4px 0 /, "4px pad shadow ends exactly at the 168px console edge");
  assert.doesNotMatch(template, /\.utility-bar \{[^}]*border-bottom|\.control-slot \{[^}]*border-(top|left):1px/, "no web-form separator rules around the board and pad");
  assert.match(template, /\.direction-button span \{ color:var\(--paper\)/, "cream arrows on the charcoal pad");
  assert.match(template, /<span class="dpad-center" aria-hidden="true"><\/span>/, "pad center is inert decoration");
  assert.doesNotMatch(template, /class="[^"]*dpad-center[^"]*"[^>]*(tabindex|data-game-action)|<button[^>]*dpad-center/, "pad center has no action or focus");
  assert.match(template, /\.piece-mark \{[^}]*border-radius:50%/);
  assert.match(template, /data-piece-index="1"\] \.piece-mark \{[^}]*rotate\(45deg\)/);
  assert.match(template, /data-piece-index="2"\] \.piece-mark \{[^}]*background:var\(--teal\)/);
  assert.match(template, /class="goal-hint"><span class="goal-mark" aria-hidden="true"><\/span><span><strong class="goal-piece">파란 말<\/strong>을 목표 칸에 넣어요<\/span>/);
  assert.match(template, /\.goal-hint \{[^}]*font-size:clamp\(18px,/, "quest line stays at least 18px");
  assert.match(template, /현재 진행을 지우고 첫 번째 판부터 다시 시작해요/);
  assert.match(template, /Math\.floor\(Math\.min\(bounds\.width, bounds\.height, 560\)\)/);
  assert.match(template, /id="turn-runline" class="moves">0번 이동/);
  assert.match(template, /class="top-actions"><button id="restart-top" class="quick-retry"[^>]*data-game-action="Restart"/);
  assert.match(template, /\.utility-bar \{ width:min\(100%,560px\); justify-self:center;/, "HUD stays on the board axis on wide screens");
  assert.match(template, /id="help-toggle"/);
  assert.match(template, /id="help-dialog"/);
  assert.match(template, /\.play-controls\[hidden\] \{ display:grid!important; visibility:hidden; pointer-events:none; \}/);
  assert.doesNotMatch(template, /data-game-action="Undo"|undo-button|되돌/, "solo play has no undo button, style, or help text");
  assert.doesNotMatch(runtime, /"Undo"/, "the web layer can neither map nor dispatch Undo");
  assert.match(template, /canvas \{[^}]*border:0; border-radius:0; background:var\(--stage\)/, "no card frame around the 3D board");
  assert.match(template, /--stage:#f0ece2/, "page paper matches the Unity camera background");
  assert.match(template, /html,body \{[^}]*background:var\(--stage\)/);
  assert.match(template, /id="piece-row" class="piece-row" hidden/, "selector row starts hidden until a multi-piece state arrives");
  assert.match(template, /\.piece-row\[hidden\] \{ display:flex!important; visibility:hidden; pointer-events:none; \}/, "hidden selector keeps its slot so the pad does not move");
  assert.match(template, /data-piece-index="0"/);
  assert.match(template, /id="menu-save"[^>]*>저장하기/);
  assert.match(template, /id="menu-start-over"[^>]*>처음부터 하기/);
  assert.match(template, /id="result-menu"[^>]*>메뉴/);
  assert.match(template, /result-par/);
  assert.doesNotMatch(template, /width:340px|width:96px/);
  assert.match(template, /<a href="\.\.\/index\.html">다른 모드<\/a>/);
  assert.match(template, /#result-dialog/);
  assert.doesNotMatch(template, /#share-entry/);
  assert.match(runtime, /typeof state\.transitioning !== "boolean"/);
  assert.match(runtime, /state\.selectedPieceIndex/);
  assert.match(runtime, /selectionForKeyboardEvent/);
  assert.match(template, /reportReducedMotionPreference/);
  assert.doesNotMatch(template, /button:hover:enabled/);
  assert.match(template, /\.direction-button:enabled:hover \{/);
  assert.match(template, /\.direction-button:enabled:active span \{ transform:translateY\(2px\); \}/);
  assert.match(template, /button:focus-visible,a:focus-visible,canvas:focus-visible,textarea:focus-visible/);
  assert.match(runtime, /event\.repeat/);
  assert.doesNotMatch(runtime, /focusCanvas/);

  const h = createHarness();
  const receive = (next) => h.sandbox.window.__nectorial.receiveState(JSON.stringify(next));
  const body = { closest() { return null; } };
  receive(state());
  assert.equal(h.pieceButtons[0].attributes["aria-pressed"], "true", "target piece starts selected at zero cost");
  assert.equal(h.elements["#turn-runline"].textContent, "0번 이동");
  h.elements["#help-toggle"].dispatch("click");
  assert.equal(h.elements["#help-dialog"].open, true, "help is reachable without leaving the board");
  const beforeHelpInput = h.sent.length;
  h.actionButtons.find((button) => button.dataset.gameAction === "Up").dispatch("click");
  h.pieceButtons[1].dispatch("click");
  assert.equal(keyboard(h, "ArrowUp", "ArrowUp", { closest() { return null; } }), false);
  assert.equal(h.sent.length, beforeHelpInput, "help blocks touch and keyboard game actions");
  h.elements["#help-close"].dispatch("click");
  assert.equal(h.elements["#help-dialog"].open, false);

  h.elements["#menu-toggle"].dispatch("click");
  assert.equal(h.elements["#menu-dialog"].open, true, "the persistent-menu action is available from regular play");
  assert.equal(h.elements["#menu-save-status"].textContent, "저장하기로 이 브라우저 저장을 확인합니다.");
  const beforeManualSave = h.sent.length;
  h.elements["#menu-save"].dispatch("click");
  assert.equal(h.sent.length, beforeManualSave + 1, "manual save uses the normal Unity action channel");
  assert.equal(h.sent.at(-1).action, "Save");
  assert.equal(h.elements["#menu-dialog"].open, true, "the menu stays open while durable save status arrives");

  receive(state({
    inputEnabled: false,
    restartEnabled: false,
    manualSaveEnabled: false,
    startOverEnabled: false,
    savePending: true,
    saveStatus: "pending",
    saveRequestId: 1,
    fingerprint: "save-pending"
  }));
  assert.equal(h.elements["#menu-save"].disabled, true, "manual save cannot overlap an outstanding persistence request");
  assert.equal(h.elements["#menu-start-over"].disabled, true, "state-changing reset is paused while persistence is pending");
  assert.equal(h.elements["#menu-save-status"].textContent, "이 브라우저에 저장하는 중…");
  const pendingInputCount = h.sent.length;
  h.actionButtons.find((button) => button.dataset.gameAction === "Restart").dispatch("click");
  assert.equal(h.sent.length, pendingInputCount, "pending persistence cannot be bypassed through the existing retry control");

  receive(state({
    saveStatus: "failed",
    saveError: "webgl_persist_timeout",
    fingerprint: "save-timeout"
  }));
  assert.equal(h.elements["#menu-save"].disabled, false, "a failed durable save remains retryable without resetting the run");
  assert.equal(h.elements["#menu-save-status"].textContent, "저장하지 못했습니다. 다시 저장할 수 있습니다.");
  receive(state({
    inputEnabled: false,
    manualSaveEnabled: false,
    startOverEnabled: true,
    error: "content_version_mismatch",
    saveStatus: "failed",
    saveError: "content_version_mismatch",
    fingerprint: "restore-blocked"
  }));
  assert.equal(h.elements["#menu-save"].disabled, true, "an incompatible restored payload cannot be overwritten by an ordinary save");
  assert.equal(h.elements["#menu-start-over"].disabled, false, "full reset remains an explicit recovery for a blocked restore");
  assert.equal(h.elements["#menu-save-status"].textContent, "저장된 진행을 확인할 수 없습니다. 이 방을 다시 시작하거나 처음부터 할 수 있습니다.");
  const blockedMovementCount = h.sent.length;
  h.actionButtons.find((button) => button.dataset.gameAction === "Up").dispatch("click");
  assert.equal(h.sent.length, blockedMovementCount, "open menu and blocked restore cannot mutate the rejected payload");
  h.elements["#menu-continue"].dispatch("click");
  h.actionButtons.find((button) => button.dataset.gameAction === "Restart").dispatch("click");
  assert.equal(h.sent.at(-1).action, "Restart", "current-room retry is an explicit recovery path for blocked restore data");
  receive(state({ saveStatus: "failed", saveError: "webgl_persist_timeout", fingerprint: "save-timeout-retry" }));
  h.elements["#menu-toggle"].dispatch("click");
  const beforeCancelledStartOver = h.sent.length;
  h.elements["#menu-start-over"].dispatch("click");
  assert.equal(h.elements["#menu-confirmation"].hidden, false, "start-over requires an in-game confirmation");
  h.elements["#menu-start-over-cancel"].dispatch("click");
  assert.equal(h.elements["#menu-main"].hidden, false, "cancel returns to the unchanged menu state");
  assert.equal(h.sent.length, beforeCancelledStartOver, "cancel does not dispatch a state or save mutation");
  h.elements["#menu-start-over"].dispatch("click");
  let menuCancelPrevented = false;
  h.elements["#menu-dialog"].dispatch("cancel", { preventDefault() { menuCancelPrevented = true; } });
  assert.equal(menuCancelPrevented, true, "Escape cancels the start-over confirmation in place");
  assert.equal(h.elements["#menu-confirmation"].hidden, true);
  assert.equal(h.sent.length, beforeCancelledStartOver, "Escape does not dispatch a reset or persistence request");
  h.elements["#menu-start-over"].dispatch("click");
  h.elements["#menu-start-over-confirm"].dispatch("click");
  assert.equal(h.sent.at(-1).action, "StartOver", "confirmed start-over uses the same guarded Unity channel");
  assert.equal(h.elements["#menu-dialog"].open, true, "the menu remains open to show the reset persistence outcome");
  h.elements["#menu-continue"].dispatch("click");
  assert.equal(h.elements["#menu-dialog"].open, false);
  receive(state());

  assert.equal(keyboard(h, "ArrowRight", "ArrowRight", body), true);
  assert.equal(h.sent.at(-1).action, "Right");
  receive(state({ turn: 1, fingerprint: "after-slide" }));
  const beforeSpace = h.sent.length;
  assert.equal(keyboard(h, "Space", " ", body), false, "Space is not a hidden undo shortcut in solo play");
  assert.equal(keyboard(h, "Space", "Spacebar", body), false);
  assert.equal(h.sent.length, beforeSpace, "Space on the page dispatches nothing");
  assert.equal(keyboard(h, "KeyR", "r", body), true);
  assert.equal(h.sent.at(-1).action, "Restart");

  const upButton = h.actionButtons.find((button) => button.dataset.gameAction === "Up");
  const sentBeforeNativeButton = h.sent.length;
  assert.equal(keyboard(h, "Space", " ", upButton), false, "native button default is simulated separately");
  upButton.dispatch("click");
  assert.equal(h.sent.length, sentBeforeNativeButton + 1, "button Space/Enter default produces one click path");
  assert.equal(h.sent.at(-1).action, "Up");
  assert.equal(keyboard(h, "Space", " ", upButton, true), true, "repeated native button activation is suppressed");
  receive(state({ turn: 2, fingerprint: "after-native-space" }));
  const sentBeforeEnter = h.sent.length;
  assert.equal(keyboard(h, "Enter", "Enter", upButton), false, "native Enter default is simulated separately");
  upButton.dispatch("click");
  assert.equal(h.sent.length, sentBeforeEnter + 1, "button Enter default produces one click path");
  assert.equal(h.sent.at(-1).action, "Up");
  assert.equal(keyboard(h, "Enter", "Enter", upButton, true), true, "repeated Enter activation is suppressed");
  assert.equal(h.sent.length, sentBeforeEnter + 1, "held Enter cannot add another turn");
  assert.equal(keyboard(h, "ArrowLeft", "ArrowLeft", upButton), true, "focused game buttons still accept directional hotkeys");
  assert.equal(h.sent.at(-1).action, "Left");
  assert.equal(keyboard(h, "KeyR", "r", upButton), true, "focused game buttons still accept restart hotkeys");
  assert.equal(h.sent.at(-1).action, "Restart");
  const beforeModifiedButtonClick = h.sent.length;
  upButton.dispatch("click", { ctrlKey: true });
  assert.equal(h.sent.length, beforeModifiedButtonClick, "modified button activation cannot bypass the hotkey guard");

  const beforeRepeat = h.sent.length;
  assert.equal(keyboard(h, "ArrowUp", "ArrowUp", body, true), true);
  assert.equal(h.sent.length, beforeRepeat, "held-key repeat must not dispatch another turn");

  const editable = element({ tag: "input" });
  const beforeEditable = h.sent.length;
  assert.equal(keyboard(h, "ArrowLeft", "ArrowLeft", editable), false);
  h.elements["#menu-dialog"].open = true;
  assert.equal(keyboard(h, "ArrowLeft", "ArrowLeft", body), false);
  h.elements["#menu-dialog"].open = false;
  assert.equal(h.sent.length, beforeEditable, "editable and dialog contexts cannot feed gameplay");
  const beforeModifier = h.sent.length;
  assert.equal(keyboard(h, "ArrowDown", "ArrowDown", body, false, { ctrlKey: true }), false);
  assert.equal(keyboard(h, "KeyR", "r", body, false, { metaKey: true }), false);
  assert.equal(h.sent.length, beforeModifier, "modified keyboard shortcuts cannot feed gameplay");

  assert.equal(h.elements["#piece-row"].hidden, false, "multi-piece rooms show the piece selector");
  receive(state({ pieceCount: 1, selectedPieceIndex: 0, fingerprint: "single-piece" }));
  assert.equal(h.elements["#piece-row"].hidden, true, "a single-piece room shows no selector");
  const beforeSingleSelection = h.sent.length;
  assert.equal(keyboard(h, "Digit1", "1", body), false, "a lone piece has nothing to select");
  h.pieceButtons[0].dispatch("click");
  assert.equal(h.sent.length, beforeSingleSelection, "no selection message for a single-piece room");
  assert.equal(keyboard(h, "ArrowRight", "ArrowRight", body), true, "directions still work in a single-piece room");
  assert.equal(h.sent.at(-1).action, "Right");
  receive(state({ fingerprint: "multi-piece-again" }));
  assert.equal(h.elements["#piece-row"].hidden, false, "the selector returns for multi-piece rooms");

  const selectionCount = h.sent.length;
  h.pieceButtons[1].dispatch("click");
  assert.equal(h.sent.length, selectionCount + 1, "piece chip selects without sending a move");
  assert.equal(h.sent.at(-1).method, "SelectPiece");
  assert.equal(h.sent.at(-1).action, "1");
  assert.equal(keyboard(h, "Digit3", "3", body), true, "number keys select available pieces");
  assert.equal(h.sent.at(-1).method, "SelectPiece");
  assert.equal(h.sent.at(-1).action, "2");
  receive(state({ pieceCount: 2, selectedPieceIndex: 1, turn: 1, fingerprint: "two-pieces" }));
  assert.equal(h.pieceButtons[2].hidden, true, "unavailable helper chip is hidden from the runline");
  assert.equal(h.pieceButtons[1].attributes["aria-pressed"], "true", "selected chip exposes pressed state");
  const beforeUnavailableSelection = h.sent.length;
  assert.equal(keyboard(h, "Digit3", "3", body), false);
  assert.equal(h.sent.length, beforeUnavailableSelection, "selection outside current piece count is free but unavailable");
  receive(state({ turn: 1, fingerprint: "after-selection" }));

  receive(state({ inputEnabled: false, manualSaveEnabled: false, startOverEnabled: false, transitioning: true, fingerprint: "moving-slide" }));
  assert.equal(h.elements["#result-dialog"].open, false, "terminal result waits for the motion commit");
  assert.equal(h.elements["#play-controls"].hidden, false, "transition keeps the reserved playing controls layout");
  const sentBeforeTransitionInput = h.sent.length;
  keyboard(h, "ArrowDown", "ArrowDown", body);
  assert.equal(h.sent.length, sentBeforeTransitionInput, "movement input is locked during the board transition");
  assert.equal(keyboard(h, "Space", " ", body), false, "Space does not undo or cancel a glide from the page");
  assert.equal(h.sent.length, sentBeforeTransitionInput, "no web input reaches Unity during the glide");
  const transitionRetry = h.actionButtons.find((button) => button.dataset.gameAction === "Restart");
  assert.equal(transitionRetry.disabled, false, "restart remains available during transition cancellation");

  receive(clearedState());
  assert.equal(h.elements["#result-dialog"].open, true, "cleared state opens an overlay result dialog");
  assert.equal(h.elements["#result-title"].textContent, "클리어");
  assert.equal(h.elements["#result-turn"].textContent, "9수");
  assert.equal(h.elements["#result-par"].textContent, "최단 5수");
  assert.equal(h.elements["#result-share"].hidden, false, "share is reachable inside the cleared result dialog");
  assert.equal(h.elements["#result-next"].hidden, false, "next is visible only for a cleared state with a next room");
  const beforeResultInput = h.sent.length;
  assert.equal(keyboard(h, "ArrowDown", "ArrowDown", body), false, "result dialog blocks gameplay input behind its layer");
  assert.equal(h.sent.length, beforeResultInput);
  let resultCancelPrevented = false;
  h.elements["#result-dialog"].dispatch("cancel", { preventDefault() { resultCancelPrevented = true; } });
  assert.equal(resultCancelPrevented, true, "result dialog remains action-gated until retry or next");
  h.elements["#result-menu"].dispatch("click");
  assert.equal(h.elements["#menu-dialog"].open, true, "the clear result exposes a menu path even on the final room");
  assert.equal(h.elements["#result-dialog"].open, false, "the result is temporarily behind the menu, not discarded");
  h.elements["#menu-continue"].dispatch("click");
  assert.equal(h.elements["#result-dialog"].open, true, "leaving the clear-state menu restores the result overlay");

  let resolveOldNative;
  let nativeCalls = 0;
  let nativePayload;
  const copied = [];
  h.navigator.share = (payload) => {
    nativeCalls += 1;
    nativePayload = payload;
    return new Promise((resolve) => { resolveOldNative = resolve; });
  };
  h.navigator.clipboard = { writeText: async (text) => { copied.push(text); } };
  h.elements["#result-share"].dispatch("click");
  assert.equal(h.elements["#share-dialog"].open, true, "share opens above the cleared result dialog");
  assert.equal(h.elements["#result-dialog"].open, true);
  assert.equal(h.elements["#share-text"].value, "Few Moves | 방 1 / 3 | 9수 클리어");
  h.elements["#share-native"].dispatch("click");
  await flush();
  assert.equal(nativeCalls, 1);
  assert.equal(nativePayload.title, "Few Moves");
  assert.equal(nativePayload.text, "Few Moves | 방 1 / 3 | 9수 클리어");
  assert.equal(h.elements["#share-native"].disabled, true);

  receive(state({ turn: 0, fingerprint: "restart-0" }));
  assert.equal(h.elements["#result-dialog"].open, false, "restart state closes the result dialog");
  receive(clearedState({ turn: 10, fingerprint: "clear-10" }));
  h.elements["#result-share"].dispatch("click");
  h.elements["#share-copy"].dispatch("click");
  await flush();
  assert.deepEqual(copied, ["Few Moves | 방 1 / 3 | 10수 클리어"], "new clear can copy while an old native share hangs");
  h.elements["#share-native"].dispatch("click");
  await flush();
  assert.equal(nativeCalls, 1, "only one native share operation remains outstanding");
  resolveOldNative();
  await flush();
  assert.equal(h.elements["#share-panel-feedback"].textContent, "기록을 클립보드에 복사했습니다.", "old native completion cannot overwrite new-copy feedback");
  h.elements["#share-close"].dispatch("click");
  assert.equal(h.elements["#share-dialog"].open, false, "local close remains available while sharing recovers");
  assert.equal(h.elements["#result-dialog"].open, true, "closing the share subdialog restores result-dialog access");
  receive(state({ turn: 1, fingerprint: "playing-1" }));
  keyboard(h, "ArrowDown", "ArrowDown", body);
  assert.equal(h.sent.at(-1).action, "Down", "closing a share panel does not block resumed gameplay");

  h.navigator.share = undefined;
  h.navigator.clipboard = { writeText: async () => { throw new Error("clipboard blocked"); } };
  receive(clearedState({ turn: 11, fingerprint: "clear-11" }));
  h.elements["#result-share"].dispatch("click");
  h.elements["#share-copy"].dispatch("click");
  await flush();
  assert.equal(h.elements["#share-panel-feedback"].textContent, "위 기록을 선택해 직접 복사하세요.");
  assert.equal(h.elements["#share-text"].value, "Few Moves | 방 1 / 3 | 11수 클리어");
  h.elements["#share-close"].dispatch("click");

  let clipboardCalls = 0;
  h.navigator.clipboard = { writeText: async () => { clipboardCalls += 1; } };
  h.navigator.share = async () => { const error = new Error("cancel"); error.name = "AbortError"; throw error; };
  receive(clearedState({ turn: 12, fingerprint: "clear-12" }));
  h.elements["#result-share"].dispatch("click");
  h.elements["#share-native"].dispatch("click");
  await flush();
  assert.equal(clipboardCalls, 0, "native-share cancellation must not auto-copy");
  assert.equal(h.elements["#share-panel-feedback"].textContent, "", "native-share cancellation must not claim delivery");
  h.elements["#share-close"].dispatch("click");

  receive(state({ saveStatus: "failed", saveError: "save_write_failed:IOException", message: "저장할 수 없습니다." }));
  const restartButton = h.actionButtons.find((button) => button.dataset.gameAction === "Restart");
  assert.equal(restartButton.disabled, false, "a retryable save failure keeps the current-room retry available");
  restartButton.dispatch("click");
  assert.equal(h.sent.at(-1).action, "Restart");

  receive(clearedState({ nextEnabled: false, turn: 13, fingerprint: "clear-final" }));
  assert.equal(h.elements["#result-dialog"].open, true);
  assert.equal(h.elements["#result-next"].hidden, true, "final cleared room hides next stage");

  const sentBeforeMalformed = h.sent.length;
  receive(state({ selectedPieceIndex: 3, fingerprint: "invalid-selection" }));
  assert.equal(h.elements["#feedback"].hidden, false, "out-of-range selection fails closed");
  const partialState = state();
  delete partialState.transitioning;
  receive(partialState);
  assert.equal(h.elements["#result-dialog"].open, false, "partial observations fail closed and close result layers");
  h.sandbox.window.__nectorial.receiveState("{}");
  assert.equal(h.elements["#feedback"].hidden, false);
  assert.equal(h.elements["#result-dialog"].open, false);
  keyboard(h, "ArrowDown", "ArrowDown", body);
  assert.equal(h.sent.length, sentBeforeMalformed, "malformed state fails closed before keyboard dispatch");

  assert.equal(h.sent.some((entry) => entry.action === "Undo"), false, "Undo was never dispatched from solo UI");
  receive(state());
  h.sandbox.window.__nectorial.receiveState("{}");
  assert.equal(h.elements["#piece-row"].hidden, true, "invalid observations hide the selector");

  console.log("mobile UI contract checks passed");
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
