using System;
using System.Collections.Generic;
using Nectorial.SlideEscape;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nectorial.SlideEscape.Unity
{
    // 3D presentation of the same board the 2D GameBoardView draws. Cell (x, y) maps to (x, 0, -y),
    // so row 0 stays at the top of the screen under the tilted camera. Rules and state are untouched.
    internal sealed class Board3DView : IBoardView
    {
        public const string ProductName = "Few Moves 3D Preview";
        private const string LitShaderName = "Nectorial/Preview3DLit";
        private const string ShadowShaderName = "Nectorial/Preview3DContactShadow";
        private const string LitShaderResource = "Visuals3D/Preview3DLit";
        private const string ShadowShaderResource = "Visuals3D/Preview3DContactShadow";
        private const float GoalMarkerDiameter = 0.34f;
        // Presentation-only proportions: taller walls and puck read as solid pieces under the tilted camera.
        private const float WallHeight = 0.44f;
        private const float PuckHeightScale = 1.25f;
        // Shallow tray under the board, built from the shared FloorTile mesh scaled to the board bounds.
        private const float TrayRim = 0.35f;
        private const float TrayDepth = 0.18f;

        private static readonly Color Floor = new Color32(238, 231, 216, 255);
        private static readonly Color FloorAlt = new Color32(234, 227, 211, 255);
        private static readonly Color Wall = new Color32(52, 62, 68, 255);
        private static readonly Color Tray = new Color32(201, 192, 176, 255);
        private static readonly Color Target = new Color32(67, 116, 183, 255);
        private static readonly Color Goal = new Color32(102, 155, 211, 255);
        private static readonly Color HelperAmber = new Color32(208, 151, 67, 255);
        private static readonly Color HelperTeal = new Color32(55, 126, 120, 255);
        private static readonly Color Selection = new Color32(93, 137, 96, 255);
        private static readonly Color ShadowTint = new Color(0.16f, 0.2f, 0.22f, 0.24f);

        private readonly Preview3DMeshLibrary _library;
        private readonly List<Object> _ownedAssets = new List<Object>();
        private readonly Material _floor;
        private readonly Material _floorAlt;
        private readonly Material _wall;
        private readonly Material _tray;
        private readonly Material _target;
        private readonly Material _goal;
        private readonly Material _helperAmber;
        private readonly Material _helperTeal;
        private readonly Material _selection;
        private readonly Material _shadow;
        private readonly Mesh _quad;
        private readonly Mesh _cube;
        private readonly Transform _root;
        private readonly List<GameObject> _staticObjects = new List<GameObject>();
        private readonly List<GameObject> _dynamicObjects = new List<GameObject>();
        private readonly List<Transform> _pieceActors = new List<Transform>();
        private readonly List<GameObject> _selectionMarkers = new List<GameObject>();
        private RoomDefinition _staticRoom;
        private RoomDefinition _actorRoom;
        private Transform _movingActor;
        private Vector3 _moveStart;
        private Vector3 _moveEnd;
        private bool _transitionActive;
        private bool _disposed;

        public static bool IsPreviewProduct =>
            string.Equals(Application.productName, ProductName, StringComparison.Ordinal);

        public static bool TryCreate(out IBoardView view, out string error)
        {
            view = null;
            Shader lit = Resources.Load<Shader>(LitShaderResource);
            Shader shadow = Resources.Load<Shader>(ShadowShaderResource);
            if (lit == null || shadow == null || lit.name != LitShaderName || shadow.name != ShadowShaderName ||
                !lit.isSupported || !shadow.isSupported)
            {
                error = "preview3d_shader_unavailable";
                return false;
            }

            Preview3DMeshLibrary library;
            if (!Preview3DMeshLibrary.TryLoad(out library, out error)) return false;
            view = new Board3DView(library, lit, shadow);
            return true;
        }

        private Board3DView(Preview3DMeshLibrary library, Shader lit, Shader shadow)
        {
            _library = library;
            _floor = Own(CreateMaterial(lit, "Preview3D-Floor", Floor));
            _floorAlt = Own(CreateMaterial(lit, "Preview3D-FloorAlt", FloorAlt));
            _wall = Own(CreateMaterial(lit, "Preview3D-Wall", Wall, 0.05f, 0.06f));
            _tray = Own(CreateMaterial(lit, "Preview3D-Tray", Tray, 0.03f, 0f));
            _target = Own(CreateMaterial(lit, "Preview3D-Target", Target, 0.14f, 0.03f));
            _goal = Own(CreateMaterial(lit, "Preview3D-Goal", Goal));
            _helperAmber = Own(CreateMaterial(lit, "Preview3D-HelperAmber", HelperAmber));
            _helperTeal = Own(CreateMaterial(lit, "Preview3D-HelperTeal", HelperTeal));
            _selection = Own(CreateMaterial(lit, "Preview3D-Selection", Selection));
            _shadow = Own(CreateMaterial(shadow, "Preview3D-ContactShadow", ShadowTint));
            _quad = Own(CreateQuad());
            _cube = Own(CreateCube());
            _root = new GameObject("Board3D").transform;
            Application.quitting += Dispose;
        }

        public void Render(RoomDefinition room, GameState state, int selectedPieceIndex)
        {
            if (_disposed) return;
            if (!ReferenceEquals(_staticRoom, room))
            {
                Clear(_staticObjects);
                DrawStaticRoom(room);
                _staticRoom = room;
            }

            EnsurePieceActors(room);
            for (int index = 0; index < room.Pieces.Length; index++)
            {
                _pieceActors[index].localPosition = ToLocalPosition(state.Positions[index]);
                _selectionMarkers[index].SetActive(index == selectedPieceIndex);
            }

            _root.position = new Vector3(-(room.Width - 1) * 0.5f, 0f, (room.Height - 1) * 0.5f);
        }

        public bool BeginTransition(RoomDefinition room, GameState before, GameState after, int selectedPieceIndex)
        {
            if (_disposed || room == null || before == null || after == null ||
                selectedPieceIndex < 0 || selectedPieceIndex >= room.Pieces.Length) return false;
            Render(room, before, selectedPieceIndex);
            if (_pieceActors.Count != room.Pieces.Length) return false;

            _movingActor = _pieceActors[selectedPieceIndex];
            _moveStart = ToLocalPosition(before.Positions[selectedPieceIndex]);
            _moveEnd = ToLocalPosition(after.Positions[selectedPieceIndex]);
            _transitionActive = true;
            return true;
        }

        public void AdvanceTransition(float progress)
        {
            if (!_transitionActive || _movingActor == null) return;
            _movingActor.localPosition = Vector3.Lerp(_moveStart, _moveEnd, Mathf.Clamp01(progress));
        }

        public void CompleteTransition(RoomDefinition room, GameState state, int selectedPieceIndex)
        {
            _transitionActive = false;
            Render(room, state, selectedPieceIndex);
        }

        public void CancelTransition()
        {
            _transitionActive = false;
            _movingActor = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Application.quitting -= Dispose;
            ClearDynamicObjects();
            Clear(_staticObjects);
            if (_root != null) Object.Destroy(_root.gameObject);
            // Only assets this board created. Imported FBX meshes are shared and stay alive.
            for (int index = 0; index < _ownedAssets.Count; index++)
                if (_ownedAssets[index] != null) Object.Destroy(_ownedAssets[index]);
            _ownedAssets.Clear();
        }

        private void DrawStaticRoom(RoomDefinition room)
        {
            Preview3DMeshLibrary.Piece floorTile = _library["FloorTile"];
            GameObject tray = AddImported(_staticObjects, _root, "Tray", "FloorTile", _tray,
                new Vector3((room.Width - 1) * 0.5f, -TrayDepth, -(room.Height - 1) * 0.5f));
            tray.transform.localScale = Vector3.Scale(floorTile.Scale, new Vector3(
                (room.Width + TrayRim * 2f) / floorTile.Bounds.size.x,
                TrayDepth / floorTile.Bounds.size.y,
                (room.Height + TrayRim * 2f) / floorTile.Bounds.size.z));

            Preview3DMeshLibrary.Piece wallTile = _library["WallTile"];
            Vector3 wallScale = Vector3.Scale(wallTile.Scale, new Vector3(1f, WallHeight / wallTile.Bounds.size.y, 1f));
            for (int y = 0; y < room.Height; y++)
            {
                for (int x = 0; x < room.Width; x++)
                {
                    Vector3 cell = new Vector3(x, 0f, -y);
                    AddImported(_staticObjects, _root, "Floor", "FloorTile", (x + y) % 2 == 0 ? _floor : _floorAlt, cell);
                    if (room.Rows[y][x] == '#')
                        AddImported(_staticObjects, _root, "Wall", "WallTile", _wall, cell).transform.localScale = wallScale;
                }
            }

            // The authored GoalDisk is a generic 0.6 disc; the solo board keeps its small blue goal marker, so the
            // disc is narrowed at runtime (height untouched) and drawn with the blue goal material. It rests on
            // the floor top, above the 0.07 floor thickness.
            GridPoint goal = room.Goal;
            GameObject marker = AddImported(_staticObjects, _root, "Goal", "GoalDisk", _goal,
                new Vector3(goal.X, _library.FloorTop + 0.001f, -goal.Y));
            Preview3DMeshLibrary.Piece disk = _library["GoalDisk"];
            float narrow = GoalMarkerDiameter / Mathf.Max(disk.Bounds.size.x, disk.Bounds.size.z);
            marker.transform.localScale = Vector3.Scale(disk.Scale, new Vector3(narrow, 1f, narrow));
        }

        private void EnsurePieceActors(RoomDefinition room)
        {
            if (ReferenceEquals(_actorRoom, room) && _pieceActors.Count == room.Pieces.Length) return;
            ClearDynamicObjects();
            for (int index = 0; index < room.Pieces.Length; index++)
                _pieceActors.Add(CreatePieceActor(index, index == room.TargetPieceIndex));
            _actorRoom = room;
        }

        private Transform CreatePieceActor(int pieceIndex, bool targetPiece)
        {
            var actor = new GameObject("Piece " + pieceIndex);
            actor.transform.SetParent(_root, false);
            _dynamicObjects.Add(actor);
            float top = _library.FloorTop;

            AddQuad(actor.transform, "Contact Shadow", _shadow, new Vector3(0.03f, top + 0.003f, -0.03f), 0.86f);
            if (targetPiece)
            {
                GameObject puck = AddImported(null, actor.transform, "Target Piece", "TargetPuck", _target, new Vector3(0f, top, 0f));
                puck.transform.localScale = Vector3.Scale(_library["TargetPuck"].Scale, new Vector3(1f, PuckHeightScale, 1f));
            }
            else
            {
                bool amber = pieceIndex % 2 == 1;
                AddImported(null, actor.transform, "Helper Piece", amber ? "HelperDiamond" : "HelperSquare",
                    amber ? _helperAmber : _helperTeal, new Vector3(0f, top, 0f));
            }

            _selectionMarkers.Add(AddSelectionMarker(actor.transform, top));
            return actor.transform;
        }

        // Thin green frame just above the floor, matching the 2D selection outline.
        private GameObject AddSelectionMarker(Transform actor, float top)
        {
            var marker = new GameObject("Selected Marker");
            marker.transform.SetParent(actor, false);
            const float Span = 0.86f;
            const float Thickness = 0.05f;
            const float Height = 0.02f;
            float y = top + Height * 0.5f + 0.002f;
            float edge = (Span - Thickness) * 0.5f;
            AddBox(marker.transform, _selection, new Vector3(0f, y, edge), new Vector3(Span, Height, Thickness));
            AddBox(marker.transform, _selection, new Vector3(0f, y, -edge), new Vector3(Span, Height, Thickness));
            AddBox(marker.transform, _selection, new Vector3(-edge, y, 0f), new Vector3(Thickness, Height, Span));
            AddBox(marker.transform, _selection, new Vector3(edge, y, 0f), new Vector3(Thickness, Height, Span));
            return marker;
        }

        private GameObject AddImported(List<GameObject> owner, Transform parent, string name, string meshName, Material material, Vector3 position)
        {
            Preview3DMeshLibrary.Piece piece = _library[meshName];
            var visual = new GameObject(name);
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = position;
            visual.transform.localRotation = piece.Rotation;
            visual.transform.localScale = piece.Scale;
            visual.AddComponent<MeshFilter>().sharedMesh = piece.Mesh;
            AddRenderer(visual, material);
            if (owner != null) owner.Add(visual);
            return visual;
        }

        private void AddQuad(Transform parent, string name, Material material, Vector3 position, float size)
        {
            var visual = new GameObject(name);
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = position;
            visual.transform.localScale = new Vector3(size, 1f, size);
            visual.AddComponent<MeshFilter>().sharedMesh = _quad;
            AddRenderer(visual, material);
        }

        private void AddBox(Transform parent, Material material, Vector3 position, Vector3 size)
        {
            var visual = new GameObject("Marker Edge");
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = position;
            visual.transform.localScale = size;
            visual.AddComponent<MeshFilter>().sharedMesh = _cube;
            AddRenderer(visual, material);
        }

        private static void AddRenderer(GameObject visual, Material material)
        {
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private void ClearDynamicObjects()
        {
            _transitionActive = false;
            _movingActor = null;
            _actorRoom = null;
            _pieceActors.Clear();
            _selectionMarkers.Clear();
            Clear(_dynamicObjects);
        }

        private T Own<T>(T asset) where T : Object
        {
            _ownedAssets.Add(asset);
            return asset;
        }

        private static Vector3 ToLocalPosition(GridPoint point)
        {
            return new Vector3(point.X, 0f, -point.Y);
        }

        private static void Clear(List<GameObject> objects)
        {
            for (int index = 0; index < objects.Count; index++) Object.Destroy(objects[index]);
            objects.Clear();
        }

        private static Material CreateMaterial(Shader shader, string name, Color color, float specular = 0f, float topLift = 0f)
        {
            var material = new Material(shader) { name = name };
            material.SetColor("_Color", color);
            if (material.HasProperty("_Specular")) material.SetFloat("_Specular", specular);
            if (material.HasProperty("_TopLift")) material.SetFloat("_TopLift", topLift);
            return material;
        }

        // Unit quad in the XZ plane facing up, UV 0..1, used for contact shadows.
        private static Mesh CreateQuad()
        {
            var mesh = new Mesh { name = "Preview3D-Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f)
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        // Unit cube centered at the origin with flat per-face normals, used for the selection frame.
        private static Mesh CreateCube()
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            Vector3[] faces = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            for (int face = 0; face < faces.Length; face++)
            {
                Vector3 normal = faces[face];
                Vector3 tangent = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 bitangent = Vector3.Cross(normal, tangent);
                int start = vertices.Count;
                vertices.Add((normal - tangent - bitangent) * 0.5f);
                vertices.Add((normal - tangent + bitangent) * 0.5f);
                vertices.Add((normal + tangent + bitangent) * 0.5f);
                vertices.Add((normal + tangent - bitangent) * 0.5f);
                for (int corner = 0; corner < 4; corner++) normals.Add(normal);
                // Clockwise when seen from outside the face: Unity's front-face winding.
                triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            var mesh = new Mesh { name = "Preview3D-Cube" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
