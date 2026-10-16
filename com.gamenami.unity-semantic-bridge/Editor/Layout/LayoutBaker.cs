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
        /// <summary>Scene content the layout cannot express (off-grid, scaled, off-board, not a prefab).</summary>
        public readonly List<string> Issues = new List<string>();
        /// <summary>Occupancy derived from the baked pieces' footprints.</summary>
        public LayoutGrid Derived;
        /// <summary>Occupancy the game itself would compute (adapter physics bake).</summary>
        public LayoutGrid Physics;
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
    /// Reads a scene (hand-made or compiled) back into a layout document. Prefab instances
    /// the adapter classifies as blockers become pieces, the rest decor. Anything that is
    /// rotated off a quarter turn, scaled away from its prefab, between cells, off the board
    /// or not a prefab instance is reported, never snapped.
    /// </summary>
    public static class LayoutBaker
    {
        /// <summary>How far a footprint centre may sit from cell alignment, meters.</summary>
        public const float PositionTolerance = 0.05f;

        public static LayoutBakeResult Bake(Scene scene, IGridRulesAdapter adapter = null)
        {
            adapter ??= LayoutAdapters.Find();
            if (!scene.IsValid() || !scene.isLoaded) throw new BridgeToolException("Open the scene before baking its layout.");
            if (!adapter.TryGetBoard(scene, out var board, out var boardError)) throw new BridgeToolException(boardError);

            var result = new LayoutBakeResult { Document = new LayoutDocument(board.Width, board.Height) };
            result.Document.Deploy.AddRange(adapter.ReadDeployCells(scene));
            var visited = new HashSet<Transform>();
            var ids = new HashSet<string>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var candidate in PrefabInstanceRoots(root))
                {
                    visited.Add(candidate.transform);
                    BakeInstance(candidate, board, adapter, result, ids);
                }
            ReportLooseBlockers(scene, visited, adapter, result);

            var buildIssues = new List<LayoutIssue>();
            result.Derived = LayoutGrid.Build(result.Document,
                p => result.Specs.TryGetValue(p.LocalId, out var s) ? s : (PieceSpec?)null, adapter.Rules, buildIssues);
            result.Issues.AddRange(buildIssues.Select(i => i.ToString()));
            var physicsIssues = new List<string>();
            result.Physics = adapter.BakeOccupancy(scene, physicsIssues);
            result.Issues.AddRange(physicsIssues);
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
            LayoutBakeResult result, HashSet<string> ids)
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
            var scale = instance.transform.lossyScale;
            var prefabScale = asset.transform.localScale;
            if ((scale - prefabScale).magnitude > 0.001f * Mathf.Max(1f, prefabScale.magnitude))
            {
                result.Issues.Add($"OFFGRID_SCALE ~{nearCell} {name}: scale {Format(scale)} differs from the prefab's {Format(prefabScale)}; a layout piece has the prefab's scale");
                return;
            }

            var frame = PieceGeometry.Frame(turns.Value);
            var bounds = role == PieceRole.Blocker
                ? PieceGeometry.MeasureColliders(instance, frame, adapter.IsBlockingCollider)
                : PieceGeometry.MeasureRenderers(instance, frame);
            if (!bounds.HasValue)
            {
                if (role == PieceRole.Blocker)
                    result.Issues.Add($"NO_BLOCKING_COLLIDER ~{nearCell} {name}: classified as a blocker without a blocking collider");
                return;
            }

            var cellSize = adapter.Rules.CellSize;
            var (width, depth) = PieceGeometry.Footprint(bounds.Value, cellSize);
            var cover = role == PieceRole.Blocker ? adapter.DescribeCover(instance) : CoverLevel.None;
            var spec = new PieceSpec(width, depth, cover);
            var size = spec.RotatedSize(turns.Value);
            var center = instance.transform.position + frame * bounds.Value.Center;
            var halfSpan = new Vector3((size.X - 1) * cellSize * 0.5f, 0f, (size.Y - 1) * cellSize * 0.5f);
            var anchor = adapter.WorldToCell(center - halfSpan);
            var expected = LayoutCompiler.FootprintCenter(adapter, board, anchor, size);
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
            var piece = new LayoutPiece(localId, AssetDatabase.AssetPathToGUID(path), anchor, turns.Value);
            if (role == PieceRole.Blocker)
            {
                result.Document.Pieces.Add(piece);
                result.Specs[localId] = spec;
            }
            else
            {
                result.Document.Decor.Add(piece);
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
