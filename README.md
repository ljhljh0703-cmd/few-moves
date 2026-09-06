# Few Moves

Few Moves is a Unity 6 sliding-puzzle source project for Apps in Toss. Its public source keeps the puzzle rule engine, solver, save format, WebGL persistence bridge, and mobile control contracts in reviewable files.

## What is in the Repository

- A C# rule core and solver under `Assets/Nectorial/Core/`.
- A sliding-puzzle core and 12 production rooms under `Assets/Nectorial/SlideCore/` and `Assets/Nectorial/Resources/SlideRooms/`.
- Runtime input, save, and WebGL persistence adapters under `Assets/Nectorial/Runtime/` and `Assets/Plugins/WebGL/`.
- Source-level browser, artifact-manifest, .NET core, sliding-puzzle, and Toss policy checks.

## Verification

Run from the repository root:

    python3 -m unittest tools.tests.test_artifact_manifest
    node tests/mobile_ui_contract.test.cjs
    node tests/webgl_persistence_contract.test.cjs
    dotnet run --project tools/CoreChecks/CoreChecks.csproj --configuration Release
    dotnet run --project tools/SlideChecks/SlideChecks.csproj --configuration Release
    dotnet run --project tools/TossPlatformChecks/TossPlatformChecks.csproj --configuration Release

The 2026-09-06 source check run passed 12 artifact-manifest tests, 2 browser contract suites, 18 core checks, 10 sliding-puzzle checks, and 7 Toss policy checks. These results are source and contract evidence; they are not a Unity runtime, device, store, or live-release claim.

## Portfolio Boundary

The public source was committed by the repository owner, but individual code authorship and delegation boundaries require confirmation before an external portfolio assigns personal implementation credit. See [PORTFOLIO-EVIDENCE.md](PORTFOLIO-EVIDENCE.md) for the claim ledger.
