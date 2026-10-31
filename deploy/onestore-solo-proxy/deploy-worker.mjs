import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const ACCOUNT_ID = '957eada44e00cdb84fec322133a89349';
const WORKER_NAME = 'few-moves-onestore';
const API_URL = `https://api.cloudflare.com/client/v4/accounts/${ACCOUNT_ID}/workers/scripts/${WORKER_NAME}`;
const BUNDLE_NAME = 'worker.bundle.r1.js';
const EXPECTED = Object.freeze({
  'worker.mjs': '2f12069ec6c72dccde2a5ad9cc62ff877085cd1f73f2bf89a4f77430a0a63c63',
  'package-manifest.mjs': 'b9a2d64b0c9674663ff324aa16f4604db798943dd0a2dfed64f6c8b4e28c291a',
  [BUNDLE_NAME]: 'ae3309f679adce5504309784750add1536ea5020bf60aae6f492a37d86861478',
  'wrangler.onestore-live.jsonc': '997564db1cbb1364fb66816683d38f2733e42234e72b518458522f9270ff4cb9',
});
const VARS = Object.freeze({
  UPSTREAM_ORIGIN: 'https://ljhljh0703-cmd.github.io',
  UPSTREAM_BASE_PATH: '/few-moves/releases/solo-raid-20261027-r1/',
  PACKAGE_MANIFEST_SHA256: 'a925b9dbd3c6996e202aaac570fdc7dd5e368bebe19a3471f7583c880b423daf',
});

function sha256(bytes) { return createHash('sha256').update(bytes).digest('hex'); }
function same(a, b) { assert.deepEqual(a, b); }
function safeId(value) { return typeof value === 'string' && /^[A-Za-z0-9._:-]{1,150}$/.test(value) ? value : null; }

export async function prepareUpload() {
  const bytes = {};
  for (const [name, digest] of Object.entries(EXPECTED)) {
    bytes[name] = await readFile(join(HERE, name));
    assert.equal(sha256(bytes[name]), digest, `${name} changed from reviewed input`);
  }
  const config = JSON.parse(bytes['wrangler.onestore-live.jsonc'].toString('utf8'));
  assert.equal(config.name, WORKER_NAME);
  assert.equal(config.account_id, ACCOUNT_ID);
  assert.equal(config.main, `./${BUNDLE_NAME}`);
  assert.equal(config.compatibility_date, '2026-10-31');
  assert.equal(config.workers_dev, true);
  assert.equal(config.preview_urls, false);
  assert.equal(config.upload_source_maps, false);
  assert.equal(config.send_metrics, false);
  same(config.dependencies_instrumentation, { enabled: false });
  same(config.observability, {
    enabled: false, redact_query_string: true,
    logs: { enabled: false }, traces: { enabled: false }, issues: { enabled: false },
  });
  same(config.vars, VARS);
  for (const forbidden of ['routes', 'route', 'assets', 'triggers', 'build', 'secrets']) {
    assert.equal(Object.hasOwn(config, forbidden), false, `${forbidden} must be absent`);
  }
  const metadata = {
    main_module: BUNDLE_NAME,
    compatibility_date: '2026-10-31',
    bindings: Object.entries(VARS).map(([name, text]) => ({ type: 'plain_text', name, text })),
    logpush: false,
    observability: {
      enabled: false,
      redact_query_string: true,
      logs: { enabled: false, invocation_logs: false, persist: false },
      traces: { enabled: false, persist: false },
      issues: { enabled: false },
    },
  };
  return {
    bundle: bytes[BUNDLE_NAME],
    metadata,
    publicPlan: {
      worker: WORKER_NAME,
      accountId: ACCOUNT_ID,
      method: 'PUT',
      apiPath: new URL(API_URL).pathname,
      bundleSha256: EXPECTED[BUNDLE_NAME],
      configSha256: EXPECTED['wrangler.onestore-live.jsonc'],
      metadataSha256: sha256(Buffer.from(JSON.stringify(metadata))),
      bindingNames: metadata.bindings.map(binding => binding.name),
      compatibilityDate: metadata.compatibility_date,
      observabilityEnabled: false,
    },
  };
}

export async function uploadWorker(plan, token, fetchImpl = fetch) {
  assert.equal(typeof token, 'string');
  assert.ok(token.length > 10, 'a scoped API token is required');
  const body = new FormData();
  body.append('metadata', JSON.stringify(plan.metadata));
  body.append(BUNDLE_NAME, new Blob([plan.bundle], { type: 'application/javascript+module' }), BUNDLE_NAME);
  let response;
  try {
    response = await fetchImpl(API_URL, {
      method: 'PUT', redirect: 'manual',
      headers: { Authorization: `Bearer ${token}` }, body,
      signal: AbortSignal.timeout(30_000),
    });
  } catch {
    return { ...plan.publicPlan, state: 'network_failure', requestSent: true, httpStatus: null, success: false, errorCodes: [] };
  }
  let parsed = null;
  try { parsed = await response.json(); } catch { /* Raw API bodies are never recorded. */ }
  const success = [200, 201].includes(response.status) && parsed?.success === true;
  const result = parsed?.result && typeof parsed.result === 'object' ? parsed.result : {};
  const errorCodes = Array.isArray(parsed?.errors)
    ? parsed.errors.map(error => error?.code).filter(code => Number.isInteger(code)).slice(0, 8)
    : [];
  return {
    ...plan.publicPlan,
    state: success ? 'api_accepted_live_validation_required' : 'api_rejected',
    requestSent: true,
    httpStatus: response.status,
    success,
    resultId: success ? safeId(result.id) : null,
    versionId: success ? safeId(result.version_id) : null,
    etag: success ? safeId(result.etag) : null,
    errorCodes,
  };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const dryRun = process.argv.length === 3 && process.argv[2] === '--dry-run';
  if (!dryRun && process.argv.length !== 2) throw new Error('usage: node deploy-worker.mjs [--dry-run]');
  const plan = await prepareUpload(); // Fail before reading any API token.
  if (dryRun) {
    process.stdout.write(JSON.stringify({ ...plan.publicPlan, state: 'local_plan_only', requestSent: false }) + '\n');
  } else {
    const token = process.env.CLOUDFLARE_API_TOKEN;
    if (!token) throw new Error('scoped API token missing');
    const receipt = await uploadWorker(plan, token);
    process.stdout.write(JSON.stringify(receipt) + '\n');
    if (!receipt.success) process.exitCode = 1;
  }
}
