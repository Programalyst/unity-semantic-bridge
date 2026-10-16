using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    public sealed class LayoutApplyResult
    {
        public GameObject Root;
        /// <summary>Occupancy the layout should produce, from the measured footprints.</summary>
        public LayoutGrid Expected;
        public readonly Dictionary<string, PieceSpec> Specs = new Dictionary<string, PieceSpec>();
        /// <summary>Problems: the scene may not match the layout where these point.</summary>
        public readonly List<string> Issues = new List<string>();
        /// <summary>The placement problems alone (fit, missing colliders or geometry; also in Issues).</summary>
        public readonly List<string> PlacementIssues = new List<string>();
        /// <summary>Adjustments made on purpose, e.g. colliders shrunk into their footprint.</summary>
        public readonly List<string> Notes = new List<string>();
        public int PieceCount;
        public int DecorCount;

        internal void AddPlacementIssue(string line)
        {
            Issues.Add(line);
            PlacementIssues.Add(line);
        }

        public override string ToString()
        {
            var lines = new List<string>
            {
                $"Applied {PieceCount} pieces and {DecorCount} decor under {LayoutApplier.RootName}; {Expected?.BlockedCount ?? 0} blocked cells.",
                $"Issues ({Issues.Count}):",
            };
            lines.AddRange(Issues);
            lines.Add($"Notes ({Notes.Count}):");
            lines.AddRange(Notes);
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Builds a layout document into a scene: prefab instances by GUID under one
    /// [Generated Layout] root, quarter-turn rotation, footprint-centred placement
    /// (pivot offset), the game's piece hook, and blocking colliders shrunk inside their
    /// footprint cells so runtime triggers reproduce the layout. One Undo group;
    /// reapplying replaces only that root.
    /// </summary>
    public static class LayoutApplier
    {
        public const string RootName = "[Generated Layout]";
        public const string PiecesName = "Pieces";
        public const string DecorName = "Decor";
        /// <summary>Gap kept between a fitted collider and its footprint edge, meters.</summary>
        public const float ColliderInset = 0.1f;

        public static LayoutApplyResult Apply(LayoutDocument document, Scene scene, IGridRulesAdapter adapter = null)
        {
            adapter ??= LayoutAdapters.Find();
            if (!scene.IsValid() || !scene.isLoaded) throw new BridgeToolException("Open the target scene before applying a layout.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new BridgeToolException("Exit Play Mode before applying a layout.");
            if (!adapter.TryGetBoard(scene, out var board, out var boardError)) throw new BridgeToolException(boardError);
            if (board.Width != document.Width || board.Height != document.Height)
                throw new BridgeToolException($"Layout size {document.Width}x{document.Height} differs from the level's board {board.Width}x{board.Height}. Change one of them.");
            var prefabs = ResolvePrefabs(document);
            CheckEnemyAssets(document);

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Layout");
            var result = new LayoutApplyResult();
            try
            {
                return Build(document, scene, adapter, board, prefabs, undoGroup, result);
            }
            catch
            {
                // A late failure (e.g. the game rejecting a spawn asset) leaves nothing half-built:
                // the new root is not registered with Undo yet, so it goes first; the revert then
                // restores the previous root and the level.
                if (result.Root != null) Object.DestroyImmediate(result.Root);
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
        }

        private static LayoutApplyResult Build(LayoutDocument document, Scene scene, IGridRulesAdapter adapter,
            GridBoard board, Dictionary<string, GameObject> prefabs, int undoGroup, LayoutApplyResult result)
        {
            foreach (var old in scene.GetRootGameObjects().Where(go => go.name == RootName))
                Undo.DestroyObjectImmediate(old);

            var root = new GameObject(RootName);
            result.Root = root;
            SceneManager.MoveGameObjectToScene(root, scene);
            var piecesParent = new GameObject(PiecesName).transform;
            piecesParent.SetParent(root.transform, false);
            var decorParent = new GameObject(DecorName).transform;
            decorParent.SetParent(root.transform, false);

            foreach (var piece in document.Pieces)
            {
                var spec = Place(piece, prefabs[piece.PrefabGuid], piecesParent, true, scene, board, adapter, result);
                result.Specs[piece.LocalId] = spec;
                result.PieceCount++;
            }
            foreach (var decor in document.Decor)
            {
                Place(decor, prefabs[decor.PrefabGuid], decorParent, false, scene, board, adapter, result);
                result.DecorCount++;
            }

            var buildIssues = new List<LayoutIssue>();
            result.Expected = LayoutGrid.Build(document,
                p => result.Specs.TryGetValue(p.LocalId, out var s) ? s : (PieceSpec?)null, adapter.Rules, buildIssues);
            result.Issues.AddRange(buildIssues.Select(i => i.ToString()));

            if (board.LevelOwner != null) Undo.RecordObject(board.LevelOwner, "Apply Layout");
            adapter.WriteEnemySpawns(scene, document.Enemies);
            adapter.WriteDeployCells(scene, document.Deploy);
            if (board.LevelOwner != null) EditorUtility.SetDirty(board.LevelOwner);

            // Registered once fully built, so Undo removes the whole root and Redo restores it as built.
            Undo.RegisterCreatedObjectUndo(root, "Apply Layout");
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);
            Physics.SyncTransforms();
            return result;
        }

        private static Dictionary<string, GameObject> ResolvePrefabs(LayoutDocument document)
        {
            var prefabs = new Dictionary<string, GameObject>();
            var missing = new List<string>();
            foreach (var piece in document.Pieces.Concat(document.Decor))
            {
                if (prefabs.ContainsKey(piece.PrefabGuid)) continue;
                var path = AssetDatabase.GUIDToAssetPath(piece.PrefabGuid);
                var prefab = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
                    missing.Add($"{piece.LocalId} ({piece.PrefabGuid})");
                else
                    prefabs[piece.PrefabGuid] = prefab;
            }
            if (missing.Count > 0)
                throw new BridgeToolException($"No prefab asset for: {string.Join(", ", missing)}. Import the pieces (GUIDs must be preserved) before applying.");
            return prefabs;
        }

        // Generic check only: the GUIDs name assets. The adapter checks their types when writing.
        private static void CheckEnemyAssets(LayoutDocument document)
        {
            var missing = new List<string>();
            foreach (var enemy in document.Enemies)
                foreach (var guid in new[] { enemy.ProfileGuid, enemy.VisualGuid })
                    if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                        missing.Add($"{enemy.LocalId} ({guid})");
            if (missing.Count > 0)
                throw new BridgeToolException($"No asset for enemy spawn GUIDs: {string.Join(", ", missing)}.");
        }

        private static PieceSpec Place(LayoutPiece piece, GameObject prefab, Transform parent, bool blocker, Scene scene,
            GridBoard board, IGridRulesAdapter adapter, LayoutApplyResult result)
        {
            var frame = PieceGeometry.Frame(piece.Rotation);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = piece.LocalId;
            instance.transform.SetParent(parent, false);
            instance.transform.SetPositionAndRotation(Vector3.zero, frame * prefab.transform.localRotation);

            var cover = CoverLevel.None;
            if (blocker)
            {
                cover = adapter.DescribeCover(instance);
                adapter.ConfigurePiece(instance, cover);
            }

            var bounds = blocker
                ? PieceGeometry.MeasureColliders(instance, frame, adapter.IsBlockingCollider)
                : PieceGeometry.MeasureRenderers(instance, frame);
            var cellSize = adapter.Rules.CellSize;
            if (!bounds.HasValue)
            {
                result.AddPlacementIssue(blocker
                    ? $"NO_BLOCKING_COLLIDER {piece.Anchor} {piece.LocalId}: the piece hook left no blocking collider"
                    : $"NO_GEOMETRY {piece.Anchor} {piece.LocalId}: no renderers to measure; placed as one cell");
                bounds = new PieceBounds(Vector3.zero, new Vector3(cellSize, 0f, cellSize));
            }

            var (width, depth) = PieceGeometry.Footprint(bounds.Value, cellSize);
            var spec = new PieceSpec(width, depth, cover);
            var size = spec.RotatedSize(piece.Rotation);
            var center = FootprintCenter(adapter, board, piece.Anchor, size);
            var offset = frame * bounds.Value.Center;
            instance.transform.position = new Vector3(center.x - offset.x, board.FloorY, center.z - offset.z);

            if (blocker) FitColliders(instance, piece, center, size, cellSize, adapter, result);
            return spec;
        }

        /// <summary>World centre (X/Z, floor height) of a footprint, origin-agnostic through the adapter.</summary>
        public static Vector3 FootprintCenter(IGridRulesAdapter adapter, GridBoard board, Cell anchor, Cell rotatedSize)
        {
            var first = adapter.CellToWorld(anchor, board);
            var last = adapter.CellToWorld(new Cell(anchor.X + rotatedSize.X - 1, anchor.Y + rotatedSize.Y - 1), board);
            return (first + last) * 0.5f;
        }

        // Shrinks each blocking box collider to its footprint (inset on every side), so the
        // game's per-cell trigger boxes only touch footprint cells.
        private static void FitColliders(GameObject instance, LayoutPiece piece, Vector3 center, Cell size, float cellSize,
            IGridRulesAdapter adapter, LayoutApplyResult result)
        {
            var halfX = size.X * cellSize * 0.5f - ColliderInset;
            var halfZ = size.Y * cellSize * 0.5f - ColliderInset;
            var limitMin = new Vector2(center.x - halfX, center.z - halfZ);
            var limitMax = new Vector2(center.x + halfX, center.z + halfZ);

            foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
            {
                if (!adapter.IsBlockingCollider(collider)) continue;
                var (min, max) = WorldBox(PieceGeometry.ColliderCorners(collider));
                if (min.x >= limitMin.x && min.z >= limitMin.y && max.x <= limitMax.x && max.z <= limitMax.y) continue;

                var path = RelativePath(instance.transform, collider.transform);
                if (!(collider is BoxCollider box) || !IsAxisAligned(box.transform))
                {
                    result.AddPlacementIssue($"FIT_UNSUPPORTED {piece.Anchor} {piece.LocalId}/{path}: {collider.GetType().Name} reaches past its footprint and only axis-aligned boxes can be fitted; neighbouring cells may block");
                    continue;
                }

                var fittedMin = new Vector3(Mathf.Max(min.x, limitMin.x), min.y, Mathf.Max(min.z, limitMin.y));
                var fittedMax = new Vector3(Mathf.Min(max.x, limitMax.x), max.y, Mathf.Min(max.z, limitMax.y));
                if (fittedMin.x >= fittedMax.x || fittedMin.z >= fittedMax.z)
                {
                    result.AddPlacementIssue($"FIT_EMPTY {piece.Anchor} {piece.LocalId}/{path}: collider lies outside its footprint");
                    continue;
                }

                var oldSize = max - min;
                var (localMin, localMax) = WorldBox(PieceGeometry.BoxCorners(box.transform.worldToLocalMatrix,
                    (fittedMin + fittedMax) * 0.5f, fittedMax - fittedMin));
                box.center = (localMin + localMax) * 0.5f;
                box.size = localMax - localMin;
                var newSize = fittedMax - fittedMin;
                result.Notes.Add($"FIT {piece.Anchor} {piece.LocalId}/{path}: {oldSize.x:0.00} x {oldSize.z:0.00} m -> {newSize.x:0.00} x {newSize.z:0.00} m");
            }
        }

        private static (Vector3 min, Vector3 max) WorldBox(IEnumerable<Vector3> points)
        {
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            foreach (var point in points)
            {
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            return (min, max);
        }

        private static bool IsAxisAligned(Transform transform)
        {
            foreach (var axis in new[] { transform.right, transform.up, transform.forward })
            {
                var a = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));
                if (Mathf.Max(a.x, Mathf.Max(a.y, a.z)) < 0.9999f) return false;
            }
            return true;
        }

        private static string RelativePath(Transform root, Transform target)
        {
            if (target == root) return ".";
            var path = target.name;
            for (var parent = target.parent; parent != null && parent != root; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }
    }
}
