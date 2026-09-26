---
state: provisional
gate: gate_pending
scope: raid-01-v3 core-content-and-rule-replay
base_commit: 70811b6420ef63be719c7b0b4a5b8deab3fa2af7
---

# Raid scale v3 core evidence

- Active arena: `raid-01-v3`, 16x16, 28-cell central ring, 12-segment body.
- Arena fingerprint: `d28fe769aec762eeea0db782f2d5e2d08b93325c7070fedffa3573d663ca4bd3`.
- Measured shortest clear: `RLRDRDRURLU`, 11 actions.
- Measured clear with Shield, Magnet, and Slow all collected: `RLDRURLULDR`, 11 actions.
- Measured collision failure: `DRULRLUU`.
- Blocked `Up` from the initial state retains its fingerprint.
- `dotnet run --project tools/RaidChecks/RaidChecks.csproj --configuration Release` passed 11/11 checks after the changes.
- Legacy source-data byte hashes remained: v1 `7119766996e1f1403573200e0aad43aa3532af15280432121bea654e240151fc`; v2 `91c73e35a0dbea062e44b415b84a33398dd6e71d1e089252a1a64f3a4107fdaa`.

Not run here: Unity Editor serialization probe, WebGL build, browser/mobile play. Those need integration verification.
