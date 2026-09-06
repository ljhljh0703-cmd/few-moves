const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const repoRoot = path.resolve(__dirname, "..");
const bridgeSource = readFileSync(repoRoot + "/Assets/Plugins/WebGL/NectorialState.jslib", "utf8");

function createTimers() {
  let now = 0;
  let nextId = 1;
  const tasks = new Map();

  function setTimeout(callback, delay) {
    const id = nextId++;
    tasks.set(id, { at: now + Math.max(0, Number(delay) || 0), callback });
    return id;
  }

  function clearTimeout(id) {
    tasks.delete(id);
  }

  function advance(milliseconds) {
    const target = now + milliseconds;
    while (true) {
      let candidateId = null;
      let candidate = null;
      for (const [id, task] of tasks) {
        if (task.at <= target && (!candidate || task.at < candidate.at || (task.at === candidate.at && id < candidateId))) {
          candidateId = id;
          candidate = task;
        }
      }
      if (!candidate) break;
      tasks.delete(candidateId);
      now = candidate.at;
      candidate.callback();
    }
    now = target;
  }

  return { setTimeout, clearTimeout, advance };
}

function createBridgeHarness(options = {}) {
  const timers = createTimers();
  const messages = [];
  const syncCalls = [];
  const syncCallbacks = [];
  const mount = options.mount || { idbPersistState: 0 };
  const hasFs = Object.prototype.hasOwnProperty.call(options, "fs");
  const fs = hasFs ? options.fs : {
    syncfs(populate, callback) {
      syncCalls.push(populate);
      syncCallbacks.push(callback);
    }
  };
  const library = {};
  let sandbox;
  const mergeInto = (target, additions) => {
    Object.assign(target, additions);
    for (const [name, value] of Object.entries(additions)) {
      if (name.startsWith("$")) sandbox[name.slice(1)] = value;
    }
  };

  sandbox = {
    LibraryManager: { library },
    Module: { __unityIdbfsMount: { mount } },
    FS: fs,
    SendMessage(receiver, method, payload) { messages.push({ receiver, method, payload }); },
    UTF8ToString() { return ""; },
    setTimeout: timers.setTimeout,
    clearTimeout: timers.clearTimeout,
    console: { warn() {} },
    mergeInto
  };
  vm.runInNewContext(bridgeSource, sandbox, { filename: "NectorialState.jslib" });
  return {
    library,
    mount,
    messages,
    persist: library.NectorialPersistSave,
    syncCalls,
    syncCallbacks,
    timers
  };
}

function payloads(messages) {
  return messages.map((message) => message.payload);
}

assert.match(bridgeSource, /\$NectorialPersistenceBridge/);
assert.match(bridgeSource, /NectorialPersistSave__deps:\s*\["\$NectorialPersistenceBridge"\]/);

{
  const bridge = createBridgeHarness();
  assert.equal(bridge.library.NectorialPersistSave__deps[0], "$NectorialPersistenceBridge", "the runtime function declares its emitted helper dependency");
  bridge.persist(1);
  assert.deepEqual(bridge.syncCalls, [false], "manual confirmation asks FS.syncfs for a write pass");
  bridge.syncCallbacks[0](null);
  assert.deepEqual(payloads(bridge.messages), ["1|ok"], "success is reported only from the syncfs callback");
}

{
  const bridge = createBridgeHarness();
  bridge.persist(2);
  bridge.syncCallbacks[0](new Error("indexeddb"));
  assert.deepEqual(payloads(bridge.messages), ["2|sync_error"], "callback errors never claim durable success");
}

{
  const bridge = createBridgeHarness({ fs: null });
  bridge.persist(3);
  assert.deepEqual(payloads(bridge.messages), ["3|unavailable_fs"], "missing filesystem support is a retryable failure result");
}

{
  const bridge = createBridgeHarness({
    fs: { syncfs() { throw new Error("sync throw"); } }
  });
  bridge.persist(4);
  assert.deepEqual(payloads(bridge.messages), ["4|sync_throw"], "synchronous bridge failures are surfaced instead of hanging");
}

{
  const bridge = createBridgeHarness({ mount: { idbPersistState: "idb" } });
  bridge.persist(5);
  assert.equal(bridge.syncCalls.length, 0, "the bridge waits for Unity auto-persistence already in flight");
  bridge.timers.advance(16);
  assert.equal(bridge.syncCalls.length, 0);
  bridge.mount.idbPersistState = 0;
  bridge.timers.advance(16);
  assert.equal(bridge.syncCalls.length, 1, "manual confirmation starts after Unity's queued persistence becomes idle");
  bridge.syncCallbacks[0](null);
  assert.deepEqual(payloads(bridge.messages), ["5|ok"]);
}

{
  const bridge = createBridgeHarness();
  bridge.persist(6);
  bridge.persist(7);
  assert.equal(bridge.syncCalls.length, 1, "overlapping requests are serialized");
  bridge.syncCallbacks[0](null);
  assert.equal(bridge.syncCalls.length, 2, "the second request starts only after the first callback");
  bridge.syncCallbacks[1](null);
  assert.deepEqual(payloads(bridge.messages), ["6|ok", "7|ok"]);
}

{
  const bridge = createBridgeHarness();
  bridge.persist(8);
  bridge.timers.advance(8000);
  assert.deepEqual(payloads(bridge.messages), ["8|timeout"], "a missing callback produces a bounded failure");
  bridge.persist(9);
  bridge.syncCallbacks[0](null);
  bridge.timers.advance(16);
  assert.equal(bridge.syncCalls.length, 2, "a retry waits for the stale sync to release before starting another write pass");
  bridge.syncCallbacks[1](null);
  assert.deepEqual(payloads(bridge.messages), ["8|timeout", "9|ok"], "a stale late callback cannot turn a timed-out request into success");
}

console.log("WebGL persistence bridge contract checks passed");
