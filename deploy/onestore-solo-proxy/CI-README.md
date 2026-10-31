# Few Moves ONE Worker: fixed release deployment

This directory owns only the `few-moves-onestore` proxy. It has no Unity build, Toss upload, game content, static hosting, account creation, or billing action. The reviewed Pages release remains fixed at `https://ljhljh0703-cmd.github.io/few-moves/releases/solo-raid-20261027-r1/`.

## Fixed deployment input

- Worker source `worker.mjs`: SHA-256 `2f12069ec6c72dccde2a5ad9cc62ff877085cd1f73f2bf89a4f77430a0a63c63`.
- Package manifest `package-manifest.mjs`: SHA-256 `b9a2d64b0c9674663ff324aa16f4604db798943dd0a2dfed64f6c8b4e28c291a`.
- Reviewed single-module upload `worker.bundle.r1.js`: 8,630 bytes, SHA-256 `ae3309f679adce5504309784750add1536ea5020bf60aae6f492a37d86861478`.
- Live configuration `wrangler.onestore-live.jsonc`: exact account, exact existing Worker, 2026-10-31 compatibility date, `workers.dev` enabled, three fixed public bindings, Logs/Traces/Issues disabled. `main` points at the reviewed bundle for byte-preserving `--no-bundle` checks.
- Wrangler 4.155.0 is locked in `package.json`/`package-lock.json`. CI copies these two files into its temporary tools directory and installs there with `npm ci --ignore-scripts --no-audit --no-fund --omit=dev --registry=https://registry.npmjs.org`. Installing `node_modules` in the Worker source directory made Wrangler attach unrelated optional modules even with `--no-bundle`; the isolated tools directory keeps the dry-run at the reviewed 8,630-byte module and the job checks exactly two output files. The CLI is only needed for this package dry-run. The deployment command uses the official one-Worker Script Upload API because a Worker-scoped token cannot satisfy Wrangler's additional account-wide metadata requests.

## One-Worker API boundary

`deploy-worker.mjs` validates the approved source, bundle, config, account, Worker name, bindings and privacy settings **before** reading `CLOUDFLARE_API_TOKEN`. With `--dry-run` it sends nothing. With the token available it sends exactly one multipart `PUT` to `/accounts/957eada44e00cdb84fec322133a89349/workers/scripts/few-moves-onestore`. The metadata names the single ES module, includes the three plain-text bindings, sets compatibility date 2026-10-31 and disables observability/logs/traces/issues. It does not call the account-wide bindings, routes or domains endpoints. HTTP redirects fail, and its JSON receipt contains only allowed status, IDs and numeric error codes. No raw API response or token is logged.

Official Cloudflare sources: [Upload Worker Module API](https://developers.cloudflare.com/api/resources/workers/subresources/scripts/methods/update/) (lists Individual Workers Editor permission), [multipart metadata](https://developers.cloudflare.com/workers/configuration/multipart-upload-metadata/).

## CI order and ownership

The separate `.github/workflows/few-moves-onestore.yml` is push-triggered only on `codex/few-moves-onestore-deploy`; its deployment job is OFF while repository variable `FEW_MOVES_ONE_DEPLOY_ENABLED` is absent or `false`. It does not use `workflow_dispatch`, which requires the workflow on the older default branch. The environment `few-moves-onestore` must permit only this branch and contain `FEW_MOVES_CF_DEPLOY_TOKEN` restricted to this one Worker.

1. The secret-free `verify` job checks the fixed hashes and configuration. Its path gate compares **pinned base 050e2747... through HEAD**, so an earlier protected-file change cannot be hidden by a later allowed push. It checks the original `verify.yml` bytes and the absence of Toss preview files in this branch.
2. It waits at most 15 minutes for the existing Verify workflow's successful push run for the same repository, branch and commit. The Verify job itself has a 15-minute timeout; a missing, failed or late result fails closed. No Cloudflare token is available in this job.
3. It downloads the 17 fixed Pages files to an ignored CI fixture while checking all SHA-256 values and critical MIME, then runs the original seven proxy tests plus the direct-upload and cumulative-gate counterexamples. It runs a pinned Wrangler `--dry-run --no-bundle` and requires the exact reviewed 8,630-byte bundle SHA.
4. Only after `verify` succeeds and `FEW_MOVES_ONE_DEPLOY_ENABLED` is exactly `true` can the protected environment release the one-Worker token. The deploy step uses the fixed one-Worker API; then the live URL is checked for 14 public file hashes, HTML GET/POST, 307 aliases, blocked paths and headers. Allowed upload/live JSON receipts are printed as selected fields and stored as a pinned GitHub Actions artifact for seven days; no raw response or token is included.

This branch intentionally does not contain `.github/workflows/few-moves-preview.yml`, `tools/auto_preview`, Unity outputs, `Builds/`, `node_modules`, credentials or logs. The `gh-pages` static release and `main` remain separate. A successful API response is an accepted upload; live HTTP verification and ONE in-app/device review are separate evidence.
