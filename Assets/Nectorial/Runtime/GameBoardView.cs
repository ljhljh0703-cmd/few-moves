using System;
using System.Collections.Generic;
using Nectorial.SlideEscape;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nectorial.SlideEscape.Unity
{
    internal sealed class GameBoardView : IDisposable
    {
        private const int TilePixels = 16;
        private static readonly Color Floor = new Color32(229, 224, 211, 255);
        private static readonly Color Grid = new Color32(210, 203, 187, 150);
        private static readonly Color Wall = new Color32(41, 50, 56, 255);
        private static readonly Color WallRim = new Color32(74, 85, 88, 255);
        private static readonly Color WallShadow = new Color32(27, 35, 40, 255);
        private static readonly Color Target = new Color32(67, 116, 183, 255);
        private static readonly Color Goal = new Color32(102, 155, 211, 255);
        private static readonly Color HelperAmber = new Color32(208, 151, 67, 255);
        private static readonly Color HelperTeal = new Color32(67, 148, 145, 255);
        private static readonly Color Green = new Color32(93, 137, 96, 255);

        private readonly List<GameObject> _staticTiles = new List<GameObject>();
        private readonly List<GameObject> _dynamicTiles = new List<GameObject>();
        private readonly Sprite[] _sprites;
        private readonly Sprite _whiteSprite;
        private readonly Material _atlasMaterial;
        private readonly Transform _root;
        private RoomDefinition _staticRoom;
        private RoomDefinition _actorRoom;
        private readonly List<Transform> _pieceActors = new List<Transform>();
        private readonly List<Transform> _selectionMarkers = new List<Transform>();
        private Transform _movingActor;
        private Vector3 _moveStart;
        private Vector3 _moveEnd;
        private bool _transitionActive;
        private bool _disposed;

        public GameBoardView(Texture2D atlas)
        {
            atlas.filterMode = FilterMode.Point;
            atlas.wrapMode = TextureWrapMode.Clamp;
            _sprites = new Sprite[8];
            for (int index = 0; index < _sprites.Length; index++)
            {
                _sprites[index] = Sprite.Create(atlas, new Rect(index * TilePixels, 0, TilePixels, TilePixels),
                    new Vector2(0.5f, 0.5f), TilePixels, 0, SpriteMeshType.FullRect);
                _sprites[index].name = "Tile-" + index;
            }

            _whiteSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _whiteSprite.name = "Board-White-Primitive";
            Shader atlasShader = Resources.Load<Shader>("Visuals/MatteSprite");
            if (atlasShader == null)
            {
                throw new InvalidOperationException("Required matte sprite shader is missing.");
            }

            _atlasMaterial = new Material(atlasShader) { name = "Board-Matte-Sprite-Material" };
            _root = new GameObject("Board").transform;
            Application.quitting += Dispose;
        }

        public void Render(RoomDefinition room, GameState state, int selectedPieceIndex)
        {
            if (!ReferenceEquals(_staticRoom, room))
            {
                Clear(_staticTiles);
                DrawStaticRoom(room);
                _staticRoom = room;
            }

            EnsurePieceActors(room);
            for (int index = 0; index < room.Pieces.Length; index++)
            {
                _pieceActors[index].localPosition = ToLocalPosition(state.Positions[index]);
                _selectionMarkers[index].gameObject.SetActive(index == selectedPieceIndex);
            }

            _root.position = new Vector3(-(room.Width - 1) * 0.5f, (room.Height - 1) * 0.5f, 0f);
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
            float clamped = Mathf.Clamp01(progress);
            _movingActor.localPosition = Vector3.Lerp(_moveStart, _moveEnd, clamped);
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
            ClearDynamicTiles();
            Clear(_staticTiles);
            if (_root != null) Object.Destroy(_root.gameObject);
            for (int index = 0; index < _sprites.Length; index++)
                if (_sprites[index] != null) Object.Destroy(_sprites[index]);
            if (_whiteSprite != null) Object.Destroy(_whiteSprite);
            if (_atlasMaterial != null) Object.Destroy(_atlasMaterial);
        }

        private void DrawStaticRoom(RoomDefinition room)
        {
            for (int y = 0; y < room.Height; y++)
            {
                for (int x = 0; x < room.Width; x++)
                {
                    GridPoint point = new GridPoint(x, y);
                    DrawFloor(point);
                    if (room.Rows[y][x] == '#') DrawWall(point);
                }
            }

            DrawGoal(room.Goal);
        }

        private void DrawFloor(GridPoint point)
        {
            AddPrimitive(_staticTiles, "Floor", Floor, point, new Vector2(1f, 1f), Vector2.zero, 0);
            AddPrimitive(_staticTiles, "Grid Right", Grid, point, new Vector2(0.025f, 1f), new Vector2(0.4875f, 0f), 1);
            AddPrimitive(_staticTiles, "Grid Bottom", Grid, point, new Vector2(1f, 0.025f), new Vector2(0f, -0.4875f), 1);
        }

        private void DrawWall(GridPoint point)
        {
            AddPrimitive(_staticTiles, "Wall Shadow", WallShadow, point, new Vector2(0.94f, 0.94f), new Vector2(0.03f, -0.03f), 2);
            AddPrimitive(_staticTiles, "Wall", Wall, point, new Vector2(0.92f, 0.92f), Vector2.zero, 3);
            AddPrimitive(_staticTiles, "Wall Top Rim", WallRim, point, new Vector2(0.84f, 0.035f), new Vector2(0f, 0.4225f), 4);
            AddPrimitive(_staticTiles, "Wall Left Rim", WallRim, point, new Vector2(0.035f, 0.84f), new Vector2(-0.4225f, 0f), 4);
        }

        private void DrawGoal(GridPoint point)
        {
            AddAtlas(_staticTiles, "Goal", 2, Goal, point, 0.48f, 4);
        }

        private void EnsurePieceActors(RoomDefinition room)
        {
            if (ReferenceEquals(_actorRoom, room) && _pieceActors.Count == room.Pieces.Length) return;

            ClearDynamicTiles();
            for (int index = 0; index < room.Pieces.Length; index++)
            {
                _pieceActors.Add(CreatePieceActor(index, index == room.TargetPieceIndex));
            }

            _actorRoom = room;
        }

        private Transform CreatePieceActor(int pieceIndex, bool targetPiece)
        {
            var actor = new GameObject("Piece " + pieceIndex);
            actor.transform.SetParent(_root, false);
            _dynamicTiles.Add(actor);

            if (targetPiece)
            {
                AddActorAtlas(actor.transform, "Target Piece", 2, Target, 0.8f, 8);
            }
            else
            {
                Color color = pieceIndex % 2 == 1 ? HelperAmber : HelperTeal;
                float rotation = pieceIndex % 2 == 1 ? 45f : 0f;
                AddActorPrimitive(actor.transform, "Helper Piece", color, new Vector2(0.62f, 0.62f), Vector2.zero, 8, rotation);
            }

            _selectionMarkers.Add(AddSelectionMarker(actor.transform));
            return actor.transform;
        }

        private Transform AddSelectionMarker(Transform actor)
        {
            var marker = new GameObject("Selected Marker");
            marker.transform.SetParent(actor, false);
            AddActorPrimitive(marker.transform, "Selected Top", Green, new Vector2(0.82f, 0.045f), new Vector2(0f, 0.445f), 9, 0f);
            AddActorPrimitive(marker.transform, "Selected Bottom", Green, new Vector2(0.82f, 0.045f), new Vector2(0f, -0.445f), 9, 0f);
            AddActorPrimitive(marker.transform, "Selected Left", Green, new Vector2(0.045f, 0.82f), new Vector2(-0.445f, 0f), 9, 0f);
            AddActorPrimitive(marker.transform, "Selected Right", Green, new Vector2(0.045f, 0.82f), new Vector2(0.445f, 0f), 9, 0f);
            return marker.transform;
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

        private GameObject AddAtlas(string name, int spriteIndex, Color color, GridPoint point, float scale, int order)
        {
            return AddAtlas(_dynamicTiles, name, spriteIndex, color, point, scale, order);
        }

        private GameObject AddAtlas(List<GameObject> owner, string name, int spriteIndex, Color color, GridPoint point, float scale, int order)
        {
            return AddVisual(owner, name, _sprites[spriteIndex], color, point, new Vector2(scale, scale), Vector2.zero, order, _atlasMaterial);
        }

        private void AddPrimitive(List<GameObject> owner, string name, Color color, GridPoint point, Vector2 scale, Vector2 offset, int order)
        {
            AddVisual(owner, name, _whiteSprite, color, point, scale, offset, order, null);
        }

        private GameObject AddVisual(List<GameObject> owner, string name, Sprite sprite, Color color, GridPoint point,
            Vector2 scale, Vector2 offset, int order, Material material)
        {
            var tile = new GameObject(name);
            tile.transform.SetParent(_root, false);
            tile.transform.localPosition = new Vector3(point.X + offset.x, -point.Y + offset.y, 0f);
            tile.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            var renderer = tile.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = color;
            if (material != null) renderer.sharedMaterial = material;
            renderer.sortingOrder = order;
            owner.Add(tile);
            return tile;
        }

        private void ClearDynamicTiles()
        {
            _transitionActive = false;
            _movingActor = null;
            _actorRoom = null;
            _pieceActors.Clear();
            _selectionMarkers.Clear();
            Clear(_dynamicTiles);
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
