using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    public sealed class LayoutBakeResult
    {
        public LayoutDocument Document;
        public readonly Dictionary<string, PieceSpec> Specs = new Dictionary<string, PieceSpec>();
        /// <summary>
        /// Every issue: scene content the layout cannot express (off-grid, scaled, off-board, not a
        /// prefab, spawns without assets), footprint problems, and the adapter's physics bake issues.
        /// </summary>
        public readonly List<string> Issues = new List<string>();
        /// <summary>The adapter's physics bake issues alone (also in Issues).</summary>
        public readonly List<string> PhysicsIssues = new List<string>();
        /// <summary>Occupancy derived from the baked pieces' footprints.</summary>
        public LayoutGrid Derived;
        /// <summary>Occupancy the game itself would compute (adapter physics bake).</summary>
        public LayoutGrid Physics;
        /// <summary>
        /// Instances with a level catalog card: the baked ones, and those left out for a scale other than the
        /// card's (Scale set; their OFFGRID_SCALE line is in OffScaleIssues), for the scale and plausibility validators.
        /// </summary>
        public readonly List<PlacedCard> Cards = new List<PlacedCard>();
        /// <summary>The scene instance of each baked card piece in Cards (not those left out for their scale).</summary>
        public readonly Dictionary<PlacedCard, GameObject> CardInstances = new Dictionary<PlacedCard, GameObject>();
        /// <summary>The OFFGRID_SCALE lines of card pieces (also in Issues); the scale validator reports them cell-addressed.</summary>
        public readonly List<string> OffScaleIssues = new List<string>();
        /// <summary>Cells where Derived and Physics disagree.</summary>
        public List<string> OccupancyDiff = new List<string>();

        public override string ToString()
        {
            var lines = new List<string> { LayoutJson.Serialize(Document).TrimEnd() };
            lines.Add($"Issues ({Issues.Count}):");
            lines.AddRange(Issues);
            lines.Add($"Occupancy differences, layout footprints vs game physics bake ({OccupancyDiff.Count}):");
            lines.AddRange(OccupancyDiff);
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Reads a scene (hand-made or applied) back into a layout document. Prefab instances
    /// the adapter classifies as blockers become pieces, the rest decor; deploy cells and enemy
    /// spawns come from the level through the adapter. Pieces with a level catalog card are read
    /// at the card's placementScale and footprint (height, i.e. lift, is not checked); others are
    /// measured at their prefab's scale. Anything that is rotated off a quarter turn, scaled away
    /// from its card (or prefab), between cells, off the board or not a prefab instance is
    /// reported, never snapped.
    /// </summary>
    public static class LayoutBaker
    {
        /// <summary>How far a footprint centre may sit from cell alignment, meters.</summary>
        public const float PositionTolerance = 0.05f;

        /// <param name="cards">Where pieces' catalog cards come from; the project's reviewed pieces when null.</param>
        public static LayoutBakeResult Bake(Scene scene, IGridRulesAdapter adapter = null, LevelCardSource cards = null)
        {
            adapter ??= LayoutAdapters.Find();
            if (!scene.IsValid() || !scene.isLoaded) throw new BridgeToolException("Open the scene before baking its layout.");
            if (!adapter.TryGetBoard(scene, out var board, out var boardError)) throw new BridgeToolException(boardError);

            cards ??= LevelCardSource.ForProject();
            var result = new LayoutBakeResult { Document = new LayoutDocument(board.Width, board.Height) };
            result.Document.Deploy.AddRange(adapter.ReadDeployCells(scene));
            var visited = new HashSet<Transform>();
            var ids = new HashSet<string>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var candidate in PrefabInstanceRoots(root))
                {
                    visited.Add(candidate.transform);
                    BakeInstance(candidate, board, adapter, cards, result, ids);
                }
            ReportLooseBlockers(scene, visited, adapter, result);
            BakeEnemies(scene, adapter, result, ids);

            var buildIssues = new List<LayoutIssue>();
            result.Derived = LayoutGrid.Build(result.Document,
                p => result.Specs.TryGetValue(p.LocalId, out var s) ? s : (PieceSpec?)null, adapter.Rules, buildIssues);
            result.Issues.AddRange(buildIssues.Select(i => i.ToString()));
            var physicsIssues = new List<string>();
            result.Physics = adapter.BakeOccupancy(scene, physicsIssues);
            result.Issues.AddRange(physicsIssues);
            result.PhysicsIssues.AddRange(physicsIssues);
            result.OccupancyDiff = result.Derived.DiffOccupancy(result.Physics);
            return result;
        }

        // Outermost active prefab instances; children of an instance belong to it.
        private static IEnumerable<GameObject> PrefabInstanceRoots(GameObject go)
        {
            if (!go.activeInHierarchy) yield break;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                yield return go;
                yield break;
            }
            foreach (Transform child in go.transform)
                foreach (var found in PrefabInstanceRoots(child.gameObject))
                    yield return found;
        }

        private static void BakeInstance(GameObject instance, GridBoard board, IGridRulesAdapter adapter,
            LevelCardSource cards, LayoutBakeResult result, HashSet<string> ids)
        {
            var role = adapter.Classify(instance);
            if (role == PieceRole.Ignore) return;
            // Scenery wholly off the board is not part of the level, however it is placed.
            if (role == PieceRole.Decor && IsOffBoard(instance, board, adapter)) return;
            var name = instance.name;
            var nearCell = adapter.WorldToCell(instance.transform.position);
            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                result.Issues.Add($"MISSING_PREFAB ~{nearCell} {name}: prefab asset not found");
                return;
            }

            var turns = PieceGeometry.QuarterTurns(instance.transform.rotation, asset.transform.localRotation);
            if (!turns.HasValue)
            {
                result.Issues.Add($"OFFGRID_ROTATION ~{nearCell} {name}: rotation {instance.transform.eulerAngles} is not a quarter turn of its prefab");
                return;
            }
            var guid = AssetDatabase.AssetPathToGUID(path);
            var card = cards.Find(guid);
            var scale = instance.transform.lossyScale;
            var expectedScale = card != null ? LayoutApplier.ToVector3(card.PlacementScale) : asset.transform.localScale;
            var frame = PieceGeometry.Frame(turns.Value);
            var cellSize = adapter.Rules.CellSize;
            if ((scale - expectedScale).magnitude > 0.001f * Mathf.Max(1f, expectedScale.magnitude))
            {
                if (card == null)
                {
                    result.Issues.Add($"OFFGRID_SCALE ~{nearCell} {name}: scale {Format(scale)} differs from the prefab's {Format(expectedScale)}; a layout piece has the prefab's scale");
                    return;
                }
                var line = $"OFFGRID_SCALE ~{nearCell} {name}: scale {Format(scale)} differs from its card's placementScale {Format(expectedScale)}; a layout piece has its card's scale";
                result.Issues.Add(line);
                result.OffScaleIssues.Add(line);
                // Left out of the layout, but placed where its card footprint at this scale would put it, for the scale report.
                var actual = new Vec3(scale.x, scale.y, scale.z);
                var scaled = LevelCards.Footprint(card, cellSize, actual, role == PieceRole.Blocker);
                var scaledSize = scaled.RotatedSize(turns.Value);
                result.Cards.Add(new PlacedCard
                {
                    LocalId = name, Card = card, Size = scaledSize, Rotation = turns.Value, Blocking = role == PieceRole.Blocker,
                    Anchor = AnchorOf(instance, frame, LayoutApplier.ToVector3(scaled.Center), scaledSize, cellSize, adapter),
                    Cover = role == PieceRole.Blocker ? adapter.DescribeCover(instance) : CoverLevel.None, Scale = actual,
                });
                return;
            }

            int width, depth;
            Vector3 boundsCenter;
            var walled = card?.Walls != null && adapter.Rules.SideWalls && role == PieceRole.Blocker;
            if (card != null)
            {
                // The card's footprint (claimed cells for an overridden one), as apply placed it: the footprint
                // centre sits footprintOffset away from the bounds centre.
                var footprint = LevelCards.Footprint(card, cellSize, blocking: role == PieceRole.Blocker);
                (width, depth) = (footprint.Width, footprint.Depth);
                boundsCenter = LayoutApplier.ToVector3(footprint.Center) - LayoutApplier.FootprintOffset(card);
            }
            else
            {
                var bounds = role == PieceRole.Blocker
                    ? PieceGeometry.MeasureColliders(instance, frame, adapter.IsBlockingCollider)
                    : PieceGeometry.MeasureRenderers(instance, frame);
                if (!bounds.HasValue)
                {
                    if (role == PieceRole.Blocker)
                        result.Issues.Add($"NO_BLOCKING_COLLIDER ~{nearCell} {name}: classified as a blocker without a blocking collider");
                    return;
                }
                (width, depth) = PieceGeometry.Footprint(bounds.Value, cellSize);
                boundsCenter = bounds.Value.Center;
            }

            var cover = walled ? card.Cover ?? CoverLevel.Full
                : role == PieceRole.Blocker ? adapter.DescribeCover(instance) : CoverLevel.None;
            var spec = new PieceSpec(width, depth, cover, walled ? card.Walls : null);
            var size = spec.RotatedSize(turns.Value);
            var center = instance.transform.position + frame * boundsCenter;
            var anchor = AnchorOf(instance, frame, boundsCenter, size, cellSize, adapter);
            var expected = LayoutApplier.FootprintCenter(adapter, board, anchor, size);
            var offset = new Vector2(center.x - expected.x, center.z - expected.z);
            if (offset.magnitude > PositionTolerance)
            {
                result.Issues.Add($"OFFGRID_POSITION ~{anchor} {name}: {size.X}x{size.Y} footprint centre is {offset.x:0.00}, {offset.y:0.00} m from cell alignment; not snapped");
                return;
            }

            var last = new Cell(anchor.X + size.X - 1, anchor.Y + size.Y - 1);
            var inside = anchor.X >= 0 && anchor.Y >= 0 && last.X < board.Width && last.Y < board.Height;
            var outside = last.X < 0 || last.Y < 0 || anchor.X >= board.Width || anchor.Y >= board.Height;
            if (!inside)
            {
                // Scenery wholly off the board is not part of the level; straddling pieces are.
                if (role == PieceRole.Blocker || !outside)
                    result.Issues.Add($"OFF_BOARD {anchor} {name}: {size.X}x{size.Y} footprint leaves the {board.Width}x{board.Height} board");
                return;
            }

            var localId = UniqueId(name, ids);
            var piece = new LayoutPiece(localId, guid, anchor, turns.Value);
            if (walled) CheckWalls(instance, card, anchor, name, adapter, result);
            if (role == PieceRole.Blocker)
            {
                result.Document.Pieces.Add(piece);
                result.Specs[localId] = spec;
            }
            else
            {
                result.Document.Decor.Add(piece);
            }
            if (card != null)
            {
                var placed = new PlacedCard
                {
                    LocalId = localId, Card = card, Anchor = anchor, Size = size, Rotation = turns.Value,
                    Blocking = role == PieceRole.Blocker, Cover = cover,
                };
                result.Cards.Add(placed);
                result.CardInstances[placed] = instance;
            }
        }

        /// <summary>How far a door point may sit from its card's, meters.</summary>
        public const double DoorPointTolerance = 0.05;

        // A walled building's walls in the scene must be its card's: same sides and kinds, same door points.
        private static void CheckWalls(GameObject instance, LevelCard card, Cell anchor, string name, IGridRulesAdapter adapter,
            LayoutBakeResult result)
        {
            if (!adapter.TryReadWalls(instance, out var walls))
            {
                result.Issues.Add($"WALLS_MISSING {anchor} {name}: its card has walls, the building in the scene has none; reapply the layout");
                return;
            }
            var expected = card.Walls;
            var differences = new List<string>();
            if (!expected.ToLines().SequenceEqual(walls.ToLines()))
                differences.Add($"outline [{string.Join(" / ", walls.ToLines())}], card [{string.Join(" / ", expected.ToLines())}]");
            else
            {
                if (walls.WallCover != expected.WallCover || walls.WindowCover != expected.WindowCover)
                    differences.Add($"cover wall {walls.WallCover} window {walls.WindowCover}, card wall {expected.WallCover} window {expected.WindowCover}");
                foreach (var side in expected.Sides.Where(s => s.Kind == SideKind.Door))
                {
                    var want = expected.DoorPoints.Where(p => p.Cell.Equals(side.Cell) && p.Side == side.Side).Select(p => p.Offset).DefaultIfEmpty(0).First();
                    var got = walls.DoorPoints.Where(p => p.Cell.Equals(side.Cell) && p.Side == side.Side).Select(p => p.Offset).DefaultIfEmpty(0).First();
                    if (System.Math.Abs(want - got) > DoorPointTolerance)
                        differences.Add($"door point on {side.Cell} {GridSides.Name(side.Side)} at {got:0.00} m, card {want:0.00} m");
                }
            }
            if (differences.Count > 0)
                result.Issues.Add($"WALLS_DIFF {anchor} {name}: {string.Join("; ", differences)}");
        }

        // The min-corner cell of a footprint whose bounds centre sits at boundsCenter in the piece's frame.
        private static Cell AnchorOf(GameObject instance, Quaternion frame, Vector3 boundsCenter, Cell size, float cellSize,
            IGridRulesAdapter adapter)
        {
            var center = instance.transform.position + frame * boundsCenter;
            var halfSpan = new Vector3((size.X - 1) * cellSize * 0.5f, 0f, (size.Y - 1) * cellSize * 0.5f);
            return adapter.WorldToCell(center - halfSpan);
        }

        // Spawns keep the level's order. Levels store no ids, so unnamed spawns become enemy1, enemy2, ...
        // A spawn without a profile or visual asset cannot be written as JSON; it is reported.
        private static void BakeEnemies(Scene scene, IGridRulesAdapter adapter, LayoutBakeResult result, HashSet<string> ids)
        {
            var spawns = adapter.ReadEnemySpawns(scene);
            for (var i = 0; i < spawns.Count; i++)
            {
                var spawn = spawns[i];
                var id = UniqueId(string.IsNullOrWhiteSpace(spawn.LocalId) ? $"enemy{i + 1}" : spawn.LocalId, ids);
                if (string.IsNullOrEmpty(spawn.ProfileGuid) || string.IsNullOrEmpty(spawn.VisualGuid))
                {
                    result.Issues.Add($"ENEMY_NO_ASSET {spawn.Cell} {id}: spawn #{i + 1} has no {(string.IsNullOrEmpty(spawn.ProfileGuid) ? "unit profile" : "visual")} asset; left out");
                    continue;
                }
                result.Document.Enemies.Add(new LayoutEnemySpawn(id, spawn.ProfileGuid, spawn.VisualGuid, spawn.Cell, spawn.Facing));
            }
        }

        // Blocking colliders that no prefab instance owns cannot be written as pieces.
        private static void ReportLooseBlockers(Scene scene, HashSet<Transform> instanceRoots, IGridRulesAdapter adapter,
            LayoutBakeResult result)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var collider in root.GetComponentsInChildren<Collider>(false))
                {
                    if (!adapter.IsBlockingCollider(collider) || HasAncestorIn(collider.transform, instanceRoots)) continue;
                    result.Issues.Add($"NOT_PREFAB ~{adapter.WorldToCell(collider.bounds.center)} {collider.name}: blocking collider outside any prefab instance; not representable in a layout");
                }
        }

        private static bool IsOffBoard(GameObject instance, GridBoard board, IGridRulesAdapter adapter)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return true;
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            var half = adapter.Rules.CellSize * 0.5f;
            var min = adapter.CellToWorld(new Cell(0, 0), board) - new Vector3(half, 0f, half);
            var max = adapter.CellToWorld(new Cell(board.Width - 1, board.Height - 1), board) + new Vector3(half, 0f, half);
            return bounds.max.x <= min.x || bounds.max.z <= min.z || bounds.min.x >= max.x || bounds.min.z >= max.z;
        }

        private static bool HasAncestorIn(Transform transform, HashSet<Transform> roots)
        {
            for (var t = transform; t != null; t = t.parent)
                if (roots.Contains(t)) return true;
            return false;
        }

        private static string UniqueId(string name, HashSet<string> ids)
        {
            var id = string.IsNullOrWhiteSpace(name) ? "piece" : name;
            var candidate = id;
            for (var n = 2; !ids.Add(candidate); n++) candidate = $"{id}#{n}";
            return candidate;
        }

        private static string Format(Vector3 v) => $"({v.x:0.###}, {v.y:0.###}, {v.z:0.###})";
    }
}
