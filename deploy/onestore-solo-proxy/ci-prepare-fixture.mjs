import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { mkdir, readFile, rename, unlink } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { Readable, Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import { FILE_HASHES, PACKAGE_MANIFEST_SHA256 } from './package-manifest.mjs';
import { validateDeploymentConfig } from './worker.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '../..');
const FIXTURE = join(ROOT, 'Builds/SubmissionPreparation-2026-10-27/packaging/onestore-solo-first-r1');
const CONFIG = join(HERE, 'wrangler.onestore-live.jsonc');

export async function prepareFixture(fetchImpl = fetch) {
  const config = JSON.parse(await readFile(CONFIG, 'utf8'));
  const upstream = validateDeploymentConfig(config.vars);
  assert.equal(upstream.origin, 'https://ljhljh0703-cmd.github.io');
  assert.equal(upstream.base, '/few-moves/releases/solo-raid-20261027-r1/');
  const files = [['release-manifest.json', PACKAGE_MANIFEST_SHA256], ...Object.entries(FILE_HASHES)];
  assert.equal(files.length, 17);
  for (const [relative, expected] of files) {
    assert.match(relative, /^[A-Za-z0-9._/-]+$/);
    assert.ok(!relative.split('/').includes('..'));
    const response = await fetchImpl(upstream.origin + upstream.base + relative, {
      method: 'GET', redirect: 'manual', signal: AbortSignal.timeout(90_000),
    });
    assert.equal(response.status, 200, `${relative} HTTP`);
    const type = (response.headers.get('content-type') ?? '').toLowerCase();
    if (relative.endsWith('.html')) assert.ok(type.startsWith('text/html'), `${relative} MIME`);
    if (relative.endsWith('.js')) assert.ok(type.includes('javascript'), `${relative} MIME`);
    if (relative.endsWith('.wasm')) assert.ok(type.startsWith('application/wasm'), `${relative} MIME`);
    assert.ok(response.body, `${relative} body`);
    const destination = join(FIXTURE, relative);
    const temporary = `${destination}.partial`;
    await mkdir(dirname(destination), { recursive: true });
    const hash = createHash('sha256');
    const tap = new Transform({ transform(chunk, _, next) { hash.update(chunk); next(null, chunk); } });
    try {
      await pipeline(Readable.fromWeb(response.body), tap, createWriteStream(temporary, { flags: 'wx' }));
      assert.equal(hash.digest('hex'), expected, `${relative} SHA-256`);
      await rename(temporary, destination);
    } catch (error) {
      await unlink(temporary).catch(() => {});
      throw error;
    }
  }
  return { state: 'upstream_verified_fixture_ready', files: 17, failed: 0,
    manifestSha256: PACKAGE_MANIFEST_SHA256, fixtureRoot: 'Builds/SubmissionPreparation-2026-10-27/packaging/onestore-solo-first-r1' };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 2) throw new Error('usage: node ci-prepare-fixture.mjs');
  try { process.stdout.write(JSON.stringify(await prepareFixture()) + '\n'); }
  catch (error) {
    process.stderr.write(JSON.stringify({ state: 'upstream_verify_failed', reason: String(error.message).slice(0, 220) }) + '\n');
    process.exitCode = 1;
  }
}
