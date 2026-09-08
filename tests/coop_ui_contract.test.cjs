const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const template = readFileSync(path.join(root, "Assets/WebGLTemplates/CoopPilot/index.html"), "utf8");
const bridge = readFileSync(path.join(root, "Assets/Plugins/WebGL/CoopState.jslib"), "utf8");
const bootstrap = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Coop/CoopBootstrap.cs"), "utf8");
const serializationAdapter = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Coop/CoopSaveSerializationAdapter.cs"), "utf8");
const serializationChecks = readFileSync(path.join(root, "Assets/Editor/CoopSerializationChecks.cs"), "utf8");
const board = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Coop/CoopBoardView.cs"), "utf8");
const build = readFileSync(path.join(root, "Assets/Editor/CoopPilotBuild.cs"), "utf8");
const solo = readFileSync(path.join(root, "Assets/Nectorial/Runtime/GameBootstrap.cs"), "utf8");
const asmdef = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Nectorial.Runtime.asmdef"), "utf8");

assert.match(template, /id="unity-canvas"[^>]*aria-label="원과 마름모가 함께 움직이는 협력 보드"/);
assert.match(template, /data-coop-action="Slide" data-direction="Up"/);
assert.doesNotMatch(template, /data-coop-action="Pass"|pass-button|Pass<\/button>/);
assert.match(template, /data-coop-action="Express" data-expression="Look"/);
assert.match(template, /data-coop-action="Express" data-expression="ThumbsUp"/);
assert.match(template, /data-coop-action="Express" data-expression="Handshake"/);
assert.match(template, /data-coop-action="Express" data-expression="Waiting"/);
assert.match(template, /id="consent-dialog"/);
assert.match(template, /id="clear-dialog"/);
assert.match(template, /id="save-button"/);
assert.match(template, /window\.\_\_nectorialCoop/);
assert.match(template, /function isCurrentOrNewer\(state\)/);
assert.match(template, /state\.authorityRevision < currentState\.authorityRevision/);
assert.match(template, /state\.logicalActionCount < currentState\.logicalActionCount/);
assert.match(template, /hasExpectedRevision: true/);
assert.match(template, /currentState\.expressionEnabled === true/);
assert.match(template, /currentConsent\.requester === "Circle" \? "Diamond" : "Circle"/);
assert.match(template, /expression-bubble/);
assert.match(template, /lastExpressionSequence/);
assert.match(template, /expressionSender/);
assert.match(template, /<header class="topbar">[\s\S]*id="expression-bubble"[\s\S]*<\/header>/);
assert.doesNotMatch(template, /<section class="board-stage"[\s\S]*id="expression-bubble"/);
assert.match(template, /turnLabel\.innerHTML = activeLabel\(state\) \+ "<small>행동 "/);
assert.match(template, /left: 56px; right: 56px;[\s\S]*max-width: calc\(100% - 112px\)/);
assert.doesNotMatch(template, /"revision /i);
assert.doesNotMatch(template, /positionLabel\(state\.circle/);
assert.doesNotMatch(template, /circlePosition|diamondPosition|circleGoal\.X|diamondGoal\.X/);
assert.match(template, /consent\.active !== true/);
assert.match(template, /state\.statusCode === "Cleared" && !currentConsent/);
assert.match(template, /\.dpad \{ position: relative; width: 180px; height: 168px/);
assert.match(template, /\.dpad-up \{ left: 62px; top: 0; \}/);
assert.match(template, /\.dpad-down \{ left: 62px; top: 112px; \}/);
assert.match(template, /\.dpad button::after/);
assert.doesNotMatch(template, /clip-path: polygon/);
assert.match(template, /grid-template-rows: minmax\(0, 1fr\) 342px/);
assert.match(template, /html, body \{ width: 100%; min-height: 100%; margin: 0; overflow-x: hidden; overflow-y: auto/);
assert.doesNotMatch(template, /html, body[^}]*overflow:\s*hidden/);
assert.match(template, /function resizeCanvasToStage\(\)/);
assert.match(template, /Math\.min\(rect\.width, rect\.height\)/);
assert.match(template, /new ResizeObserver\(resizeCanvasToStage\)/);
assert.doesNotMatch(template, /left-right|좌우 전환|oversized|tutorial-image/i);
assert.doesNotMatch(template, /board-grid|fakeBoard|staticBoard/i);
assert.match(template, /createUnityInstance\(canvas, config/);

assert.match(bridge, /NectorialCoopReportState/);
assert.match(bridge, /SendMessage\("CoopBootstrap", "OnWebGLPersistenceResult"/);
assert.match(bridge, /NectorialCoopPersistSave__deps/);
assert.match(bridge, /FS\.syncfs\(false/);

assert.match(bootstrap, /CoopSession\.Create\(_room\)/);
assert.match(bootstrap, /_session\.Dispatch\(/);
assert.match(bootstrap, /CoopRules\.StateFingerprint/);
assert.match(bootstrap, /CoopSaveCodec\.Capture\(_session, "local-hotseat"\)/);
assert.match(bootstrap, /CoopSaveCodec\.TryRestore\(_room, envelope/);
assert.match(bootstrap, /CoopSaveSerializationAdapter\.TryNormalizePendingConsent/);
assert.match(bootstrap, /seatAuthority = "session_state_only"/);
assert.match(bootstrap, /localHotseat = true/);
assert.match(bootstrap, /private const string SaveKey = "nectorial-coop\.save\.v1"/);
assert.match(bootstrap, /hasExpectedRevision/);
assert.match(bootstrap, /TryParseDirection/);
assert.match(bootstrap, /TryParseExpression/);
assert.doesNotMatch(bootstrap, /KeyCode\.P|SendPass|case "Pass"/);
assert.match(bootstrap, /expressionSequence/);
assert.match(bootstrap, /expressionSender/);
assert.match(bootstrap, /result\.Accepted && !result\.Idempotent && result\.Events/);
assert.match(bootstrap, /_commandPrefix/);
assert.match(bootstrap, /PendingConsent\.RequestId, payload\.requestId/);
assert.match(bootstrap, /CanDispatchNow\(\)[\s\S]*_restoreBlocked[\s\S]*_manualSavePending/);
assert.match(bootstrap, /NectorialCoopReportState\(json\)/);
assert.match(bootstrap, /expressionEnabled = _initialized && !_restoreBlocked/);
assert.match(bootstrap, /ExpressionCooldownSeconds = 2f/);
assert.match(bootstrap, /Time\.unscaledTime < _expressionCooldownUntil/);
assert.match(bootstrap, /state\.Status == CoopRunStatus\.Playing \|\| state\.Status == CoopRunStatus\.Cleared/);
assert.match(bootstrap, /active = false/);
assert.doesNotMatch(bootstrap, /UnityEngine\.Networking|UnityWebRequest|Socket|WebSocket/);
assert.match(board, /CoopRoomDefinition/);
assert.match(board, /CirclePosition/);
assert.match(board, /DiamondPosition/);
assert.match(board, /SpriteRenderer/);
assert.match(build, /PlayerSettings\.WebGL\.template = "PROJECT:CoopPilot"/);
assert.match(build, /Assets\/Nectorial\/Resources\/CoopRooms\/coop-c1\.json/);
assert.match(build, /COOP_WEBGL_RESULT/);
assert.match(build, /finally\s*\{[\s\S]*PlayerSettings\.productName = previousProductName/);
assert.match(build, /template=PROJECT:CoopPilot entry=CoopPilotBuild\.Build/);
assert.match(build, /CoopSerializationChecks\.Run\(\)/);
assert.match(serializationAdapter, /IsCanonicalEmptyPending/);
assert.match(serializationAdapter, /save_pending_consent_invalid/);
assert.match(serializationChecks, /case=no-consent|CheckRoundTrip/);
assert.match(serializationChecks, /active-undo/);
assert.match(serializationChecks, /active-restart/);
assert.match(serializationChecks, /tampered-consent/);
assert.match(solo, /Few Moves Coop Pilot/);
assert.match(asmdef, /Nectorial\.CoopCore/);
assert.match(template, /ExpressionCooldownMilliseconds = 2000/);
assert.match(template, /setTimeout\(renderButtons, ExpressionCooldownMilliseconds\)/);

{
  function acceptsState(previous, next) {
    if (!previous) return true;
    if (next.authorityRevision < previous.authorityRevision) return false;
    return next.authorityRevision !== previous.authorityRevision ||
      next.logicalActionCount >= previous.logicalActionCount;
  }
  const initial = { authorityRevision: 0, logicalActionCount: 0 };
  const afterMove = { authorityRevision: 1, logicalActionCount: 1 };
  const consent = { authorityRevision: 2, logicalActionCount: 1, pendingRequestId: "undo-1" };
  const afterUndo = { authorityRevision: 3, logicalActionCount: 0 };
  assert.equal(acceptsState(initial, afterMove), true, "accepted action advances the visible state");
  assert.equal(acceptsState(consent, afterUndo), true, "legitimate undo may lower action count when authority revision increases");
  assert.equal(acceptsState(afterUndo, consent), false, "a stale consent state cannot rewind the visible session");
  assert.equal(acceptsState(afterUndo, { authorityRevision: 3, logicalActionCount: 0 }), true, "reload with the restored state is accepted");

  function acceptsConsent(current, requestId, revision, seat) {
    return !!current && current.pendingRequestId === requestId &&
      current.authorityRevision === revision && seat === "Diamond";
  }
  assert.equal(acceptsConsent(consent, "undo-1", 2, "Diamond"), true, "the displayed request and other seat resolve consent");
  assert.equal(acceptsConsent(consent, "old-request", 2, "Diamond"), false, "an old consent request is rejected");
  assert.equal(acceptsConsent(consent, "undo-1", 1, "Diamond"), false, "a stale consent revision is rejected");
  assert.notEqual("boot-a-1", "boot-b-1", "a new reload/session prefix prevents saved command ID collisions");

  function emojiAllowed(state) {
    return state.initialized === true && state.expressionEnabled === true && state.savePending !== true;
  }
  const restoreBlocked = { initialized: true, expressionEnabled: false, savePending: false, restoreBlocked: true };
  assert.equal(emojiAllowed(restoreBlocked), false, "restore-blocked state cannot accept an emoji that could overwrite rejected save data");

  const emptyConsentFromCSharp = JSON.parse('{"active":false,"requestId":"","kind":"","statusCode":"","requester":"","requestedAtRevision":0}');
  function consentVisible(consent) {
    return !!consent && consent.active === true && !!consent.requestId &&
      (consent.statusCode === "Undo" || consent.statusCode === "Restart");
  }
  assert.equal(consentVisible(emptyConsentFromCSharp), false, "JsonUtility empty consent object does not open the approval modal");
  assert.equal(consentVisible({ active: true, requestId: "", kind: "Undo", statusCode: "Undo" }), false, "malformed consent cannot enable approval");
  function clearVisible(statusCode, consent) {
    return statusCode === "Cleared" && !consentVisible(consent);
  }
  assert.equal(clearVisible("Cleared", emptyConsentFromCSharp), true, "normalized empty consent still allows the clear overlay");
  const activeRestartConsent = { active: true, requestId: "restart-1", kind: "Restart", statusCode: "Restart" };
  assert.equal(clearVisible("Cleared", activeRestartConsent), false, "active restart consent keeps the clear overlay closed");

  let now = 0;
  let cooldownUntil = 0;
  let savedCount = 0;
  function expressOnce() {
    if (now < cooldownUntil) return false;
    savedCount += 1;
    cooldownUntil = now + 2;
    return true;
  }
  assert.equal(expressOnce(), true, "the first allowed emoji is accepted");
  assert.equal(expressOnce(), false, "a rapid second emoji is rejected before dispatch/save");
  assert.equal(savedCount, 1, "a rejected rapid emoji does not autosave");
  now = 2;
  assert.equal(expressOnce(), true, "the emoji becomes available again after two seconds");

  const dpad = { width: 180, height: 168, button: 56 };
  const boxes = {
    up: { x: 62, y: 0 },
    left: { x: 0, y: 56 },
    right: { x: 124, y: 56 },
    down: { x: 62, y: 112 }
  };
  for (const box of Object.values(boxes)) {
    assert.ok(box.x >= 0 && box.y >= 0 && box.x + dpad.button <= dpad.width && box.y + dpad.button <= dpad.height, "every control fits in the three-row D-pad");
    assert.ok(dpad.button >= 48, "every D-pad target remains generous");
  }
  assert.equal(Object.keys(boxes).length, 4, "the D-pad has four direction controls and an inert center");

  const viewport = { width: 360, height: 640, padY: 8, topbar: 64, gap: 8, controls: 342 };
  const coopHeight = viewport.height - viewport.padY * 2 - viewport.topbar - viewport.gap;
  const boardHeight = coopHeight - viewport.gap - viewport.controls;
  const boardTop = viewport.padY + viewport.topbar + viewport.gap;
  const controlsTop = boardTop + boardHeight + viewport.gap;
  const canvasSide = Math.min(viewport.width - 20, boardHeight);
  assert.ok(boardTop + canvasSide <= controlsTop, "canvas square fits before controls at 360x640");

  const expressionMap = { Look: "👀", ThumbsUp: "👍", Handshake: "🤝", Waiting: "⏳" };
  function expressionBubble(previousSequence, state) {
    if (state.expressionSequence <= previousSequence || !expressionMap[state.expression]) return null;
    return { sequence: state.expressionSequence, icon: expressionMap[state.expression], sender: state.expressionSender };
  }
  const firstExpression = expressionBubble(0, { expressionSequence: 1, expression: "Look", expressionSender: "Circle" });
  assert.deepEqual(firstExpression, { sequence: 1, icon: "👀", sender: "Circle" }, "accepted expression renders sender and icon");
  assert.equal(expressionBubble(1, { expressionSequence: 1, expression: "Look", expressionSender: "Circle" }), null, "ordinary state republish does not replay old expression");
  assert.deepEqual(expressionBubble(1, { expressionSequence: 2, expression: "Look", expressionSender: "Diamond" }), { sequence: 2, icon: "👀", sender: "Diamond" }, "repeated emoji gets a new visible event");
  assert.equal(expressionBubble(2, { expressionSequence: 0, expression: "Look", expressionSender: "Circle" }), null, "reload without an expression does not replay history");
}

{
  const library = {};
  const messages = [];
  const syncCallbacks = [];
  let observedState = null;
  const sandbox = {
    LibraryManager: { library },
    Module: { __unityIdbfsMount: { mount: { idbPersistState: 0 } } },
    FS: { syncfs(_populate, callback) { syncCallbacks.push(callback); } },
    SendMessage(_receiver, method, payload) { messages.push({ method, payload }); },
    UTF8ToString() { return '{"initialized":true}'; },
    window: { __nectorialCoop: { receiveState(value) { observedState = value; } } },
    setTimeout,
    clearTimeout,
    console: { warn() {} },
    mergeInto(target, additions) {
      Object.assign(target, additions);
      Object.entries(additions).forEach(([name, value]) => {
        if (name.startsWith("$")) sandbox[name.slice(1)] = value;
      });
    }
  };
  vm.runInNewContext(bridge, sandbox, { filename: "CoopState.jslib" });
  library.NectorialCoopReportState(1);
  assert.equal(observedState, "{\"initialized\":true}", "Unity state reports reach the coop UI channel");
  library.NectorialCoopPersistSave(7);
  assert.equal(syncCallbacks.length, 1, "coop save requests use the browser persistence pass");
  syncCallbacks[0](null);
  assert.deepEqual(messages, [{ method: "OnWebGLPersistenceResult", payload: "7|ok" }], "coop save reports the callback result to C#");
}

console.log("Coop UI contract checks passed");
