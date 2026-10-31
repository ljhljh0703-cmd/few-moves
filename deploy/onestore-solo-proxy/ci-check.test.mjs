import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { checkPushScope } from './ci-check.mjs';

test('cumulative base-to-head gate rejects a protected-file change hidden by a later allowed push', async () => {
  const root = await mkdtemp(join(tmpdir(), 'few-moves-one-gate-'));
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
  async function put(path, value) {
    const target = join(root, path);
    await mkdir(dirname(target), { recursive: true });
    await writeFile(target, value);
  }
  try {
    git('init', '-q');
    git('config', 'user.name', 'Gate Fixture');
    git('config', 'user.email', 'fixture@example.invalid');
    await put('.github/workflows/verify.yml', 'baseline\n');
    git('add', '.github/workflows/verify.yml');
    git('commit', '-qm', 'base');
    const base = git('rev-parse', 'HEAD');
    await put('deploy/onestore-solo-proxy/worker.mjs', 'allowed first\n');
    git('add', 'deploy/onestore-solo-proxy/worker.mjs');
    git('commit', '-qm', 'allowed first');
    const first = git('rev-parse', 'HEAD');
    const env = sha => ({ GITHUB_REPOSITORY: 'ljhljh0703-cmd/few-moves',
      GITHUB_REF: 'refs/heads/codex/few-moves-onestore-deploy', GITHUB_EVENT_NAME: 'push',
      GITHUB_SHA: sha, GITHUB_EVENT_BEFORE: base });
    assert.equal(checkPushScope(env(first), { root, base }).state, 'push_scope_pass');
    await put('.github/workflows/verify.yml', 'tampered\n');
    git('add', '.github/workflows/verify.yml');
    git('commit', '-qm', 'protected tamper');
    const tamper = git('rev-parse', 'HEAD');
    await put('deploy/onestore-solo-proxy/ci-check.mjs', 'allowed second\n');
    git('add', 'deploy/onestore-solo-proxy/ci-check.mjs');
    git('commit', '-qm', 'allowed second');
    const second = git('rev-parse', 'HEAD');
    assert.throws(() => checkPushScope({ ...env(second), GITHUB_EVENT_BEFORE: tamper }, { root, base }),
      /path outside ONE ownership: \.github\/workflows\/verify\.yml/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
