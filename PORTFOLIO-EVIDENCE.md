---
state: provisional
gate: gate_pending
scope: Few Moves portfolio repository preparation
updated: 2026-09-06
---

# Few Moves — Portfolio Evidence Packet

## Claim Ledger

| ID | Claim | Pointer | Evidence status | Verification | Attribution | Boundary |
|---|---|---|---|---|---|---|
| FEW-01 | The source baseline uses Unity 6000.4.3f1 and a pinned .NET 10.0.203 toolchain. | `ProjectSettings/ProjectVersion.txt`, `global.json` | source-backed | current | repository owner committed the baseline | Versioning does not prove a device build. |
| FEW-02 | The repository contains a C# puzzle core, solver, save codec, runtime adapters, and 12 production sliding-puzzle rooms. | `Assets/Nectorial/Core/`, `SlideCore/`, `Runtime/`, `Resources/SlideRooms/` | source-backed | current | individual code authorship unknown | Do not infer a completed public service. |
| FEW-03 | Current checks passed: 12 artifact-manifest tests, 2 browser contract suites, 18 core checks, 10 sliding-puzzle checks, and 7 Toss policy checks. | commands in `README.md`; `.github/workflows/verify.yml` | measured | current | test execution by this preparation pass | Static and source checks are not Unity runtime proof. |
| FEW-04 | The repository has a GitHub Actions source-verification workflow and its public repository returned HTTP 200 on 2026-09-06. | `.github/workflows/verify.yml`, `https://github.com/ljhljh0703-cmd/few-moves` | measured | current | repository owner | Current remote CI status was not inspected in this pass. |

## Required Before External Portfolio Credit

1. Confirm the author's direct role for the puzzle rules, room design, code, verification, and release decision.
2. Capture a Unity runtime or device receipt if the portfolio needs an interaction or deployment claim.
3. Verify any public URL live immediately before submission.

## Allowed Framing Now

- A source-verified Unity puzzle project with deterministic rule, solver, save, and WebGL persistence contracts.
- A repository that makes core and platform policy behavior repeatable through named checks.
