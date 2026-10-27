using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Nectorial.SlideEscape.Unity
{
    // Shared meshes from the imported Blender FBX. The library only references imported assets;
    // it never creates or destroys meshes, so boards may be disposed freely.
    internal sealed class Preview3DMeshLibrary
    {
        public const string ModelResource = "Visuals3D/few-moves-pieces";
        public static readonly string[] RequiredNames =
            { "FloorTile", "WallTile", "TargetPuck", "HelperSquare", "HelperDiamond", "GoalDisk" };

        internal struct Piece
        {
            public Mesh Mesh;
            public Quaternion Rotation;
            public Vector3 Scale;
            public Bounds Bounds;
        }

        private readonly Dictionary<string, Piece> _pieces;

        private Preview3DMeshLibrary(Dictionary<string, Piece> pieces)
        {
            _pieces = pieces;
        }

        public Piece this[string name] => _pieces[name];

        // Top surface of the floor tile; pieces, goal and contact shadows rest on it.
        public float FloorTop => _pieces["FloorTile"].Bounds.max.y;

        public static bool TryLoad(out Preview3DMeshLibrary library, out string error)
        {
            library = null;
            GameObject model = Resources.Load<GameObject>(ModelResource);
            if (model == null)
            {
                error = "preview3d_asset_missing:few-moves-pieces";
                return false;
            }

            var pieces = new Dictionary<string, Piece>();
            MeshFilter[] filters = model.GetComponentsInChildren<MeshFilter>(true);
            for (int index = 0; index < filters.Length; index++)
            {
                MeshFilter filter = filters[index];
                string name = filter.gameObject.name;
                if (filter.sharedMesh == null) continue;
                if (pieces.ContainsKey(name))
                {
                    error = "preview3d_mesh_duplicate:" + name;
                    return false;
                }

                // Keep the full accumulated rotation and scale from the imported root down to this node, so any FBX
                // unit scale or axis conversion Unity places on the root or node survives. Only the Blender layout
                // position is dropped: each mesh origin is its ground center and the board places it per cell.
                // No extra rotation is added here; the HelperDiamond orientation is baked in the mesh.
                Transform source = filter.transform;
                Quaternion rotation = source.rotation;
                Vector3 scale = source.lossyScale;
                pieces.Add(name, new Piece
                {
                    Mesh = filter.sharedMesh,
                    Rotation = rotation,
                    Scale = scale,
                    Bounds = TransformBounds(filter.sharedMesh.bounds, rotation, scale)
                });
            }

            for (int index = 0; index < RequiredNames.Length; index++)
            {
                if (!pieces.ContainsKey(RequiredNames[index]))
                {
                    error = "preview3d_mesh_missing:" + RequiredNames[index];
                    return false;
                }
            }

            if (!CheckContract(pieces, out error)) return false;
            library = new Preview3DMeshLibrary(pieces);
            return true;
        }

        public static string Describe(Piece piece)
        {
            Bounds b = piece.Bounds;
            return string.Format(CultureInfo.InvariantCulture, "size=({0:0.###},{1:0.###},{2:0.###}) min_y={3:0.###} center_xz=({4:0.###},{5:0.###})",
                b.size.x, b.size.y, b.size.z, b.min.y, b.center.x, b.center.z);
        }

        // Loose checks against the Blender -> Unity asset contract (one cell = 1 unit, Y up, origin at ground center).
        // A wrong axis mapping or unapplied scale shows up here instead of as a silently wrong board.
        private static bool CheckContract(Dictionary<string, Piece> pieces, out string error)
        {
            if (!Within(pieces, "FloorTile", 0.85f, 1.05f, 0.005f, 0.2f, out error)) return false;
            if (!Within(pieces, "WallTile", 0.85f, 1.0f, 0.25f, 0.45f, out error)) return false;
            if (!Within(pieces, "TargetPuck", 0.6f, 0.8f, 0.12f, 0.3f, out error)) return false;
            if (!Within(pieces, "HelperSquare", 0.5f, 0.8f, 0.12f, 0.3f, out error)) return false;
            if (!Within(pieces, "HelperDiamond", 0.5f, 0.95f, 0.12f, 0.3f, out error)) return false;
            if (!Within(pieces, "GoalDisk", 0.15f, 0.65f, 0.002f, 0.08f, out error)) return false;
            return true;
        }

        private static bool Within(Dictionary<string, Piece> pieces, string name, float minWidth, float maxWidth,
            float minHeight, float maxHeight, out string error)
        {
            Bounds b = pieces[name].Bounds;
            float width = Mathf.Max(b.size.x, b.size.z);
            bool grounded = Mathf.Abs(b.min.y) <= 0.02f;
            bool centered = Mathf.Abs(b.center.x) <= 0.05f && Mathf.Abs(b.center.z) <= 0.05f;
            if (width < minWidth || width > maxWidth || b.size.y < minHeight || b.size.y > maxHeight || !grounded || !centered)
            {
                error = "preview3d_mesh_contract:" + name + " " + Describe(pieces[name]);
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static Bounds TransformBounds(Bounds local, Quaternion rotation, Vector3 scale)
        {
            Vector3 min = local.min;
            Vector3 max = local.max;
            var result = new Bounds();
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                Vector3 world = rotation * Vector3.Scale(point, scale);
                if (corner == 0) result = new Bounds(world, Vector3.zero);
                else result.Encapsulate(world);
            }

            return result;
        }
    }
}
