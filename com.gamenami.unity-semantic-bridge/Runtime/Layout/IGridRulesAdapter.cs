using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>The board a scene's level defines.</summary>
    public sealed class GridBoard
    {
        public int Width;
        public int Height;
        /// <summary>World Y that pieces stand on.</summary>
        public float FloorY;
        /// <summary>The asset or component holding the level's settings (deploy cells, enemy spawns); recorded for Undo.</summary>
        public Object LevelOwner;
    }

    public enum PieceRole
    {
        /// <summary>Not part of the layout (managers, cameras, UI, units, lights).</summary>
        Ignore,
        /// <summary>Blocks movement and may give cover.</summary>
        Blocker,
        /// <summary>Visual or ground piece that never blocks.</summary>
        Decor,
    }

    /// <summary>
    /// What a game tells USB about its grid. Implement it once in the game project
    /// (Assembly-CSharp can, since this assembly is auto-referenced); USB Editor finds
    /// the single implementation through TypeCache. Implementations need a public
    /// parameterless constructor.
    /// </summary>
    public interface IGridRulesAdapter
    {
        /// <summary>Cell size, neighbour rule and cover rule for the pure model.</summary>
        GridRules Rules { get; }

        /// <summary>Board size and floor height of the level in <paramref name="scene"/>; false with a reason when it has none.</summary>
        bool TryGetBoard(Scene scene, out GridBoard board, out string error);

        /// <summary>World position of a cell's centre on the floor.</summary>
        Vector3 CellToWorld(Cell cell, GridBoard board);

        /// <summary>The cell containing a world position (X/Z only).</summary>
        Cell WorldToCell(Vector3 position);

        /// <summary>
        /// Meters the applier raises every ground-role piece (catalog card role ground: roads, sidewalks)
        /// above the floor, against z-fighting with the terrain and parts modelled below the pivot (kerb
        /// skirts). One value for the whole game, so ground pieces stay level with each other; 0 for none.
        /// </summary>
        float GroundLift { get; }

        /// <summary>Layers whose colliders the game's line-of-sight test hits.</summary>
        LayerMask SightLayers { get; }

        /// <summary>True for a collider on a sight layer that the game's LOS test still ignores (units, low cover).</summary>
        bool IgnoredBySight(Collider collider);

        /// <summary>Whether this collider makes the game treat the cells it overlaps as blocked.</summary>
        bool IsBlockingCollider(Collider collider);

        /// <summary>Whether a scene prefab instance (or prefab contents root) is a layout blocker, decor, or not part of the layout.</summary>
        PieceRole Classify(GameObject root);

        /// <summary>Cover the piece gives when placed as a blocker.</summary>
        CoverLevel DescribeCover(GameObject root);

        /// <summary>
        /// Piece hook, called by the layout applier on a fresh blocker instance before its
        /// blocking colliders are fitted: add whatever the game needs for occupancy and cover. After
        /// the hook, at least one collider must satisfy IsBlockingCollider. For a piece with a catalog
        /// card the instance is already scaled and placed, <paramref name="cover"/> is the card's cover
        /// when it has one (None for movement-only blockers), and colliders the card footprint could not
        /// contain are already disabled and replaced by one box fitted to the footprint, which the hook
        /// should treat like any other solid collider.
        /// </summary>
        void ConfigurePiece(GameObject instance, CoverLevel cover);

        /// <summary>Occupancy as the game would see it at runtime, computed in Edit Mode; issues are report lines.</summary>
        LayoutGrid BakeOccupancy(Scene scene, List<string> issues);

        IReadOnlyList<Cell> ReadDeployCells(Scene scene);

        /// <summary>Replaces the level's deploy cells. The caller records Undo on GridBoard.LevelOwner and marks it dirty.</summary>
        void WriteDeployCells(Scene scene, IReadOnlyList<Cell> cells);

        /// <summary>
        /// The level's enemy spawns in order. LocalId may be null (levels store no ids; the baker
        /// names them); a GUID is null when the level references no asset there.
        /// </summary>
        IReadOnlyList<LayoutEnemySpawn> ReadEnemySpawns(Scene scene);

        /// <summary>
        /// Replaces the level's enemy spawns. Throws, before changing anything, when a GUID is not
        /// an asset of the type the game expects. The caller records Undo on GridBoard.LevelOwner.
        /// </summary>
        void WriteEnemySpawns(Scene scene, IReadOnlyList<LayoutEnemySpawn> spawns);

        /// <summary>
        /// Walls on cell sides (plan Phase W; GridRules.SideWalls): called by the applier on a walled building
        /// (a card with a walls outline) instead of ConfigurePiece, after it is scaled and placed. Write the
        /// game's wall data: <paramref name="outline"/> at layout rotation 0, its frame centred at
        /// <paramref name="frameCentre"/> (world, floor height), turned by <paramref name="layoutRotation"/>
        /// clockwise quarter turns. Its colliders are left as authored (no fitting), so sight keeps them;
        /// they must not block cells. Games without side walls need not implement it.
        /// </summary>
        void ConfigureWalls(GameObject instance, WallOutline outline, Vector3 frameCentre, int layoutRotation) { }

        /// <summary>
        /// The walls a placed building carries in the scene (what ConfigureWalls wrote, or a hand-made
        /// equivalent), at layout rotation 0 with door points; false when it has none.
        /// </summary>
        bool TryReadWalls(GameObject instance, out WallOutline outline)
        {
            outline = null;
            return false;
        }
    }
}
