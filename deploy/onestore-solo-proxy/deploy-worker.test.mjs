import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { prepareUpload, uploadWorker } from './deploy-worker.mjs';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');

test('fixed local plan pins one Worker, approved bundle, three public bindings and privacy settings', async () => {
  const plan = await prepareUpload();
  assert.equal(plan.publicPlan.worker, 'few-moves-onestore');
  assert.equal(plan.publicPlan.accountId, '957eada44e00cdb84fec322133a89349');
  assert.equal(digest(plan.bundle), 'ae3309f679adce5504309784750add1536ea5020bf60aae6f492a37d86861478');
  assert.equal(plan.metadata.main_module, 'worker.bundle.r1.js');
  assert.equal(plan.metadata.compatibility_date, '2026-10-31');
  assert.deepEqual(plan.metadata.bindings.map(x => x.name), [
    'UPSTREAM_ORIGIN', 'UPSTREAM_BASE_PATH', 'PACKAGE_MANIFEST_SHA256',
  ]);
  assert.ok(plan.metadata.bindings.every(x => x.type === 'plain_text'));
  assert.equal(plan.metadata.observability.enabled, false);
  assert.equal(plan.metadata.observability.logs.enabled, false);
  assert.equal(plan.metadata.observability.traces.enabled, false);
  assert.equal(plan.metadata.observability.issues.enabled, false);
});

test('upload sends one exact-worker multipart PUT and returns only allowed receipt fields', async () => {
  const plan = await prepareUpload();
  const dummy = 'synthetic-test-value-only';
  let calls = 0;
  const fetcher = async (url, init) => {
    calls++;
    assert.equal(url, 'https://api.cloudflare.com/client/v4/accounts/957eada44e00cdb84fec322133a89349/workers/scripts/few-moves-onestore');
    assert.equal(init.method, 'PUT');
    assert.equal(init.redirect, 'manual');
    assert.equal(init.headers.Authorization, `Bearer ${dummy}`);
    assert.deepEqual([...init.body.keys()], ['metadata', 'worker.bundle.r1.js']);
    assert.deepEqual(JSON.parse(init.body.get('metadata')), plan.metadata);
    assert.equal(digest(Buffer.from(await init.body.get('worker.bundle.r1.js').arrayBuffer())), plan.publicPlan.bundleSha256);
    return Response.json({ success: true, result: { id: 'few-moves-onestore', version_id: 'v1', etag: 'etag-1', extra_private: dummy } });
  };
  const result = await uploadWorker(plan, dummy, fetcher);
  assert.equal(calls, 1);
  assert.equal(result.state, 'api_accepted_live_validation_required');
  assert.equal(result.success, true);
  assert.doesNotMatch(JSON.stringify(result), /synthetic-test-value-only|extra_private/);
});

test('redirect and API error fail closed without recording body, location or token', async () => {
  const plan = await prepareUpload();
  const dummy = 'synthetic-test-value-only';
  let calls = 0;
  const redirect = await uploadWorker(plan, dummy, async () => {
    calls++;
    return new Response(null, { status: 302, headers: { Location: 'https://other.example/private' } });
  });
  assert.equal(calls, 1);
  assert.equal(redirect.success, false);
  assert.equal(redirect.httpStatus, 302);
  assert.doesNotMatch(JSON.stringify(redirect), /other\.example|synthetic-test-value-only/);
  const error = await uploadWorker(plan, dummy, async () => Response.json({
    success: false, errors: [{ code: 10000, message: dummy }],
  }, { status: 403 }));
  assert.equal(error.success, false);
  assert.deepEqual(error.errorCodes, [10000]);
  assert.doesNotMatch(JSON.stringify(error), /synthetic-test-value-only/);
});

test('network failure returns a sanitized failure receipt', async () => {
  const plan = await prepareUpload();
  const result = await uploadWorker(plan, 'synthetic-test-value-only', async () => {
    throw new Error('secret transport details');
  });
  assert.equal(result.state, 'network_failure');
  assert.doesNotMatch(JSON.stringify(result), /secret transport details|synthetic-test-value-only/);
});
