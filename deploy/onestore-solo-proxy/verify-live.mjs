import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FILE_HASHES, PUBLIC_PATHS } from './package-manifest.mjs';

export const LIVE_BASE = 'https://few-moves-onestore.ljhljh0703.workers.dev/';
const CSP = 'frame-ancestors https://*.onestore.co.kr https://*.onestore.net';
const MIME = Object.freeze({
  html: 'text/html', css: 'text/css', js: 'javascript', json: 'application/json',
  wasm: 'application/wasm', data: 'application/octet-stream',
});

async function checked(url, options = {}, expectedStatus = 200, fetchImpl = fetch) {
  const response = await fetchImpl(url, { ...options, redirect: 'manual', signal: AbortSignal.timeout(60_000) });
  assert.equal(response.status, expectedStatus, `${new URL(url).pathname} status`);
  return response;
}

async function hashBody(response, keepText = false) {
  const hash = createHash('sha256');
  const parts = [];
  for await (const chunk of response.body ?? []) {
    hash.update(chunk);
    if (keepText) parts.push(Buffer.from(chunk));
  }
  return { sha256: hash.digest('hex'), text: keepText ? Buffer.concat(parts).toString('utf8') : null };
}

function checkHtmlHeaders(response) {
  assert.equal(response.headers.get('content-security-policy'), CSP);
  assert.equal(response.headers.get('cache-control'), 'no-store');
  assert.equal(response.headers.get('x-frame-options'), null);
  assert.equal(response.headers.get('set-cookie'), null);
  assert.notEqual(response.headers.get('access-control-allow-origin'), '*');
  assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
}

export async function verifyLive(fetchImpl = fetch) {
  const startedAt = new Date().toISOString();
  assert.equal(PUBLIC_PATHS.length, 14);
  const results = [];
  const html = {};
  for (const path of PUBLIC_PATHS) {
    const response = await checked(LIVE_BASE + path, {}, 200, fetchImpl);
    const type = (response.headers.get('content-type') ?? '').toLowerCase();
    const extension = path.split('.').at(-1);
    assert.ok(type.includes(MIME[extension]), `${path} MIME`);
    if (extension === 'html') checkHtmlHeaders(response);
    const body = await hashBody(response, extension === 'html');
    assert.equal(body.sha256, FILE_HASHES[path], `${path} SHA-256`);
    if (extension === 'html') html[path] = body.text;
    results.push({ path, status: 200, contentType: type, sha256: body.sha256 });
  }

  const entryCases = [
    ['/', 'index.html'], ['/index.html', 'index.html'],
    ['/solo/', 'solo/index.html'], ['/solo/index.html', 'solo/index.html'],
    ['/raid/', 'raid/index.html'], ['/raid/index.html', 'raid/index.html'],
  ];
  for (const [path, expectedFile] of entryCases) {
    for (const method of ['GET', 'POST']) {
      const response = await checked(new URL(path, LIVE_BASE), { method }, 200, fetchImpl);
      checkHtmlHeaders(response);
      const body = await hashBody(response);
      assert.equal(body.sha256, FILE_HASHES[expectedFile], `${method} ${path} HTML SHA-256`);
    }
  }

  for (const mode of ['solo', 'raid']) {
    const loader = PUBLIC_PATHS.find(path => path.startsWith(`${mode}/Build/`) && path.endsWith('.loader.js'));
    assert.ok(loader && html[`${mode}/index.html`].includes(loader.split('/').at(-1)));
    for (const method of ['GET', 'POST']) {
      const original = new URL(`${mode}?probe=one`, LIVE_BASE);
      const response = await checked(original, { method }, 307, fetchImpl);
      const location = response.headers.get('location');
      assert.equal(location, new URL(`${mode}/?probe=one`, LIVE_BASE).href);
      const target = new URL(`Build/${loader.split('/').at(-1)}`, location);
      assert.equal(target.href, new URL(loader, LIVE_BASE).href);
      if (method === 'POST') {
        const final = await checked(location, { method: 'POST' }, 200, fetchImpl);
        const body = await hashBody(final);
        assert.equal(body.sha256, FILE_HASHES[`${mode}/index.html`]);
      }
    }
  }

  const wasm = PUBLIC_PATHS.find(path => path.endsWith('.wasm'));
  const head = await checked(LIVE_BASE + wasm, { method: 'HEAD' }, 200, fetchImpl);
  assert.equal((head.headers.get('content-type') ?? '').toLowerCase(), 'application/wasm');
  assert.equal(head.body, null);
  for (const path of ['/api', '/api/coop/v1', '/coop', '/coop/room', '/unknown']) {
    for (const method of ['GET', 'POST']) await checked(new URL(path, LIVE_BASE), { method }, 404, fetchImpl);
  }
  for (const path of ['/style.css', '/' + wasm, '/' + PUBLIC_PATHS.find(x => x.endsWith('.loader.js'))]) {
    await checked(new URL(path, LIVE_BASE), { method: 'POST' }, 405, fetchImpl);
  }
  return {
    state: 'live_http_verified', startedAt, finishedAt: new Date().toISOString(),
    workerUrl: LIVE_BASE, staticHashPass: results.length, staticHashFail: 0,
    staticFiles: results, canonicalEntryMethodChecks: 12,
    slashRedirectChecks: 4, postRedirectFollowChecks: 2,
    blockedPathChecks: 10, assetPostRejectChecks: 3, wasmHeadPass: true,
    realDeviceVerified: false, storeReviewSubmitted: false,
  };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 2) throw new Error('usage: node verify-live.mjs');
  try { process.stdout.write(JSON.stringify(await verifyLive()) + '\n'); }
  catch (error) {
    process.stderr.write(JSON.stringify({ state: 'live_http_failed', reason: String(error.message).slice(0, 220) }) + '\n');
    process.exitCode = 1;
  }
}
