using System;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Walls on cell sides (plan Phase W): the outline format, its rotation, the grid rules and the
    // card fields. Pure: no scene, no UnityEngine types.
    public class WallOutlineTests
    {
        // SM_Bld_Building_08 at (1.15, 1, 1.15): door on +y of (2,2), windows on -x of (0,1) and +x of (2,1).
        private static readonly string[] House =
        {
            "+-+-+D+",
            "|     |",
            "+ + + +",
            "w     w",
            "+ + + +",
            "|     |",
            "+-+-+-+",
        };

        // SM_Bld_Shop_Medium_02 at (1.1, 1, 1.1): 8x4, boarded front, back door on -y of (1,0).
        private static readonly string[] Shop =
        {
            "+-+-+-+-+-+-+-+-+",
            "|               |",
            "+ + + + + + + + +",
            "|               |",
            "+ + + + + + + + +",
            "|               |",
            "+ + + + + + + + +",
            "|               |",
            "+-+D+-+-+-+-+-+-+",
        };

        private const string HouseGuid = "1b69bd820a5d18d439809c81ed2b9279";
        private const string BlockGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Profile = "cccccccccccccccccccccccccccccccc";
        private const string Visual = "dddddddddddddddddddddddddddddddd";

        private static readonly GridRules Walls = new GridRules { CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true, SideWalls = true };

        private static WallOutline HouseOutline() =>
            WallOutline.FromLines(House, doorPoints: new[] { new DoorPoint(new Cell(2, 2), GridSide.PlusY, -0.44) });

        [Test]
        public void ParsesTheHousePicture()
        {
            var outline = HouseOutline();
            Assert.AreEqual(3, outline.Width);
            Assert.AreEqual(3, outline.Height);
            Assert.AreEqual(12, outline.Sides.Count, "perimeter only");
            Assert.AreEqual(SideKind.Door, outline.KindAt(new Cell(2, 2), GridSide.PlusY));
            Assert.AreEqual(SideKind.Wall, outline.KindAt(new Cell(1, 2), GridSide.PlusY));
            Assert.AreEqual(SideKind.Window, outline.KindAt(new Cell(0, 1), GridSide.MinusX));
            Assert.AreEqual(SideKind.Window, outline.KindAt(new Cell(2, 1), GridSide.PlusX));
            Assert.AreEqual(SideKind.Wall, outline.KindAt(new Cell(0, 0), GridSide.MinusY));
            Assert.AreEqual(SideKind.Open, outline.KindAt(new Cell(1, 1), GridSide.PlusX), "interior");
            Assert.AreEqual(CoverLevel.Full, outline.CoverFor(SideKind.Wall));
            Assert.AreEqual(CoverLevel.Half, outline.CoverFor(SideKind.Window), "windows give Half cover by default");
            Assert.AreEqual(CoverLevel.None, outline.CoverFor(SideKind.Door));
            CollectionAssert.AreEqual(House, outline.ToLines());
        }

        [Test]
        public void InteriorSidesAreStoredOnce()
        {
            var outline = WallOutline.FromLines(new[] { "+-+-+", "| | |", "+-+-+" });
            Assert.AreEqual(7, outline.Sides.Count);
            Assert.AreEqual(SideKind.Wall, outline.KindAt(new Cell(1, 0), GridSide.MinusX), "read from either cell");
            Assert.AreEqual(SideKind.Wall, outline.KindAt(new Cell(0, 0), GridSide.PlusX));
        }

        [TestCase(new[] { "+-+", "| |" }, "odd number of lines")]
        [TestCase(new[] { "+-+-", "|   ", "+-+-" }, "odd length")]
        [TestCase(new[] { "+-+", "| |", "+-" }, "must be 3 characters")]
        [TestCase(new[] { "+|+", "| |", "+-+" }, "unknown side '|'")]
        [TestCase(new[] { "+-+", "|x|", "+-+" }, "a cell is ' '")]
        [TestCase(new[] { "--+", "| |", "+-+" }, "expected '+'")]
        public void RejectsMalformedPictures(string[] lines, string message)
        {
            var e = Assert.Throws<FormatException>(() => WallOutline.FromLines(lines));
            StringAssert.Contains(message, e.Message);
        }

        [Test]
        public void RotationTurnsSidesCellsAndDoorPoints()
        {
            var outline = HouseOutline();
            var turned = outline.Rotate(1);
            // Clockwise: (x, y) -> (y, w - 1 - x); +y -> +x.
            Assert.AreEqual(SideKind.Door, turned.KindAt(new Cell(2, 0), GridSide.PlusX));
            Assert.AreEqual(SideKind.Window, turned.KindAt(new Cell(1, 2), GridSide.PlusY));
            Assert.AreEqual(SideKind.Window, turned.KindAt(new Cell(1, 0), GridSide.MinusY));
            var point = turned.DoorPoints.Single();
            Assert.AreEqual(new Cell(2, 0), point.Cell);
            Assert.AreEqual(GridSide.PlusX, point.Side);
            Assert.AreEqual(0.44, point.Offset, 1e-9, "the left part of the top side becomes the top part of the right side");
            CollectionAssert.AreEqual(House, outline.Rotate(4).ToLines());
            CollectionAssert.AreEqual(outline.Rotate(2).ToLines(), turned.Rotate(1).ToLines());
            Assert.AreEqual(0.44, outline.Rotate(2).DoorPoints.Single().Offset, 1e-9, "half a turn puts the door on the bottom side, right part");

            var shop = WallOutline.FromLines(Shop).Rotate(1);
            Assert.AreEqual(4, shop.Width);
            Assert.AreEqual(8, shop.Height);
            Assert.AreEqual(SideKind.Door, shop.KindAt(new Cell(0, 6), GridSide.MinusX));
        }

        [Test]
        public void JsonRoundTripsAndIsStrict()
        {
            var json = JObject.Parse(@"{""outline"": [""+-+D+"", ""w   |"", ""+-+-+""], ""windowCover"": ""Full"",
                ""doorPoints"": [[1, 0, ""+y"", 0.3]], ""sightFill"": [[0, 0, ""-y""]], ""cutHeight"": 2.4, ""roofed"": [[0, 1, 1, 1]]}");
            var outline = WallOutline.FromJson(json);
            Assert.AreEqual(CoverLevel.Full, outline.WindowCover);
            Assert.AreEqual(2.4, outline.CutHeight);
            Assert.AreEqual((new Cell(0, 1), new Cell(1, 1)), outline.Roofed.Single());
            Assert.IsTrue(JToken.DeepEquals(json, outline.ToJson()), outline.ToJson().ToString());

            StringAssert.Contains("unknown key 'door'", Assert.Throws<FormatException>(() =>
                WallOutline.FromJson(JObject.Parse(@"{""outline"": [""+-+"", ""| |"", ""+-+""], ""door"": 1}"))).Message);
            StringAssert.Contains("which is not a door", Assert.Throws<FormatException>(() =>
                WallOutline.FromJson(JObject.Parse(@"{""outline"": [""+-+"", ""| |"", ""+-+""], ""doorPoints"": [[0, 0, ""+y"", 0.1]]}"))).Message);
            StringAssert.Contains("not a wall or window", Assert.Throws<FormatException>(() =>
                WallOutline.FromJson(JObject.Parse(@"{""outline"": [""+D+"", ""| |"", ""+-+""], ""sightFill"": [[0, 0, ""+y""]]}"))).Message);
            CollectionAssert.IsEmpty(HouseOutline().CheckDoorPoints(2.0));
            Assert.AreEqual(1, WallOutline.FromLines(House, doorPoints: new[] { new DoorPoint(new Cell(2, 2), GridSide.PlusY, 0.95) })
                .CheckDoorPoints(2.0).Count());
        }

        // ---- Grid rules ----

        private static LayoutGrid Room()
        {
            // The house at 2,2 on an 8x8 board: frame x 2..4, y 2..4, door on +y of (4,4).
            var grid = new LayoutGrid(8, 8, Walls);
            foreach (var side in HouseOutline().Placed(new Cell(2, 2), 0))
                grid.SetSide(side.Cell, side.Side, side.Kind, side.Kind == SideKind.Window ? CoverLevel.Half : side.Kind == SideKind.Wall ? CoverLevel.Full : CoverLevel.None, "house");
            return grid;
        }

        [Test]
        public void WallsRefuseStraightStepsAndWalledCorners()
        {
            var grid = Room();
            Assert.IsTrue(grid.IsWalled(new Cell(1, 2), GridSide.PlusX), "stored on the outside cell too");
            Assert.IsFalse(grid.CanStep(new Cell(1, 2), new Cell(2, 2)), "straight across a wall");
            Assert.IsFalse(grid.CanStep(new Cell(2, 2), new Cell(1, 2)), "and back");
            Assert.IsTrue(grid.CanStep(new Cell(4, 5), new Cell(4, 4)), "through the door");
            Assert.IsFalse(grid.CanStep(new Cell(3, 5), new Cell(4, 4)), "not diagonally into the doorway");
            Assert.IsFalse(grid.CanStep(new Cell(5, 5), new Cell(4, 4)), "nor from the other side");
            Assert.IsFalse(grid.CanStep(new Cell(1, 1), new Cell(2, 2)), "diagonal past the frame's corner");
            Assert.IsTrue(grid.CanStep(new Cell(2, 2), new Cell(3, 3)), "inside is open");

            // Each of the four sides at the corner between (2,2) and (3,3) refuses that diagonal, even without the corner rule.
            var loose = new GridRules { CellSize = 2f, DiagonalMoves = true, NoCornerCutting = false, SideWalls = true };
            foreach (var (cell, side) in new[] { (new Cell(2, 2), GridSide.PlusX), (new Cell(2, 2), GridSide.PlusY),
                         (new Cell(3, 2), GridSide.PlusY), (new Cell(2, 3), GridSide.PlusX) })
            {
                var grid1 = new LayoutGrid(6, 6, loose);
                grid1.SetSide(cell, side, SideKind.Wall, CoverLevel.Full, "w");
                Assert.IsFalse(grid1.CanStep(new Cell(2, 2), new Cell(3, 3)), $"{cell} {GridSides.Name(side)}");
                Assert.IsFalse(grid1.CanStep(new Cell(3, 3), new Cell(2, 2)));
                Assert.IsFalse(grid1.CanStep(new Cell(2, 3), new Cell(3, 2)), "the other diagonal through that corner");
            }
        }

        [Test]
        public void WallCoverIsTheBetterOfWallAndNeighbour()
        {
            var grid = Room();
            var inside = grid.CoverSides(new Cell(2, 3));
            Assert.AreEqual(CoverLevel.Half, inside.left, "window");
            Assert.AreEqual(CoverLevel.None, inside.forward);
            var outside = grid.CoverSides(new Cell(1, 3));
            Assert.AreEqual(CoverLevel.Half, outside.right, "the window covers both cells");
            Assert.AreEqual(CoverLevel.Full, grid.CoverSides(new Cell(3, 1)).forward, "back wall from outside");
            Assert.AreEqual(CoverLevel.None, grid.CoverSides(new Cell(4, 5)).back, "a door gives none");
            grid.Block(new Cell(0, 3), "box", CoverLevel.Full);
            Assert.AreEqual(CoverLevel.Full, grid.CoverSides(new Cell(1, 3)).left, "a blocker still counts");
            grid.Block(new Cell(1, 4), "low", CoverLevel.Full);
            Assert.AreEqual(CoverLevel.Full, grid.CoverOn(new Cell(1, 3), GridSide.PlusY));
        }

        [Test]
        public void DiffAndAsciiShowWalls()
        {
            var expected = Room();
            var actual = Room();
            CollectionAssert.IsEmpty(expected.DiffOccupancy(actual));
            actual.SetSide(new Cell(4, 4), GridSide.PlusY, SideKind.Wall, CoverLevel.Full, "scene");
            actual.SetSide(new Cell(2, 3), GridSide.MinusX, SideKind.Window, CoverLevel.Full, "scene");
            actual.SetSide(new Cell(3, 2), GridSide.MinusY, SideKind.Open, CoverLevel.None, null);
            actual.SetSide(new Cell(6, 6), GridSide.PlusX, SideKind.Wall, CoverLevel.Full, "scene");
            CollectionAssert.AreEquivalent(new[]
            {
                "WALL_MISSING 3,1 +y expected Wall Full from house",
                "WALL_COVER 1,3 +x expected Half from house, got Full from scene",
                "WALL_KIND 4,4 +y expected Door from house, got Wall from scene",
                "WALL_EXTRA 6,6 +x Wall Full from scene",
            }, expected.DiffOccupancy(actual));

            var small = new LayoutGrid(3, 2, Walls);
            small.SetSide(new Cell(1, 0), GridSide.PlusY, SideKind.Door, CoverLevel.None, "d");
            small.SetSide(new Cell(1, 0), GridSide.PlusX, SideKind.Wall, CoverLevel.Full, "d");
            small.SetSide(new Cell(0, 1), GridSide.MinusX, SideKind.Window, CoverLevel.Half, "d");
            small.Block(new Cell(0, 0), "box", CoverLevel.Half);
            Assert.AreEqual(
                "+      \n" +
                "w. . . \n" +
                "+ +D+  \n" +
                " h .|. \n" +
                "    +  \n", small.ToAscii());
        }

        // ---- Build and validators ----

        private static PieceSpec? Resolve(LayoutPiece piece) => piece.PrefabGuid switch
        {
            HouseGuid => new PieceSpec(3, 3, CoverLevel.Full, HouseOutline()),
            BlockGuid => new PieceSpec(1, 1, CoverLevel.Full),
            _ => null,
        };

        private static string Layout(string pieces, string enemy = "[\"e1\",\"" + Profile + "\",\"" + Visual + "\",3,3,0]") =>
            "{\"v\":1,\"size\":[8,8],\"pieces\":[" + pieces + "],\"deploy\":[[0,0]],\"enemies\":[" + enemy + "],\"decor\":[]}";

        [Test]
        public void BuildClaimsTheFrameAndPlacesTurnedWalls()
        {
            var issues = new System.Collections.Generic.List<LayoutIssue>();
            var grid = LayoutGrid.Build(LayoutJson.Parse(Layout("[\"house\",\"" + HouseGuid + "\",2,2,1],[\"box\",\"" + BlockGuid + "\",3,3,0]")),
                Resolve, Walls, issues);
            CollectionAssert.IsEmpty(issues, "a piece inside a building is allowed");
            Assert.IsFalse(grid.IsBlocked(new Cell(2, 2)), "the frame stays walkable");
            Assert.AreEqual("house", grid.ClaimedBy(new Cell(4, 4)));
            Assert.IsTrue(grid.IsBlocked(new Cell(3, 3)));
            Assert.AreEqual(SideKind.Door, grid.SideAt(new Cell(4, 2), GridSide.PlusX).Kind, "rotated door on +x of (2,0) of the frame");
            Assert.AreEqual(SideKind.Window, grid.SideAt(new Cell(3, 4), GridSide.PlusY).Kind);
            Assert.AreEqual(CoverLevel.Half, grid.SideAt(new Cell(3, 5), GridSide.MinusY).Cover);

            issues.Clear();
            LayoutGrid.Build(LayoutJson.Parse(Layout("[\"a\",\"" + HouseGuid + "\",2,2,0],[\"b\",\"" + HouseGuid + "\",4,4,0]")), Resolve, Walls, issues);
            CollectionAssert.AreEqual(new[] { "OVERLAP 4,4 b overlaps a" }, issues.Select(i => i.ToString()));

            issues.Clear();
            var solid = LayoutGrid.Build(LayoutJson.Parse(Layout("[\"house\",\"" + HouseGuid + "\",2,2,0]")), Resolve,
                new GridRules { CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true }, issues);
            Assert.IsTrue(solid.IsBlocked(new Cell(3, 3)) && !solid.HasWalls, "without side walls a walled card builds solid");

            issues.Clear();
            LayoutGrid.Build(LayoutJson.Parse(Layout("[\"house\",\"" + HouseGuid + "\",2,2,0]")),
                p => new PieceSpec(2, 3, CoverLevel.Full, HouseOutline()), Walls, issues);
            StringAssert.StartsWith("OUTLINE_SIZE 2,2", issues.Single().ToString());
        }

        [Test]
        public void ReachabilityGoesThroughDoors()
        {
            var house = "[\"house\",\"" + HouseGuid + "\",2,2,0]";
            var report = LayoutValidator.Validate(LayoutJson.Parse(Layout(house)), Resolve, Walls);
            CollectionAssert.IsEmpty(report.Issues.Select(i => i.ToString()), "the enemy inside is reached through the door");

            // The same house turned twice puts its door on the -y side of (2,2); a box in front of it seals it.
            report = LayoutValidator.Validate(LayoutJson.Parse(Layout(house.Replace(",0]", ",2]") + ",[\"box\",\"" + BlockGuid + "\",2,1,0]")), Resolve, Walls);
            CollectionAssert.Contains(report.Issues.Select(i => i.ToString()), "ENEMY_UNREACHABLE 3,3 enemy e1 cannot be reached from the deploy cells");
            StringAssert.Contains("+-+-+-+", report.Format(ascii: true), "the map draws the walls");
        }

        [Test]
        public void StructureReportsPiecesAcrossWallsAndDoorPointsOffTheirSide()
        {
            var house = "[\"house\",\"" + HouseGuid + "\",2,2,0]";
            // A 1x1 block inside the house is fine; a 3x1 one reaching through its -x wall is not.
            PieceSpec? Resolve3(LayoutPiece p) => p.LocalId == "long" ? new PieceSpec(3, 1, CoverLevel.Half) : Resolve(p);
            var report = LayoutValidator.Validate(LayoutJson.Parse(Layout(house + ",[\"box\",\"" + BlockGuid + "\",3,3,0],[\"long\",\"" + BlockGuid + "\",0,2,0]")),
                Resolve3, Walls);
            CollectionAssert.AreEqual(new[] { "WALL_CROSSES_PIECE 1,2 long straddles the wall of house on its +x side; move it to one side of the wall" },
                report.Issues.Where(i => i.Code.StartsWith("WALL")).Select(i => i.ToString()));

            PieceSpec? FarDoor(LayoutPiece p) => p.PrefabGuid == HouseGuid
                ? new PieceSpec(3, 3, CoverLevel.Full, WallOutline.FromLines(House, doorPoints: new[] { new DoorPoint(new Cell(2, 2), GridSide.PlusY, 1.2) }))
                : Resolve(p);
            report = LayoutValidator.Validate(LayoutJson.Parse(Layout(house)), FarDoor, Walls);
            StringAssert.StartsWith("DOOR_POINT_RANGE 2,2 house: door point on 2,2 +y is 1.20 m", report.Issues.Single(i => i.Code == "DOOR_POINT_RANGE").ToString());
        }

        [Test]
        public void DoorsReplaceTheFrontCheckForWalledBuildings()
        {
            var card = new LevelCard { Id = "Pack/SM_Bld_Building_08.prefab", Role = LevelRole.Building, Front = GridSide.PlusY, Walls = HouseOutline() };
            LayoutGrid Grid(int rotation, params Cell[] blocked)
            {
                var grid = new LayoutGrid(8, 8, Walls);
                foreach (var side in HouseOutline().Placed(new Cell(2, 2), rotation))
                    grid.SetSide(side.Cell, side.Side, side.Kind, CoverLevel.Full, "house");
                foreach (var cell in blocked) grid.Block(cell, "crate", CoverLevel.Half);
                return grid;
            }
            PlacedCard Placed(int rotation, Cell anchor) =>
                new PlacedCard { LocalId = "house", Card = card, Anchor = anchor, Size = new Cell(3, 3), Rotation = rotation, Blocking = true, Cover = CoverLevel.Full };

            CollectionAssert.IsEmpty(LayoutValidator.Plausibility(Grid(0), new[] { Placed(0, new Cell(2, 2)) }), "door on +y of 4,4 opens onto 4,5");
            CollectionAssert.AreEqual(new[]
            {
                "DOOR_BLOCKED 4,4 house: the door on +y of 4,4 opens onto crate at 4,5; keep that cell open",
                "BUILDING_SEALED 2,2 house: none of its 1 door opens onto an open cell, so nobody can get in or out",
            }, LayoutValidator.Plausibility(Grid(0, new Cell(4, 5)), new[] { Placed(0, new Cell(2, 2)) }).Select(i => i.ToString()));

            // At 5,5 turned once the door is on +x of 7,5: off the 8x8 board.
            var grid = new LayoutGrid(8, 8, Walls);
            foreach (var side in HouseOutline().Placed(new Cell(5, 5), 1)) grid.SetSide(side.Cell, side.Side, side.Kind, CoverLevel.Full, "house");
            CollectionAssert.AreEqual(new[]
            {
                "DOOR_OFF_BOARD 7,5 house: the door on +x of 7,5 opens off the board; turn or move the building",
                "BUILDING_SEALED 5,5 house: none of its 1 door opens onto an open cell, so nobody can get in or out",
            }, LayoutValidator.Plausibility(grid, new[] { Placed(1, new Cell(5, 5)) }).Select(i => i.ToString()));

            var unwalled = new LevelCard { Id = card.Id, Role = LevelRole.Building, Front = GridSide.PlusY };
            StringAssert.StartsWith("BUILDING_FRONT 5,5 house: its front (+x) faces the board edge",
                LayoutValidator.Plausibility(new LayoutGrid(8, 8, Walls), new[] { new PlacedCard
                {
                    LocalId = "house", Card = unwalled, Anchor = new Cell(5, 5), Size = new Cell(3, 3), Rotation = 1, Blocking = true, Cover = CoverLevel.Full,
                } }).Single().ToString(), "a building without walls keeps the front check");
        }

        // ---- Overrides and cards ----

        [Test]
        public void OverridesCarryWallsAndOffsetToBuildingCards()
        {
            var overrides = LevelCatalogOverrides.Parse(@"{""v"": 1, ""pieces"": {""" + HouseGuid + @""": {
                ""role"": ""building"", ""placementScale"": [1.15, 1, 1.15], ""footprint"": [6, 6], ""footprintOffset"": [0, 0.58], ""lift"": -0.09,
                ""walls"": {""outline"": [""+-+-+D+"", ""|     |"", ""+ + + +"", ""w     w"", ""+ + + +"", ""|     |"", ""+-+-+-+""],
                          ""doorPoints"": [[2, 2, ""+y"", -0.44]]}}}}");
            var entry = overrides.Resolve(HouseGuid, "Pack/SM_Bld_Building_08.prefab");
            var card = new LevelCard
            {
                Guid = HouseGuid,
                Name = "SM_Bld_Building_08",
                Id = "Pack/SM_Bld_Building_08.prefab",
                VisualBounds = new Box3(new Vec3(0, 3, 0), new Vec3(5.4, 6.3, 5.5)),
                ColliderBounds = new Box3(new Vec3(0, 2.9, 0), new Vec3(5.37, 6.21, 5.5)),
            };
            LevelCards.Classify(card, Vec3.One, entry);
            Assert.AreEqual(LevelRole.Building, card.Role);
            Assert.AreEqual(-0.09, card.Lift, 1e-9, "a negative lift sinks a floor slab to the ground");
            Assert.AreEqual(new FootprintSize(0, 0.58), card.FootprintOffset);
            CollectionAssert.AreEqual(House, card.Walls.ToLines());
            var footprint = LevelCards.Footprint(card, 2.0);
            Assert.AreEqual(3, footprint.Width);
            Assert.AreEqual(3, footprint.Depth);

            var json = LevelCards.ToJson(card);
            var back = LevelCards.FromJson(HouseGuid, json);
            CollectionAssert.AreEqual(House, back.Walls.ToLines());
            Assert.AreEqual(-0.44, back.Walls.DoorPoints.Single().Offset, 1e-9);
            Assert.AreEqual(new FootprintSize(0, 0.58), back.FootprintOffset);

            var prop = new LevelCard { Name = "SM_Prop_Crate_01", Id = "Pack/SM_Prop_Crate_01.prefab", VisualBounds = new Box3(new Vec3(0, 0.5, 0), new Vec3(1, 1, 1)) };
            LevelCards.Classify(prop, Vec3.One, new LevelOverride { Walls = entry.Walls });
            Assert.IsNull(prop.Walls, "only buildings take walls");
            Assert.IsNull(LevelCards.ToJson(new LevelCard { VisualBounds = prop.VisualBounds })["walls"], "cards without walls keep their shape");

            StringAssert.Contains("'walls'", Assert.Throws<FormatException>(() => LevelCatalogOverrides.Parse(
                @"{""v"": 1, ""pieces"": {""x"": {""walls"": {""outline"": [""+-+""]}}}}")).Message);
        }
    }
}
