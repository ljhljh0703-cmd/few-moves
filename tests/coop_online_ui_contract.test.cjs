const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const template = readFileSync(path.join(root, "Assets/WebGLTemplates/CoopOnline/index.html"), "utf8");
const bridge = readFileSync(path.join(root, "Assets/Plugins/WebGL/CoopOnlineNetwork.jslib"), "utf8");
const bootstrap = readFileSync(path.join(root, "Assets/Nectorial/Runtime/CoopOnline/CoopOnlineBootstrap.cs"), "utf8");
const onlineSerialization = readFileSync(path.join(root, "Assets/Editor/CoopOnlineSerializationChecks.cs"), "utf8");
const build = readFileSync(path.join(root, "Assets/Editor/CoopOnlineBuild.cs"), "utf8");
const solo = readFileSync(path.join(root, "Assets/Nectorial/Runtime/GameBootstrap.cs"), "utf8");

assert.match(template, /data-online-action="Slide" data-direction="Up"/);
assert.doesNotMatch(template, /data-online-action="Pass"|>Pass</);
assert.doesNotMatch(template, /원과 마름모|원 차례|원 좌석|>원</);
assert.match(template, /circle-progress">사각형/);
assert.match(template, /activeLabel\(state\).*사각형 차례/);
assert.match(template, /data-online-action="Express" data-expression="Look"/);
assert.match(template, /id="create-button"/);
assert.match(template, /id="invite-input"/);
assert.match(template, /id="join-button"/);
assert.match(template, /id="leave-button"/);
assert.match(template, /id="resume-button"/);
assert.match(template, /expressionSequence/);
assert.match(template, /lastExpressionSequence/);
assert.match(template, /activeActorCode/);
assert.match(template, /availabilityCode/);
assert.match(template, /transportLocked/);
assert.match(template, /pendingConsent && currentState\.pendingConsent\.active === true/);
assert.match(template, /consentReject\.disabled = requester/);
assert.match(template, /consentApprove\.disabled = requester/);
assert.match(template, /function canResolveConsent\(\)/);
assert.doesNotMatch(template, /resolverSeat/);
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
assert.match(bridge, /completePoll/);
assert.match(bridge, /disconnectSession/);
assert.match(bridge, /resume_selection_required/);
assert.match(bridge, /storage_unavailable/);
assert.match(bridge, /secure_random_unavailable/);
assert.match(bridge, /return true;/);
assert.match(bridge, /createRequestId/);
assert.match(bridge, /joinRequestId/);
assert.match(bridge, /NectorialOnlineCreate/);
assert.match(bridge, /NectorialOnlineJoin/);
assert.match(bridge, /NectorialOnlineCommand/);
assert.match(bridge, /NectorialOnlineReportState/);
assert.match(bridge, /NectorialOnlineResume__deps/);
assert.match(bridge, /NectorialOnlineResumeInvite__deps/);
assert.match(bridge, /NectorialOnlineCreate__deps/);
assert.match(bridge, /NectorialOnlineJoin__deps/);
assert.match(bridge, /NectorialOnlineCommand__deps/);
assert.match(bridge, /NectorialOnlineLeave__deps/);
assert.match(bridge, /sanitizedCopy/);
assert.match(bridge, /delete copy\.seatToken/);
assert.doesNotMatch(bridge, /delete records\[/);
assert.doesNotMatch(bridge, /console\.log\(.*seatToken|console\.log\(.*bearer/);

assert.match(bootstrap, /NectorialOnlineCreate/);
assert.match(bootstrap, /NectorialOnlineJoin/);
assert.match(bootstrap, /NectorialOnlineCommand/);
assert.match(bootstrap, /bearer_derived/);
assert.match(bootstrap, /private static void ConfigureCamera\(\)/);
assert.match(bootstrap, /using Nectorial\.SlideEscape\.Unity\.Coop;/);
assert.match(bootstrap, /MatchesBundledRoom/);
assert.match(bootstrap, /wire\.roomId, room\.Id/);
assert.doesNotMatch(bootstrap, /wire\.roomId, _roomId/);
assert.match(bootstrap, /RoomId = wire\.roomId/);
assert.match(bootstrap, /TryNormalizeWirePending/);
assert.match(bootstrap, /CoopSaveSerializationAdapter\.TryNormalizePendingConsent/);
assert.match(bootstrap, /CoopRules\.ValidateState/);
assert.match(bootstrap, /TryDeserializeServerStateForCheck/);
assert.match(bootstrap, /expressionHighWater/);
assert.match(bootstrap, /_expressionHydrated/);
assert.match(bootstrap, /result\.op == "resume"/);
assert.match(bootstrap, /result\.op == "resume" && sessionPresent/);
assert.match(bootstrap, /LockForTransportFailure/);
assert.match(bootstrap, /ApplyAuthenticatedResult/);
assert.match(bootstrap, /if \(!ApplyAuthenticatedResult\(result\)\) LockForTransportFailure\(_error\)/);
assert.match(bootstrap, /transportLocked/);
assert.match(bootstrap, /NectorialOnlineResumeInvite/);
assert.match(bootstrap, /TryParseDirection/);
assert.match(bootstrap, /TryParseExpression/);
assert.match(bootstrap, /online_direction_invalid/);
assert.match(bootstrap, /online_expression_invalid/);
assert.match(bootstrap, /CanResolveConsent/);
assert.doesNotMatch(bootstrap, /private static int ParseDirection|private static int ParseExpression/);
assert.match(bootstrap, /_roomView = null/);
assert.match(bootstrap, /COOP_ONLINE_STATE_OBSERVATION/);
assert.match(bootstrap, /OnlineSafeLog/);
assert.match(bootstrap, /bearer_derived/);
assert.doesNotMatch(bootstrap, /CoopSession\.Dispatch|CoopRules\.ApplyAction/);
assert.match(build, /PROJECT:CoopOnline/);
assert.match(build, /COOP_ONLINE_WEBGL_RESULT/);
assert.match(build, /CoopOnlineSerializationChecks\.Run\(\)/);
assert.match(build, /previousCompanyName/);
assert.match(build, /previousRunInBackground/);
assert.match(build, /previousCompression/);
assert.match(solo, /Few Moves Online Pilot/);
assert.match(onlineSerialization, /pending-null/);
assert.match(onlineSerialization, /active-pending/);
assert.match(onlineSerialization, /malformed-pending/);
assert.match(onlineSerialization, /COOP_ONLINE_JSON_PROBE_RESULT pass=true/);

const serverStateFixture = {
  ok: true,
  op: "state",
  seat: 0,
  room: {
    roomId: "7b3c5a3d8c2f4e65a2b5d7089134c6de",
    rulesVersion: "coop-rules-v1",
    contentVersion: "coop-c1-v1",
    roomFingerprint: "fe7f53267547b37fbaf1b566fdee9b7af3aa3d159488cdf55439739dc0a86a89"
  },
  state: {
    roomId: "coop-c1",
    authorityRevision: 0,
    logicalActionCount: 0,
    activeActor: 0,
    status: 0,
    circlePosition: { x: 1, y: 1 },
    diamondPosition: { x: 2, y: 2 }
  },
  availability: { status: 1, circleConnected: true, diamondConnected: true },
  expressions: { expressionSequence: 0, events: [] }
};
assert.notEqual(serverStateFixture.room.roomId, serverStateFixture.state.roomId, "network session and Core puzzle IDs are deliberately distinct");
assert.equal(serverStateFixture.state.roomId, "coop-c1", "Core state keeps the bundled puzzle definition ID");
assert.equal(serverStateFixture.room.roomId.length, 32, "network room identity remains the opaque session UUID");

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((nextResolve, nextReject) => { resolve = nextResolve; reject = nextReject; });
  return { promise, resolve, reject };
}

function response(body, status = 200) {
  return { ok: status >= 200 && status < 300, status, text: () => Promise.resolve(JSON.stringify(body)) };
}

function fixture(search = "") {
  const library = {};
  const local = new Map();
  const session = new Map();
  const reports = [];
  const requests = [];
  const timers = new Map();
  let nextTimer = 1;
  const sandbox = {
    LibraryManager: { library },
    localStorage: { getItem: key => local.get(key) || null, setItem: (key, value) => local.set(key, value), removeItem: key => local.delete(key) },
    sessionStorage: { getItem: key => session.get(key) || null, setItem: (key, value) => session.set(key, value), removeItem: key => session.delete(key) },
    mergeInto(target, additions) {
      Object.assign(target, additions);
      Object.entries(additions).forEach(([name, value]) => { if (name.startsWith("$")) sandbox[name.slice(1)] = value; });
    },
    SendMessage(_target, _method, json) { reports.push(JSON.parse(json)); },
    fetch(url, options) { const item = deferred(); requests.push({ url, options, item }); return item.promise; },
    setInterval(callback, milliseconds) { const id = nextTimer++; timers.set(id, { callback, milliseconds }); return id; },
    clearInterval(id) { timers.delete(id); },
    crypto: { getRandomValues(bytes) { for (let index = 0; index < bytes.length; index++) bytes[index] = index + 1; return bytes; } },
    Uint8Array,
    URLSearchParams,
    location: { search },
    UTF8ToString(value) { return typeof value === "string" ? value : ""; },
    console: { warn() {} }
  };
  vm.runInNewContext(bridge, sandbox, { filename: "CoopOnlineNetwork.jslib" });
  return { library, local, session, reports, requests, sandbox };
}

async function settle() {
  await Promise.resolve();
  await Promise.resolve();
  await new Promise(resolve => setImmediate(resolve));
}

function seat(roomId, seatCode, inviteCode, token) {
  return { roomId, seat: seatCode, inviteCode, seatToken: token };
}

function stateBody(roomId, seatCode) {
  return {
    ok: true,
    seat: seatCode,
    room: { roomId, rulesVersion: "coop-rules-v1", contentVersion: "coop-c1-v1", roomFingerprint: "fixture" },
    state: { roomId, authorityRevision: 3, logicalActionCount: 2 },
    availability: { status: 1, circleConnected: true, diamondConnected: true },
    expressions: { expressionSequence: 4, events: [] }
  };
}

async function runBridgeFixtures() {
  {
    const harness = fixture();
    const bridgeRuntime = harness.sandbox.NectorialOnlineBridge;
    const rawCreate = { ok: true, room: { roomId: "room-1" }, seat: 0, inviteCode: "INV-1", seatToken: "token-private-1" };
    assert.equal(bridgeRuntime.writeSession(seat("room-1", 0, "INV-1", rawCreate.seatToken)), true, "seat credential is retained privately");
    harness.session.clear();
    const restored = bridgeRuntime.readSession();
    assert.equal(restored.seatToken, "token-private-1", "local credential restores after session storage loss");
    assert.match(harness.session.get(bridgeRuntime.activeKey), /room-1/, "safe unique recovery recreates active tab selection");
    const safeProjection = bridgeRuntime.sanitizedCopy(rawCreate);
    assert.equal(rawCreate.seatToken, "token-private-1", "raw response is not mutated before private storage");
    assert.equal(safeProjection.seatToken, undefined, "Unity projection excludes bearer token");
    assert.equal(safeProjection.inviteCode, "INV-1", "safe Unity projection may retain invite code");

    harness.library.NectorialOnlineLeave();
    assert.equal(harness.session.get(bridgeRuntime.activeKey), undefined, "Leave disconnects the current tab only");
    assert.match(harness.local.get(bridgeRuntime.storageKey), /token-private-1/, "Leave preserves stored credential for later return");
    harness.library.NectorialOnlineResume();
    assert.equal(harness.reports.at(-1).op, "resume", "return starts through the resume operation");
    assert.equal(harness.reports.at(-1).seat, 0, "Leave then return restores the same stored seat");
    assert.equal(harness.requests.length, 1, "resume starts a fresh authoritative poll");
    bridgeRuntime.stopPoll();
  }

  {
    const harness = fixture("?invite=INV-2");
    const bridgeRuntime = harness.sandbox.NectorialOnlineBridge;
    bridgeRuntime.writeSession(seat("room-1", 0, "INV-1", "token-private-1"));
    bridgeRuntime.writeSession(seat("room-2", 1, "INV-2", "token-private-2"));
    harness.library.NectorialOnlineResume();
    assert.equal(harness.reports.at(-1).seat, 1, "invite query selects its matching stored seat over a different active record");
    assert.match(harness.session.get(bridgeRuntime.activeKey), /room-2/, "invite-matched seat becomes the tab selection");
    bridgeRuntime.stopPoll();

    harness.session.clear();
    harness.sandbox.location.search = "";
    harness.library.NectorialOnlineResume();
    const ambiguous = harness.reports.at(-1);
    assert.equal(ambiguous.ok, false, "ambiguous stored seats do not guess a room");
    assert.equal(ambiguous.error.code, "resume_selection_required", "ambiguous storage returns a safe selection state");
  }

  {
    const harness = fixture();
    const bridgeRuntime = harness.sandbox.NectorialOnlineBridge;
    bridgeRuntime.writeSession(seat("room-1", 0, "INV-1", "token-private-1"));
    bridgeRuntime.startPoll();
    assert.equal(bridgeRuntime.pollInFlight, true, "first poll enters in-flight state");
    harness.requests[0].item.reject(new Error("offline"));
    await settle();
    assert.equal(bridgeRuntime.pollInFlight, false, "matching-generation network failure clears in-flight state");
    assert.equal(harness.reports.at(-1).error.code, "network_unavailable", "network failure is reported without credentials");
    bridgeRuntime.poll(bridgeRuntime.generation);
    assert.equal(harness.requests.length, 2, "a later poll retries without a reload");
    harness.requests[1].item.resolve(response(stateBody("room-1", 0)));
    await settle();
    assert.equal(bridgeRuntime.pollInFlight, false, "successful retry completes the poll");
    assert.equal(harness.reports.at(-1).ok, true, "successful retry restores an authenticated state report");
    assert.equal(harness.reports.at(-1).seat, 0, "retry retains the authenticated seat");
    bridgeRuntime.stopPoll();
  }

  {
    const harness = fixture();
    const bridgeRuntime = harness.sandbox.NectorialOnlineBridge;
    bridgeRuntime.writeSession(seat("room-old", 0, "INV-OLD", "token-old"));
    bridgeRuntime.startPoll();
    const oldRequest = harness.requests[0];
    bridgeRuntime.stopPoll();
    bridgeRuntime.writeSession(seat("room-new", 1, "INV-NEW", "token-new"));
    bridgeRuntime.startPoll();
    const newRequest = harness.requests[1];
    oldRequest.item.resolve(response(stateBody("room-old", 0)));
    await settle();
    assert.equal(bridgeRuntime.pollInFlight, true, "old-generation completion cannot clear the new poll flag");
    newRequest.item.resolve(response(stateBody("room-new", 1)));
    await settle();
    const stateReports = harness.reports.filter(item => item.op === "state");
    assert.equal(stateReports.length, 1, "old-generation response cannot report over the new room");
    assert.equal(stateReports[0].room.roomId, "room-new", "only new-generation room state reaches Unity");
    bridgeRuntime.stopPoll();
  }

  {
    const harness = fixture();
    const bridgeRuntime = harness.sandbox.NectorialOnlineBridge;
    bridgeRuntime.writeSession(seat("room-expired", 0, "INV-X", "token-expired"));
    bridgeRuntime.startPoll();
    harness.requests[0].item.resolve(response({ ok: false, error: { code: "room_expired" } }, 410));
    await settle();
    assert.equal(harness.session.get(bridgeRuntime.activeKey), undefined, "terminal authorization or expiry removes only active tab selection");
    assert.match(harness.local.get(bridgeRuntime.storageKey), /token-expired/, "terminal response does not copy or erase the private credential record");
    assert.equal(harness.reports.at(-1).seatToken, undefined, "terminal report never carries a bearer token");
    bridgeRuntime.stopPoll();
  }
}

runBridgeFixtures().then(function () {
  console.log("Coop online UI contract checks passed");
}).catch(function (error) {
  console.error(error && error.stack ? error.stack : String(error));
  process.exitCode = 1;
});
