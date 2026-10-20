using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Level metrics (plan Phase 6): small hand-made boards whose answers are known.
    public class LayoutMetricsTests
    {
        private static GridRules Rules(MetricSettings metrics = null) => new GridRules
        {
            CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true, SideWalls = true, GridSight = true, StepOut = true,
            Metrics = metrics ?? new MetricSettings { VisionRange = 8, MovePerTurn = 10, NearSteps = 3 },
        };

        private static LayoutGrid Board(int w, int h, GridRules rules = null) => new LayoutGrid(w, h, rules ?? Rules());

        private static void Wall(LayoutGrid grid, int x0, int y0, int x1, int y1, CoverLevel cover = CoverLevel.Full)
        {
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                    grid.Block(new Cell(x, y), "wall", cover);
        }

        private static List<LayoutEnemySpawn> Enemy(int x, int y) =>
            new List<LayoutEnemySpawn> { new LayoutEnemySpawn("raider", "p", "v", new Cell(x, y), 180) };

        private static MetricsReport Measure(LayoutGrid grid, Cell deploy, int ex, int ey) =>
            LayoutMetrics.Measure(grid, new List<Cell> { deploy }, Enemy(ex, ey));

        private static MetricLine One(MetricsReport report, string code) => report.WithCode(code).Single();

        [Test]
        public void DistanceIsStepsAndTurns()
        {
            var report = Measure(Board(12, 3), new Cell(0, 1), 11, 1);
            var line = One(report, "DISTANCE");
            Assert.AreEqual(1.1, line.Value, 1e-9);
            StringAssert.Contains("11 steps from deploy #1 (1.1 turns)", line.Message);
        }

        [Test]
        public void UnreachableEnemyIsReportedNotMeasured()
        {
            var grid = Board(9, 3);
            Wall(grid, 4, 0, 4, 2);
            var report = Measure(grid, new Cell(0, 1), 8, 1);
            StringAssert.Contains("unreachable", One(report, "DISTANCE").Message);
            Assert.IsEmpty(report.WithCode("ROUTES"));
            Assert.IsEmpty(report.WithCode("OPEN_GROUND"));
        }

        [Test]
        public void CorridorHasOneRouteAndADividedRoadTwo()
        {
            var corridor = Board(11, 3);
            Wall(corridor, 1, 0, 9, 0);
            Wall(corridor, 1, 2, 9, 2);
            Assert.AreEqual(1, One(Measure(corridor, new Cell(0, 1), 10, 1), "ROUTES").Value);

            var divided = Board(11, 5);
            Wall(divided, 2, 2, 8, 2);
            var routes = One(Measure(divided, new Cell(0, 2), 10, 2), "ROUTES");
            Assert.AreEqual(2, routes.Value, routes.Message);
        }

        [Test]
        public void ARouteMuchLongerThanTheShortestDoesNotCount()
        {
            // A short lane along y = 1 and a long detour round a wall: the detour is more than 1.5x the lane.
            var grid = Board(11, 9);
            Wall(grid, 1, 0, 9, 0);
            Wall(grid, 1, 2, 9, 2);
            Wall(grid, 1, 3, 1, 7);
            Wall(grid, 9, 3, 9, 7);
            var routes = One(Measure(grid, new Cell(0, 1), 10, 1), "ROUTES");
            Assert.AreEqual(1, routes.Value, routes.Message);
        }

        [Test]
        public void CoverNearCountsOnlyCoverFacingTheSquad()
        {
            // Half cover between the enemy and the squad counts; the same wall behind the enemy does not.
            var facing = Board(9, 9);
            Wall(facing, 2, 5, 6, 5, CoverLevel.Half);
            var front = One(Measure(facing, new Cell(4, 0), 4, 6), "COVER_NEAR");
            Assert.Greater(front.Value, 0, front.Message);

            var behind = Board(9, 9);
            Wall(behind, 2, 7, 6, 7, CoverLevel.Half);
            var back = One(Measure(behind, new Cell(4, 0), 4, 6), "COVER_NEAR");
            Assert.AreEqual(0, back.Value, back.Message);
            StringAssert.Contains("0 Full, 0 Half", back.Message);
        }

        [Test]
        public void CoverNearDeployFacesTheEnemies()
        {
            var grid = Board(9, 9);
            Wall(grid, 2, 2, 6, 2, CoverLevel.Full);
            var line = One(Measure(grid, new Cell(4, 1), 4, 8), "COVER_NEAR_DEPLOY");
            Assert.Greater(line.Value, 0, line.Message);
            StringAssert.StartsWith("squad:", line.Message);
        }

        [Test]
        public void OpenGroundIsShorterWithCoverOnTheWay()
        {
            var open = Board(13, 3, Rules(new MetricSettings { VisionRange = 20 }));
            var bare = One(Measure(open, new Cell(0, 1), 12, 1), "OPEN_GROUND");
            Assert.AreEqual(12, bare.Value, bare.Message); // the start and the 11 cells before the enemy

            var covered = Board(13, 3, Rules(new MetricSettings { VisionRange = 20 }));
            covered.Block(new Cell(6, 0), "crate", CoverLevel.Half);
            covered.Block(new Cell(6, 2), "crate", CoverLevel.Half);
            var line = One(Measure(covered, new Cell(0, 1), 12, 1), "OPEN_GROUND");
            Assert.Less(line.Value, bare.Value, line.Message);
        }

        [Test]
        public void OpenGroundIgnoresCellsTheEnemyCannotSee()
        {
            // Beyond the enemy's 4-tile vision nothing is exposed: the best route keeps to the edge row until it is
            // 3 cells out ((26,0) is 3.16 tiles away; (25,0) is 4.12).
            var grid = Board(30, 3, Rules(new MetricSettings { VisionRange = 4 }));
            var line = One(Measure(grid, new Cell(0, 1), 29, 1), "OPEN_GROUND");
            Assert.AreEqual(3, line.Value, line.Message);
        }

        [Test]
        public void CoverSpacingCountsCoverWhetherOrNotTheEnemySees()
        {
            // Vision 4: OPEN_GROUND sees only the last cells, COVER_SPACING the whole way.
            var bare = Board(13, 3, Rules(new MetricSettings { VisionRange = 4 }));
            var report = Measure(bare, new Cell(0, 1), 12, 1);
            Assert.AreEqual(12, One(report, "COVER_SPACING").Value, One(report, "COVER_SPACING").Message);
            Assert.Less(One(report, "OPEN_GROUND").Value, 12);

            var covered = Board(13, 3, Rules(new MetricSettings { VisionRange = 4 }));
            covered.Block(new Cell(6, 0), "crate", CoverLevel.Half);
            covered.Block(new Cell(6, 2), "crate", CoverLevel.Half);
            var line = One(Measure(covered, new Cell(0, 1), 12, 1), "COVER_SPACING");
            Assert.Less(line.Value, 12, line.Message);
            StringAssert.Contains("moves)", line.Message);
        }

        [Test]
        public void DeadSpaceIsWhatNoReasonableRouteCrosses()
        {
            // A lane along the bottom of an open field: the far side of the field is off every route.
            var field = Board(12, 12);
            var whole = (new Cell(0, 0), new Cell(11, 11));
            var open = LayoutMetrics.Measure(field, new List<Cell> { new Cell(0, 0) }, Enemy(11, 0), null, whole);
            Assert.Greater(One(open, "DEAD_SPACE").Value, 0.2, One(open, "DEAD_SPACE").Message);

            var corridor = Board(11, 3);
            Wall(corridor, 1, 0, 9, 0);
            Wall(corridor, 1, 2, 9, 2);
            var line = One(Measure(corridor, new Cell(0, 1), 10, 1), "DEAD_SPACE");
            Assert.Less(line.Value, 0.2, line.Message);
        }

        [Test]
        public void SightlinesAreAllVisibleOnAnOpenFieldAndCutByAWall()
        {
            var field = One(Measure(Board(9, 9), new Cell(0, 0), 8, 8), "SIGHTLINES");
            StringAssert.Contains("1-3 100%", field.Message);
            Assert.IsFalse(field.Message.Contains("%") && field.Message.Split(',').Any(p => p.Contains("%") && !p.Contains("100%")), field.Message);

            var walled = Board(9, 9);
            Wall(walled, 0, 4, 7, 4);
            var cut = One(Measure(walled, new Cell(0, 0), 8, 8), "SIGHTLINES");
            Assert.Less(cut.Value, field.Value, cut.Message);
        }

        [Test]
        public void RegionDefaultsToUnitsPlusMarginAndCanBeGiven()
        {
            var grid = Board(40, 40);
            var report = Measure(grid, new Cell(10, 10), 14, 12);
            Assert.AreEqual(new Cell(6, 6), report.RegionMin);
            Assert.AreEqual(new Cell(18, 16), report.RegionMax);

            var given = LayoutMetrics.Measure(grid, new List<Cell> { new Cell(10, 10) }, Enemy(14, 12), null, (new Cell(-3, 0), new Cell(50, 4)));
            Assert.AreEqual(new Cell(0, 0), given.RegionMin);
            Assert.AreEqual(new Cell(39, 4), given.RegionMax);
            StringAssert.StartsWith("LAYOUT_METRICS region 0,0-39,4 (200 cells, 200 open)", given.Format());
        }

        [Test]
        public void TargetsPutValuesOutsideThemFirst()
        {
            var metrics = new MetricSettings { VisionRange = 20 };
            metrics.Targets["OPEN_GROUND"] = new MetricTarget(null, 5);
            metrics.Targets["ROUTES"] = new MetricTarget(1, null);
            var report = Measure(Board(13, 3, Rules(metrics)), new Cell(0, 1), 12, 1);
            Assert.AreEqual(1, report.OutsideCount);
            var text = report.Format();
            StringAssert.Contains("; 1 outside their targets\nOutside targets (1):\nOPEN_GROUND 12,1 ", text);
            StringAssert.Contains("[target <= 5]", text);
            StringAssert.Contains("[target >= 1]", text);
        }

        [Test]
        public void ReportIsDeterministicAndHasNoTargetSectionWithoutTargets()
        {
            var grid = Board(16, 16);
            Wall(grid, 5, 5, 9, 5);
            Wall(grid, 3, 9, 4, 9, CoverLevel.Half);
            var a = Measure(grid, new Cell(1, 1), 12, 13).Format(0);
            var b = Measure(grid, new Cell(1, 1), 12, 13).Format(0);
            Assert.AreEqual(a, b);
            StringAssert.DoesNotContain("Outside targets", a);
            StringAssert.Contains("Values (8):", a);
        }

        [Test]
        public void RoutesGoThroughADoor()
        {
            // A room with one door: one way in.
            var grid = Board(12, 12);
            for (var x = 4; x <= 7; x++)
            {
                grid.SetSide(new Cell(x, 4), GridSide.MinusY, SideKind.Wall, CoverLevel.Full, "room");
                grid.SetSide(new Cell(x, 7), GridSide.PlusY, SideKind.Wall, CoverLevel.Full, "room");
            }
            for (var y = 4; y <= 7; y++)
            {
                grid.SetSide(new Cell(4, y), GridSide.MinusX, SideKind.Wall, CoverLevel.Full, "room");
                grid.SetSide(new Cell(7, y), GridSide.PlusX, y == 5 ? SideKind.Door : SideKind.Wall, CoverLevel.Full, "room");
            }
            var report = Measure(grid, new Cell(0, 0), 5, 6);
            Assert.AreEqual(1, One(report, "ROUTES").Value, One(report, "ROUTES").Message);
            StringAssert.DoesNotContain("unreachable", One(report, "DISTANCE").Message);
        }
    }
}
