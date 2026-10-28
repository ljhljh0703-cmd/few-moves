# auto_preview — Apps in Toss self-only preview upload

Builds and verifies the Few Moves Apps in Toss package from a reviewed native (Unity) base plus the current Toss UI
files. When enabled, it uploads the package to the Apps in Toss **test environment only**, using the official CLI.

## Scope

- **Automated on this path (GitHub-hosted runner + official CLI with a deploy key):**
  - native base verification;
  - Toss UI overlay;
  - official `ait build`;
  - `.ait` verification;
  - unlisted test upload with official `ait deploy`. It uploads the verified file unchanged; it never rebuilds or releases.
- **Not on this path:**
  - **Test push:** the CLI and deploy key have no route; it is done outside, for example in the console.
  - **Unity rebuilds:** hosted runners have no Unity license. Any change under `Assets/`, `Packages/` or `ProjectSettings/` stops the run with a rebuild request.
- **No code for:** review requests, release, rollback, terms, account, payment.

## Activation conditions (all required; none are set by default)

| Condition | Value |
|---|---|
| Repository | `ljhljh0703-cmd/few-moves`. Never pull requests or forks. |
| Branch | `codex/few-moves-preview`, for both push and `workflow_dispatch`. `workflow_dispatch` also needs this workflow on the default branch. |
| Variable `FEW_MOVES_NATIVE_BASE_TAG` | The release tag of the native base archive. It is verified against `native-base.lock.json`. |
| Variable `FEW_MOVES_PREVIEW_ENABLED` | Exactly `true`. Without it, the run builds and verifies only. |
| Environment `few-moves-preview` | Deployment branches restricted to `codex/few-moves-preview`; holds secret `FEW_MOVES_AIT_DEPLOY_KEY` (deploy key limited to this app). |
| Existing Verify checks | The newest run of exactly `.github/workflows/verify.yml` (event `push`, same commit, branch `codex/few-moves-preview`, this repository) must be `completed` / `success`. Read-only `actions: read`, checked before the deploy key is read. Runs still in progress are polled a bounded number of times; failed, missing or unreachable → no upload. |

## Guarantees and limits

**Guaranteed:**
- The upload is exactly the verified `.ait`: header, app binding, embedded deploymentId, contents, native files byte-identical to the base.
- The receipt must match the commit, which must still be the trusted branch HEAD, and that exact commit must have passed `verify.yml` on that branch (another workflow, an old SHA or another branch never counts).
- **Credentials and output:**
  - the key is never in argv, the CLI environment, or logs;
  - the CLI credentials file is created exclusively on the hosted runner, and only that file is removed afterwards;
  - HOME is never changed.
- `uploaded: true` only when the server reports `CREATED`.

**Duplicates and failures:**
- Only a server-confirmed `CREATED` upload counts as done; that content is skipped afterwards.
- An upload whose outcome is not confirmed (unknown, PREPARE or BUILDING) blocks every new upload until it is resolved with `status` (same deploymentId).
- A deploymentId already recorded is never uploaded again, including one that failed the server build. Failed content needs changed inputs.

**Limits:**
- The duplicate ledger is an Actions cache. If it is lost, exactly-once is not guaranteed; the server still rejects a reused deploymentId.
- A pre-approved `.ait` hash cannot be used in CI, because every `ait build` mints a new deploymentId.
- Real-device testing is never implied: `isTested` and "user tested" stay false.

## Commands

```
python3 -B tools/auto_preview/native_base.py verify --tar <archive>
python3 -B tools/auto_preview/pipeline.py build --native-tar <archive> --work <new dir> [--offline]
python3 -B tools/auto_preview/pipeline.py publish --dry-run --receipt <work>/build-receipt.json --ait <work>/ait-project/few-moves.ait \
  --project <work>/ait-project --ledger <ledger.json> --out <publish.json> --log <publish.log>
python3 -B tools/auto_preview/clean_checkout_check.py --native-tar <archive> --out <new dir>
python3 -m unittest tests.auto_preview.test_auto_preview
```

- `publish` without `--dry-run`, `status` and `verify-gate` run only on a GitHub-hosted runner.
- Exit codes: 0 ok/skip, 1 failure, 2 input/config, 3 native change (Unity rebuild), 4 refused (stale/duplicate/unknown/Verify not passed).
