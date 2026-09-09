const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "..");
const template = readFileSync(path.join(root, "Assets/WebGLTemplates/Raid/index.html"), "utf8");
const bridge = readFileSync(path.join(root, "Assets/Plugins/WebGL/RaidState.jslib"), "utf8");
assert.match(template, /data-raid-action="Slide" data-direction="Up"/);
assert.match(template, /data-raid-action="Restart"/);
assert.match(template, /ResizeObserver/);
assert.match(template, /window\.__nectorialRaid = \{\s*state:null, receiveState:receiveState\s*\}/);
assert.match(template, /RaidBootstrap/);
assert.match(template, /statusCode/);
assert.match(template, /tailCount/);
assert.match(template, /shieldCharges/);
assert.doesNotMatch(template, /snake\.advance|enemy\.move|Math\.random/);
assert.match(bridge, /NectorialRaidReportState/);
assert.doesNotMatch(bridge, /RaidRules|RaidSession|RaidSolver/);

function element(id, dataset) {
  return {
    id,
    dataset: dataset || {},
    hidden: false,
    disabled: false,
    open: false,
    textContent: "",
    innerHTML: "",
    style: {},
    value: "",
    listeners: {},
    addEventListener(name, handler) { this.listeners[name] = handler; },
    showModal() { this.open = true; },
    close() { this.open = false; },
    focus() {},
    select() {},
    getBoundingClientRect() { return { width: 0, height: 0 }; }
  };
}

const ids = [
  "unity-canvas", "feedback", "turn-label", "player-state", "snake-state", "shield-count", "magnet-count", "slow-count",
  "result-dialog", "result-title", "result-copy", "result-restart", "restart-top"
];
const elements = Object.fromEntries(ids.map(id => [id, element(id)]));
const actions = [
  element("up", { raidAction: "Slide", direction: "Up" }),
  element("left", { raidAction: "Slide", direction: "Left" }),
  element("right", { raidAction: "Slide", direction: "Right" }),
  element("down", { raidAction: "Slide", direction: "Down" }),
  elements["result-restart"]
];
actions[4].dataset = { raidAction: "Restart" };
const boardStage = element("board-stage");
const windowObject = { __nectorialRaid: null, addEventListener() {} };
const documentObject = {
  body: { appendChild() {} },
  querySelector(selector) {
    if (selector === ".board-stage") return boardStage;
    if (selector.startsWith("#")) return elements[selector.slice(1)] || element(selector.slice(1));
    return null;
  },
  querySelectorAll(selector) { return selector === "button[data-raid-action]" ? actions : []; },
  createElement() { return element("loader"); },
  addEventListener() {}
};
const sandbox = {
  window: windowObject,
  document: documentObject,
  ResizeObserver: undefined,
  setTimeout() { return 1; },
  clearTimeout() {},
  console,
  JSON,
  Math,
  Number,
  String
};
const scriptStart = template.indexOf("<script>") + "<script>".length;
const scriptEnd = template.lastIndexOf("</script>");
vm.runInNewContext(template.slice(scriptStart, scriptEnd), sandbox, { filename: "Raid/index.html" });

function observation(status, overrides) {
  return Object.assign({
    initialized: true,
    inputEnabled: status === "Playing" || status === "Armed",
    transitioning: false,
    statusCode: status,
    actions: 2,
    hits: status === "Failed" ? 1 : 0,
    shieldCharges: 1,
    magnetStepsRemaining: 0,
    slowStepsRemaining: 0,
    tailCount: status === "Armed" ? 3 : 0,
    tailTarget: 3,
    playerX: 1,
    playerY: 1,
    snakeHeadIndex: 0,
    message: "상태",
    saveStatus: "idle",
    saveError: "",
    stateFingerprint: "fingerprint"
  }, overrides || {});
}

windowObject.__nectorialRaid.receiveState(JSON.stringify(observation("Playing")));
assert.equal(actions.slice(0, 4).every(button => !button.disabled), true, "playing enables four directions");
assert.equal(elements["restart-top"].disabled, true, "playing keeps restart disabled");
assert.equal(elements["result-dialog"].open, false, "playing keeps result closed");

windowObject.__nectorialRaid.receiveState(JSON.stringify(observation("Armed", { tailCount: 3 })));
assert.equal(actions.slice(0, 4).every(button => !button.disabled), true, "armed keeps movement enabled");

windowObject.__nectorialRaid.receiveState(JSON.stringify(observation("Failed", { inputEnabled: false })));
assert.equal(actions.slice(0, 4).every(button => button.disabled), true, "failed locks movement");
assert.equal(elements["result-dialog"].open, true, "failed opens the small result dialog");
assert.equal(elements["restart-top"].disabled, false, "failed enables immediate restart");
assert.equal(elements["result-restart"].disabled, false, "failed result action is available");

windowObject.__nectorialRaid.receiveState(JSON.stringify(observation("Cleared", { inputEnabled: false })));
assert.equal(elements["result-title"].textContent, "뱀을 피하고 격파");
assert.equal(elements["result-dialog"].open, true, "cleared keeps result dialog open");

windowObject.__nectorialRaid.receiveState(JSON.stringify(observation("Playing", { inputEnabled: true })));
assert.equal(elements["result-dialog"].open, false, "new run closes result dialog");
assert.equal(elements["restart-top"].disabled, true, "new run disables result restart");

console.log("Raid UI contract checks passed");
