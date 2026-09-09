using System;
using System.Collections.Generic;
using Nectorial.SlideEscape;
using Nectorial.SlideEscape.Raid;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nectorial.SlideEscape.Unity.Raid
{
    internal sealed class RaidBoardView : IDisposable
    {
        private static readonly Color Floor = new Color32(229, 224, 211, 255);
        private static readonly Color Grid = new Color32(210, 203, 187, 150);
        private static readonly Color Wall = new Color32(41, 50, 56, 255);
        private static readonly Color WallRim = new Color32(74, 85, 88, 255);
        private static readonly Color WallShadow = new Color32(27, 35, 40, 255);
        private static readonly Color Ring = new Color32(90, 98, 96, 95);
        private static readonly Color Player = new Color32(67, 116, 183, 255);
        private static readonly Color Snake = new Color32(73, 83, 87, 255);
        private static readonly Color SnakeHead = new Color32(49, 58, 59, 255);
        private static readonly Color Tail = new Color32(208, 151, 67, 255);
        private static readonly Color Shield = new Color32(93, 137, 96, 255);
        private static readonly Color Magnet = new Color32(155, 106, 154, 255);
        private static readonly Color Slow = new Color32(82, 125, 141, 255);

        private readonly List<GameObject> _staticTiles = new List<GameObject>();
        private readonly List<GameObject> _dynamicTiles = new List<GameObject>();
        private readonly Dictionary<string, GameObject> _pickupActors = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Sprite _whiteSprite;
        private readonly Transform _root;
        private readonly List<Transform> _snakeActors = new List<Transform>();
        private Transform _playerActor;
        private RaidArenaDefinition _staticArena;
        private RaidFrame[] _activeFrames;
        private bool _transitionActive;
        private bool _disposed;

        public RaidBoardView()
        {
            _whiteSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f),
                new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            _whiteSprite.name = "RaidBoard-White-Primitive";
            _root = new GameObject("RaidBoard").transform;
            Application.quitting += Dispose;
        }

        public void Render(RaidArenaDefinition arena, RaidState state)
        {
            if (_disposed || arena == null || state == null) return;
            EnsureStaticArena(arena);
            EnsureActors(arena.SnakeBodyLength);
            _playerActor.localPosition = ToLocalPosition(state.PlayerPosition);
            RenderSnake(arena, state.SnakeHeadIndex, arena.SnakeBodyLength, null);
            UpdatePickups(state.CollectedTailIds, state.CollectedItemIds);
            SetRootPosition(arena);
        }

        public bool BeginAction(RaidArenaDefinition arena, RaidFrame[] frames)
        {
            if (_disposed || arena == null || frames == null || frames.Length == 0) return false;
            EnsureStaticArena(arena);
            EnsureActors(arena.SnakeBodyLength);
            _activeFrames = frames;
            _transitionActive = true;
            ApplyFrame(arena, frames[0], 0f);
            SetRootPosition(arena);
            return true;
        }

        public void AdvanceAction(float normalizedActionProgress)
        {
            if (!_transitionActive || _activeFrames == null || _activeFrames.Length == 0 || _staticArena == null) return;
            float normalized = Mathf.Clamp01(normalizedActionProgress);
            float scaled = normalized * _activeFrames.Length;
            int frameIndex = Mathf.Min(_activeFrames.Length - 1, Mathf.FloorToInt(scaled));
            float frameProgress = normalized >= 1f ? 1f : scaled - frameIndex;
            ApplyFrame(_staticArena, _activeFrames[frameIndex], frameProgress);
        }

        public void CompleteAction(RaidArenaDefinition arena, RaidState finalState)
        {
            _activeFrames = null;
            _transitionActive = false;
            Render(arena, finalState);
        }

        public void CancelAction()
        {
            _activeFrames = null;
            _transitionActive = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _activeFrames = null;
            _transitionActive = false;
            Application.quitting -= Dispose;
            Clear(_dynamicTiles);
            Clear(_staticTiles);
            _pickupActors.Clear();
            _snakeActors.Clear();
            if (_root != null) Object.Destroy(_root.gameObject);
            if (_whiteSprite != null) Object.Destroy(_whiteSprite);
        }

        private void EnsureStaticArena(RaidArenaDefinition arena)
        {
            if (ReferenceEquals(_staticArena, arena)) return;
            Clear(_staticTiles);
            _pickupActors.Clear();
            DrawStaticArena(arena);
            _staticArena = arena;
        }

        private void DrawStaticArena(RaidArenaDefinition arena)
        {
            for (int y = 0; y < arena.Height; y++)
            {
                for (int x = 0; x < arena.Width; x++)
                {
                    GridPoint point = new GridPoint(x, y);
                    AddPrimitive(_staticTiles, "Floor", Floor, point, new Vector2(1f, 1f), Vector2.zero, 0);
                    AddPrimitive(_staticTiles, "Grid Right", Grid, point, new Vector2(0.025f, 1f), new Vector2(0.4875f, 0f), 1);
                    AddPrimitive(_staticTiles, "Grid Bottom", Grid, point, new Vector2(1f, 0.025f), new Vector2(0f, -0.4875f), 1);
                    if (arena.Rows != null && y < arena.Rows.Length && arena.Rows[y] != null && x < arena.Rows[y].Length && arena.Rows[y][x] == '#')
                        DrawWall(point);
                }
            }

            if (arena.SnakeRing != null)
            {
                for (int index = 0; index < arena.SnakeRing.Length; index++)
                    AddPrimitive(_staticTiles, "Snake Ring", Ring, arena.SnakeRing[index], new Vector2(0.18f, 0.18f), Vector2.zero, 2);
            }

            if (arena.TailFragments != null)
            {
                for (int index = 0; index < arena.TailFragments.Length; index++)
                {
                    RaidTailDefinition tail = arena.TailFragments[index];
                    if (tail == null || string.IsNullOrEmpty(tail.Id)) continue;
                    _pickupActors[tail.Id] = AddPrimitive(_staticTiles, "Tail " + tail.Id, Tail, tail.Position,
                        new Vector2(0.42f, 0.42f), Vector2.zero, 5, 45f);
                }
            }

            if (arena.Items != null)
            {
                for (int index = 0; index < arena.Items.Length; index++)
                {
                    RaidItemDefinition item = arena.Items[index];
                    if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                    Color color = item.Kind == RaidItemKind.Shield ? Shield : item.Kind == RaidItemKind.Magnet ? Magnet : Slow;
                    _pickupActors[item.Id] = AddPrimitive(_staticTiles, "Item " + item.Kind, color, item.Position,
                        new Vector2(0.34f, 0.34f), Vector2.zero, 5, item.Kind == RaidItemKind.Magnet ? 45f : 0f);
                }
            }
        }

        private void EnsureActors(int bodyLength)
        {
            if (_playerActor != null && _snakeActors.Count == Mathf.Max(1, bodyLength)) return;
            Clear(_dynamicTiles);
            _snakeActors.Clear();
            _playerActor = AddActor("Player", Player, new Vector2(0.62f, 0.62f), 8, 0f);
            int count = Mathf.Max(1, bodyLength);
            for (int index = 0; index < count; index++)
            {
                Transform actor = AddActor("Snake " + index, index == 0 ? SnakeHead : Snake,
                    new Vector2(index == 0 ? 0.72f : 0.58f, index == 0 ? 0.72f : 0.58f), 8, index == 0 ? 45f : 0f);
                _snakeActors.Add(actor);
            }
        }

        private void ApplyFrame(RaidArenaDefinition arena, RaidFrame frame, float progress)
        {
            if (frame == null) return;
            _playerActor.localPosition = Vector3.Lerp(ToLocalPosition(frame.PlayerBefore), ToLocalPosition(frame.PlayerAfter), Mathf.Clamp01(progress));
            int count = Mathf.Min(_snakeActors.Count, Mathf.Max(frame.SnakeBefore == null ? 0 : frame.SnakeBefore.Length,
                frame.SnakeAfter == null ? 0 : frame.SnakeAfter.Length));
            for (int index = 0; index < count; index++)
            {
                GridPoint before = frame.SnakeBefore != null && index < frame.SnakeBefore.Length ? frame.SnakeBefore[index] : frame.SnakeAfter[index];
                GridPoint after = frame.SnakeAfter != null && index < frame.SnakeAfter.Length ? frame.SnakeAfter[index] : before;
                _snakeActors[index].localPosition = Vector3.Lerp(ToLocalPosition(before), ToLocalPosition(after), Mathf.Clamp01(progress));
            }
            SetRootPosition(arena);
        }

        private void RenderSnake(RaidArenaDefinition arena, int headIndex, int bodyLength, RaidFrame frame)
        {
            if (frame != null)
            {
                ApplyFrame(arena, frame, 1f);
                return;
            }
            if (arena.SnakeRing == null || arena.SnakeRing.Length == 0) return;
            int ringLength = arena.SnakeRing.Length;
            int count = Mathf.Min(_snakeActors.Count, Mathf.Max(1, bodyLength));
            for (int index = 0; index < count; index++)
            {
                int ringIndex = ((headIndex - index) % ringLength + ringLength) % ringLength;
                _snakeActors[index].localPosition = ToLocalPosition(arena.SnakeRing[ringIndex]);
            }
        }

        private void UpdatePickups(string[] collectedTails, string[] collectedItems)
        {
            var collected = new HashSet<string>(StringComparer.Ordinal);
            AddAll(collected, collectedTails);
            AddAll(collected, collectedItems);
            foreach (KeyValuePair<string, GameObject> pair in _pickupActors)
                if (pair.Value != null) pair.Value.SetActive(!collected.Contains(pair.Key));
        }

        private static void AddAll(HashSet<string> values, string[] items)
        {
            if (items == null) return;
            for (int index = 0; index < items.Length; index++)
                if (!string.IsNullOrEmpty(items[index])) values.Add(items[index]);
        }

        private Transform AddActor(string name, Color color, Vector2 scale, int order, float rotation)
        {
            var actor = new GameObject(name);
            actor.transform.SetParent(_root, false);
            _dynamicTiles.Add(actor);
            AddActorPrimitive(actor.transform, name + " Shape", color, scale, order, rotation);
            return actor.transform;
        }

        private void AddActorPrimitive(Transform parent, string name, Color color, Vector2 scale, int order, float rotation)
        {
            var visual = new GameObject(name);
            visual.transform.SetParent(parent, false);
            visual.transform.localScale = new Vector3(scale.x, scale.y, 1f);
            visual.transform.localRotation = Quaternion.Euler(0f, 0f, rotation);
            var renderer = visual.AddComponent<SpriteRenderer>();
            renderer.sprite = _whiteSprite;
            renderer.color = color;
            renderer.sortingOrder = order;
        }

        private GameObject AddPrimitive(List<GameObject> owner, string name, Color color, GridPoint point, Vector2 scale, Vector2 offset, int order, float rotation = 0f)
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
            return tile;
        }

        private void DrawWall(GridPoint point)
        {
            AddPrimitive(_staticTiles, "Wall Shadow", WallShadow, point, new Vector2(0.94f, 0.94f), new Vector2(0.03f, -0.03f), 2);
            AddPrimitive(_staticTiles, "Wall", Wall, point, new Vector2(0.92f, 0.92f), Vector2.zero, 3);
            AddPrimitive(_staticTiles, "Wall Top Rim", WallRim, point, new Vector2(0.84f, 0.035f), new Vector2(0f, 0.4225f), 4);
            AddPrimitive(_staticTiles, "Wall Left Rim", WallRim, point, new Vector2(0.035f, 0.84f), new Vector2(-0.4225f, 0f), 4);
        }

        private void SetRootPosition(RaidArenaDefinition arena)
        {
            _root.position = new Vector3(-(arena.Width - 1) * 0.5f, (arena.Height - 1) * 0.5f, 0f);
        }

        private static Vector3 ToLocalPosition(GridPoint point)
        {
            return new Vector3(point.X, -point.Y, 0f);
        }

        private static void Clear(List<GameObject> tiles)
        {
            for (int index = 0; index < tiles.Count; index++)
                if (tiles[index] != null) Object.Destroy(tiles[index]);
            tiles.Clear();
        }
    }
}
