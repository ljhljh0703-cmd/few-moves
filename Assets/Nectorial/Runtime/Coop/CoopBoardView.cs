using System.Collections.Generic;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Coop;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nectorial.SlideEscape.Unity.Coop
{
    internal sealed class CoopBoardView : System.IDisposable
    {
        private const int TilePixels = 16;
        private static readonly Color Floor = new Color32(229, 224, 211, 255);
        private static readonly Color Grid = new Color32(210, 203, 187, 150);
        private static readonly Color Wall = new Color32(41, 50, 56, 255);
        private static readonly Color WallRim = new Color32(74, 85, 88, 255);
        private static readonly Color WallShadow = new Color32(27, 35, 40, 255);
        private static readonly Color Circle = new Color32(67, 116, 183, 255);
        private static readonly Color Diamond = new Color32(208, 151, 67, 255);
        private static readonly Color CircleGoal = new Color32(102, 155, 211, 150);
        private static readonly Color DiamondGoal = new Color32(208, 151, 67, 150);

        private readonly List<GameObject> _staticTiles = new List<GameObject>();
        private readonly List<GameObject> _dynamicTiles = new List<GameObject>();
        private readonly Sprite[] _sprites;
        private readonly Sprite _whiteSprite;
        private readonly Material _atlasMaterial;
        private readonly Transform _root;
        private CoopRoomDefinition _staticRoom;
        private Transform _circleActor;
        private Transform _diamondActor;
        private Transform _movingActor;
        private Vector3 _moveStart;
        private Vector3 _moveEnd;
        private bool _transitionActive;
        private bool _disposed;

        public CoopBoardView(Texture2D atlas)
        {
            atlas.filterMode = FilterMode.Point;
            atlas.wrapMode = TextureWrapMode.Clamp;
            _sprites = new Sprite[8];
            for (int index = 0; index < _sprites.Length; index++)
            {
                _sprites[index] = Sprite.Create(atlas, new Rect(index * TilePixels, 0, TilePixels, TilePixels),
                    new Vector2(0.5f, 0.5f), TilePixels, 0, SpriteMeshType.FullRect);
                _sprites[index].name = "CoopTile-" + index;
            }

            _whiteSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _whiteSprite.name = "CoopBoard-White-Primitive";
            Shader atlasShader = Resources.Load<Shader>("Visuals/MatteSprite");
            if (atlasShader == null) throw new System.InvalidOperationException("Required matte sprite shader is missing.");
            _atlasMaterial = new Material(atlasShader) { name = "CoopBoard-Matte-Sprite-Material" };
            _root = new GameObject("CoopBoard").transform;
            Application.quitting += Dispose;
        }

        public void Render(CoopRoomDefinition room, CoopState state)
        {
            if (!ReferenceEquals(_staticRoom, room))
            {
                Clear(_staticTiles);
                DrawStaticRoom(room);
                _staticRoom = room;
            }

            EnsureActors();
            _circleActor.localPosition = ToLocalPosition(state.CirclePosition);
            _diamondActor.localPosition = ToLocalPosition(state.DiamondPosition);
            _root.position = new Vector3(-(room.Width - 1) * 0.5f, (room.Height - 1) * 0.5f, 0f);
        }

        public bool BeginTransition(CoopRoomDefinition room, CoopState before, CoopState after, CoopActor actor)
        {
            if (_disposed || room == null || before == null || after == null) return false;
            Render(room, before);
            EnsureActors();
            _movingActor = actor == CoopActor.Circle ? _circleActor : _diamondActor;
            GridPoint from = actor == CoopActor.Circle ? before.CirclePosition : before.DiamondPosition;
            GridPoint to = actor == CoopActor.Circle ? after.CirclePosition : after.DiamondPosition;
            _moveStart = ToLocalPosition(from);
            _moveEnd = ToLocalPosition(to);
            _movingActor.localPosition = _moveStart;
            _transitionActive = true;
            return true;
        }

        public void AdvanceTransition(float progress)
        {
            if (!_transitionActive || _movingActor == null) return;
            _movingActor.localPosition = Vector3.Lerp(_moveStart, _moveEnd, Mathf.Clamp01(progress));
        }

        public void CompleteTransition(CoopRoomDefinition room, CoopState state)
        {
            _transitionActive = false;
            _movingActor = null;
            Render(room, state);
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
            _transitionActive = false;
            _movingActor = null;
            Application.quitting -= Dispose;
            Clear(_dynamicTiles);
            Clear(_staticTiles);
            if (_root != null) Object.Destroy(_root.gameObject);
            for (int index = 0; index < _sprites.Length; index++)
                if (_sprites[index] != null) Object.Destroy(_sprites[index]);
            if (_whiteSprite != null) Object.Destroy(_whiteSprite);
            if (_atlasMaterial != null) Object.Destroy(_atlasMaterial);
        }

        private void DrawStaticRoom(CoopRoomDefinition room)
        {
            for (int y = 0; y < room.Height; y++)
            {
                for (int x = 0; x < room.Width; x++)
                {
                    GridPoint point = new GridPoint(x, y);
                    AddPrimitive(_staticTiles, "Floor", Floor, point, new Vector2(1f, 1f), Vector2.zero, 0);
                    AddPrimitive(_staticTiles, "Grid Right", Grid, point, new Vector2(0.025f, 1f), new Vector2(0.4875f, 0f), 1);
                    AddPrimitive(_staticTiles, "Grid Bottom", Grid, point, new Vector2(1f, 0.025f), new Vector2(0f, -0.4875f), 1);
                    if (room.Rows[y][x] == '#') DrawWall(point);
                }
            }

            AddAtlas(_staticTiles, "Circle Goal", 2, CircleGoal, room.CircleGoal, 0.78f, 4);
            AddPrimitive(_staticTiles, "Diamond Goal", DiamondGoal, room.DiamondGoal, new Vector2(0.78f, 0.78f), Vector2.zero, 4, 45f);
        }

        private void DrawWall(GridPoint point)
        {
            AddPrimitive(_staticTiles, "Wall Shadow", WallShadow, point, new Vector2(0.94f, 0.94f), new Vector2(0.03f, -0.03f), 2);
            AddPrimitive(_staticTiles, "Wall", Wall, point, new Vector2(0.92f, 0.92f), Vector2.zero, 3);
            AddPrimitive(_staticTiles, "Wall Top Rim", WallRim, point, new Vector2(0.84f, 0.035f), new Vector2(0f, 0.4225f), 4);
            AddPrimitive(_staticTiles, "Wall Left Rim", WallRim, point, new Vector2(0.035f, 0.84f), new Vector2(-0.4225f, 0f), 4);
        }

        private void EnsureActors()
        {
            if (_circleActor != null && _diamondActor != null) return;
            _circleActor = CreateCircleActor();
            _diamondActor = CreateDiamondActor();
        }

        private Transform CreateCircleActor()
        {
            var actor = new GameObject("Circle");
            actor.transform.SetParent(_root, false);
            _dynamicTiles.Add(actor);
            AddActorAtlas(actor.transform, "Circle Piece", 2, Circle, 0.82f, 8);
            return actor.transform;
        }

        private Transform CreateDiamondActor()
        {
            var actor = new GameObject("Diamond");
            actor.transform.SetParent(_root, false);
            _dynamicTiles.Add(actor);
            AddActorPrimitive(actor.transform, "Diamond Piece", Diamond, new Vector2(0.62f, 0.62f), Vector2.zero, 8, 45f);
            return actor.transform;
        }

        private void AddActorAtlas(Transform actor, string name, int spriteIndex, Color color, float scale, int order)
        {
            var visual = new GameObject(name);
            visual.transform.SetParent(actor, false);
            visual.transform.localScale = new Vector3(scale, scale, 1f);
            var renderer = visual.AddComponent<SpriteRenderer>();
            renderer.sprite = _sprites[spriteIndex];
            renderer.color = color;
            renderer.sharedMaterial = _atlasMaterial;
            renderer.sortingOrder = order;
        }

        private void AddActorPrimitive(Transform actor, string name, Color color, Vector2 scale, Vector2 offset, int order, float rotation)
        {
            var visual = new GameObject(name);
            visual.transform.SetParent(actor, false);
            visual.transform.localPosition = new Vector3(offset.x, offset.y, 0f);
            visual.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            visual.transform.localRotation = Quaternion.Euler(0f, 0f, rotation);
            var renderer = visual.AddComponent<SpriteRenderer>();
            renderer.sprite = _whiteSprite;
            renderer.color = color;
            renderer.sortingOrder = order;
        }

        private void AddAtlas(List<GameObject> owner, string name, int spriteIndex, Color color, GridPoint point, float scale, int order)
        {
            var tile = new GameObject(name);
            tile.transform.SetParent(_root, false);
            tile.transform.localPosition = new Vector3(point.X, -point.Y, 0f);
            tile.transform.localScale = new Vector3(scale, scale, 1f);
            var renderer = tile.AddComponent<SpriteRenderer>();
            renderer.sprite = _sprites[spriteIndex];
            renderer.color = color;
            renderer.sharedMaterial = _atlasMaterial;
            renderer.sortingOrder = order;
            owner.Add(tile);
        }

        private void AddPrimitive(List<GameObject> owner, string name, Color color, GridPoint point, Vector2 scale, Vector2 offset, int order, float rotation = 0f)
        {
            var tile = new GameObject(name);
            tile.transform.SetParent(_root, false);
            tile.transform.localPosition = new Vector3(point.X + offset.x, -point.Y + offset.y, 0f);
            tile.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            tile.transform.localRotation = Quaternion.Euler(0f, 0f, rotation);
            var renderer = tile.AddComponent<SpriteRenderer>();
            renderer.sprite = _whiteSprite;
            renderer.color = color;
            renderer.sortingOrder = order;
            owner.Add(tile);
        }

        private static Vector3 ToLocalPosition(GridPoint point)
        {
            return new Vector3(point.X, -point.Y, 0f);
        }

        private static void Clear(List<GameObject> tiles)
        {
            for (int index = 0; index < tiles.Count; index++) Object.Destroy(tiles[index]);
            tiles.Clear();
        }
    }
}
