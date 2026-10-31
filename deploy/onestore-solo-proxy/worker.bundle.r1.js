var __defProp = Object.defineProperty;
var __name = (target, value) => __defProp(target, "name", { value, configurable: true });

// package-manifest.mjs
var PACKAGE_MANIFEST_SHA256 = "a925b9dbd3c6996e202aaac570fdc7dd5e368bebe19a3471f7583c880b423daf";
var FILE_HASHES = Object.freeze({
  "index.html": "b24388d50c676483f2a8de87989ca2156e466fc154109be00295dca05324d3e6",
  "platform/fewmoves-onestore.js": "da588f98c4862d2a8c5da96ad1c720f513f0f58b9d6162f90929d408b078a2d3",
  "raid/Build/3aa011ed3972568aa93388d304bf8d64.loader.js": "19a2f6769623bc78cdd00f6d2ac9036fc1829a82825e4b7f95b45c8df0c1e591",
  "raid/Build/42ea6d2a8e2577bbaff4acc375cb5fbb.framework.js": "2b2ab9f3552899c835887a2c9830bc8487bfe8dd3acff540dfefb375139b98db",
  "raid/Build/52340ed46e985d9a1d268c276433fe63.symbols.json": "d5fe56e2caa562f40ad5d07514dbc945b74218541736faa1fac7001466ab00b4",
  "raid/Build/bfe98245a92901fbde73e85103e54837.data": "d9ee1d7174519d3abcff269d901082911c9ebff12549f04b302450091653e733",
  "raid/index.html": "7d2069be0a6538866c29f9aac58ec27f2f92f3608b72673befd7ddc9044fc931",
  "raid/unity-build.provenance.json": "efe5249b870de98da683306628278f7d40cef56592db33bef7f6cabfe6555617",
  "shared/Build/bf0907728e94a8908068043c76cbbac4.wasm": "3b1e0910f4eb51b4595ddc3d1beaee3b5350cdcabcd1e8637d729243f56044b8",
  "solo/Build/3aa011ed3972568aa93388d304bf8d64.loader.js": "19a2f6769623bc78cdd00f6d2ac9036fc1829a82825e4b7f95b45c8df0c1e591",
  "solo/Build/427d670ff41660465423ed7003083f21.data": "b751914ba02c312ee4f95c0fb8ea6beb1102dcfb15efeda14206ea037cf76bbe",
  "solo/Build/42ea6d2a8e2577bbaff4acc375cb5fbb.framework.js": "2b2ab9f3552899c835887a2c9830bc8487bfe8dd3acff540dfefb375139b98db",
  "solo/Build/52340ed46e985d9a1d268c276433fe63.symbols.json": "d5fe56e2caa562f40ad5d07514dbc945b74218541736faa1fac7001466ab00b4",
  "solo/index.html": "6ee4fe610a955aa2f72fa778d616f63f5a401728bb1bcb158ebc444bcca8952a",
  "solo/unity-build.provenance.json": "c8b5fc6604cdfa195e01fc8d2cddba5ec3491c263761f58530e0866cfb1c1eff",
  "style.css": "a425d5baa8f0b5acc38b25056319b6e95e1d74f306afe1442f62a9c03c5c59ab"
});
var PUBLIC_PATHS = Object.freeze([
  "index.html",
  "platform/fewmoves-onestore.js",
  "raid/Build/3aa011ed3972568aa93388d304bf8d64.loader.js",
  "raid/Build/42ea6d2a8e2577bbaff4acc375cb5fbb.framework.js",
  "raid/Build/52340ed46e985d9a1d268c276433fe63.symbols.json",
  "raid/Build/bfe98245a92901fbde73e85103e54837.data",
  "raid/index.html",
  "shared/Build/bf0907728e94a8908068043c76cbbac4.wasm",
  "solo/Build/3aa011ed3972568aa93388d304bf8d64.loader.js",
  "solo/Build/427d670ff41660465423ed7003083f21.data",
  "solo/Build/42ea6d2a8e2577bbaff4acc375cb5fbb.framework.js",
  "solo/Build/52340ed46e985d9a1d268c276433fe63.symbols.json",
  "solo/index.html",
  "style.css"
]);
var PUBLIC_PATH_SET = new Set(PUBLIC_PATHS);

// worker.mjs
var ENTRY_FILES = Object.freeze({
  "/": "index.html",
  "/index.html": "index.html",
  "/solo/": "solo/index.html",
  "/solo/index.html": "solo/index.html",
  "/raid/": "raid/index.html",
  "/raid/index.html": "raid/index.html"
});
var POST_ENTRIES = new Set(Object.keys(ENTRY_FILES));
var FRAME_ANCESTORS = "frame-ancestors https://*.onestore.co.kr https://*.onestore.net";
var PROXY_HEADERS = Object.freeze(["content-type", "content-encoding", "content-length", "etag", "last-modified"]);
function simple(status, message, isHead = false, extra = {}) {
  const headers = new Headers({
    "Content-Type": "text/plain; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "no-referrer",
    ...extra
  });
  return new Response(isHead ? null : message, { status, headers });
}
__name(simple, "simple");
function isPrivatePath(path) {
  return path === "/api" || path.startsWith("/api/") || path === "/coop" || path.startsWith("/coop/");
}
__name(isPrivatePath, "isPrivatePath");
function validateDeploymentConfig(env) {
  if (!env || typeof env.UPSTREAM_ORIGIN !== "string" || typeof env.UPSTREAM_BASE_PATH !== "string") {
    throw new Error("missing fixed upstream configuration");
  }
  if (env.PACKAGE_MANIFEST_SHA256 !== PACKAGE_MANIFEST_SHA256) {
    throw new Error("package manifest version mismatch");
  }
  if (/REPLACE|EXAMPLE|PLACEHOLDER/i.test(env.UPSTREAM_ORIGIN + env.UPSTREAM_BASE_PATH)) {
    throw new Error("example upstream configuration is not deployable");
  }
  let origin;
  try {
    origin = new URL(env.UPSTREAM_ORIGIN);
  } catch {
    throw new Error("invalid upstream origin");
  }
  if (origin.protocol !== "https:" || !/^[a-z0-9-]+\.github\.io$/.test(origin.hostname) || origin.username || origin.password || origin.port || origin.pathname !== "/" || origin.search || origin.hash) {
    throw new Error("upstream must be an exact HTTPS GitHub Pages origin");
  }
  const base = env.UPSTREAM_BASE_PATH;
  const segments = base.split("/").filter(Boolean);
  if (!/^\/(?:[A-Za-z0-9._-]+\/)+$/.test(base) || segments.length < 2 || segments.some((s) => s === "." || s === "..")) {
    throw new Error("upstream base path must be fixed and have at least two segments");
  }
  return { origin: origin.origin, base };
}
__name(validateDeploymentConfig, "validateDeploymentConfig");
function fileForPath(pathname) {
  if (Object.hasOwn(ENTRY_FILES, pathname)) return ENTRY_FILES[pathname];
  if (!pathname.startsWith("/")) return null;
  const file = pathname.slice(1);
  return PUBLIC_PATH_SET.has(file) ? file : null;
}
__name(fileForPath, "fileForPath");
async function handleRequest(request, env, fetchImpl = fetch) {
  let url;
  try {
    url = new URL(request.url);
  } catch {
    return simple(400, "\uC798\uBABB\uB41C \uC694\uCCAD \uC8FC\uC18C\uC785\uB2C8\uB2E4.");
  }
  const method = request.method.toUpperCase();
  const isHead = method === "HEAD";
  const pathname = url.pathname;
  if (isPrivatePath(pathname)) return simple(404, "\uD398\uC774\uC9C0\uB97C \uCC3E\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.", isHead);
  if (!["GET", "HEAD", "POST"].includes(method)) {
    return simple(405, "\uD5C8\uC6A9\uB418\uC9C0 \uC54A\uC740 \uC694\uCCAD \uBC29\uC2DD\uC785\uB2C8\uB2E4.", isHead, { Allow: "GET, HEAD, POST" });
  }
  if (pathname === "/solo" || pathname === "/raid") {
    url.pathname = pathname + "/";
    return new Response(null, { status: 307, headers: {
      Location: url.toString(),
      "Cache-Control": "no-store",
      "Referrer-Policy": "no-referrer"
    } });
  }
  const file = fileForPath(pathname);
  if (!file) return simple(404, "\uD398\uC774\uC9C0\uB97C \uCC3E\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.", isHead);
  if (method === "POST" && !POST_ENTRIES.has(pathname)) {
    return simple(405, "\uC774 \uACBD\uB85C\uB294 POST \uC9C4\uC785\uC744 \uBC1B\uC9C0 \uC54A\uC2B5\uB2C8\uB2E4.", false, { Allow: "GET, HEAD" });
  }
  let config;
  try {
    config = validateDeploymentConfig(env);
  } catch {
    return simple(503, "\uAC8C\uC784 \uC5F0\uACB0 \uC124\uC815\uC744 \uD655\uC778 \uC911\uC785\uB2C8\uB2E4. \uC7A0\uC2DC \uD6C4 \uB2E4\uC2DC \uC2DC\uB3C4\uD574 \uC8FC\uC138\uC694.", isHead);
  }
  const upstreamUrl = config.origin + config.base + file;
  let upstream;
  try {
    upstream = await fetchImpl(upstreamUrl, { method: isHead ? "HEAD" : "GET", redirect: "manual" });
  } catch {
    return simple(502, "\uAC8C\uC784 \uD30C\uC77C\uC5D0 \uC5F0\uACB0\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4. \uC7A0\uC2DC \uD6C4 \uB2E4\uC2DC \uC2DC\uB3C4\uD574 \uC8FC\uC138\uC694.", isHead);
  }
  if (upstream.status >= 300 && upstream.status < 400) {
    return simple(502, "\uAC8C\uC784 \uD30C\uC77C \uACBD\uB85C\uB97C \uD655\uC778\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.", isHead);
  }
  if (upstream.status !== 200) {
    return simple(503, "\uAC8C\uC784 \uD30C\uC77C\uC744 \uBD88\uB7EC\uC624\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4. \uC7A0\uC2DC \uD6C4 \uB2E4\uC2DC \uC2DC\uB3C4\uD574 \uC8FC\uC138\uC694.", isHead);
  }
  const headers = new Headers();
  for (const name of PROXY_HEADERS) {
    const value = upstream.headers.get(name);
    if (value !== null) headers.set(name, value);
  }
  headers.set("Cache-Control", file.endsWith(".html") ? "no-store" : "public, max-age=300, must-revalidate");
  headers.set("X-Content-Type-Options", "nosniff");
  headers.set("Referrer-Policy", "no-referrer");
  if (file.endsWith(".html")) headers.set("Content-Security-Policy", FRAME_ANCESTORS);
  return new Response(isHead ? null : upstream.body, { status: 200, headers });
}
__name(handleRequest, "handleRequest");
var worker_default = {
  fetch(request, env) {
    return handleRequest(request, env);
  }
};
export {
  worker_default as default,
  handleRequest,
  validateDeploymentConfig
};
