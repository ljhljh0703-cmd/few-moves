# Few Moves 3D mesh kit — provisional

State: `provisional`  
Gate: `runtime_import_pending`

This folder contains six editable, low-poly Blender meshes and a visual reference for the unchanged first Slide room. The PNG is Blender art reference only; it is neither a playable Unity frame nor a store screenshot.

The reference render is deliberately treated as geometry verification: its board appears dark and diagonally framed. The Unity preview should present a brighter cream board and an aligned fixed camera so game **Up** maps to screen-up. The frozen FBX geometry does not prescribe that runtime framing or lighting.

## Files

- `few-moves-kit.blend` — source library and exact room-01 instance scene, with fixed orthographic camera and soft lighting.
- `few-moves-pieces.fbx` — frozen Unity handoff containing **only** `FloorTile`, `WallTile`, `TargetPuck`, `HelperSquare`, `HelperDiamond`, and `GoalDisk`.
- `mesh-manifest.json` — source room hash, per-mesh bounds and triangles, axes, palette, and frozen file hashes.
- `room-01-art-reference.png` — 1200×1200 Blender render of the exact first room.
- `generate_few_moves_3d.py` — reproducible authoring script for Blender 5.2.2.

The FBX was frozen after export. Its SHA-256 is `b90765d6559a4e8cfe7b4eac0d23d60325c3b56130ebc6e246611dea14256fce`. Do not regenerate over this file after Unity import; use a new versioned sibling for revisions. The `.blend` SHA-256 is `ab4f1b9c33874eebabf04a449018e239abc57b5d9467fe62953a4fb30778c103`.

## Geometry and placement

One room cell is one unit. Every reusable mesh has identity object transform in the source blend and origin at the center of its bottom face. Source geometry uses Blender Z-up. The FBX export uses `axis_forward='-Z'`, `axis_up='Y'`, and `bake_space_transform=True` for Unity Y-up import. The first room follows `Rows[y][x]`; room coordinates `(x,y)` are placed at Blender `(x,-y)` (corresponding Unity horizontal `(x,-y)` after axis conversion). Place floors at base height 0, walls at base height 0.07, goal at 0.077, and pieces at 0.078, then adjust only if Unity's imported bounds require it. Do not recenter mesh pivots in the importer.

The palette uses cream `#eee7d8`, charcoal `#293238`, blue `#4374b7`, gold `#d5a043`, and teal `#377e78`. Materials are matte and contain no external textures. The source library is 1,000 triangles total; each mesh is at most 284 triangles. Room 01 has 64 floor cells, 30 wall cells, one target, and one goal. Helper shapes are reusable assets but are absent from this room, matching its JSON.

## Verification

Blender 5.2.2 saved the `.blend`, exported FBX, and rendered the PNG. A fresh Blender FBX round-trip found all six named meshes, triangle counts matching the manifest, and ground-centered origins. The 8×8 Rows, target `(1,1)`, and goal `(4,3)` were asserted against `Assets/Nectorial/Resources/SlideRooms/room-01.json` during generation. Visual inspection of the PNG confirmed the goal and target are visible and the interior walls are present. Unity ModelImporter orientation, material interpretation, runtime framing, touch behavior, and performance remain for the Unity preview gate.

To reproduce from the repo, copy this folder to a new versioned sibling and run:

```sh
/opt/homebrew/bin/blender -b --factory-startup -t 4 --python-exit-code 12 --python generate_few_moves_3d.py -- --stage assets
/opt/homebrew/bin/blender -b few-moves-kit.blend -t 4 --python-exit-code 12 --python generate_few_moves_3d.py -- --stage render
```

The script reads the first room JSON from the same repo, fails if its locked topology or coordinates differ, and writes only alongside itself.
