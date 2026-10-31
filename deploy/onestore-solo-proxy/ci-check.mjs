import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FILE_HASHES, PACKAGE_MANIFEST_SHA256, PUBLIC_PATHS } from './package-manifest.mjs';
import { prepareUpload } from './deploy-worker.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '../..');
const BRANCH = 'codex/few-moves-onestore-deploy';
const BASE = '050e2747a24cf995945f26598f05efa77ee7458a';
const ALLOWLIST = new Set([
  '.github/workflows/few-moves-onestore.yml',
  ...[
    'worker.mjs', 'package-manifest.mjs', 'verify-upstream.mjs', 'test-worker.mjs',
    'worker.bundle.r1.js', 'wrangler.onestore-live.jsonc', 'package.json', 'package-lock.json',
    'deploy-worker.mjs', 'deploy-worker.test.mjs', 'verify-live.mjs',
    'ci-prepare-fixture.mjs', 'ci-verify-run.mjs', 'ci-verify-run.test.mjs',
    'ci-check.mjs', 'ci-check.test.mjs', 'CI-README.md',
  ].map(name => `deploy/onestore-solo-proxy/${name}`),
]);

export async function checkFiles() {
  const plan = await prepareUpload();
  assert.equal(PACKAGE_MANIFEST_SHA256, 'a925b9dbd3c6996e202aaac570fdc7dd5e368bebe19a3471f7583c880b423daf');
  assert.equal(Object.keys(FILE_HASHES).length, 16);
  assert.equal(PUBLIC_PATHS.length, 14);
  const packageJson = JSON.parse(await readFile(resolve(HERE, 'package.json'), 'utf8'));
  const lock = JSON.parse(await readFile(resolve(HERE, 'package-lock.json'), 'utf8'));
  assert.equal(packageJson.private, true);
  assert.deepEqual(packageJson.dependencies, { wrangler: '4.155.0' });
  assert.equal(lock.packages[''].dependencies.wrangler, '4.155.0');
  assert.equal(lock.packages['node_modules/wrangler'].version, '4.155.0');
  const resolved = Object.values(lock.packages).map(pkg => pkg.resolved).filter(Boolean);
  assert.ok(resolved.length > 0 && resolved.every(url => url.startsWith('https://registry.npmjs.org/')));
  const verify = await readFile(resolve(ROOT, '.github/workflows/verify.yml'));
  assert.equal(createHash('sha256').update(verify).digest('hex'), '27fa391ec6c40947dc2631266491ce1669ab88a80b607723f0053b7b4a26860c');
  assert.equal(existsSync(resolve(ROOT, '.github/workflows/few-moves-preview.yml')), false, 'Toss workflow must remain outside this branch');
  assert.equal(existsSync(resolve(ROOT, 'tools/auto_preview')), false, 'Toss preview files must remain outside this branch');
  return { state: 'local_contract_pass', worker: plan.publicPlan.worker,
    bundleSha256: plan.publicPlan.bundleSha256, fixedInputCount: 4,
    upstreamFileCount: 17, publicRouteFileCount: 14, registryEntries: resolved.length };
}

export function checkPushScope(env = process.env, { root = ROOT, base = BASE } = {}) {
  assert.equal(env.GITHUB_REPOSITORY, 'ljhljh0703-cmd/few-moves');
  assert.equal(env.GITHUB_REF, `refs/heads/${BRANCH}`);
  assert.equal(env.GITHUB_EVENT_NAME, 'push');
  const head = env.GITHUB_SHA;
  assert.match(head, /^[a-f0-9]{40}$/);
  const before = env.GITHUB_EVENT_BEFORE;
  const checkout = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
  assert.equal(checkout, head, 'checkout SHA mismatch');
  execFileSync('git', ['merge-base', '--is-ancestor', base, head], { cwd: root });
  if (before && /^[a-f0-9]{40}$/.test(before) && !/^0+$/.test(before)) {
    execFileSync('git', ['merge-base', '--is-ancestor', before, head], { cwd: root });
  }
  const lines = execFileSync('git', ['diff', '--name-status', '--no-renames', base, head], { cwd: root, encoding: 'utf8' }).trim().split('\n').filter(Boolean);
  assert.ok(lines.length > 0, 'empty deployment commit range');
  const paths = [];
  for (const line of lines) {
    const [status, path] = line.split('\t');
    assert.ok(status === 'A' || status === 'M', `unexpected change kind ${status}`);
    assert.ok(ALLOWLIST.has(path), `path outside ONE ownership: ${path}`);
    paths.push(path);
  }
  return { state: 'push_scope_pass', base, head, changedPaths: paths.sort() };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 2) throw new Error('usage: node ci-check.mjs');
  try {
    const files = await checkFiles();
    const scope = process.env.GITHUB_ACTIONS === 'true' ? checkPushScope() : { state: 'local_no_push_scope' };
    process.stdout.write(JSON.stringify({ ...files, scope }) + '\n');
  } catch (error) {
    process.stderr.write(JSON.stringify({ state: 'ci_contract_failed', reason: String(error.message).slice(0, 220) }) + '\n');
    process.exitCode = 1;
  }
}
