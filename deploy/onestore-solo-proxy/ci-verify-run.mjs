import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = 'ljhljh0703-cmd/few-moves';
const BRANCH = 'codex/few-moves-onestore-deploy';
const WORKFLOW = '.github/workflows/verify.yml';
const API = `https://api.github.com/repos/${REPO}/actions/workflows/verify.yml/runs?branch=${encodeURIComponent(BRANCH)}&event=push&per_page=30`;

export function judgeVerifyRuns(payload, sha) {
  const runs = Array.isArray(payload?.workflow_runs) ? payload.workflow_runs : [];
  const exact = runs.filter(run => run?.head_sha === sha && run?.head_branch === BRANCH
    && run?.event === 'push' && run?.repository?.full_name === REPO
    && run?.head_repository?.full_name === REPO
    && (run?.path === WORKFLOW || (typeof run?.path === 'string' && run.path.startsWith(`${WORKFLOW}@`))));
  exact.sort((a, b) => Number(b.id) - Number(a.id));
  if (!exact.length) return { state: 'waiting' };
  const run = exact[0];
  if (run.status !== 'completed') return { state: 'waiting', runId: run.id };
  if (run.conclusion !== 'success') return { state: 'failed', runId: run.id, conclusion: run.conclusion };
  return { state: 'passed', runId: run.id, sha };
}

export async function waitForVerify({ sha, token, fetchImpl = fetch, attempts = 90, delayMs = 10_000 }) {
  assert.match(sha, /^[a-f0-9]{40}$/);
  assert.ok(typeof token === 'string' && token.length > 10);
  for (let attempt = 1; attempt <= attempts; attempt++) {
    const response = await fetchImpl(API, {
      method: 'GET', redirect: 'manual',
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' },
      signal: AbortSignal.timeout(20_000),
    });
    assert.equal(response.status, 200, 'Verify run lookup unavailable');
    const result = judgeVerifyRuns(await response.json(), sha);
    if (result.state === 'passed') return { ...result, attempt };
    if (result.state === 'failed') throw new Error(`same-SHA Verify run failed: ${result.conclusion}`);
    if (attempt < attempts) await new Promise(done => setTimeout(done, delayMs));
  }
  throw new Error('same-SHA Verify run did not complete successfully in time');
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 2) throw new Error('usage: node ci-verify-run.mjs');
  assert.equal(process.env.GITHUB_REPOSITORY, REPO);
  assert.equal(process.env.GITHUB_REF, `refs/heads/${BRANCH}`);
  assert.equal(process.env.GITHUB_EVENT_NAME, 'push');
  const sha = process.env.GITHUB_SHA;
  const token = process.env.GITHUB_TOKEN;
  try {
    const result = await waitForVerify({ sha, token });
    process.stdout.write(JSON.stringify({ state: 'same_sha_verify_pass', runId: result.runId, sha, attempts: result.attempt }) + '\n');
  } catch (error) {
    process.stderr.write(JSON.stringify({ state: 'same_sha_verify_blocked', reason: String(error.message).slice(0, 180) }) + '\n');
    process.exitCode = 1;
  }
}
