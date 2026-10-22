using System;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Grid levels (plan H2a): upper-level cells, ladders with their tile cost and the ladder rule, on LayoutGrid. Pure.
    public class GridLevelTests
    {
        private static readonly GridRules Rules = new GridRules
        {
            CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true, MaxStepHeight = 0.5f, LadderTiles = 2,
        };

        // A 3 x 3 roof (x 4-6, y 3-5) at 5.2 m over an 8 x 8 board, its ladder from (3,4) to (4,4) on level 1.
        private static LayoutGrid Roofed()
        {
            var grid = new LayoutGrid(8, 8, Rules);
            for (var x = 4; x <= 6; x++)
                for (var y = 3; y <= 5; y++)
                    grid.AddUpperCell(new LevelCell(x, y, 1), 5.2f);
            grid.AddLadder(new LevelCell(3, 4), new LevelCell(4, 4, 1));
            return grid;
        }

        [Test]
        public void UpperLevelsAreSparseAndClosedAtTheirEdge()
        {
            var grid = Roofed();
            Assert.IsTrue(grid.Exists(new LevelCell(0, 0)) && grid.Exists(new LevelCell(4, 4, 1)));
            Assert.IsFalse(grid.Exists(new LevelCell(3, 4, 1)) || grid.Exists(new LevelCell(4, 4, 2)));
            Assert.AreEqual(5.2f, grid.FloorAt(new LevelCell(5, 5, 1)));
            Assert.AreEqual("4,4@1", new LevelCell(4, 4, 1).ToString());
            Assert.AreEqual("4,4", new LevelCell(4, 4).ToString());
            Assert.IsFalse(grid.CanStep(new LevelCell(4, 4), new LevelCell(4, 4, 1)), "no step between levels without a ladder");
            Assert.IsFalse(grid.CanStep(new LevelCell(4, 4, 1), new LevelCell(3, 3, 1)), "no step off the roof");
            Assert.IsTrue(grid.CanStep(new LevelCell(4, 3, 1), new LevelCell(5, 4, 1)));
            Assert.AreEqual(6, grid.Neighbours(new LevelCell(4, 4, 1)).Count(), "5 roof neighbours and the ladder");
            Assert.AreEqual(2, grid.Neighbours(new LevelCell(4, 4, 1)).Single(n => n.cell.Level == 0).tiles);
            Assert.AreEqual(1, grid.Ladders.Count);
            Assert.Throws<ArgumentException>(() => grid.AddLadder(new LevelCell(0, 0), new LevelCell(1, 0)));
            Assert.Throws<ArgumentException>(() => grid.AddLadder(new LevelCell(0, 0), new LevelCell(0, 0, 1)), "both ends must exist");
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.AddUpperCell(new LevelCell(8, 0, 1), 1f));
        }

        [Test]
        public void StepsCrossLaddersAtTheirCostAndOnlyWithTheLadderRule()
        {
            var grid = Roofed();
            var steps = grid.StepsFrom(new[] { new LevelCell(0, 4) });
            Assert.AreEqual(3, steps[new LevelCell(3, 4)]);
            Assert.AreEqual(5, steps[new LevelCell(4, 4, 1)], "3 tiles and a 2-tile ladder");
            Assert.AreEqual(7, steps[new LevelCell(6, 4, 1)]);
            Assert.AreEqual(64 + 9, steps.Count);
            Assert.IsFalse(grid.StepsFrom(new[] { new LevelCell(0, 4) }, maxSteps: 6).ContainsKey(new LevelCell(6, 4, 1)));

            var walking = grid.StepsFrom(new[] { new LevelCell(0, 4) }, canUseLadder: false);
            Assert.AreEqual(64, walking.Count, "a unit that can't climb keeps to the ground");
            Assert.IsFalse(grid.CanStep(new LevelCell(3, 4), new LevelCell(4, 4, 1), canUseLadder: false));
            Assert.AreEqual(4, grid.StepsFrom(new[] { new LevelCell(4, 4, 1) })[new LevelCell(1, 4)], "down the ladder and two tiles");

            grid.Block(new Cell(3, 4), "crate", CoverLevel.Half);
            Assert.IsFalse(grid.StepsFrom(new[] { new LevelCell(0, 4) }).ContainsKey(new LevelCell(4, 4, 1)), "a blocked ladder foot");
        }

        [Test]
        public void TheStepRuleHoldsOnUpperLevels()
        {
            var grid = new LayoutGrid(4, 4, Rules);
            grid.AddUpperCell(new LevelCell(0, 0, 1), 3f);
            grid.AddUpperCell(new LevelCell(1, 0, 1), 3.6f);
            grid.AddUpperCell(new LevelCell(0, 1, 1), 3.5f);
            grid.AddUpperCell(new LevelCell(1, 1, 1), 3.5f);
            Assert.IsFalse(grid.CanStep(new LevelCell(0, 0, 1), new LevelCell(1, 0, 1)), "0.6 m up");
            Assert.IsTrue(grid.CanStep(new LevelCell(0, 0, 1), new LevelCell(0, 1, 1)), "0.5 m up");
            Assert.IsFalse(grid.CanStep(new LevelCell(0, 0, 1), new LevelCell(1, 1, 1)), "the diagonal passes the 0.6 m side");
        }

        // H2b: a walled building's card carries its levels and ladders in its walls object.
        private const string RoofJson = "{\"outline\":[\"+-+-+-+\",\"|     |\",\"+ + + +\",\"|     |\",\"+-+D+-+\"]," +
                                        "\"levels\":[{\"height\":3.5,\"cells\":[[0,0,2,0],[0,1,1,1]]}],\"ladders\":[[2,2,0,2,1,1]]}";

        [Test]
        public void WallsCarryLevelsAndLaddersThroughJsonAndRotation()
        {
            // The ladder's top (2,1) must be a level cell; this level leaves it out, so the parse fails.
            var error = Assert.Throws<FormatException>(() => WallOutline.FromJson(JToken.Parse(RoofJson)));
            StringAssert.Contains("is not a cell of level 1", error.Message);

            var json = RoofJson.Replace("[0,1,1,1]", "[0,1,2,1]");
            var walls = WallOutline.FromJson(JToken.Parse(json));
            Assert.AreEqual(1, walls.Levels.Count);
            Assert.AreEqual(6, walls.Levels[0].Cells().Count);
            Assert.AreEqual(3.5, walls.Levels[0].Height);
            Assert.AreEqual(new LadderLink(new Cell(2, 2), 0, new Cell(2, 1), 1), walls.Ladders.Single());
            Assert.IsTrue(JToken.DeepEquals(walls.ToJson(), WallOutline.FromJson(walls.ToJson()).ToJson()), "JSON round trip");

            // Turned once clockwise in a 3 x 2 frame: (x, y) -> (y, 2 - x); the ladder foot outside the frame turns too.
            var turned = walls.Rotate(1);
            CollectionAssert.AreEquivalent(walls.Levels[0].Cells().Select(c => new Cell(c.Y, 2 - c.X)), turned.Levels[0].Cells());
            Assert.AreEqual(new LadderLink(new Cell(2, 0), 0, new Cell(1, 0), 1), turned.Ladders.Single());
            Assert.AreEqual(walls.Ladders.Single(), turned.Rotate(3).Ladders.Single(), "four turns come back");

            Assert.Throws<FormatException>(() => WallOutline.FromJson(JToken.Parse(json.Replace("[[2,2,0,2,1,1]]", "[[2,3,0,2,1,1]]"))),
                "ladder ends must be neighbours");
            Assert.Throws<FormatException>(() => WallOutline.FromJson(JToken.Parse(json.Replace("[[2,2,0,2,1,1]]", "[[2,1,1,2,1,1]]"))),
                "a ladder joins two levels");
            Assert.Throws<FormatException>(() => WallOutline.FromJson(JToken.Parse(json.Replace("[0,0,2,0]", "[0,0,3,0]"))),
                "level cells stay in the frame");
            Assert.Throws<FormatException>(() => WallOutline.FromJson(JToken.Parse(json.Replace("\"height\":3.5", "\"height\":0"))));
        }

        [Test]
        public void LevelRimsRoundTripAndTurnWithTheOutline()
        {
            var json = RoofJson.Replace("[0,1,1,1]", "[0,1,2,1]").Replace("\"height\":3.5,", "\"height\":3.5,\"rim\":{\"+x\":0.3,\"-y\":0.1},");
            var walls = WallOutline.FromJson(JToken.Parse(json));
            var level = walls.Levels.Single();
            Assert.AreEqual(0.3, level.RimOn(GridSide.PlusX));
            Assert.AreEqual(0.1, level.RimOn(GridSide.MinusY));
            Assert.AreEqual(0, level.RimOn(GridSide.PlusY), "no rim where none is given");
            Assert.IsTrue(JToken.DeepEquals(walls.ToJson(), WallOutline.FromJson(walls.ToJson()).ToJson()), "JSON round trip");
            var turned = walls.Rotate(1).Levels.Single();
            Assert.AreEqual(0.3, turned.RimOn(GridSides.Rotate(GridSide.PlusX, 1)));
            Assert.AreEqual(0.1, turned.RimOn(GridSides.Rotate(GridSide.MinusY, 1)));
            Assert.AreEqual(2, turned.Rim.Count);
            Assert.Throws<FormatException>(() => WallOutline.FromJson(JToken.Parse(json.Replace("0.3", "-0.3"))), "a rim is not negative");
            Assert.Throws<FormatException>(() => WallOutline.FromJson(JToken.Parse(json.Replace("\"+x\"", "\"up\""))), "rim sides are +x, -x, +y, -y");
        }

        [Test]
        public void LadderOffsetsRoundTripAndTurnLikeDoorPoints()
        {
            var walls = WallOutline.FromJson(JToken.Parse(RoofJson.Replace("[0,1,1,1]", "[0,1,2,1]").Replace("[[2,2,0,2,1,1]]", "[[2,2,0,2,1,1,0.4]]")));
            var ladder = walls.Ladders.Single();
            Assert.AreEqual(0.4, ladder.Offset);
            Assert.AreEqual(GridSide.MinusY, ladder.Side, "from (2,2) down to (2,1)");
            Assert.AreEqual(7, ((JArray)walls.ToJson()["ladders"][0]).Count, "an offset ladder writes its offset");
            // On a -y side the offset runs towards +x. Turned clockwise, the old +x points -y (once), -x (twice), +y (three
            // times), while the turned side's offset runs towards +y, +x, +y: so the sign flips, flips, and comes back.
            Assert.AreEqual(-0.4, walls.Rotate(1).Ladders.Single().Offset);
            Assert.AreEqual(-0.4, walls.Rotate(2).Ladders.Single().Offset);
            Assert.AreEqual(0.4, walls.Rotate(3).Ladders.Single().Offset);
            Assert.AreEqual(ladder, walls.Rotate(2).Rotate(2).Ladders.Single());
            Assert.AreEqual(6, ((JArray)WallOutline.FromJson(JToken.Parse(RoofJson.Replace("[0,1,1,1]", "[0,1,2,1]"))).ToJson()["ladders"][0]).Count,
                "no offset, six values");
        }

        [Test]
        public void BuildPutsALevelAndItsLadderOnTheGrid()
        {
            var walls = WallOutline.FromJson(JToken.Parse(RoofJson.Replace("[0,1,1,1]", "[0,1,2,1]")));
            var document = LayoutJson.Parse("{\"v\":1,\"size\":[8,8],\"pieces\":[[\"shop\",\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",2,2,0]],\"deploy\":[[0,0]],\"decor\":[]}");
            var rules = new GridRules { CellSize = 2f, SideWalls = true, LadderTiles = 2 };
            var issues = new System.Collections.Generic.List<LayoutIssue>();
            var grid = LayoutGrid.Build(document, _ => new PieceSpec(3, 2, CoverLevel.Full, walls), rules, issues);
            CollectionAssert.IsEmpty(issues);
            Assert.AreEqual(6, grid.UpperCells.Count());
            Assert.IsTrue(grid.Exists(new LevelCell(2, 2, 1)) && grid.Exists(new LevelCell(4, 3, 1)));
            Assert.AreEqual(3.5f, grid.FloorAt(new LevelCell(3, 3, 1)));
            Assert.IsTrue(grid.IsLadder(new LevelCell(4, 4), new LevelCell(4, 3, 1)));
            Assert.AreEqual(7, grid.StepsFrom(new[] { new LevelCell(4, 7) })[new LevelCell(2, 2, 1)], "3 to the ladder, 2 up, 2 across");
        }

        // ---- Validators (plan H2e) ----

        private static string[] Lines(System.Collections.Generic.IEnumerable<LayoutIssue> issues) => issues.Select(i => i.ToString()).ToArray();

        [Test]
        public void ALadderFootOnABlockerIsNotWalkable()
        {
            var grid = Roofed();
            CollectionAssert.IsEmpty(LayoutValidator.Transitions(grid));
            grid.Block(new Cell(3, 4), "crate", CoverLevel.Half);
            CollectionAssert.AreEqual(new[] { "TRANSITION_NOT_WALKABLE 3,4 ladder 3,4 - 4,4@1: its foot is under crate; keep that cell open" },
                Lines(LayoutValidator.Transitions(grid)));
            Assert.AreEqual(IssueSeverity.Error, LayoutValidator.Transitions(grid).Single().Severity);
        }

        [Test]
        public void BuildReportsALadderOffTheBoardAndItsLevelThenHasNoLadder()
        {
            // The frame's top row is the board's top row, so the ladder's foot, above the frame, is off the board.
            var walls = WallOutline.FromJson(JToken.Parse(RoofJson.Replace("[0,1,1,1]", "[0,1,2,1]")));
            var document = LayoutJson.Parse("{\"v\":1,\"size\":[8,8],\"pieces\":[[\"shop\",\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",2,6,0]],\"deploy\":[[0,0]],\"decor\":[]}");
            var rules = new GridRules { CellSize = 2f, SideWalls = true, LadderTiles = 2, DiagonalMoves = true, NoCornerCutting = true };
            var report = LayoutValidator.Validate(document, _ => new PieceSpec(3, 2, CoverLevel.Full, walls), rules);
            CollectionAssert.AreEqual(new[]
            {
                "TRANSITION_NOT_WALKABLE 2,6 shop: ladder 4,8 - 4,7@1 has an end off the board; the ladder is left out",
                "LEVEL_UNREACHABLE 2,6 level 1 of shop: 6 cells no ladder from the ground reaches; add a ladder or leave the cells out of the level",
            }, Lines(report.Issues));
            Assert.AreEqual(IssueSeverity.Warning, report.WithCode("LEVEL_UNREACHABLE").Single().Severity);
        }

        [Test]
        public void EachUpperRegionNeedsALadder()
        {
            var grid = Roofed();
            CollectionAssert.IsEmpty(LayoutValidator.Levels(grid));
            grid.AddUpperCell(new LevelCell(0, 0, 1), 3f);
            grid.AddUpperCell(new LevelCell(1, 0, 1), 3f);
            grid.AddUpperCell(new LevelCell(1, 1, 2), 6f);
            CollectionAssert.AreEqual(new[]
            {
                "LEVEL_UNREACHABLE 0,0 level 1: 2 cells no ladder from the ground reaches; add a ladder or leave the cells out of the level",
                "LEVEL_UNREACHABLE 1,1 level 2: 1 cell no ladder from the ground reaches; add a ladder or leave the cells out of the level",
            }, Lines(LayoutValidator.Levels(grid)));
            grid.AddLadder(new LevelCell(1, 0, 1), new LevelCell(1, 1, 2));
            Assert.AreEqual(2, LayoutValidator.Levels(grid).Count, "a ladder between two upper levels doesn't reach the ground");
            grid.AddLadder(new LevelCell(0, 1), new LevelCell(0, 0, 1));
            CollectionAssert.IsEmpty(LayoutValidator.Levels(grid), "up from the ground to level 1, then on to level 2");
        }

        // A wall of blockers across row 4 of an 8 x 8 board, a roof over it at (3, 3-5) with a ladder at each end.
        private static LayoutGrid WalledOff(bool secondLadder)
        {
            var grid = new LayoutGrid(8, 8, Rules);
            for (var x = 0; x < 8; x++) grid.Block(new Cell(x, 4), "wall", CoverLevel.Full);
            for (var y = 3; y <= 5; y++) grid.AddUpperCell(new LevelCell(3, y, 1), 3f);
            grid.AddLadder(new LevelCell(3, 2), new LevelCell(3, 3, 1));
            if (secondLadder) grid.AddLadder(new LevelCell(3, 6), new LevelCell(3, 5, 1));
            return grid;
        }

        [Test]
        public void AnEnemyReachedOnlyByLadderIsLadderOnly()
        {
            var enemies = new[] { new LayoutEnemySpawn("raider1", null, null, new Cell(5, 6), 180f) };
            var report = LayoutValidator.ForGrid(WalledOff(true), new[] { new Cell(0, 0) }, enemies);
            CollectionAssert.AreEqual(new[]
            {
                "LADDER_ONLY 5,6 enemy raider1 can be reached from the deploy cells only by ladder, so a unit type that cannot climb never gets to it",
            }, Lines(report.Issues));
            Assert.AreEqual(IssueSeverity.Warning, report.Issues.Single().Severity);
            CollectionAssert.IsEmpty(report.Unreachable, "the far side is reached over the roof");

            report = LayoutValidator.ForGrid(WalledOff(false), new[] { new Cell(0, 0) }, enemies);
            CollectionAssert.AreEqual(new[]
            {
                "ENEMY_UNREACHABLE 5,6 enemy raider1 cannot be reached from the deploy cells",
                "POCKET 0,5 24 open cells unreachable from the deploy cells (holds raider1)",
            }, Lines(report.Issues), "one way up and no way down");
        }

        [Test]
        public void StepsTooHighAreReportedPerLevel()
        {
            var grid = new LayoutGrid(4, 4, Rules);
            grid.AddUpperCell(new LevelCell(0, 0, 1), 3f);
            grid.AddUpperCell(new LevelCell(1, 0, 1), 3.6f);
            grid.AddUpperCell(new LevelCell(0, 1, 1), 3.5f);
            grid.AddUpperCell(new LevelCell(1, 1, 1), 3.5f);
            CollectionAssert.AreEqual(new[]
            {
                "STEP_TOO_HIGH 0,0 level 1: floor 3.00 m, 1,0 next to it 3.60 m: a 0.60 m step, more than 0.50 m; units cannot cross",
            }, Lines(LayoutValidator.Steps(grid)), "the board has no floor heights; the level's are checked anyway");
            grid.AddUpperCell(new LevelCell(1, 0, 1), 3.5f);
            CollectionAssert.IsEmpty(LayoutValidator.Steps(grid));
        }
    }
}
