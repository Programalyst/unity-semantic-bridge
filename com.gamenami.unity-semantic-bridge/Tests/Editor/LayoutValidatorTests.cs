using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Pure grid-tier validators: deliberately broken layout fixtures, each with the report
    // line it must produce. No scene, no UnityEngine types.
    public class LayoutValidatorTests
    {
        private const string Block = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // 1x1, full cover
        private const string Long = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";  // 3x1, half cover
        private const string Profile = "cccccccccccccccccccccccccccccccc";
        private const string Visual = "dddddddddddddddddddddddddddddddd";
        private const string Unknown = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        private static readonly GridRules Rules = new GridRules { CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true };

        private static PieceSpec? Resolve(LayoutPiece piece) => piece.PrefabGuid switch
        {
            Block => new PieceSpec(1, 1, CoverLevel.Full),
            Long => new PieceSpec(3, 1, CoverLevel.Half),
            _ => null,
        };

        // A 6x6 board with deploy at the bottom left and one enemy at the top right.
        private static string Layout(string pieces = "", string deploy = "[0,0],[1,0]", string enemies = null, string decor = "") =>
            "{\"v\":1,\"size\":[6,6],\"pieces\":[" + pieces + "],\"deploy\":[" + deploy + "],\"enemies\":[" +
            (enemies ?? Enemy("e1", 5, 5, 180)) + "],\"decor\":[" + decor + "]}";

        private static string Piece(string id, string guid, int x, int y, int rot = 0) => $"[\"{id}\",\"{guid}\",{x},{y},{rot}]";
        private static string Enemy(string id, int x, int y, string facing) => $"[\"{id}\",\"{Profile}\",\"{Visual}\",{x},{y},{facing}]";
        private static string Enemy(string id, int x, int y, int facing) => Enemy(id, x, y, facing.ToString());

        private static LayoutReport Validate(string json, GridRules rules = null) =>
            LayoutValidator.Validate(LayoutJson.Parse(json), Resolve, rules ?? Rules);

        private static string[] Lines(LayoutReport report) => report.Issues.Select(i => i.ToString()).ToArray();

        // A wall ring around 3,3 seals it; the corner rule keeps diagonals from slipping through.
        private static readonly string Ring = string.Join(",", new[]
        {
            Piece("r1", Long, 2, 2), Piece("r2", Long, 2, 4), Piece("r3", Block, 2, 3), Piece("r4", Block, 4, 3),
        });

        [Test]
        public void CleanLayoutHasNoIssues()
        {
            var report = Validate(Layout(Piece("wall", Long, 2, 2) + "," + Piece("box", Block, 4, 4)));
            CollectionAssert.IsEmpty(Lines(report));
            Assert.AreEqual("LAYOUT_REPORT 0 errors, 0 warnings", report.Format());
        }

        public static IEnumerable<TestCaseData> BrokenFixtures()
        {
            TestCaseData Case(string name, string json, string line, IssueSeverity severity = IssueSeverity.Error) =>
                new TestCaseData(json, line, severity).SetName("Fixture_" + name);

            // Structural
            yield return Case("PieceLeavesBoard", Layout(Piece("w", Long, 4, 2)), "OUT_OF_BOUNDS 6,2 w (3,1 at 4,2)");
            yield return Case("PiecesOverlap", Layout(Piece("a", Long, 1, 2) + "," + Piece("b", Block, 3, 2)), "OVERLAP 3,2 b overlaps a");
            yield return Case("UnknownPrefab", Layout(Piece("x", Unknown, 2, 2)), $"UNKNOWN_PIECE 2,2 x: no footprint for prefab {Unknown}");
            yield return Case("DecorOffBoard", Layout(decor: Piece("road", Unknown, -1, 2)), "DECOR_OUT_OF_BOUNDS -1,2 road is anchored off the 6x6 board", IssueSeverity.Warning);
            // Encounter
            yield return Case("EnemyOnWall", Layout(Piece("w", Long, 2, 2), enemies: Enemy("e1", 3, 2, 0)), "ENEMY_BLOCKED 3,2 enemy e1 is under w");
            yield return Case("EnemyOffBoard", Layout(enemies: Enemy("e1", 6, 1, 0)), "ENEMY_OUT_OF_BOUNDS 6,1 enemy e1 is off the 6x6 board");
            yield return Case("DeployOnWall", Layout(Piece("b", Block, 1, 0)), "DEPLOY_BLOCKED 1,0 deploy #2 is under b");
            yield return Case("DeployOffBoard", Layout(deploy: "[0,0],[0,9]"), "DEPLOY_OUT_OF_BOUNDS 0,9 deploy #2 is off the 6x6 board");
            yield return Case("EnemyOnDeployCell", Layout(enemies: Enemy("e1", 1, 0, 0)), "CELL_SHARED 1,0 enemy e1 starts on the same cell as deploy #2");
            yield return Case("TwoEnemiesOneCell", Layout(enemies: Enemy("e1", 5, 5, 0) + "," + Enemy("e2", 5, 5, 90)), "CELL_SHARED 5,5 enemy e2 starts on the same cell as enemy e1");
            yield return Case("FacingFullTurn", Layout(enemies: Enemy("e1", 5, 5, 360)), "FACING_RANGE 5,5 enemy e1 faces 360 degrees; use 0-359");
            yield return Case("FacingNegative", Layout(enemies: Enemy("e1", 5, 5, -90)), "FACING_RANGE 5,5 enemy e1 faces -90 degrees; use 0-359");
            yield return Case("FacingOffAxis", Layout(enemies: Enemy("e1", 5, 5, "22.5")), "FACING_OFF_AXIS 5,5 enemy e1 faces 22.5 degrees, between the eight grid directions", IssueSeverity.Warning);
            // Reachability
            yield return Case("SealedPocket", Layout(Ring), "POCKET 3,3 1 open cell unreachable from the deploy cells", IssueSeverity.Warning);
            yield return Case("EnemyInPocket", Layout(Ring, enemies: Enemy("e1", 3, 3, 0)), "ENEMY_UNREACHABLE 3,3 enemy e1 cannot be reached from the deploy cells");
            yield return Case("PocketNamesItsEnemy", Layout(Ring, enemies: Enemy("e1", 3, 3, 0)), "POCKET 3,3 1 open cell unreachable from the deploy cells (holds e1)", IssueSeverity.Warning);
            yield return Case("NoDeploy", Layout(deploy: ""), "NO_DEPLOY no deploy cell is on an open board cell; reachability not checked");
        }

        [TestCaseSource(nameof(BrokenFixtures))]
        public void BrokenFixtureReportsItsIssue(string json, string line, IssueSeverity severity)
        {
            var report = Validate(json);
            var match = report.Issues.Where(i => i.ToString() == line).ToList();
            Assert.AreEqual(1, match.Count, "Expected the line once in:\n" + report.Format(0));
            Assert.AreEqual(severity, match[0].Severity);
        }

        [Test]
        public void DiagonalGapSealsOnlyUnderTheCornerRule()
        {
            // A diagonal of blocks from 0,3 to 3,0 cuts the bottom-left corner off.
            var diagonal = string.Join(",", Enumerable.Range(0, 4).Select(i => Piece("d" + i, Block, i, 3 - i)));
            var json = Layout(diagonal);
            var strict = Validate(json);
            Assert.AreEqual(new[] { "POCKET 4,0 26 open cells unreachable from the deploy cells (holds e1)" },
                strict.WithCode("POCKET").Select(i => i.ToString()).ToArray(), strict.Format(0));
            Assert.AreEqual(1, strict.WithCode("ENEMY_UNREACHABLE").Count());

            var cutting = Validate(json, new GridRules { CellSize = 2f, DiagonalMoves = true, NoCornerCutting = false });
            CollectionAssert.IsEmpty(Lines(cutting));
        }

        [Test]
        public void FourWayRulesNeedOrthogonalGaps()
        {
            // Two blocks touching at a corner leave the board connected under 8-way and 4-way rules.
            var json = Layout(Piece("a", Block, 1, 2) + "," + Piece("b", Block, 2, 1));
            CollectionAssert.IsEmpty(Lines(Validate(json)));
            CollectionAssert.IsEmpty(Lines(Validate(json, new GridRules { DiagonalMoves = false })));
        }

        [Test]
        public void ReportCapsLinesAndDrawsTheMap()
        {
            var enemies = string.Join(",", Enumerable.Range(0, 5).Select(i => Enemy("e" + i, i, 5, 30)));
            var report = Validate(Layout(Ring, enemies: enemies));
            Assert.AreEqual(0, report.ErrorCount);
            Assert.AreEqual(6, report.WarningCount); // five off-axis facings and the pocket
            var text = report.Format(maxLines: 2, ascii: true);
            StringAssert.StartsWith("LAYOUT_REPORT 0 errors, 6 warnings\nWarnings (6):\nFACING_OFF_AXIS 0,5", text);
            StringAssert.Contains("... 4 more lines not shown (cap 2)\n", text);
            StringAssert.EndsWith(
                "EEEEE.\n" +
                "..hhh.\n" +
                "..#-#.\n" +
                "..hhh.\n" +
                "......\n" +
                "AA....", text);
        }

        [Test]
        public void ConsistencyComparesOccupancyAndSight()
        {
            var layout = new LayoutGrid(4, 4, Rules);
            layout.Block(new Cell(1, 1), "wall", CoverLevel.Full);
            layout.Block(new Cell(2, 1), "wall", CoverLevel.Full);
            var physics = new LayoutGrid(4, 4, Rules);
            physics.Block(new Cell(1, 1), "Wall SF", CoverLevel.Full);
            physics.Block(new Cell(3, 3), "Crate", CoverLevel.Half);
            var sight = new[]
            {
                new SightCollider("Rocks/Rock_SET_01", false, new[] { new Cell(3, 2), new Cell(2, 2), new Cell(3, 1) }),
                new SightCollider("Wall SF", true, new[] { new Cell(1, 1) }),
                new SightCollider("Tower", false, new Cell[0]),
            };
            var lines = LayoutValidator.Consistency(layout, physics, sight).Select(i => i.ToString()).ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "MISSING_BLOCK 2,1 expected wall",
                "EXTRA_BLOCK 3,3 Crate",
                "SIGHT_NO_OBSTACLE 3,1 Rocks/Rock_SET_01 blocks sight over 3 cells but not movement",
            }, lines);
            Assert.AreEqual(IssueSeverity.Warning, LayoutValidator.Consistency(layout, layout, sight).Single().Severity);
        }

        [Test]
        public void ForGridChecksAGameOccupancyBake()
        {
            var physics = new LayoutGrid(5, 5, Rules);
            foreach (var cell in new[] { new Cell(0, 2), new Cell(1, 2), new Cell(2, 2), new Cell(3, 2), new Cell(4, 2) })
                physics.Block(cell, "Fence", CoverLevel.Half);
            var enemies = new List<LayoutEnemySpawn> { new LayoutEnemySpawn("enemy1", Profile, Visual, new Cell(2, 4), 180) };
            var report = LayoutValidator.ForGrid(physics, new[] { new Cell(0, 0) }, enemies);
            CollectionAssert.AreEqual(new[]
            {
                "ENEMY_UNREACHABLE 2,4 enemy enemy1 cannot be reached from the deploy cells",
                "POCKET 0,3 10 open cells unreachable from the deploy cells (holds enemy1)",
            }, Lines(report));
            Assert.AreEqual(10, report.Unreachable.Count);
        }
    }
}
