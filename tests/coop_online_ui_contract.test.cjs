const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const template = readFileSync(path.join(root, "Assets/WebGLTemplates/CoopOnline/index.html"), "utf8");
const bridge = readFileSync(path.join(root, "Assets/Plugins/WebGL/CoopOnlineNetwork.jslib"), "utf8");
const bootstrap = readFileSync(path.join(root, "Assets/Nectorial/Runtime/CoopOnline/CoopOnlineBootstrap.cs"), "utf8");
const build = readFileSync(path.join(root, "Assets/Editor/CoopOnlineBuild.cs"), "utf8");
const solo = readFileSync(path.join(root, "Assets/Nectorial/Runtime/GameBootstrap.cs"), "utf8");

assert.match(template, /data-online-action="Slide" data-direction="Up"/);
assert.doesNotMatch(template, /data-online-action="Pass"|>Pass</);
assert.match(template, /data-online-action="Express" data-expression="Look"/);
assert.match(template, /id="create-button"/);
assert.match(template, /id="invite-input"/);
assert.match(template, /id="join-button"/);
assert.match(template, /id="leave-button"/);
assert.match(template, /expressionSequence/);
assert.match(template, /lastExpressionSequence/);
assert.match(template, /activeActorCode/);
assert.match(template, /availabilityCode/);
assert.match(template, /pendingConsent && currentState\.pendingConsent\.active === true/);
assert.match(template, /state\.authorityRevision === currentState\.authorityRevision && state\.logicalActionCount < currentState\.logicalActionCount/);
assert.doesNotMatch(template, /seatToken|bearer|createRequestId|joinRequestId/);

assert.match(bridge, /\/api\/coop\/v1/);
assert.match(bridge, /Authorization.*Bearer/);
assert.match(bridge, /localStorage/);
assert.match(bridge, /sessionStorage/);
assert.match(bridge, /fewmoves\.online\.seats\.v1/);
assert.match(bridge, /fewmoves\.online\.current\.v1/);
assert.match(bridge, /fewmoves\.online\.retry\.v1/);
assert.match(bridge, /retrySecret/);
assert.match(bridge, /generation/);
assert.match(bridge, /storage_unavailable/);
assert.match(bridge, /secure_random_unavailable/);
assert.match(bridge, /return true;/);
assert.match(bridge, /createRequestId/);
assert.match(bridge, /joinRequestId/);
assert.match(bridge, /NectorialOnlineCreate/);
assert.match(bridge, /NectorialOnlineJoin/);
assert.match(bridge, /NectorialOnlineCommand/);
assert.match(bridge, /NectorialOnlineReportState/);
assert.match(bridge, /sanitizedCopy/);
assert.match(bridge, /delete copy\.seatToken/);
assert.doesNotMatch(bridge, /console\.log\(.*seatToken|console\.log\(.*bearer/);

assert.match(bootstrap, /NectorialOnlineCreate/);
assert.match(bootstrap, /NectorialOnlineJoin/);
assert.match(bootstrap, /NectorialOnlineCommand/);
assert.match(bootstrap, /bearer_derived/);
assert.match(bootstrap, /private static void ConfigureCamera\(\)/);
assert.match(bootstrap, /using Nectorial\.SlideEscape\.Unity\.Coop;/);
assert.match(bootstrap, /MatchesBundledRoom/);
assert.match(bootstrap, /expressionHighWater/);
assert.match(bootstrap, /_expressionHydrated/);
assert.match(bootstrap, /result\.op == "resume"/);
assert.match(bootstrap, /_roomView = null/);
assert.match(bootstrap, /COOP_ONLINE_STATE_OBSERVATION/);
assert.match(bootstrap, /OnlineSafeLog/);
assert.match(bootstrap, /bearer_derived/);
assert.doesNotMatch(bootstrap, /CoopSession\.Dispatch|CoopRules\.ApplyAction/);
assert.match(build, /PROJECT:CoopOnline/);
assert.match(build, /COOP_ONLINE_WEBGL_RESULT/);
assert.match(build, /previousCompanyName/);
assert.match(build, /previousRunInBackground/);
assert.match(build, /previousCompression/);
assert.match(solo, /Few Moves Online Pilot/);

{
  const library = {};
  const local = new Map();
  const session = new Map();
  const sandbox = {
    LibraryManager: { library },
    localStorage: { getItem: key => local.get(key) || null, setItem: (key, value) => local.set(key, value), removeItem: key => local.delete(key) },
    sessionStorage: { getItem: key => session.get(key) || null, setItem: (key, value) => session.set(key, value), removeItem: key => session.delete(key) },
    mergeInto(target, additions) {
      Object.assign(target, additions);
      Object.entries(additions).forEach(([name, value]) => { if (name.startsWith("$")) sandbox[name.slice(1)] = value; });
    },
    console: { warn() {} }
  };
  vm.runInNewContext(bridge, sandbox, { filename: "CoopOnlineNetwork.jslib" });
  const rawCreate = { ok: true, room: { roomId: "room-1" }, seat: 0, inviteCode: "ABC", seatToken: "bearer-secret" };
  sandbox.NectorialOnlineBridge.writeSession({ roomId: "room-1", seat: 0, seatToken: rawCreate.seatToken, inviteCode: rawCreate.inviteCode });
  const stored = sandbox.NectorialOnlineBridge.readSession();
  assert.equal(stored.seatToken, "bearer-secret", "seat credential remains in private browser storage");
  const safeProjection = sandbox.NectorialOnlineBridge.sanitizedCopy(rawCreate);
  assert.equal(rawCreate.seatToken, "bearer-secret", "raw create response is not mutated before private storage");
  assert.equal(safeProjection.seatToken, undefined, "Unity projection excludes bearer token");
  assert.equal(safeProjection.inviteCode, "ABC", "safe Unity projection may retain invite code");
}

console.log("Coop online UI contract checks passed");
