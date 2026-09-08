# Few Moves co-op server — host-neutral run input

This service has no provider SDK, account integration, or deployment command. It needs two absolute directories supplied by the operator:

- `FEW_MOVES_WEB_ROOT`: the finished, uncompressed Unity WebGL output containing `index.html` and its assets.
- `FEW_MOVES_STATE_ROOT`: an empty/private directory outside that public root. For a local checkout, use an ignored path such as `Builds/FewMoves.Coop/private-state`; the service creates `rooms.v1.json` there with Unix owner-only permissions.

Run locally with a loopback listener:

```text
FEW_MOVES_WEB_ROOT=/absolute/public-webgl \
FEW_MOVES_STATE_ROOT=/absolute/private-room-state \
FEW_MOVES_LISTEN_URL=http://127.0.0.1:5088 \
dotnet run --project servers/FewMoves.Coop/FewMoves.Coop.csproj --configuration Release
```

`FEW_MOVES_SERVER_BUILD_ID` is optional and only labels `/healthz`; it must not contain a token, path, or credential. The server’s default C1 resource is copied with the binary. Override it only with a reviewed `FEW_MOVES_ROOM_PATH` that has the same intended online content contract.

Build a container from the repository root only after the public Unity build is available:

```text
docker build -f servers/FewMoves.Coop/Dockerfile -t few-moves-coop-server .
docker run --rm -p 8080:8080 \
  -e FEW_MOVES_WEB_ROOT=/srv/public \
  -e FEW_MOVES_STATE_ROOT=/srv/private-state \
  -v /absolute/public-webgl:/srv/public:ro \
  -v /absolute/private-room-state:/srv/private-state \
  few-moves-coop-server
```

The static root is the named public mount only. Do not place private state, raw logs, bearer tokens, or build source under it. A QuickTunnel or other host exposure is a separate root-owned test step; this document does not publish a service.
