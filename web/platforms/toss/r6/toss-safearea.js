// Few Moves — Apps in Toss only. Feeds the official SDK safe-area insets and OS into the CSS variables
// that toss-ui.css uses. Loaded as a module after the SDK unity-bridge (window.AppsInToss).
// It changes no game state, sends no messages to Unity and touches no storage.
const root = document.documentElement;
const SIDES = ["top", "right", "bottom", "left"];
let unsubscribe = null;

function apply(insets) {
  if (!insets || typeof insets !== "object") return;
  for (const side of SIDES) {
    const value = Number(insets[side]);
    if (Number.isFinite(value) && value >= 0 && value <= 400) root.style.setProperty(`--fm-sdk-${side}`, `${value}px`);
  }
}

function connect() {
  const ait = window.AppsInToss;
  try {
    const safeArea = ait && ait.SafeAreaInsets;
    if (!safeArea || typeof safeArea.get !== "function") throw new Error("SafeAreaInsets unavailable");
    apply(safeArea.get());
    if (typeof safeArea.subscribe === "function") {
      const off = safeArea.subscribe({ onEvent: apply });
      unsubscribe = typeof off === "function" ? off : null;
    }
    root.dataset.fmInsets = "sdk";
    root.style.removeProperty("--fm-top-floor");
  } catch (error) {
    // Project fallback (not official): CSS env() plus a 24px status-bar floor.
    root.dataset.fmInsets = "fallback";
    root.style.setProperty("--fm-top-floor", "24px");
  }
  try {
    const os = ait && ait.Device && ait.Device.os;
    root.dataset.fmOs = os === "ios" || os === "android" ? os : "unknown";
    if (os === "ios") root.style.setProperty("--fm-cap-offset", "5px");
    else root.style.removeProperty("--fm-cap-offset"); // Android or unknown: the CSS default (10px) applies
  } catch (error) {
    root.dataset.fmOs = "unknown";
  }
}

function disconnect() {
  if (unsubscribe) {
    try { unsubscribe(); } catch (error) { /* already detached */ }
  }
  unsubscribe = null;
}

connect();
window.addEventListener("pagehide", disconnect);
window.addEventListener("pageshow", (event) => { if (event.persisted && !unsubscribe) connect(); });
