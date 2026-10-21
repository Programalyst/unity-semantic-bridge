using System;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
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
    }
}
