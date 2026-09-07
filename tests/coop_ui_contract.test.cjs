const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const template = readFileSync(path.join(root, "Assets/WebGLTemplates/CoopPilot/index.html"), "utf8");
const bridge = readFileSync(path.join(root, "Assets/Plugins/WebGL/CoopState.jslib"), "utf8");
const bootstrap = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Coop/CoopBootstrap.cs"), "utf8");
const board = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Coop/CoopBoardView.cs"), "utf8");
const build = readFileSync(path.join(root, "Assets/Editor/CoopPilotBuild.cs"), "utf8");
const solo = readFileSync(path.join(root, "Assets/Nectorial/Runtime/GameBootstrap.cs"), "utf8");
const asmdef = readFileSync(path.join(root, "Assets/Nectorial/Runtime/Nectorial.Runtime.asmdef"), "utf8");

assert.match(template, /id="unity-canvas"[^>]*aria-label="원과 마름모가 함께 움직이는 협력 보드"/);
assert.match(template, /data-coop-action="Slide" data-direction="Up"/);
assert.match(template, /data-coop-action="Pass"/);
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
assert.match(bootstrap, /seatAuthority = "session_state_only"/);
assert.match(bootstrap, /localHotseat = true/);
assert.match(bootstrap, /private const string SaveKey = "nectorial-coop\.save\.v1"/);
assert.match(bootstrap, /NectorialCoopReportState\(json\)/);
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
assert.match(solo, /Few Moves Coop Pilot/);
assert.match(asmdef, /Nectorial\.CoopCore/);

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
