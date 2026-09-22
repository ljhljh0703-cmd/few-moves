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
assert.match(template, />사각형<\/strong>/);
assert.match(template, /activeLabel\(state\).*내 차례/);
assert.match(template, /친구 차례/);
assert.match(template, /번 이동/);
assert.match(template, /첫 번째 판/);
assert.match(template, /두 번째 판/);
assert.match(template, /세 번째 판/);
assert.match(template, /left:50%; top:0; width:180px; height:168px/);
assert.match(template, /grid-template-rows:48px 48px 56px 168px 18px/);
assert.match(template, /id="join-open"/);
assert.match(template, /id="lobby-intro"/);
assert.match(template, /class="board-stage" hidden/);
assert.match(template, /class="play-controls" hidden/);
assert.match(template, /function canUseLobby\(\).*currentState\.initialized.*!currentState\.joined/);
assert.match(template, /id="menu-dialog"/);
assert.match(template, /id="help-dialog"/);
assert.match(template, /id="invite-dialog"/);
assert.match(template, /id="clear-close"/);
assert.match(template, /id="invite-link-fallback"/);
assert.match(template, /다시하기 요청/);
assert.doesNotMatch(template, /저장된 방|>C1<|>C2<|>C3<|온라인 저장/);
assert.match(template, /id="definition-picker"/);
assert.match(template, /id="record-dialog"/);
assert.match(template, /href="\.\.\/index\.html"/);
assert.match(template, /width:56px; height:56px/);
assert.match(template, /min-height:48px/);
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
assert.match(template, /pendingConsent&&currentState\.pendingConsent\.active===true/);
assert.match(template, /consentReject\.disabled=requester/);
assert.match(template, /consentApprove\.disabled=requester/);
assert.match(template, /function canResolveConsent\(\)/);
assert.doesNotMatch(template, /resolverSeat/);
assert.match(template, /state\.authorityRevision===currentState\.authorityRevision&&state\.logicalActionCount<currentState\.logicalActionCount/);
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
assert.match(bridge, /NectorialOnlineCreateDefinition/);
assert.match(bridge, /NectorialOnlineGetRecord/);
assert.match(bridge, /recordRequestId/);
assert.match(bridge, /NectorialOnlineJoin/);
assert.match(bridge, /NectorialOnlineCommand/);
assert.match(bridge, /NectorialOnlineReportState/);
assert.match(bridge, /NectorialOnlineResume__deps/);
assert.match(bridge, /NectorialOnlineResumeInvite__deps/);
assert.match(bridge, /NectorialOnlineCreate__deps/);
assert.match(bridge, /NectorialOnlineCreateDefinition__deps/);
assert.match(bridge, /NectorialOnlineGetRecord__deps/);
assert.match(bridge, /NectorialOnlineJoin__deps/);
assert.match(bridge, /NectorialOnlineCommand__deps/);
assert.match(bridge, /NectorialOnlineLeave__deps/);
assert.match(bridge, /sanitizedCopy/);
assert.match(bridge, /delete copy\.seatToken/);
assert.match(bridge, /session && session\.inviteCode/);
assert.doesNotMatch(bridge, /delete records\[/);
assert.doesNotMatch(bridge, /console\.log\(.*seatToken|console\.log\(.*bearer/);

assert.match(bootstrap, /NectorialOnlineCreate/);
assert.match(bootstrap, /NectorialOnlineCreateDefinition/);
assert.match(bootstrap, /NectorialOnlineGetRecord/);
assert.match(bootstrap, /SelectDefinition/);
assert.match(bootstrap, /LoadSharedRecord/);
assert.match(bootstrap, /ChallengeSharedRecord/);
assert.match(bootstrap, /activeDefinitionId/);
assert.match(bootstrap, /selectedDefinitionId/);
assert.match(bootstrap, /hasMine/);
assert.match(bootstrap, /hasShared/);
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
assert.match(bootstrap, /HasAuthenticatedState/);
assert.match(bootstrap, /HasRoomPayload/);
assert.match(bootstrap, /HasStatePayload/);
assert.match(bootstrap, /IsNoSessionResume/);
assert.match(bootstrap, /online_authenticated_payload_invalid/);
assert.match(bootstrap, /pendingRecordStateFingerprint/);
assert.match(bootstrap, /_transportLocked = true/);
assert.match(bootstrap, /inputEnabled = _joined && _roomReady && _serverState != null && !_transportLocked/);
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
assert.match(onlineSerialization, /error-without-state preserve=pass input=locked/);
assert.match(onlineSerialization, /recovered-auth-state unlock=pass/);
assert.match(onlineSerialization, /record-empty-guard-and-definition-identity/);
assert.match(onlineSerialization, /record-mine-shared-request-correlation-active-room-preserved/);
assert.match(onlineSerialization, /resume-no-session-empty-invite-lobby/);
assert.match(onlineSerialization, /resume-no-session-invite-preserved/);
assert.match(onlineSerialization, /resume-invite-then-join/);
assert.match(onlineSerialization, /malformed-authenticated-payload/);
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

function uiElement(documentObject, id, dataset = {}) {
  return {
    id, dataset, hidden:false, disabled:false, open:false, textContent:"", innerHTML:"", value:"", style:{}, classList:{values:new Set(),toggle(name,force){if(force)this.values.add(name);else this.values.delete(name);},contains(name){return this.values.has(name);}}, tagName:id.includes("input")||id.includes("fallback")?"INPUT":"BUTTON", listeners:{}, children:[],
    addEventListener(name, handler){ this.listeners[name]=handler; },
    setAttribute(name,value){ if(name==="open")this.open=true; this[name]=value; }, removeAttribute(name){ if(name==="open")this.open=false; delete this[name]; },
    appendChild(child){ this.children.push(child); this.firstChild=this.children[0]||null; }, removeChild(child){const index=this.children.indexOf(child);if(index>=0)this.children.splice(index,1);this.firstChild=this.children[0]||null;}, showModal(){this.open=true;}, close(){this.open=false;},
    focus(){documentObject.activeElement=this;}, select(){this.selected=true;}, getBoundingClientRect(){return{width:320,height:320};}
  };
}

function recordSummary(actions, definitionId="coop-c2", fingerprint="fp-c2") {
  return { modeId:"coop-v1", definitionId, rulesVersion:"coop-rules-v1", contentVersion:definitionId+"-v1", definitionFingerprint:fingerprint, statusCode:"Cleared", effectiveActionCount:actions, logicalActionCount:actions, hits:0, circleX:3, circleY:2, diamondX:2, diamondY:1 };
}

function onlineObservation(overrides={}) {
  return Object.assign({ initialized:true, online:true, joined:false, roomId:"", inviteCode:"", seatCode:-1, availabilityCode:0, availabilityStatus:"대기 중", circleConnected:false, diamondConnected:false, inputEnabled:false, activeActorCode:"Circle", authorityRevision:0, logicalActionCount:0, statusCode:"Waiting", message:"실제 상태", error:"", transitioning:false, circleAtGoal:false, diamondAtGoal:false, pendingConsent:null, expressionSequence:0, expressionSender:"", expression:"", transportLocked:false, definitions:[
    {definitionId:"coop-c1",rulesVersion:"coop-rules-v1",contentVersion:"coop-c1-v1",roomFingerprint:"fp-c1"},
    {definitionId:"coop-c2",rulesVersion:"coop-rules-v1",contentVersion:"coop-c2-v1",roomFingerprint:"fp-c2"},
    {definitionId:"coop-c3",rulesVersion:"coop-rules-v1",contentVersion:"coop-c3-v1",roomFingerprint:"fp-c3"}
  ], activeDefinitionId:"coop-c2", selectedDefinitionId:"coop-c2", recordStatus:"idle", recordCapsule:"", hasMine:false, mine:{}, hasShared:false, shared:{}, sharedRecordRequestId:"", recordError:"" }, overrides);
}

function uiHarness(hash="#record=fm1.shared") {
  const documentObject={activeElement:null,listeners:{},loader:null};
  const ids=["unity-canvas","online-layout","lobby-intro","status-row","play-controls","online-controls","feedback","connection-label","room-form","room-start","join-entry","join-open","join-back","room-info","invite-input","invite-code","resume-invite-input","resume-button","copy-invite-button","invite-copy","invite-close","invite-link-fallback","room-status","turn-label","circle-goal","diamond-goal","leave-button","menu-dialog","menu-top","menu-close","menu-record","help-dialog","help-top","help-close","invite-dialog","expression-bubble","expression-sender","expression-icon","expression-name","definition-picker","definition-options","consent-dialog","consent-copy","consent-reject","consent-approve","clear-dialog","clear-copy","clear-record","clear-restart","clear-close","record-dialog","record-top","record-note","record-get","record-share","record-challenge","record-fallback","record-close","mine-value","mine-meta","shared-value","shared-meta","record-compare","create-button","join-button"];
  const elements=Object.fromEntries(ids.map(id=>[id,uiElement(documentObject,id)]));
  elements["join-entry"].hidden=true;elements["status-row"].hidden=true;elements["play-controls"].hidden=true;
  ["menu-dialog","help-dialog","invite-dialog","consent-dialog","clear-dialog","record-dialog"].forEach(id=>{elements[id].tagName="DIALOG";});
  const actions=[uiElement(documentObject,"up",{onlineAction:"Slide",direction:"Up"}),uiElement(documentObject,"left",{onlineAction:"Slide",direction:"Left"}),uiElement(documentObject,"right",{onlineAction:"Slide",direction:"Right"}),uiElement(documentObject,"down",{onlineAction:"Slide",direction:"Down"}),uiElement(documentObject,"look",{onlineAction:"Express",expression:"Look"}),uiElement(documentObject,"thumb",{onlineAction:"Express",expression:"ThumbsUp"}),uiElement(documentObject,"hand",{onlineAction:"Express",expression:"Handshake"}),uiElement(documentObject,"wait",{onlineAction:"Express",expression:"Waiting"}),uiElement(documentObject,"undo",{onlineAction:"RequestUndo"}),elements["clear-restart"]];
  elements["clear-restart"].dataset={onlineAction:"RequestRestart"};
  const boardStage=uiElement(documentObject,"board-stage"); boardStage.tagName="SECTION";boardStage.hidden=true;
  documentObject.body={appendChild(node){documentObject.loader=node;}};
  documentObject.querySelector=selector=>selector===".board-stage"?boardStage:selector===".status-row"?elements["status-row"]:selector===".play-controls"?elements["play-controls"]:selector==="dialog[open]"?[elements["menu-dialog"],elements["help-dialog"],elements["invite-dialog"],elements["consent-dialog"],elements["clear-dialog"],elements["record-dialog"]].find(item=>item.open)||null:selector.startsWith("#")?elements[selector.slice(1)]:null;
  documentObject.querySelectorAll=selector=>selector==="button[data-online-action]"?actions:[];
  documentObject.createElement=()=>uiElement(documentObject,"dynamic");
  documentObject.addEventListener=(name,handler)=>{documentObject.listeners[name]=handler;};
  const sent=[],unityReady=deferred(),clipboardCalls=[];
  const windowObject={listeners:{},addEventListener(name,handler){this.listeners[name]=handler;}};
  const sandbox={window:windowObject,document:documentObject,ResizeObserver:undefined,location:{origin:"https://game.test",pathname:"/content/coop/index.html",search:"?invite=DROP",hash},navigator:{clipboard:{writeText(value){const item=deferred();clipboardCalls.push({value,item});return item.promise;}}},setTimeout(){return 1;},clearTimeout(){},Date,console,JSON,Math,Number,String,createUnityInstance(_canvas,_config,onProgress){sandbox.progress=onProgress;return unityReady.promise;}};
  const script=template.slice(template.indexOf("<script>")+8,template.lastIndexOf("</script>"));
  vm.runInNewContext(script,sandbox,{filename:"CoopOnline/index.html"});
  const instance={SendMessage(name,method,payload){sent.push({name,method,payload:JSON.parse(payload)});}};
  return {sandbox,documentObject,elements,boardStage,actions,sent,unityReady,instance,clipboardCalls};
}

async function runUiFixtures() {
  const boot=uiHarness("");
  boot.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(onlineObservation({transportLocked:true})));
  assert.equal(boot.boardStage.hidden,true,"unjoined lobby hides the empty board");
  assert.equal(boot.elements["lobby-intro"].hidden,false);
  assert.equal(boot.elements["status-row"].hidden,true);
  assert.equal(boot.elements["play-controls"].hidden,true,"inactive pad is not shown in the lobby");
  assert.equal(boot.elements["online-controls"].classList.contains("lobby"),true);
  assert.equal(boot.elements["create-button"].disabled,false,"initial transport lock does not disable creating a room");
  assert.equal(boot.elements["join-open"].disabled,false);
  assert.equal(boot.elements["definition-options"].children.every(button=>!button.disabled),true,"board selection remains available before joining");
  boot.documentObject.loader.onload();boot.unityReady.resolve(boot.instance);await settle();
  boot.elements["create-button"].listeners.click();
  assert.deepEqual(boot.sent.at(-1).payload,{kind:"Create",definitionId:"coop-c2"},"initial locked lobby can send Create");
  boot.elements["join-open"].listeners.click();
  assert.equal(boot.elements["join-entry"].hidden,false);
  boot.elements["invite-input"].value="INV-BOOT";boot.elements["join-button"].listeners.click();
  assert.deepEqual(boot.sent.at(-1).payload,{kind:"Join",inviteCode:"INV-BOOT"},"initial locked lobby can send Join");
  const lobbyCount=boot.sent.length;boot.actions[0].listeners.click();boot.actions[4].listeners.click();
  assert.equal(boot.sent.length,lobbyCount,"transport lock still blocks room movement and expressions");
  boot.documentObject.activeElement=null;
  boot.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(onlineObservation({transportLocked:true,recordStatus:"ready",hasShared:true,shared:recordSummary(9)})));
  boot.elements["record-top"].listeners.click();
  assert.equal(boot.elements["record-challenge"].disabled,false,"shared board choice stays usable in the locked lobby");
  boot.elements["record-challenge"].listeners.click();assert.equal(boot.sent.at(-1).payload.kind,"Challenge");
  boot.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(onlineObservation({joined:true,transportLocked:true,roomId:"boot-room",seatCode:0,inviteCode:"INV-BOOT"})));
  assert.equal(boot.boardStage.hidden,false,"joined room reveals the board even during reconnection");
  assert.equal(boot.elements["lobby-intro"].hidden,true);
  assert.equal(boot.elements["play-controls"].hidden,false);
  const h=uiHarness();
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(onlineObservation()));
  assert.equal(h.sent.length,0,"initial observation before Unity instance defers record import");
  assert.equal(h.elements["definition-picker"].hidden,false,"lobby preview keeps the selector visible even with activeDefinitionId");
  assert.equal(h.elements["definition-options"].children.length,3,"C1, C2, C3 come from the runtime definition observation");
  assert.equal(h.elements["definition-options"].children[1]["aria-pressed"],"true");
  assert.deepEqual(h.elements["definition-options"].children.map(button=>button.textContent),["첫 번째 판","두 번째 판","세 번째 판"]);
  assert.equal(h.elements["room-start"].hidden,false,"first view offers creation or joining without a code field");
  assert.equal(h.elements["join-entry"].hidden,true);
  h.documentObject.loader.onload(); h.sandbox.progress(0.6);
  assert.equal(h.elements.feedback.textContent,"실제 상태","loading progress cannot overwrite an observed message");
  h.unityReady.resolve(h.instance); await settle();
  const firstRequestId=h.sent.at(-1).payload.requestId;assert.deepEqual(h.sent.at(-1).payload,{kind:"LoadSharedRecord",capsule:"fm1.shared",requestId:firstRequestId},"record import carries its bounded request ID");assert.match(firstRequestId,/^record-hash-\d+$/);assert.ok(firstRequestId.length<=96);
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(onlineObservation({sharedRecordRequestId:firstRequestId})));
  assert.equal(h.sent.filter(item=>item.payload.kind==="LoadSharedRecord").length,1,"one hash is imported once");
  h.elements["definition-options"].children[2].listeners.click();
  assert.deepEqual(h.sent.at(-1).payload,{kind:"SelectDefinition",definitionId:"coop-c3"});
  h.elements["join-open"].listeners.click();assert.equal(h.elements["join-entry"].hidden,false,"join reveals code only on request");h.elements["join-back"].listeners.click();assert.equal(h.elements["room-start"].hidden,false);

  const mine=recordSummary(12),shared=recordSummary(14);
  const joined=onlineObservation({joined:true,roomId:"opaque-room",inviteCode:"INV-1",seatCode:0,availabilityCode:1,availabilityStatus:"연결됨",circleConnected:true,diamondConnected:true,inputEnabled:true,statusCode:"Cleared",logicalActionCount:14,activeDefinitionId:"coop-c2",selectedDefinitionId:"coop-c2",recordStatus:"ready",recordCapsule:"fm1.mine",hasMine:true,mine,hasShared:true,shared,sharedRecordRequestId:firstRequestId});
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(joined));
  assert.equal(h.elements["definition-picker"].hidden,true,"joined state locks the selector");
  assert.equal(h.elements["definition-options"].children.every(button=>button.disabled),true);
  h.elements["clear-record"].listeners.click();
  assert.equal(h.elements["record-dialog"].open,true);
  assert.equal(h.elements["clear-dialog"].open,false,"record is separate from the result dialog");
  assert.equal(h.elements["mine-value"].textContent,"12수");
  assert.equal(h.elements["shared-value"].textContent,"14수");
  assert.match(h.elements["record-compare"].textContent,/2수 적습니다/);
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},joined,{shared:recordSummary(8,"coop-c3","fp-c3")})));
  assert.equal(h.elements["record-compare"].textContent,"같은 판의 기록만 비교할 수 있습니다.","different identities are never compared");
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(joined));
  assert.equal(h.elements["record-challenge"].disabled,true,"joined rooms cannot switch active boards through Challenge");
  const beforeChallenge=h.sent.length; h.elements["record-challenge"].listeners.click();
  assert.equal(h.sent.length,beforeChallenge);
  const beforeClose=h.sent.length; h.elements["record-close"].listeners.click();
  assert.equal(h.sent.length,beforeClose,"closing records never requests restart");
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(joined));
  assert.equal(h.elements["clear-dialog"].open,false,"same result is not reopened after record close");
  assert.equal(h.actions.slice(0,4).every(button=>button.disabled),true,"terminal state keeps direction buttons disabled after record close");
  const terminalKeyCount=h.sent.length;h.documentObject.activeElement=null;h.documentObject.listeners.keydown({key:"ArrowUp",repeat:false,ctrlKey:false,metaKey:false,altKey:false,preventDefault(){}});assert.equal(h.sent.length,terminalKeyCount,"terminal state blocks keyboard Slide after record close");

  h.elements["record-top"].listeners.click(); h.elements["record-share"].listeners.click();
  assert.equal(h.clipboardCalls[0].value,"https://game.test/content/coop/index.html#record=fm1.mine","record share drops the invitation query");
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},joined,{recordCapsule:"fm1.new"})));
  h.clipboardCalls[0].item.resolve(); await settle();
  assert.notEqual(h.elements["record-note"].textContent,"기록 링크를 복사했습니다.","stale clipboard success is ignored");
  h.elements["record-share"].listeners.click();h.clipboardCalls[1].item.reject(new Error("denied"));await settle();
  assert.equal(h.elements["record-fallback"].hidden,false);
  assert.equal(h.elements["record-fallback"].value,"https://game.test/content/coop/index.html#record=fm1.new");
  assert.match(h.elements["record-note"].textContent,/자동 복사가 되지 않았습니다/);
  h.elements["record-share"].listeners.click();h.clipboardCalls[2].item.resolve();await settle();
  assert.equal(h.elements["record-fallback"].hidden,false,"clipboard success also leaves a selectable link");
  assert.equal(h.elements["record-fallback"].value,"https://game.test/content/coop/index.html#record=fm1.new");
  assert.equal(h.elements["record-note"].textContent,"기록 링크를 복사했습니다.");
  const sameRecord=Object.assign({},joined,{recordCapsule:"fm1.new",authorityRevision:joined.authorityRevision+1});
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(sameRecord));
  assert.equal(h.elements["record-note"].textContent,"기록 링크를 복사했습니다.","same-record polling preserves share feedback");
  assert.equal(h.elements["record-fallback"].hidden,false);
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},sameRecord,{recordCapsule:"fm1.changed",authorityRevision:sameRecord.authorityRevision+1})));
  assert.equal(h.elements["record-fallback"].hidden,true,"a changed record context clears the old selectable link");
  assert.notEqual(h.elements["record-note"].textContent,"기록 링크를 복사했습니다.");
  const beforeKey=h.sent.length;h.documentObject.listeners.keydown({key:"ArrowUp",repeat:false,ctrlKey:false,metaKey:false,altKey:false,preventDefault(){}});assert.equal(h.sent.length,beforeKey,"modal blocks keyboard gameplay");

  h.elements["record-close"].listeners.click(); h.elements["menu-top"].listeners.click(); h.elements["leave-button"].listeners.click();
  assert.equal(h.sent.at(-1).payload.kind,"Leave","leaving is explicit");
  const lobbyShared=onlineObservation({selectedDefinitionId:"coop-c2",activeDefinitionId:"coop-c2",recordStatus:"ready",hasShared:true,shared:recordSummary(10,"coop-c3","fp-c3")});
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(lobbyShared));
  h.elements["record-top"].listeners.click();h.elements["record-challenge"].listeners.click();
  assert.equal(h.sent.at(-1).payload.kind,"Challenge","Challenge only selects the shared board before room creation");
  assert.notEqual(h.sent.at(-1).payload.kind,"Create");
  h.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},lobbyShared,{selectedDefinitionId:"coop-c3",activeDefinitionId:"coop-c3"})));
  h.elements["create-button"].listeners.click();
  assert.deepEqual(h.sent.at(-1).payload,{kind:"Create",definitionId:"coop-c3"},"new room creation is a separate explicit action");

  const oversized=uiHarness("#record="+"x".repeat(4097));
  oversized.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(onlineObservation()));oversized.documentObject.loader.onload();oversized.unityReady.resolve(oversized.instance);await settle();
  assert.equal(oversized.sent.some(item=>item.payload.kind==="LoadSharedRecord"),false,"oversized hash is discarded without calling it verified");
  assert.equal(oversized.elements["definition-picker"].hidden,false,"oversized hash does not stall the game UI");
  const reverse=uiHarness("#record=fm1.reverse");reverse.documentObject.loader.onload();reverse.unityReady.resolve(reverse.instance);await settle();assert.equal(reverse.sent.length,0,"instance alone waits for observation");reverse.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(onlineObservation()));assert.equal(reverse.sent.at(-1).payload.kind,"LoadSharedRecord");assert.equal(reverse.sent.at(-1).payload.capsule,"fm1.reverse");assert.match(reverse.sent.at(-1).payload.requestId,/^record-hash-\d+$/);

  const route=uiHarness("#record=fm1.A"),roomA=onlineObservation({joined:true,roomId:"room-stays",seatCode:0,availabilityCode:1,inputEnabled:true,statusCode:"Playing",activeDefinitionId:"coop-c2",selectedDefinitionId:"coop-c2",recordStatus:"ready",hasShared:true,shared:recordSummary(14)});
  route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(roomA));route.documentObject.loader.onload();route.unityReady.resolve(route.instance);await settle();const requestA=route.sent.at(-1).payload.requestId;route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},roomA,{sharedRecordRequestId:requestA})));route.elements["record-top"].listeners.click();assert.equal(route.elements["shared-value"].textContent,"14수");
  route.sandbox.location.hash="#record=fm1.B";route.sandbox.window.listeners.hashchange();assert.equal(route.elements["shared-value"].textContent,"확인 중","new hash hides the previous coop record before C# replies");assert.equal(route.elements["record-compare"].textContent,"새 링크의 기록을 확인하고 있습니다.");const requestB=route.sent.at(-1).payload.requestId;assert.deepEqual(route.sent.at(-1).payload,{kind:"LoadSharedRecord",capsule:"fm1.B",requestId:requestB});assert.notEqual(requestB,requestA);
  const staleA=Object.assign({},roomA,{authorityRevision:1,logicalActionCount:3,hasMine:true,mine:recordSummary(7),hasShared:true,shared:recordSummary(14),sharedRecordRequestId:requestA});route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(staleA));assert.equal(route.elements["shared-value"].textContent,"확인 중","late A success cannot release B");assert.equal(route.elements["mine-value"].textContent,"7수","mine continues to update while B waits");assert.match(route.elements["turn-label"].innerHTML,/3번 이동/);
  route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},staleA,{authorityRevision:2,sharedRecordRequestId:""})));assert.equal(route.elements["shared-value"].textContent,"확인 중","ordinary poll cannot release B");
  const roomB=Object.assign({},roomA,{authorityRevision:3,hasShared:true,shared:recordSummary(9),sharedRecordRequestId:requestB});route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(roomB));assert.equal(route.elements["shared-value"].textContent,"9수");route.sandbox.window.listeners.hashchange();assert.equal(route.sent.filter(item=>item.payload.kind==="LoadSharedRecord").length,2,"same coop hash is imported once");
  route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},roomB,{authorityRevision:4,recordStatus:"invalid",recordError:"late_A",hasShared:false,shared:{},sharedRecordRequestId:requestA})));assert.equal(route.elements["shared-value"].textContent,"9수","late A error cannot replace accepted B");route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},roomA,{authorityRevision:5,sharedRecordRequestId:""})));assert.equal(route.elements["shared-value"].textContent,"9수","empty legacy poll cannot replace accepted B");
  route.sandbox.location.hash="";route.sandbox.window.listeners.hashchange();assert.equal(route.sent.filter(item=>item.payload.kind==="LoadSharedRecord").length,2,"removing hash sends no command");assert.equal(route.elements["shared-value"].textContent,"9수");
  route.sandbox.location.hash="#record=fm1.A";route.sandbox.window.listeners.hashchange();const requestA2=route.sent.at(-1).payload.requestId;assert.notEqual(requestA2,requestA);route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},roomA,{authorityRevision:6,sharedRecordRequestId:requestA2})));assert.equal(route.elements["shared-value"].textContent,"14수");
  route.sandbox.location.hash="#record=malformed";route.sandbox.window.listeners.hashchange();assert.equal(route.elements["shared-value"].textContent,"확인 중","malformed candidate cannot look like record A");const requestC=route.sent.at(-1).payload.requestId;route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},roomB,{authorityRevision:7,recordStatus:"invalid",recordError:"stale_error",hasShared:false,shared:{},sharedRecordRequestId:requestA2})));assert.equal(route.elements["shared-value"].textContent,"확인 중","stale error cannot release C");route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},roomB,{authorityRevision:8,recordStatus:"invalid",recordError:"record_invalid",hasShared:false,shared:{},sharedRecordRequestId:requestC})));assert.equal(route.elements["shared-value"].textContent,"기록 없음");route.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},roomB,{authorityRevision:9,recordStatus:"ready",recordError:"",hasShared:true,shared:recordSummary(9),sharedRecordRequestId:requestB})));assert.equal(route.elements["shared-value"].textContent,"기록 없음","stale success cannot replace accepted latest failure");assert.equal(route.sandbox.window.__nectorialOnline.state.roomId,"room-stays");assert.equal(route.sent.every(item=>item.payload.kind==="LoadSharedRecord"),true,"record hash changes never leave, create, restart, or mutate the room");
  const ux=uiHarness("");
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(onlineObservation()));
  ux.documentObject.loader.onload();ux.unityReady.resolve(ux.instance);await settle();
  ux.elements["join-open"].listeners.click();
  assert.equal(ux.elements["join-entry"].hidden,false,"join code is requested only after choosing to join");
  ux.elements["join-button"].listeners.click();
  assert.match(ux.elements.feedback.textContent,/초대 코드를 입력/);
  ux.elements["invite-input"].value="INV-UI";ux.elements["join-button"].listeners.click();
  assert.deepEqual(ux.sent.at(-1).payload,{kind:"Join",inviteCode:"INV-UI"});
  ux.documentObject.activeElement=null;ux.elements["help-top"].listeners.click();
  assert.equal(ux.elements["help-dialog"].open,true,"help opens without changing the room");
  ux.elements["menu-top"].listeners.click();assert.equal(ux.elements["menu-dialog"].open,false,"a second modal cannot stack over help");
  const hiddenSelect=ux.sent.length;ux.elements["definition-options"].children[1].listeners.click();assert.equal(ux.sent.length,hiddenSelect,"board selection behind help cannot change the game");
  const helpBlocked=ux.sent.length;ux.actions[0].listeners.click();ux.actions[4].listeners.click();assert.equal(ux.sent.length,helpBlocked,"a visible modal blocks pointer gameplay and expressions");
  ux.elements["help-close"].listeners.click();ux.elements["menu-top"].listeners.click();
  assert.equal(ux.elements["menu-dialog"].open,true);
  ux.elements["join-back"].listeners.click();assert.equal(ux.elements["join-entry"].hidden,false,"menu prevents lobby controls behind it");
  ux.elements["menu-close"].listeners.click();
  const playing=onlineObservation({joined:true,roomId:"room-ux",inviteCode:"INV-UI",seatCode:0,availabilityCode:1,inputEnabled:true,statusCode:"Playing",authorityRevision:5,logicalActionCount:2,circleAtGoal:true,activeActorCode:"Circle"});
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(playing));
  assert.match(ux.elements["turn-label"].innerHTML,/내 차례<small>2번 이동/);
  assert.equal(ux.elements["online-controls"].classList.contains("joined"),true);
  assert.equal(ux.actions[0].disabled,false,"arrived piece is not frozen while the server still allows movement");
  ux.actions[0].listeners.click();assert.equal(ux.sent.at(-1).payload.kind,"Slide");
  ux.elements["copy-invite-button"].listeners.click();
  assert.equal(ux.elements["invite-dialog"].open,true);
  assert.equal(ux.elements["invite-link-fallback"].value,"https://game.test/content/coop/index.html?invite=INV-UI","manual invite link is always selectable");
  assert.equal(ux.elements["invite-code"].textContent,"INV-UI");
  const inviteBlocked=ux.sent.length;ux.actions[0].listeners.click();assert.equal(ux.sent.length,inviteBlocked,"invite dialog blocks the board beneath it");
  ux.elements["invite-copy"].listeners.click();ux.clipboardCalls.at(-1).item.resolve();await settle();
  assert.equal(ux.elements["invite-link-fallback"].value,"https://game.test/content/coop/index.html?invite=INV-UI","clipboard success keeps the fallback");
  ux.elements["invite-close"].listeners.click();
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},playing,{expressionSequence:1,expressionSender:"Diamond",expression:"ThumbsUp"})));
  assert.equal(ux.elements["expression-name"].textContent,"마름모");
  assert.equal(ux.elements["expression-bubble"].hidden,false);
  ux.elements["record-top"].listeners.click();assert.equal(ux.elements["record-dialog"].open,true);
  const incoming=Object.assign({},playing,{authorityRevision:6,pendingConsent:{active:true,requestId:"consent-ux",kind:"Undo",requesterCode:1}});
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(incoming));
  assert.equal(ux.elements["record-dialog"].open,false,"incoming consent supersedes the record dialog");
  assert.equal(ux.elements["consent-dialog"].open,true);
  const beforeBlocked=ux.sent.length;ux.elements["menu-top"].listeners.click();ux.actions[0].listeners.click();
  assert.equal(ux.sent.length,beforeBlocked,"game actions remain blocked under an incoming request");
  ux.elements["consent-approve"].listeners.click();
  assert.equal(ux.sent.at(-1).payload.kind,"ResolveUndo");
  assert.equal(ux.sent.at(-1).payload.requestId,"consent-ux");
  assert.equal(ux.sent.at(-1).payload.expectedRevision,6);
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},playing,{authorityRevision:7,pendingConsent:{active:true,requestId:"own-ux",kind:"Restart",requesterCode:0}})));
  assert.equal(ux.elements["consent-dialog"].open,false,"requester waits without a blocking confirmation dialog");
  assert.match(ux.elements.feedback.textContent,/친구의 확인/);
  const cleared=Object.assign({},playing,{authorityRevision:8,statusCode:"Cleared",logicalActionCount:13,pendingConsent:null});
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(cleared));
  assert.equal(ux.elements["clear-dialog"].open,true);
  ux.elements["clear-restart"].listeners.click();
  assert.equal(ux.sent.at(-1).payload.kind,"RequestRestart","result again requests consent instead of resetting locally");
  assert.match(ux.sent.at(-1).payload.requestId,/^online-\d+-RequestRestart$/);
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},playing,{authorityRevision:9,logicalActionCount:14,activeActorCode:"Diamond",inputEnabled:false})));
  assert.match(ux.elements["turn-label"].innerHTML,/친구 차례<small>14번 이동/);
  assert.equal(ux.actions[0].disabled,true,"friend turn disables direction input");
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(Object.assign({},playing,{authorityRevision:10,transportLocked:true,availabilityCode:0})));
  assert.match(ux.elements["turn-label"].innerHTML,/다시 연결 중/);
  assert.equal(ux.actions[0].disabled,true,"stale transport stays locked");
  const lockedConsent=Object.assign({},playing,{authorityRevision:11,transportLocked:true,availabilityCode:0,pendingConsent:{active:true,requestId:"locked-ux",kind:"Undo",requesterCode:1}});
  ux.sandbox.window.__nectorialOnline.receiveState(JSON.stringify(lockedConsent));
  const lockedCount=ux.sent.length;ux.elements["consent-approve"].listeners.click();
  assert.equal(ux.sent.length,lockedCount,"stale connection cannot resolve consent");
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
    assert.equal(harness.reports.at(-1).inviteCode, "INV-1", "stored host invite is projected on a no-query resume");
    assert.equal(harness.reports.at(-1).seatToken, undefined, "resume report never carries a bearer token");
    assert.equal(harness.requests.length, 1, "resume starts a fresh authoritative poll");
    bridgeRuntime.stopPoll();
  }

  {
    const harness = fixture();
    const bridgeRuntime = harness.sandbox.NectorialOnlineBridge;
    harness.library.NectorialOnlineCreateDefinition("coop-c2");
    assert.equal(harness.requests.length, 1, "definition create submits one request");
    assert.equal(JSON.parse(harness.requests[0].options.body).definitionId, "coop-c2", "definition create carries only the selected definition ID");
    harness.requests[0].item.resolve(response({ ok: true, room: { roomId: "room-c2" }, seat: 0, inviteCode: "INV-C2", seatToken: "token-private-c2" }));
    await settle();
    const created = harness.reports.find(item => item.op === "created");
    assert.equal(created.seatToken, undefined, "definition create projection never carries a bearer token");
    bridgeRuntime.stopPoll();

    bridgeRuntime.writeSession(seat("room-c2", 0, "INV-C2", "token-private-c2"));
    harness.library.NectorialOnlineGetRecord("record-bridge-1");
    const recordRequest = harness.requests.at(-1);
    assert.match(recordRequest.url, /\/record$/, "record request uses the room record endpoint");
    assert.equal(recordRequest.options.body, "{}", "record request has no claimed score or moves");
    recordRequest.item.resolve(response({ ok: true, capsule: "fm1.public-record", verification: { statusCode: "Cleared" } }));
    await settle();
    const record = harness.reports.at(-1);
    assert.equal(record.op, "record", "record response is explicitly routed to C#");
    assert.equal(record.recordRequestId, "record-bridge-1", "record response keeps its request boundary");
    assert.equal(record.capsule, "fm1.public-record", "record capsule reaches C# without bearer data");
    assert.equal(record.seatToken, undefined, "record projection never carries a bearer token");
    bridgeRuntime.stopPoll();
  }

  {
    const harness = fixture();
    const bridgeRuntime = harness.sandbox.NectorialOnlineBridge;
    harness.library.NectorialOnlineJoin("INV-GUEST");
    assert.equal(harness.requests.length, 1, "join submits the invite request");
    harness.requests[0].item.resolve(response({ ok: true, room: { roomId: "room-guest" }, seat: 1, seatToken: "token-private-guest" }));
    await settle();
    const joined = harness.reports.find(item => item.op === "joined");
    assert.equal(joined.inviteCode, "INV-GUEST", "join projects the entered invite when the server response omits it");
    assert.equal(joined.seatToken, undefined, "join projection never carries a bearer token");
    assert.match(harness.local.get(bridgeRuntime.storageKey), /INV-GUEST/, "guest invite remains only in private browser storage");
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

runUiFixtures().then(runBridgeFixtures).then(function () {
  console.log("Coop online UI contract checks passed");
}).catch(function (error) {
  console.error(error && error.stack ? error.stack : String(error));
  process.exitCode = 1;
});
