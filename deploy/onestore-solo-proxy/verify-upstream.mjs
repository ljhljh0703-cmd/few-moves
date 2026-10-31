import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { FILE_HASHES, PACKAGE_MANIFEST_SHA256 } from './package-manifest.mjs';
import { validateDeploymentConfig } from './worker.mjs';

async function sha256Stream(body) {
  if (!body) throw new Error('upstream returned no body');
  const hash = createHash('sha256');
  for await (const chunk of body) hash.update(chunk);
  return hash.digest('hex');
}

// This is a required pre-deploy check after an approved, immutable Pages version exists.
// It sends no cookie, Authorization header, query, or POST body.
export async function verifyRelease(vars, fetchImpl = fetch) {
  const config = validateDeploymentConfig(vars);
  const expected = [['release-manifest.json', PACKAGE_MANIFEST_SHA256], ...Object.entries(FILE_HASHES)];
  for (const [file, digest] of expected) {
    const url = config.origin + config.base + file;
    const response = await fetchImpl(url, { method: 'GET', redirect: 'manual' });
    if (response.status !== 200) throw new Error('upstream file unavailable or redirected: ' + file + ' status ' + response.status);
    const type = (response.headers.get('content-type') || '').toLowerCase();
    if (file.endsWith('.html') && !type.startsWith('text/html')) throw new Error('upstream HTML MIME mismatch: ' + file);
    if (file.endsWith('.wasm') && !type.startsWith('application/wasm')) throw new Error('upstream WASM MIME mismatch: ' + file);
    if (file.endsWith('.js') && !type.includes('javascript')) throw new Error('upstream JavaScript MIME mismatch: ' + file);
    const actual = await sha256Stream(response.body);
    if (actual !== digest) throw new Error('upstream hash mismatch: ' + file);
  }
  return { verifiedFiles: expected.length, packageManifestSha256: PACKAGE_MANIFEST_SHA256 };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const configPath = resolve(process.argv[2] || 'wrangler.jsonc');
  const config = JSON.parse(await readFile(configPath, 'utf8'));
  const result = await verifyRelease(config.vars);
  process.stdout.write('PASS upstream exact candidate: ' + result.verifiedFiles + ' files, manifest ' + result.packageManifestSha256 + '\n');
}
