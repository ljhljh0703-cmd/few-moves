import { PACKAGE_MANIFEST_SHA256, PUBLIC_PATH_SET } from './package-manifest.mjs';

// Only these entry documents may accept the ONE Store POST navigation.
const ENTRY_FILES = Object.freeze({
  '/': 'index.html',
  '/index.html': 'index.html',
  '/solo/': 'solo/index.html',
  '/solo/index.html': 'solo/index.html',
  '/raid/': 'raid/index.html',
  '/raid/index.html': 'raid/index.html',
});
const POST_ENTRIES = new Set(Object.keys(ENTRY_FILES));
const FRAME_ANCESTORS = 'frame-ancestors https://*.onestore.co.kr https://*.onestore.net';
const PROXY_HEADERS = Object.freeze(['content-type', 'content-encoding', 'content-length', 'etag', 'last-modified']);

function simple(status, message, isHead = false, extra = {}) {
  const headers = new Headers({
    'Content-Type': 'text/plain; charset=utf-8',
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
    ...extra,
  });
  return new Response(isHead ? null : message, { status, headers });
}

function isPrivatePath(path) {
  return path === '/api' || path.startsWith('/api/') || path === '/coop' || path.startsWith('/coop/');
}

// Deployment configuration is fixed by the operator, never by the request URL or headers.
export function validateDeploymentConfig(env) {
  if (!env || typeof env.UPSTREAM_ORIGIN !== 'string' || typeof env.UPSTREAM_BASE_PATH !== 'string') {
    throw new Error('missing fixed upstream configuration');
  }
  if (env.PACKAGE_MANIFEST_SHA256 !== PACKAGE_MANIFEST_SHA256) {
    throw new Error('package manifest version mismatch');
  }
  if (/REPLACE|EXAMPLE|PLACEHOLDER/i.test(env.UPSTREAM_ORIGIN + env.UPSTREAM_BASE_PATH)) {
    throw new Error('example upstream configuration is not deployable');
  }
  let origin;
  try { origin = new URL(env.UPSTREAM_ORIGIN); } catch { throw new Error('invalid upstream origin'); }
  if (origin.protocol !== 'https:' || !/^[a-z0-9-]+\.github\.io$/.test(origin.hostname)
      || origin.username || origin.password || origin.port || origin.pathname !== '/'
      || origin.search || origin.hash) {
    throw new Error('upstream must be an exact HTTPS GitHub Pages origin');
  }
  const base = env.UPSTREAM_BASE_PATH;
  const segments = base.split('/').filter(Boolean);
  if (!/^\/(?:[A-Za-z0-9._-]+\/)+$/.test(base) || segments.length < 2
      || segments.some(s => s === '.' || s === '..')) {
    throw new Error('upstream base path must be fixed and have at least two segments');
  }
  return { origin: origin.origin, base };
}

function fileForPath(pathname) {
  if (Object.hasOwn(ENTRY_FILES, pathname)) return ENTRY_FILES[pathname];
  if (!pathname.startsWith('/')) return null;
  const file = pathname.slice(1);
  return PUBLIC_PATH_SET.has(file) ? file : null;
}

// fetchImpl injection is used only for local tests; production uses the Workers fetch API.
export async function handleRequest(request, env, fetchImpl = fetch) {
  let url;
  try { url = new URL(request.url); } catch { return simple(400, '잘못된 요청 주소입니다.'); }
  const method = request.method.toUpperCase();
  const isHead = method === 'HEAD';
  const pathname = url.pathname;

  if (isPrivatePath(pathname)) return simple(404, '페이지를 찾을 수 없습니다.', isHead);
  if (!['GET', 'HEAD', 'POST'].includes(method)) {
    return simple(405, '허용되지 않은 요청 방식입니다.', isHead, { Allow: 'GET, HEAD, POST' });
  }
  if (pathname === '/solo' || pathname === '/raid') {
    url.pathname = pathname + '/';
    return new Response(null, { status: 307, headers: {
      Location: url.toString(), 'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer',
    } });
  }
  const file = fileForPath(pathname);
  if (!file) return simple(404, '페이지를 찾을 수 없습니다.', isHead);
  if (method === 'POST' && !POST_ENTRIES.has(pathname)) {
    return simple(405, '이 경로는 POST 진입을 받지 않습니다.', false, { Allow: 'GET, HEAD' });
  }
  let config;
  try { config = validateDeploymentConfig(env); }
  catch { return simple(503, '게임 연결 설정을 확인 중입니다. 잠시 후 다시 시도해 주세요.', isHead); }

  // file is a compile-time allowlisted relative path. Incoming query, headers, and POST body are never copied.
  const upstreamUrl = config.origin + config.base + file;
  let upstream;
  try {
    upstream = await fetchImpl(upstreamUrl, { method: isHead ? 'HEAD' : 'GET', redirect: 'manual' });
  } catch {
    return simple(502, '게임 파일에 연결할 수 없습니다. 잠시 후 다시 시도해 주세요.', isHead);
  }
  if (upstream.status >= 300 && upstream.status < 400) {
    return simple(502, '게임 파일 경로를 확인할 수 없습니다.', isHead);
  }
  if (upstream.status !== 200) {
    return simple(503, '게임 파일을 불러오지 못했습니다. 잠시 후 다시 시도해 주세요.', isHead);
  }

  const headers = new Headers();
  for (const name of PROXY_HEADERS) {
    const value = upstream.headers.get(name);
    if (value !== null) headers.set(name, value);
  }
  headers.set('Cache-Control', file.endsWith('.html') ? 'no-store' : 'public, max-age=300, must-revalidate');
  headers.set('X-Content-Type-Options', 'nosniff');
  headers.set('Referrer-Policy', 'no-referrer');
  if (file.endsWith('.html')) headers.set('Content-Security-Policy', FRAME_ANCESTORS);
  // Do not forward upstream cookies, Location, X-Frame-Options, or other response headers.
  // ReadableStream body passes through without buffering the 33 MB WASM in Worker memory.
  return new Response(isHead ? null : upstream.body, { status: 200, headers });
}

export default {
  fetch(request, env) { return handleRequest(request, env); },
};
