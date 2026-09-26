# Few Moves co-op server — local run contract

This service needs absolute operator paths:

- `FEW_MOVES_WEB_ROOT`: an uncompressed public WebGL root with `index.html`, `solo/index.html`, `coop/index.html`, and `raid/index.html` plus their build assets.
- `FEW_MOVES_STATE_ROOT`: an empty/private directory outside the public root. The service creates owner-only `rooms.v1.json` there.

The catalog is strict: `coop-c1`, `coop-c2`, and `coop-c3` must all exist and validate. `FEW_MOVES_ROOM_PATH` selects the C1 file; `FEW_MOVES_ROOM_CATALOG_ROOT` selects the directory containing all three. When omitted, both default to the copied binary resources.

Run locally on loopback:

```text
FEW_MOVES_WEB_ROOT=/absolute/public-webgl \
FEW_MOVES_STATE_ROOT=/absolute/private-room-state \
FEW_MOVES_ROOM_PATH=/absolute/source/Assets/Nectorial/Resources/CoopRooms/coop-c1.json \
FEW_MOVES_ROOM_CATALOG_ROOT=/absolute/source/Assets/Nectorial/Resources/CoopRooms \
FEW_MOVES_LISTEN_URL=http://127.0.0.1:5088 \
dotnet run --project servers/FewMoves.Coop/FewMoves.Coop.csproj --configuration Release
```

`POST /api/coop/v1/rooms/{roomId}/record` is Bearer-authenticated and only derives a completed room's verified replay capsule. A capsule is portable replay input, not author identity or an official leaderboard record; it excludes bearer tokens, room UUIDs, and request IDs, and compares only exact mode/definition/rules/content/fingerprint identities.

Build a local container from the repository root after the public WebGL build exists:

```text
docker build -f servers/FewMoves.Coop/Dockerfile -t few-moves-coop-server .
docker run --rm -p 127.0.0.1:8080:8080 \
  -e FEW_MOVES_WEB_ROOT=/srv/public \
  -e FEW_MOVES_STATE_ROOT=/srv/private-state \
  -e FEW_MOVES_LISTEN_URL=http://+:8080 \
  -v /absolute/public-webgl:/srv/public:ro \
  -v /absolute/private-room-state:/srv/private-state \
  few-moves-coop-server
```

Container build/run was not run for this change. Keep private state, raw logs, bearer tokens, and source outside the public mount. Any tunnel or public exposure is a separate root-owned test step.
