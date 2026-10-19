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
        /// <summary>Pieces and decor placed from their level catalog card (the rest were measured on the instance).</summary>
        public int CardCount;
        /// <summary>Each piece and decor placed from a card, for the scale and plausibility validators.</summary>
        public readonly List<PlacedCard> Cards = new List<PlacedCard>();

        internal void AddPlacementIssue(string line)
        {
            Issues.Add(line);
            PlacementIssues.Add(line);
        }

        public override string ToString()
        {
            var lines = new List<string>
            {
                $"Applied {PieceCount} pieces and {DecorCount} decor under {LayoutApplier.RootName} ({CardCount} from catalog cards); {Expected?.BlockedCount ?? 0} blocked cells.",
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
    /// footprint cells so runtime triggers reproduce the layout. Pieces with a level catalog
    /// card take its placementScale, lift, footprint and cover; others are measured on the
    /// instance at their prefab scale. One Undo group; reapplying replaces only that root.
    /// </summary>
    public static class LayoutApplier
    {
        public const string RootName = "[Generated Layout]";
        public const string PiecesName = "Pieces";
        public const string DecorName = "Decor";
        /// <summary>Gap kept between a fitted collider and its footprint edge, meters.</summary>
        public const float ColliderInset = 0.1f;
        /// <summary>Name of the child holding the box a card piece's oversized colliders are replaced by.</summary>
        public const string FootprintColliderName = "Footprint Collider";
        // Rounding slack when checking a collider against its footprint, meters.
        private const float FitEpsilon = 1e-4f;

        /// <param name="cards">Where pieces' catalog cards come from; the project's reviewed pieces when null.</param>
        public static LayoutApplyResult Apply(LayoutDocument document, Scene scene, IGridRulesAdapter adapter = null,
            LevelCardSource cards = null)
        {
            adapter ??= LayoutAdapters.Find();
            if (!scene.IsValid() || !scene.isLoaded) throw new BridgeToolException("Open the target scene before applying a layout.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new BridgeToolException("Exit Play Mode before applying a layout.");
            if (!adapter.TryGetBoard(scene, out var board, out var boardError)) throw new BridgeToolException(boardError);
            if (board.Width != document.Width || board.Height != document.Height)
                throw new BridgeToolException($"Layout size {document.Width}x{document.Height} differs from the level's board {board.Width}x{board.Height}. Change one of them.");
            var prefabs = ResolvePrefabs(document);
            CheckEnemyAssets(document);
            // Cards are built before the scene is touched (a bad overrides file stops here).
            cards ??= LevelCardSource.ForProject();
            var pieceCards = prefabs.Keys.ToDictionary(guid => guid, cards.Find);

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Layout");
            var result = new LayoutApplyResult();
            try
            {
                return Build(document, scene, adapter, board, prefabs, pieceCards, undoGroup, result);
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
            GridBoard board, Dictionary<string, GameObject> prefabs, Dictionary<string, LevelCard> cards, int undoGroup,
            LayoutApplyResult result)
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
                var spec = Place(piece, prefabs[piece.PrefabGuid], cards[piece.PrefabGuid], piecesParent, true, scene, board, adapter, result);
                result.Specs[piece.LocalId] = spec;
                result.PieceCount++;
            }
            foreach (var decor in document.Decor)
            {
                Place(decor, prefabs[decor.PrefabGuid], cards[decor.PrefabGuid], decorParent, false, scene, board, adapter, result);
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

        private static PieceSpec Place(LayoutPiece piece, GameObject prefab, LevelCard card, Transform parent, bool blocker,
            Scene scene, GridBoard board, IGridRulesAdapter adapter, LayoutApplyResult result)
        {
            var frame = PieceGeometry.Frame(piece.Rotation);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = piece.LocalId;
            instance.transform.SetParent(parent, false);
            instance.transform.SetPositionAndRotation(Vector3.zero, frame * prefab.transform.localRotation);
            if (card == null) return PlaceMeasured(piece, instance, frame, blocker, board, adapter, result);
            result.CardCount++;
            return PlaceFromCard(piece, instance, card, frame, blocker, board, adapter, result);
        }

        // A piece with a catalog card: its scale, lift, footprint and cover come from the card. Colliders
        // that reach past the footprint are fitted before the piece hook (boxes shrunk, other shapes
        // replaced by one fitted box), so the hook configures what the game will see.
        private static PieceSpec PlaceFromCard(LayoutPiece piece, GameObject instance, LevelCard card, Quaternion frame,
            bool blocker, GridBoard board, IGridRulesAdapter adapter, LayoutApplyResult result)
        {
            instance.transform.localScale = ToVector3(card.PlacementScale);
            var cellSize = adapter.Rules.CellSize;
            var footprint = LevelCards.Footprint(card, cellSize, blocking: blocker);
            var size = footprint.RotatedSize(piece.Rotation);
            var center = FootprintCenter(adapter, board, piece.Anchor, size);
            // The bounds centre sits at the footprint centre, plus the card's footprintOffset (e.g. a porch reaching out).
            var offset = frame * (ToVector3(footprint.Center) - FootprintOffset(card));
            var lift = (float)card.Lift + (card.Role == LevelRole.Ground ? adapter.GroundLift : 0f);
            instance.transform.position = new Vector3(center.x - offset.x, board.FloorY + lift, center.z - offset.z);
            if (footprint.Approximate)
                result.Notes.Add($"APPROXIMATE {piece.Anchor} {piece.LocalId}: the card's box overstates the piece (skewed parts or a tilted root); footprint {footprint}");

            var cover = CoverLevel.None;
            if (blocker && card.Walls != null && adapter.Rules.SideWalls)
            {
                // A walled building: its cells stay walkable, its colliders stay as authored (sight), and the game
                // gets its walls instead of the piece hook. No collider fitting, no blocking collider expected.
                cover = card.Cover ?? CoverLevel.Full;
                adapter.ConfigureWalls(instance, card.Walls, new Vector3(center.x, board.FloorY, center.z), piece.Rotation);
                result.Cards.Add(new PlacedCard
                {
                    LocalId = piece.LocalId, Card = card, Anchor = piece.Anchor, Size = size, Rotation = piece.Rotation,
                    Blocking = true, Cover = cover,
                });
                return new PieceSpec(footprint.Width, footprint.Depth, cover, card.Walls);
            }
            if (blocker)
            {
                ContainSolidColliders(instance, piece, center, size, cellSize, result);
                cover = card.Cover ?? adapter.DescribeCover(instance);
                adapter.ConfigurePiece(instance, cover);
                // Anything the hook added (e.g. a box around the renderers) is fitted like a measured piece's.
                FitColliders(instance, piece, center, size, cellSize, adapter, result);
                if (!instance.GetComponentsInChildren<Collider>(true).Any(adapter.IsBlockingCollider))
                    result.AddPlacementIssue($"NO_BLOCKING_COLLIDER {piece.Anchor} {piece.LocalId}: the piece hook left no blocking collider");
            }
            result.Cards.Add(new PlacedCard
            {
                LocalId = piece.LocalId, Card = card, Anchor = piece.Anchor, Size = size, Rotation = piece.Rotation,
                Blocking = blocker, Cover = cover,
            });
            return footprint.ToSpec(cover);
        }

        // A piece without a card: measured on the instance at its prefab scale (the Phase 2 path).
        private static PieceSpec PlaceMeasured(LayoutPiece piece, GameObject instance, Quaternion frame, bool blocker,
            GridBoard board, IGridRulesAdapter adapter, LayoutApplyResult result)
        {
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

        /// <summary>The card's footprintOffset as a vector at layout rotation 0 (zero when it has none).</summary>
        public static Vector3 FootprintOffset(LevelCard card) => card.FootprintOffset.HasValue
            ? new Vector3((float)card.FootprintOffset.Value.X, 0f, (float)card.FootprintOffset.Value.Z)
            : Vector3.zero;

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
            var (limitMin, limitMax) = FootprintLimits(center, size, cellSize);
            foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
            {
                if (!adapter.IsBlockingCollider(collider)) continue;
                var (min, max) = WorldBox(PieceGeometry.ColliderCorners(collider));
                if (Inside(min, max, limitMin, limitMax)) continue;

                var path = RelativePath(instance.transform, collider.transform);
                if (!(collider is BoxCollider box) || !IsAxisAligned(box.transform))
                {
                    result.AddPlacementIssue($"FIT_UNSUPPORTED {piece.Anchor} {piece.LocalId}/{path}: {collider.GetType().Name} reaches past its footprint and only axis-aligned boxes can be fitted; neighbouring cells may block");
                    continue;
                }
                ShrinkBox(box, min, max, limitMin, limitMax, $"{piece.Anchor} {piece.LocalId}/{path}", result);
            }
        }

        // Card pieces, before the piece hook: every solid collider (enabled, not a trigger) that reaches past
        // the footprint is fitted. Axis-aligned boxes are shrunk; other shapes (mesh colliders, rotated boxes)
        // are disabled and replaced by one box on a "Footprint Collider" child: their combined bounds clipped to
        // the footprint inset, full height. The hook then configures that box like any solid collider.
        private static void ContainSolidColliders(GameObject instance, LayoutPiece piece, Vector3 center, Cell size,
            float cellSize, LayoutApplyResult result)
        {
            var (limitMin, limitMax) = FootprintLimits(center, size, cellSize);
            var replacedMin = Vector3.positiveInfinity;
            var replacedMax = Vector3.negativeInfinity;
            var replaced = new List<string>();
            foreach (var collider in instance.GetComponentsInChildren<Collider>(false))
            {
                if (!collider.enabled || collider.isTrigger) continue;
                var (min, max) = WorldBox(PieceGeometry.ColliderCorners(collider));
                if (Inside(min, max, limitMin, limitMax)) continue;

                var path = RelativePath(instance.transform, collider.transform);
                if (collider is BoxCollider box && IsAxisAligned(box.transform))
                {
                    ShrinkBox(box, min, max, limitMin, limitMax, $"{piece.Anchor} {piece.LocalId}/{path}", result);
                    continue;
                }
                collider.enabled = false;
                replacedMin = Vector3.Min(replacedMin, min);
                replacedMax = Vector3.Max(replacedMax, max);
                replaced.Add($"{collider.GetType().Name} {path} ({max.x - min.x:0.00} x {max.z - min.z:0.00} m)");
            }
            if (replaced.Count == 0) return;

            var where = $"{piece.Anchor} {piece.LocalId}";
            var fittedMin = new Vector3(Mathf.Max(replacedMin.x, limitMin.x), replacedMin.y, Mathf.Max(replacedMin.z, limitMin.y));
            var fittedMax = new Vector3(Mathf.Min(replacedMax.x, limitMax.x), replacedMax.y, Mathf.Min(replacedMax.z, limitMax.y));
            if (fittedMin.x >= fittedMax.x || fittedMin.z >= fittedMax.z)
            {
                result.AddPlacementIssue($"FIT_EMPTY {where}: disabled {string.Join(", ", replaced)}, which lie outside the footprint");
                return;
            }

            var holder = new GameObject(FootprintColliderName);
            holder.layer = instance.layer;
            holder.transform.SetParent(instance.transform, false);
            if (!IsAxisAligned(holder.transform))
            {
                Object.DestroyImmediate(holder);
                result.AddPlacementIssue($"FIT_UNSUPPORTED {where}: disabled {string.Join(", ", replaced)}, but the piece's root is not turned in quarter turns, so no fitted box could replace them");
                return;
            }
            var fitted = holder.AddComponent<BoxCollider>();
            var (localMin, localMax) = WorldBox(PieceGeometry.BoxCorners(holder.transform.worldToLocalMatrix,
                (fittedMin + fittedMax) * 0.5f, fittedMax - fittedMin));
            fitted.center = (localMin + localMax) * 0.5f;
            fitted.size = localMax - localMin;
            var fittedSize = fittedMax - fittedMin;
            result.Notes.Add($"FIT_BOX {where}: {string.Join(", ", replaced)} disabled; {FootprintColliderName} box {fittedSize.x:0.00} x {fittedSize.z:0.00} m");
        }

        // The footprint's X/Z rectangle inset by ColliderInset on every side (min and max as X, Z pairs).
        private static (Vector2 min, Vector2 max) FootprintLimits(Vector3 center, Cell size, float cellSize)
        {
            var halfX = size.X * cellSize * 0.5f - ColliderInset;
            var halfZ = size.Y * cellSize * 0.5f - ColliderInset;
            return (new Vector2(center.x - halfX, center.z - halfZ), new Vector2(center.x + halfX, center.z + halfZ));
        }

        private static bool Inside(Vector3 min, Vector3 max, Vector2 limitMin, Vector2 limitMax) =>
            min.x >= limitMin.x - FitEpsilon && min.z >= limitMin.y - FitEpsilon &&
            max.x <= limitMax.x + FitEpsilon && max.z <= limitMax.y + FitEpsilon;

        private static void ShrinkBox(BoxCollider box, Vector3 min, Vector3 max, Vector2 limitMin, Vector2 limitMax,
            string where, LayoutApplyResult result)
        {
            var fittedMin = new Vector3(Mathf.Max(min.x, limitMin.x), min.y, Mathf.Max(min.z, limitMin.y));
            var fittedMax = new Vector3(Mathf.Min(max.x, limitMax.x), max.y, Mathf.Min(max.z, limitMax.y));
            if (fittedMin.x >= fittedMax.x || fittedMin.z >= fittedMax.z)
            {
                result.AddPlacementIssue($"FIT_EMPTY {where}: collider lies outside its footprint");
                return;
            }

            var oldSize = max - min;
            var (localMin, localMax) = WorldBox(PieceGeometry.BoxCorners(box.transform.worldToLocalMatrix,
                (fittedMin + fittedMax) * 0.5f, fittedMax - fittedMin));
            box.center = (localMin + localMax) * 0.5f;
            box.size = localMax - localMin;
            var newSize = fittedMax - fittedMin;
            result.Notes.Add($"FIT {where}: {oldSize.x:0.00} x {oldSize.z:0.00} m -> {newSize.x:0.00} x {newSize.z:0.00} m");
        }

        public static Vector3 ToVector3(Vec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

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
