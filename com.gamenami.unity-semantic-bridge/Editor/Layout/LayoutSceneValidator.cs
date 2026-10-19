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
    /// colliders its line-of-sight test hits (IGridRulesAdapter.SightLayers, IgnoredBySight). Pieces with a
    /// level catalog card also get the scale and plausibility checks (heights against the game's
    /// GridRules.CoverHeights, ground runs, building fronts).
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
            report.Issues.AddRange(LayoutValidator.Scale(applied.Cards, adapter.Rules.CoverHeights));
            report.Issues.AddRange(LayoutValidator.Plausibility(report.Grid, applied.Cards));
            var physicsIssues = new List<string>();
            var physics = adapter.BakeOccupancy(scene, physicsIssues);
            report.Issues.AddRange(physicsIssues.Select(line => LayoutIssue.FromLine(line)));
            report.Issues.AddRange(LayoutValidator.Consistency(applied.Expected, physics, GatherSight(scene, board, adapter)));
            report.Issues.AddRange(WallSight(applied.Cards, board, adapter));
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
            // The scale check reports card pieces' scales cell-addressed, with heights.
            foreach (var line in bake.OffScaleIssues) readIssues.Remove(line);
            // The encounter check reports deploy cells itself.
            report.Issues.InsertRange(0, readIssues.Where(line => !line.StartsWith("DEPLOY_")).Select(line => LayoutIssue.FromLine(line)));
            report.Issues.AddRange(LayoutValidator.Scale(bake.Cards, adapter.Rules.CoverHeights));
            report.Issues.AddRange(LayoutValidator.Plausibility(bake.Physics, bake.Cards.Where(c => !c.Scale.HasValue)));
            report.Issues.AddRange(bake.PhysicsIssues.Select(line => LayoutIssue.FromLine(line)));
            report.Issues.AddRange(LayoutValidator.Consistency(bake.Derived, bake.Physics, GatherSight(scene, board, adapter)));
            report.Issues.AddRange(WallSight(bake.Cards.Where(c => !c.Scale.HasValue), board, adapter));
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

        /// <summary>Heights above the floor, meters, at which a Full-cover wall must stop sight, and a door must not.</summary>
        public static readonly float[] WallProbeHeights = { 1.0f, 1.7f };
        /// <summary>Probe positions along a side from its midpoint, meters.</summary>
        public static readonly float[] WallProbeOffsets = { -0.5f, 0f, 0.5f };

        /// <summary>
        /// Walls on cell sides against the game's sight colliders: rays across each Full-cover wall side of a
        /// walled building (at 1.0 and 1.7 m, three points along it) must hit something the sight test does not
        /// ignore, or the wall gives cover that sight passes through (WALL_SIGHT_GAP: add a sightFill side, or
        /// check the model's collision mesh); a ray at 1.0 m through a door's point (or midpoint) must not
        /// (DOOR_COLLIDER: a closed door or boards in the doorway, which units would walk through). Warnings.
        /// </summary>
        public static List<LayoutIssue> WallSight(IEnumerable<PlacedCard> cards, GridBoard board, IGridRulesAdapter adapter)
        {
            var issues = new List<LayoutIssue>();
            if (!adapter.Rules.SideWalls) return issues;
            Physics.SyncTransforms();
            var cellSize = adapter.Rules.CellSize;
            foreach (var building in cards)
            {
                if (!building.Blocking || building.Card?.Walls == null) continue;
                var outline = building.Card.Walls.Rotate(building.Rotation);
                foreach (var side in outline.Sides)
                {
                    var cell = building.Anchor + side.Cell;
                    var step = GridSides.Step(side.Side);
                    var normal = new Vector3(step.X, 0f, step.Y);
                    var along = new Vector3(Mathf.Abs(step.Y), 0f, Mathf.Abs(step.X));
                    var midpoint = adapter.CellToWorld(cell, board) + normal * (cellSize * 0.5f);
                    var where = $"{building.LocalId}: {GridSides.Name(side.Side)} of {cell}";
                    if (side.Kind == SideKind.Wall && outline.WallCover == CoverLevel.Full)
                    {
                        var gaps = WallProbeHeights.Where(h => WallProbeOffsets.Any(a =>
                            !BlocksSight(midpoint + along * a + Vector3.up * h, normal, adapter))).ToList();
                        if (gaps.Count > 0)
                            issues.Add(new LayoutIssue("WALL_SIGHT_GAP", cell,
                                $"{where} is a Full-cover wall, but sight passes it at {string.Join(" and ", gaps.Select(h => h.ToString("0.0") + " m"))}; " +
                                "add the side to the card's sightFill or check the collision mesh", IssueSeverity.Warning));
                    }
                    else if (side.Kind == SideKind.Door)
                    {
                        var offset = outline.DoorPoints.Where(p => p.Cell.Equals(side.Cell) && p.Side == side.Side)
                            .Select(p => (float)p.Offset).DefaultIfEmpty(0f).First();
                        if (BlocksSight(midpoint + along * offset + Vector3.up * WallProbeHeights[0], normal, adapter))
                            issues.Add(new LayoutIssue("DOOR_COLLIDER", cell,
                                $"{where} is a door, but a collider fills the doorway at 1.0 m (a closed door or boards?); units would walk through it",
                                IssueSeverity.Warning));
                    }
                }
            }
            return issues;
        }

        // A segment across a side (0.6 m each way) hits a collider the game's sight test does not ignore.
        private static bool BlocksSight(Vector3 point, Vector3 normal, IGridRulesAdapter adapter)
        {
            const float reach = 0.6f;
            return Physics.RaycastAll(point - normal * reach, normal, reach * 2f, adapter.SightLayers, QueryTriggerInteraction.Ignore)
                .Any(hit => !adapter.IgnoredBySight(hit.collider));
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
