---
state: provisional
gate: gate_pending
scope: raid-bootstrap-action-feedback
base_commit: 3ce40a0546138e3cc9908e30da4df43efc8dca37
---

# Raid action feedback evidence

Action-complete feedback reads the accepted `RaidDispatchResult` only.

- Final `Cleared` and `Failed` states take priority.
- `Armed` reports that all three tail fragments are ready and the snake body can be rammed.
- A shielded collision reports that the shield blocked the hit.
- Item events name Shield, Magnet, or Slow and state the actual effect in short text.
- Tail and magnet-tail events report fragment collection; a combined item and tail event keeps both facts.
- An ordinary accepted move says `이동했습니다`.

The editor probe replays actual v3 Shield, direct-tail, Magnet+magnet-tail, Slow, shielded, Armed, clear, and fail results through the private formatter.

Not run here: Unity Editor probe, build, browser, and device runtime verification.
