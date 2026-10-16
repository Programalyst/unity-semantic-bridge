using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>
    /// Runs the layout validators against a scene: the grid tier (LayoutValidator) plus the
    /// physics tier, which compares the layout with the game's own occupancy bake and with the
    /// colliders its line-of-sight test hits (IGridRulesAdapter.SightLayers, IgnoredBySight).
    /// </summary>
    public static class LayoutSceneValidator
    {
        /// <summary>Height band above the floor, meters, in which a collider counts as blocking sight (units' body and head points, tile vision checks).</summary>
        public const float SightBandBottom = 0.5f;
        public const float SightBandTop = 2.5f;

        /// <summary>
        /// An applied layout: structural checks on the draft (with the apply's placement issues),
        /// encounter and reachability on its footprints, and the physics tier on the scene.
        /// </summary>
        public static LayoutReport ValidateApplied(LayoutDocument document, LayoutApplyResult applied, Scene scene,
            IGridRulesAdapter adapter = null)
        {
            adapter ??= LayoutAdapters.Find();
            var board = RequireBoard(scene, adapter);
            var report = LayoutValidator.Validate(document,
                p => applied.Specs.TryGetValue(p.LocalId, out var s) ? s : (PieceSpec?)null, adapter.Rules);
            report.Issues.InsertRange(0, applied.PlacementIssues.Select(line => LayoutIssue.FromLine(line)));
            var physicsIssues = new List<string>();
            var physics = adapter.BakeOccupancy(scene, physicsIssues);
            report.Issues.AddRange(physicsIssues.Select(line => LayoutIssue.FromLine(line)));
            report.Issues.AddRange(LayoutValidator.Consistency(applied.Expected, physics, GatherSight(scene, board, adapter)));
            return report;
        }

        /// <summary>
        /// Any scene, hand-made or applied. The bake's report lines (off-grid, scaled, off-board,
        /// loose blockers) are the structural tier. Encounter and reachability run on the game's
        /// own occupancy bake, since pieces the layout cannot express still block at runtime.
        /// </summary>
        public static LayoutReport ValidateScene(Scene scene, IGridRulesAdapter adapter = null, LayoutBakeResult bake = null)
        {
            adapter ??= LayoutAdapters.Find();
            var board = RequireBoard(scene, adapter);
            bake ??= LayoutBaker.Bake(scene, adapter);

            var report = LayoutValidator.ForGrid(bake.Physics, bake.Document.Deploy, bake.Document.Enemies);
            var readIssues = new List<string>(bake.Issues);
            foreach (var line in bake.PhysicsIssues) readIssues.Remove(line);
            // The encounter check reports deploy cells itself.
            report.Issues.InsertRange(0, readIssues.Where(line => !line.StartsWith("DEPLOY_")).Select(line => LayoutIssue.FromLine(line)));
            report.Issues.AddRange(bake.PhysicsIssues.Select(line => LayoutIssue.FromLine(line)));
            report.Issues.AddRange(LayoutValidator.Consistency(bake.Derived, bake.Physics, GatherSight(scene, board, adapter)));
            return report;
        }

        /// <summary>
        /// Colliders the game's sight test would hit over each board cell (an overlap box per cell
        /// across the sight band, on the adapter's sight layers, triggers and IgnoredBySight
        /// colliders excluded), grouped by the prefab instance (or GameObject) that owns them.
        /// Non-convex mesh colliders are found where their surface crosses a cell.
        /// </summary>
        public static List<SightCollider> GatherSight(Scene scene, GridBoard board, IGridRulesAdapter adapter)
        {
            Physics.SyncTransforms();
            var cellSize = adapter.Rules.CellSize;
            var halfExtents = new Vector3(cellSize * 0.5f - 0.001f, (SightBandTop - SightBandBottom) * 0.5f, cellSize * 0.5f - 0.001f);
            var bandCentre = board.FloorY + (SightBandBottom + SightBandTop) * 0.5f;
            var buffer = new Collider[256];
            var owners = new Dictionary<(GameObject owner, bool obstacle), SortedSet<(int y, int x)>>();
            for (var y = 0; y < board.Height; y++)
                for (var x = 0; x < board.Width; x++)
                {
                    var cell = new Cell(x, y);
                    var centre = adapter.CellToWorld(cell, board);
                    centre.y = bandCentre;
                    var count = Physics.OverlapBoxNonAlloc(centre, halfExtents, buffer, Quaternion.identity, adapter.SightLayers,
                        QueryTriggerInteraction.Ignore);
                    for (var i = 0; i < count; i++)
                    {
                        var collider = buffer[i];
                        if (collider.gameObject.scene != scene || adapter.IgnoredBySight(collider)) continue;
                        var owner = PrefabUtility.GetOutermostPrefabInstanceRoot(collider.gameObject);
                        var obstacle = adapter.IsBlockingCollider(collider) ||
                                       owner != null && adapter.Classify(owner) == PieceRole.Blocker;
                        var key = (owner != null ? owner : collider.gameObject, obstacle);
                        if (!owners.TryGetValue(key, out var cells)) owners[key] = cells = new SortedSet<(int, int)>();
                        cells.Add((y, x));
                    }
                }
            return owners
                .Select(pair => new SightCollider(HierarchyPath(pair.Key.owner.transform), pair.Key.obstacle,
                    pair.Value.Select(c => new Cell(c.x, c.y))))
                .ToList();
        }

        private static GridBoard RequireBoard(Scene scene, IGridRulesAdapter adapter)
        {
            if (!scene.IsValid() || !scene.isLoaded) throw new BridgeToolException("Open the scene before validating its layout.");
            if (!adapter.TryGetBoard(scene, out var board, out var error)) throw new BridgeToolException(error);
            return board;
        }

        private static string HierarchyPath(Transform transform)
        {
            var path = transform.name;
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }
    }
}
