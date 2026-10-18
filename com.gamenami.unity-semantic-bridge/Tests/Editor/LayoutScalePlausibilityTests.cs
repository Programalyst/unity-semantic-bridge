using System;
using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Pure scale and plausibility validators (asset intelligence Phase 4d): heights against a game's
    // cover bands, card scales, ground runs and building fronts, from cards and the layout only.
    public class LayoutScalePlausibilityTests
    {
        // Nuclear Knights' bands (docs/level-rules.md): half 0.9-1.2 m, nothing 1.2-1.6 m, full from 1.6 m.
        private static readonly CoverHeightBands Nk = new CoverHeightBands { HalfMin = 0.9, HalfMax = 1.2, FullMin = 1.6 };
        private static readonly GridRules Rules = new GridRules { CellSize = 2f, CoverHeights = Nk };

        private static LevelCard Card(LevelRole role, double height, double scaleY = 1, double lift = 0, bool reviewed = false,
            GridSide? front = null) => new LevelCard
        {
            Name = "piece",
            Role = role,
            RoleSource = reviewed ? "override" : "name",
            ScaleSource = reviewed ? "override" : "prefab",
            Height = height,
            Lift = lift,
            PlacementScale = new Vec3(1, scaleY, 1),
            Front = front,
        };

        private static PlacedCard Placed(string id, LevelCard card, int x, int y, CoverLevel cover, bool blocking = true,
            int width = 1, int depth = 1, int rotation = 0, Vec3? scale = null) => new PlacedCard
        {
            LocalId = id, Card = card, Anchor = new Cell(x, y), Size = new Cell(width, depth), Rotation = rotation,
            Blocking = blocking, Cover = blocking ? cover : CoverLevel.None, Scale = scale,
        };

        private static string[] Scale(params PlacedCard[] pieces) => LayoutValidator.Scale(pieces, Nk).Select(i => i.ToString()).ToArray();

        private static LayoutGrid Grid(int width = 8, int height = 8, params PlacedCard[] blockers)
        {
            var grid = new LayoutGrid(width, height, Rules);
            foreach (var piece in blockers.Where(p => p.Blocking))
                foreach (var cell in piece.Cells())
                    grid.Block(cell, piece.LocalId, piece.Cover);
            return grid;
        }

        private static string[] Plausibility(LayoutGrid grid, params PlacedCard[] pieces) =>
            LayoutValidator.Plausibility(grid, pieces).Select(i => i.ToString()).ToArray();

        // ---- Bands ----

        [Test]
        public void BandsFitByCover()
        {
            Assert.IsTrue(Nk.Fits(CoverLevel.Half, 0.93));
            Assert.IsTrue(Nk.Fits(CoverLevel.Half, 1.2004), "within the 5 mm slack");
            Assert.IsFalse(Nk.Fits(CoverLevel.Half, 1.23));
            Assert.IsFalse(Nk.Fits(CoverLevel.Half, 0.6));
            Assert.IsTrue(Nk.Fits(CoverLevel.Full, 1.68));
            Assert.IsFalse(Nk.Fits(CoverLevel.Full, 1.4), "nothing between the bands");
            Assert.IsTrue(Nk.Fits(CoverLevel.None, 1.4), "movement-only blockers may be any height");
            Assert.AreEqual("0.90-1.20 m", Nk.Describe(CoverLevel.Half));
            Assert.AreEqual("1.60 m or more", Nk.Describe(CoverLevel.Full));
            Assert.AreEqual("up to 1.20 m", CoverHeightBands.Default.Describe(CoverLevel.Half));
        }

        [Test]
        public void RoleDetectionUsesTheGameBands()
        {
            // A cover-family name at 1.7 m: USB's default needs 2 m for full cover, NK's 1.6 m.
            var placed = new CardFootprint(1, 1, new Vec3(2, 1.7, 0.5), Vec3.Zero, 0, 0, 1.7, false);
            Assert.AreEqual(LevelRole.Blocker, LevelCards.RoleFrom(LevelCards.NameHint.Cover, placed, out _));
            Assert.AreEqual(LevelRole.CoverFull, LevelCards.RoleFrom(LevelCards.NameHint.Cover, placed, out _, Nk));
        }

        // ---- Scale ----

        [Test]
        public void InBandPiecesAreClean()
        {
            CollectionAssert.IsEmpty(Scale(
                Placed("jersey", Card(LevelRole.CoverHalf, 0.93), 0, 0, CoverLevel.Half),
                Placed("barricade", Card(LevelRole.CoverFull, 1.68, 1.15, 0.75), 2, 0, CoverLevel.Full),
                Placed("tyres", Card(LevelRole.Blocker, 1.33), 4, 0, CoverLevel.None),
                Placed("road", Card(LevelRole.Ground, 0), 6, 0, CoverLevel.None, blocking: false),
                Placed("tall_decor", Card(LevelRole.Decor, 1.4), 0, 2, CoverLevel.None, blocking: false)));
        }

        [Test]
        public void UnreviewedPieceOutOfBandIsAnErrorWithAScale()
        {
            var issues = LayoutValidator.Scale(new[] { Placed("b", Card(LevelRole.CoverHalf, 1.49), 4, 4, CoverLevel.Half) }, Nk);
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(IssueSeverity.Error, issues[0].Severity);
            Assert.AreEqual("HEIGHT_BAND 4,4 b: 1.49 m tall gives Half cover, outside the half-cover band 0.90-1.20 m; Y scale 0.805 (now 1) makes it 1.20 m",
                issues[0].ToString());
        }

        [Test]
        public void ReviewedPieceOutOfBandIsANote()
        {
            // NK's Fence_04: owner-approved half cover at (0.79, 0.85, 1), 1.23 m because of its end posts.
            var issues = LayoutValidator.Scale(new[] { Placed("fence", Card(LevelRole.CoverHalf, 1.23, 0.85, reviewed: true), 5, 5, CoverLevel.Half) }, Nk);
            Assert.AreEqual(IssueSeverity.Note, issues.Single().Severity);
            Assert.AreEqual("HEIGHT_BAND 5,5 fence: 1.23 m tall gives Half cover, outside the half-cover band 0.90-1.20 m; Y scale 0.829 (now 0.85) makes it 1.20 m " +
                            "(owner-approved role and scale; the height is a bounding-box top, which thin parts such as posts can overstate)", issues[0].ToString());
        }

        [Test]
        public void ReviewedRoleWithUnreviewedScaleIsStillAnError()
        {
            var card = Card(LevelRole.CoverHalf, 1.49);
            card.RoleSource = "override";
            Assert.AreEqual(IssueSeverity.Error, LayoutValidator.Scale(new[] { Placed("b", card, 0, 0, CoverLevel.Half) }, Nk).Single().Severity);
        }

        [Test]
        public void LowFullCoverSuggestsAScaleOrHalfCover()
        {
            Assert.AreEqual(new[]
            {
                "HEIGHT_BAND 0,0 c: 1.10 m tall gives Full cover, outside the full-cover band 1.60 m or more; Y scale 1.455 (now 1) makes it 1.60 m, or give it Half cover",
            }, Scale(Placed("c", Card(LevelRole.CoverFull, 1.1), 0, 0, CoverLevel.Full)));
        }

        [Test]
        public void SuggestionScalesOnlyThePartAboveTheLift()
        {
            // 0.75 m of mesh above a 0.75 m lift at Y 1.15: Y 1.304 puts the top at 1.60 m.
            Assert.AreEqual(new[]
            {
                "HEIGHT_BAND 0,0 b: 1.50 m tall gives Full cover, outside the full-cover band 1.60 m or more; Y scale 1.304 (now 1.15) makes it 1.60 m",
            }, Scale(Placed("b", Card(LevelRole.CoverFull, 1.5, 1.15, 0.75), 0, 0, CoverLevel.Full)));
            Assert.AreEqual(new[]
            {
                "HEIGHT_BAND 0,0 l: 1.40 m tall gives Half cover, outside the half-cover band 0.90-1.20 m; lower its lift",
            }, Scale(Placed("l", Card(LevelRole.CoverHalf, 1.4, lift: 1.3), 0, 0, CoverLevel.Half)));
        }

        [Test]
        public void CoverComesFromThePlacementNotTheRole()
        {
            // An unreviewed blocker (between the bands) the game gave Full cover is checked against the full band.
            Assert.AreEqual(1, Scale(Placed("b", Card(LevelRole.Blocker, 1.4), 0, 0, CoverLevel.Full)).Length);
            CollectionAssert.IsEmpty(Scale(Placed("b", Card(LevelRole.Blocker, 1.4), 0, 0, CoverLevel.None)));
        }

        [Test]
        public void SceneScaleOtherThanTheCardsIsReportedOnItsCell()
        {
            Assert.AreEqual(new[]
            {
                "OFFGRID_SCALE 2,3 j: scale (1, 1.5, 1) differs from its card's placementScale (1, 1, 1): 1.50 m tall here, 1.00 m at the card's scale " +
                "(outside the half-cover band 0.90-1.20 m); a layout places it at the card's scale, so reapply, or make this scale its placementScale in the overrides file",
            }, Scale(Placed("j", Card(LevelRole.CoverHalf, 1.0), 2, 3, CoverLevel.Half, scale: new Vec3(1, 1.5, 1))));
            CollectionAssert.IsEmpty(Scale(Placed("j", Card(LevelRole.CoverHalf, 1.0), 2, 3, CoverLevel.Half, scale: new Vec3(1, 1.0004, 1))),
                "rounding noise is the card's scale");
        }

        // ---- Plausibility: layers and ground ----

        [Test]
        public void RolesInTheWrongLayerAreWarnings()
        {
            var road = Placed("road", Card(LevelRole.Ground, 0), 0, 0, CoverLevel.Full, width: 2, depth: 2);
            var sandbags = Placed("sandbags", Card(LevelRole.CoverHalf, 1), 4, 4, CoverLevel.None, blocking: false);
            Assert.AreEqual(new[]
            {
                "ROLE_LAYER 0,0 road: a ground piece placed as a blocker; put it in decor",
                "ROLE_LAYER 4,4 sandbags: a cover_half piece placed as decor never blocks; put it in pieces",
            }, Plausibility(Grid(8, 8, road), road, sandbags));
        }

        [Test]
        public void GroundRunsReachingAnEdgeAreClean()
        {
            // Two road tiles and a sidewalk tile along the bottom edge, and a run that meets the top edge via a corner tile.
            CollectionAssert.IsEmpty(Plausibility(Grid(),
                Placed("road_a", Card(LevelRole.Ground, 0), 2, 0, CoverLevel.None, false, 2, 2),
                Placed("road_b", Card(LevelRole.Ground, 0), 4, 0, CoverLevel.None, false, 2, 2),
                Placed("walk", Card(LevelRole.Ground, 0.09), 6, 0, CoverLevel.None, false, 2, 2),
                Placed("up", Card(LevelRole.Ground, 0), 3, 4, CoverLevel.None, false, 2, 2),
                Placed("top", Card(LevelRole.Ground, 0), 3, 6, CoverLevel.None, false, 2, 2)));
            CollectionAssert.IsEmpty(Plausibility(Grid(),
                Placed("past_edge", Card(LevelRole.Ground, 0), -1, 3, CoverLevel.None, false, 2, 2)), "a tile hanging past the edge reaches it");
        }

        [Test]
        public void MidBoardRunAndOverlapAreWarnings()
        {
            var issues = LayoutValidator.Plausibility(Grid(),
                new[]
                {
                    Placed("road_a", Card(LevelRole.Ground, 0), 2, 3, CoverLevel.None, false, 2, 2),
                    Placed("road_b", Card(LevelRole.Ground, 0), 4, 3, CoverLevel.None, false, 2, 2),
                    Placed("walk", Card(LevelRole.Ground, 0.09), 4, 3, CoverLevel.None, false, 2, 2),
                    Placed("lone", Card(LevelRole.Ground, 0), 2, 6, CoverLevel.None, false, 1, 1),
                });
            Assert.IsTrue(issues.All(i => i.Severity == IssueSeverity.Warning));
            Assert.AreEqual(new[]
            {
                "GROUND_OVERLAP 4,3 walk overlaps ground piece road_b: both sit at the same ground lift and z-fight",
                "GROUND_RUN 2,3 ground run of 8 cells (road_a, road_b, walk) reaches no board edge, so its free edges show the ground lift step; extend it to the board edge",
                "GROUND_RUN 2,6 ground run of 1 cell (lone) reaches no board edge, so its free edges show the ground lift step; extend it to the board edge",
            }, issues.Select(i => i.ToString()).ToArray());
        }

        // ---- Plausibility: building fronts ----

        private static PlacedCard Building(string id, int x, int y, GridSide? front, int rotation = 0) =>
            Placed(id, Card(LevelRole.Building, 6, front: front), x, y, CoverLevel.Full, width: 3, depth: 2, rotation: rotation);

        [Test]
        public void BuildingFacingOpenOrRoadCellsIsClean()
        {
            var shop = Building("shop", 2, 4, GridSide.MinusY);
            var road = Placed("road", Card(LevelRole.Ground, 0), 0, 0, CoverLevel.None, false, 8, 4);
            CollectionAssert.IsEmpty(Plausibility(Grid(8, 8, shop), shop, road));
            // Two of the three front cells blocked still leaves a way in.
            var crate = Placed("crate", Card(LevelRole.CoverHalf, 1), 2, 3, CoverLevel.Half, width: 2);
            CollectionAssert.IsEmpty(Plausibility(Grid(8, 8, shop, crate), shop, crate));
        }

        [Test]
        public void BuildingFrontBlockedOrAtTheEdgeIsAWarning()
        {
            var shop = Building("shop", 2, 4, GridSide.MinusY);
            var wall = Placed("wall", Card(LevelRole.CoverFull, 2.1), 2, 3, CoverLevel.Full, width: 3);
            Assert.AreEqual(new[] { "BUILDING_FRONT 2,4 shop: its front (-y) faces only blocked cells (wall); leave open or road cells before it" },
                Plausibility(Grid(8, 8, shop, wall), shop, wall));

            var edge = Building("edge", 2, 0, GridSide.MinusY);
            Assert.AreEqual(new[] { "BUILDING_FRONT 2,0 edge: its front (-y) faces the board edge; turn it towards the board" },
                Plausibility(Grid(8, 8, edge), edge));
        }

        [Test]
        public void BuildingFrontTurnsWithTheLayoutRotation()
        {
            // Front -y at rotation 0; one clockwise turn faces -x. At x 0 that is the board edge.
            var turned = Building("turned", 0, 2, GridSide.MinusY, rotation: 1);
            Assert.AreEqual(new[] { "BUILDING_FRONT 0,2 turned: its front (-x) faces the board edge; turn it towards the board" },
                Plausibility(Grid(8, 8, turned), turned));
            Assert.AreEqual(GridSide.PlusX, GridSides.Rotate(GridSide.PlusY, 1));
            Assert.AreEqual(GridSide.PlusY, GridSides.Rotate(GridSide.MinusX, 1));
            Assert.AreEqual(GridSide.MinusX, GridSides.Rotate(GridSide.PlusY, 3));
        }

        [Test]
        public void BuildingWithoutAFrontIsANote()
        {
            var issue = LayoutValidator.Plausibility(Grid(), new[] { Building("cafe", 2, 2, null) }).Single();
            Assert.AreEqual(IssueSeverity.Note, issue.Severity);
            Assert.AreEqual("BUILDING_NO_FRONT 2,2 cafe: no front known (its root is tilted); add \"front\" to its overrides entry to have it checked", issue.ToString());
        }

        // ---- Fronts on cards ----

        [Test]
        public void FrontIsTheModelForward()
        {
            Assert.AreEqual(GridSide.PlusY, LevelCards.ForwardSide(Vec3.Zero), "+Z is grid +y at rotation 0");
            Assert.AreEqual(GridSide.PlusX, LevelCards.ForwardSide(new Vec3(0, 90, 0)), "a root yawed 90° faces grid +x");
            Assert.AreEqual(GridSide.MinusY, LevelCards.ForwardSide(new Vec3(0, 180, 0)));
            Assert.AreEqual(GridSide.MinusX, LevelCards.ForwardSide(new Vec3(0, 270, 0)));
            Assert.IsNull(LevelCards.ForwardSide(new Vec3(-90, 0, 0)), "a root tilted so forward points up has no front");
        }

        [Test]
        public void FrontOverrideWinsAndRoundTrips()
        {
            var overrides = LevelCatalogOverrides.Parse(@"{""pieces"":{""g1"":{""role"":""building"",""front"":""+x""}}}");
            var card = new LevelCard
            {
                Guid = "g1", Name = "SM_Bld_Cafe_01", Id = "P/SM_Bld_Cafe_01.prefab",
                VisualBounds = new Box3(new Vec3(0, 3, 0), new Vec3(6, 6, 7)),
            };
            LevelCards.Classify(card, Vec3.One, overrides.Resolve("g1", card.Id), Nk);
            Assert.AreEqual(GridSide.PlusX, card.Front);
            Assert.AreEqual("override", card.FrontSource);

            var json = LevelCards.ToJson(card);
            Assert.AreEqual("+x", (string)json["front"]);
            Assert.AreEqual(GridSide.PlusX, LevelCards.FromJson("g1", json).Front);

            var detected = new LevelCard
            {
                Guid = "g2", Name = "SM_Bld_Shop", Id = "P/SM_Bld_Shop.prefab", VisualBounds = card.VisualBounds, RootRotation = new Vec3(0, 180, 0),
            };
            LevelCards.Classify(detected, Vec3.One, null, Nk);
            Assert.AreEqual(LevelRole.Building, detected.Role);
            Assert.AreEqual(GridSide.MinusY, detected.Front, "the model's forward, turned by its root");
            Assert.AreEqual("forward", detected.FrontSource);

            var prop = new LevelCard { Guid = "g3", Name = "SM_Prop_Crate", Id = "P/SM_Prop_Crate.prefab", VisualBounds = new Box3(new Vec3(0, 0.5, 0), new Vec3(1, 1, 1)) };
            LevelCards.Classify(prop, Vec3.One, null, Nk);
            Assert.IsNull(prop.Front, "only buildings have a front");

            var e = Assert.Throws<FormatException>(() => LevelCatalogOverrides.Parse(@"{""pieces"":{""g1"":{""front"":""north""}}}"));
            StringAssert.Contains("front", e.Message);
        }

        // ---- Report ----

        [Test]
        public void NotesFollowWarningsAndCountInTheHeader()
        {
            var report = new LayoutReport();
            report.Issues.Add(new LayoutIssue("HEIGHT_BAND", new Cell(1, 1), "n", IssueSeverity.Note));
            report.Issues.Add(new LayoutIssue("GROUND_RUN", new Cell(2, 2), "w", IssueSeverity.Warning));
            Assert.AreEqual("LAYOUT_REPORT 0 errors, 1 warnings, 1 notes\nWarnings (1):\nGROUND_RUN 2,2 w\nNotes (1):\nHEIGHT_BAND 1,1 n", report.Format());
            Assert.AreEqual("LAYOUT_REPORT 0 errors, 0 warnings", new LayoutReport().Format(), "no notes, no notes count");
        }
    }
}
