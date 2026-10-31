import test from 'node:test';
import assert from 'node:assert/strict';
import { judgeVerifyRuns, waitForVerify } from './ci-verify-run.mjs';

const SHA = 'a'.repeat(40);
const exact = (overrides = {}) => ({
  id: 10, path: '.github/workflows/verify.yml', head_sha: SHA,
  head_branch: 'codex/few-moves-onestore-deploy', event: 'push',
  repository: { full_name: 'ljhljh0703-cmd/few-moves' },
  head_repository: { full_name: 'ljhljh0703-cmd/few-moves' },
  status: 'completed', conclusion: 'success', ...overrides,
});

test('same-SHA Verify gate ignores wrong repository, branch and old success', () => {
  const payload = { workflow_runs: [
    exact({ id: 13, head_sha: 'b'.repeat(40) }),
    exact({ id: 12, head_repository: { full_name: 'fork/few-moves' } }),
    exact({ id: 11, head_branch: 'main' }),
    exact({ id: 10, conclusion: 'failure' }),
  ] };
  assert.deepEqual(judgeVerifyRuns(payload, SHA), { state: 'failed', runId: 10, conclusion: 'failure' });
  assert.equal(judgeVerifyRuns({ workflow_runs: [exact({ head_sha: 'b'.repeat(40) })] }, SHA).state, 'waiting');
});

test('same-SHA Verify gate accepts only latest successful exact push run', async () => {
  const token = 'synthetic-github-token';
  let calls = 0;
  const fetcher = async (url, init) => {
    calls++;
    assert.match(url, /^https:\/\/api\.github\.com\/repos\/ljhljh0703-cmd\/few-moves\/actions\/workflows\/verify\.yml\/runs\?/);
    assert.equal(init.headers.Authorization, `Bearer ${token}`);
    assert.equal(init.redirect, 'manual');
    return Response.json({ workflow_runs: [exact({ id: 18, conclusion: 'success' }), exact({ id: 17, conclusion: 'failure' })] });
  };
  const result = await waitForVerify({ sha: SHA, token, fetchImpl: fetcher, attempts: 1 });
  assert.equal(calls, 1);
  assert.deepEqual(result, { state: 'passed', runId: 18, sha: SHA, attempt: 1 });
  assert.doesNotMatch(JSON.stringify(result), /synthetic-github-token/);
});
