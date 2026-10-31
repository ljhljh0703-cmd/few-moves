import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { handleRequest, validateDeploymentConfig } from './worker.mjs';
import { FILE_HASHES, PACKAGE_MANIFEST_SHA256, PUBLIC_PATHS } from './package-manifest.mjs';
import { verifyRelease } from './verify-upstream.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const packageRoot = resolve(here, '../../Builds/SubmissionPreparation-2026-10-27/packaging/onestore-solo-first-r1');
const env = Object.freeze({
  UPSTREAM_ORIGIN: 'https://approved-owner.github.io',
  UPSTREAM_BASE_PATH: '/few-moves-content/releases/050e274/',
  PACKAGE_MANIFEST_SHA256,
});
const upstreamPrefix = env.UPSTREAM_BASE_PATH;
const packageFiles = new Set([...Object.keys(FILE_HASHES), 'release-manifest.json']);

function response(text = 'ok', type = 'text/plain') {
  return new Response(text, { status: 200, headers: { 'Content-Type': type, 'Set-Cookie': 'upstream-private=1', 'X-Frame-Options': 'DENY' } });
}

function mockFetch(calls, result = response()) {
  return async (url, init) => { calls.push({ url, init }); return result; };
}

function localServer() {
  const seen = [];
  const server = createServer(async (request, reply) => {
    const url = new URL(request.url, 'http://localhost');
    seen.push({ method: request.method, path: url.pathname, search: url.search, authorization: request.headers.authorization, cookie: request.headers.cookie });
    if (!url.pathname.startsWith(upstreamPrefix) || url.search) { reply.writeHead(404); reply.end('not found'); return; }
    const relative = url.pathname.slice(upstreamPrefix.length);
    if (!packageFiles.has(relative)) { reply.writeHead(404); reply.end('not found'); return; }
    const file = join(packageRoot, relative);
    let size;
    try { size = (await stat(file)).size; } catch { reply.writeHead(404); reply.end('not found'); return; }
    const extension = relative.split('.').at(-1);
    const types = { html: 'text/html; charset=utf-8', js: 'text/javascript', css: 'text/css', wasm: 'application/wasm', json: 'application/json', data: 'application/octet-stream' };
    reply.writeHead(200, { 'Content-Type': types[extension] || 'application/octet-stream', 'Content-Length': size });
    if (request.method === 'HEAD') { reply.end(); return; }
    createReadStream(file).pipe(reply);
  });
  return { server, seen };
}

test('fixed config rejects placeholders, hostile origins, and path traversal', () => {
  assert.equal(validateDeploymentConfig(env).base, upstreamPrefix);
  assert.throws(() => validateDeploymentConfig({ ...env, UPSTREAM_ORIGIN: 'https://evil.example' }));
  assert.throws(() => validateDeploymentConfig({ ...env, UPSTREAM_ORIGIN: 'https://approved-owner.github.io.evil.example' }));
  assert.throws(() => validateDeploymentConfig({ ...env, UPSTREAM_BASE_PATH: '/repo/../escape/' }));
  assert.throws(() => validateDeploymentConfig({ ...env, UPSTREAM_BASE_PATH: '/REPLACE_REPO/VERSION/' }));
  assert.throws(() => validateDeploymentConfig({ ...env, PACKAGE_MANIFEST_SHA256: '0'.repeat(64) }));
});

test('POST entry becomes GET upstream with no private body, headers, or query', async () => {
  const calls = [];
  const request = new Request('https://edge.example/solo/?token=private&mode=solo', { method: 'POST', headers: { Authorization: 'Bearer hidden', Cookie: 'sid=hidden' }, body: 'secret-payload' });
  const result = await handleRequest(request, env, mockFetch(calls, response('<html>solo</html>', 'text/html')));
  assert.equal(result.status, 200);
  assert.equal(await result.text(), '<html>solo</html>');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, env.UPSTREAM_ORIGIN + upstreamPrefix + 'solo/index.html');
  assert.equal(calls[0].init.method, 'GET');
  assert.equal(calls[0].init.redirect, 'manual');
  assert.equal(calls[0].init.body, undefined);
  assert.equal(calls[0].init.headers, undefined);
  assert.equal(result.headers.get('content-security-policy'), 'frame-ancestors https://*.onestore.co.kr https://*.onestore.net');
  assert.equal(result.headers.get('cache-control'), 'no-store');
  assert.equal(result.headers.get('set-cookie'), null);
  assert.equal(result.headers.get('x-frame-options'), null);
});

test('slash redirect is 307 and preserves client query before upstream strips it', async () => {
  const calls = [];
  const request = new Request('https://edge.example/raid?token=private', { method: 'POST', body: 'secret' });
  const result = await handleRequest(request, env, mockFetch(calls));
  assert.equal(result.status, 307);
  assert.equal(result.headers.get('location'), 'https://edge.example/raid/?token=private');
  assert.equal(calls.length, 0);
});

test('private and unlisted paths fail closed; assets reject POST', async () => {
  const calls = [];
  const fetcher = mockFetch(calls);
  for (const path of ['/api/coop/v1', '/api', '/coop', '/coop/room', '//evil.example/x', '/unknown']) {
    assert.equal((await handleRequest(new Request('https://edge.example' + path), env, fetcher)).status, 404, path);
  }
  assert.equal((await handleRequest(new Request('https://edge.example/shared/Build/' + PUBLIC_PATHS.find(p => p.endsWith('.wasm')).split('/').at(-1), { method: 'POST' }), env, fetcher)).status, 405);
  assert.equal((await handleRequest(new Request('https://edge.example/style.css', { method: 'POST' }), env, fetcher)).status, 405);
  assert.equal(calls.length, 0);
});

test('upstream redirect, missing file, network failure, and invalid config are readable errors', async () => {
  const url = 'https://edge.example/solo/';
  const redirect = await handleRequest(new Request(url), env, async () => new Response(null, { status: 302, headers: { Location: 'https://evil.example/' } }));
  assert.equal(redirect.status, 502);
  assert.equal(redirect.headers.get('location'), null);
  const missing = await handleRequest(new Request(url), env, async () => new Response('missing', { status: 404 }));
  assert.equal(missing.status, 503);
  assert.match(await missing.text(), /게임 파일/);
  const failed = await handleRequest(new Request(url), env, async () => { throw new Error('private details'); });
  assert.equal(failed.status, 502);
  assert.doesNotMatch(await failed.text(), /private details/);
  const invalid = await handleRequest(new Request(url), { ...env, UPSTREAM_ORIGIN: 'https://evil.example' }, async () => { throw new Error('must not fetch'); });
  assert.equal(invalid.status, 503);
});

test('real local HTTP adapter preserves loader-relative routes, MIME, HEAD, and exact 17-file hash gate', async () => {
  const { server, seen } = localServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  const adapter = (url, init) => {
    const fixed = new URL(url);
    assert.equal(fixed.origin, env.UPSTREAM_ORIGIN);
    return fetch('http://127.0.0.1:' + port + fixed.pathname, init);
  };
  try {
    const posted = await handleRequest(new Request('https://edge.example/solo/?token=private', { method: 'POST', body: 'private-body', headers: { Cookie: 'secret=1', Authorization: 'Bearer secret' } }), env, adapter);
    assert.equal(posted.status, 200);
    const html = await posted.text();
    assert.ok(html.includes('var buildUrl = "Build";'));
    assert.ok(html.includes('3aa011ed3972568aa93388d304bf8d64.loader.js'));
    const loaderUrl = new URL('Build/3aa011ed3972568aa93388d304bf8d64.loader.js', 'https://edge.example/solo/');
    const loader = await handleRequest(new Request(loaderUrl), env, adapter);
    assert.equal(loader.status, 200);
    assert.match(loader.headers.get('content-type'), /javascript/);
    assert.ok((await loader.text()).length > 100);
    const wasmPath = PUBLIC_PATHS.find(p => p.endsWith('.wasm'));
    const head = await handleRequest(new Request('https://edge.example/' + wasmPath, { method: 'HEAD' }), env, adapter);
    assert.equal(head.status, 200);
    assert.equal(head.headers.get('content-type'), 'application/wasm');
    assert.equal(await head.text(), '');
    assert.ok(seen.every(x => x.method === 'GET' || x.method === 'HEAD'));
    assert.ok(seen.every(x => !x.search && !x.authorization && !x.cookie));
    const gate = await verifyRelease(env, adapter);
    assert.equal(gate.verifiedFiles, 17);
    assert.equal(gate.packageManifestSha256, PACKAGE_MANIFEST_SHA256);
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});


test('real edge HTTP adapter keeps POST slash semantics and loader routes', async () => {
  const { server: upstream, seen } = localServer();
  await new Promise(resolve => upstream.listen(0, '127.0.0.1', resolve));
  const upstreamPort = upstream.address().port;
  const adapter = (url, init) => {
    const fixed = new URL(url);
    assert.equal(fixed.origin, env.UPSTREAM_ORIGIN);
    return fetch('http://127.0.0.1:' + upstreamPort + fixed.pathname, init);
  };
  const edge = createServer(async (incoming, outgoing) => {
    try {
      const chunks = [];
      for await (const chunk of incoming) chunks.push(chunk);
      const method = incoming.method;
      const clientUrl = 'http://127.0.0.1:' + edge.address().port + incoming.url;
      const request = new Request(clientUrl, {
        method, headers: incoming.headers,
        ...(['GET', 'HEAD'].includes(method) ? {} : { body: Buffer.concat(chunks) }),
      });
      const response = await handleRequest(request, env, adapter);
      outgoing.writeHead(response.status, Object.fromEntries(response.headers));
      if (method === 'HEAD' || !response.body) { outgoing.end(); return; }
      for await (const chunk of response.body) outgoing.write(chunk);
      outgoing.end();
    } catch {
      outgoing.writeHead(500); outgoing.end('local adapter failure');
    }
  });
  await new Promise(resolve => edge.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + edge.address().port;
  try {
    const redirected = await fetch(base + '/solo?token=private', { method: 'POST', body: 'private-body', redirect: 'manual' });
    assert.equal(redirected.status, 307);
    const location = redirected.headers.get('location');
    assert.equal(location, base + '/solo/?token=private');
    const page = await fetch(location, { method: 'POST', body: 'private-body', headers: { Authorization: 'Bearer hidden', Cookie: 'sid=hidden' }, redirect: 'manual' });
    assert.equal(page.status, 200);
    assert.match(page.headers.get('content-security-policy'), /^frame-ancestors /);
    const html = await page.text();
    assert.ok(html.includes('var buildUrl = "Build";'));
    const asset = await fetch(new URL('Build/3aa011ed3972568aa93388d304bf8d64.loader.js', location));
    assert.equal(asset.status, 200);
    assert.match(asset.headers.get('content-type'), /javascript/);
    assert.ok((await asset.text()).length > 100);
    assert.equal((await fetch(base + '/api/coop/v1')).status, 404);
    const wasm = PUBLIC_PATHS.find(p => p.endsWith('.wasm'));
    const head = await fetch(base + '/' + wasm, { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal(head.headers.get('content-type'), 'application/wasm');
    assert.equal(await head.text(), '');
    assert.ok(seen.every(x => x.method === 'GET' || x.method === 'HEAD'));
    assert.ok(seen.every(x => !x.search && !x.authorization && !x.cookie));
  } finally {
    edge.closeAllConnections();
    upstream.closeAllConnections();
    await Promise.all([
      new Promise(resolve => edge.close(resolve)),
      new Promise(resolve => upstream.close(resolve)),
    ]);
  }
});
