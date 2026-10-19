using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gamenami.UnitySemanticBridge.Editor.Layout;
using Gamenami.UnitySemanticBridge.Layout;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Apply and bake with level catalog cards (Phase 4c): card scale, lift, footprint override with a
    // fitted collider, ground lift, card cover (None included), and pieces without cards as before.
    // Prefabs live in a scratch folder; each test gets a new empty scene.
    public class LayoutApplierCardTests
    {
        private string folder;
        private Scene scene;
        private TestAdapter adapter;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/__LayoutCards_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            adapter = new TestAdapter();
        }

        [TearDown]
        public void TearDown()
        {
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        // A prop whose only collider is a mesh collider (like the Synty pieces): one cube child of the given
        // size standing on the pivot, its BoxCollider swapped for a MeshCollider.
        private string MakeMeshProp(string name, Vector3 size, Vector3 rootScale = default)
        {
            var root = new GameObject(name);
            if (rootScale != default) root.transform.localScale = rootScale;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            cube.transform.localPosition = new Vector3(0, size.y * 0.5f, 0);
            cube.transform.localScale = size;
            Object.DestroyImmediate(cube.GetComponent<BoxCollider>());
            cube.AddComponent<MeshCollider>().sharedMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            var path = $"{folder}/{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return AssetDatabase.AssetPathToGUID(path);
        }

        private static LevelCardSource Cards(string piecesJson) =>
            LevelCardSource.ForOverrides(LevelCatalogOverrides.Parse("{\"v\":1,\"pieces\":{" + piecesJson + "}}"));

        private static LayoutDocument Doc(string pieces, string decor = "") =>
            LayoutJson.Parse("{\"v\":1,\"size\":[6,6],\"pieces\":[" + pieces + "],\"deploy\":[[0,0]],\"decor\":[" + decor + "]}");

        private Transform Piece(LayoutApplyResult result, string id, string layer = LayoutApplier.PiecesName) =>
            result.Root.transform.Find(layer).Find(id);

        [Test]
        public void CardScaleLiftAndOverriddenFootprintFitOneBox()
        {
            // 2.2 x 1 x 2.3 m heap at scale 1 that claims one 2 m cell and stands 0.5 m up.
            var guid = MakeMeshProp("SM_Test_Heap", new Vector3(2.2f, 1f, 2.3f));
            var cards = Cards($"\"{guid}\":{{\"role\":\"cover_half\",\"placementScale\":[1,1,1],\"footprint\":[2,2],\"lift\":0.5}}");
            var document = Doc($"[\"heap\",\"{guid}\",2,3,0]");

            var result = LayoutApplier.Apply(document, scene, adapter, cards);
            Assert.AreEqual(0, result.Issues.Count, string.Join("; ", result.Issues));
            Assert.AreEqual(1, result.CardCount);
            var spec = result.Specs["heap"];
            Assert.AreEqual((1, 1, CoverLevel.Half), (spec.Width, spec.Depth, spec.Cover));
            var heap = Piece(result, "heap");
            Assert.AreEqual(0.5f, heap.position.y, 1e-4f, "the pivot stands on the card's lift");
            Assert.AreEqual(new Vector3(5f, 0.5f, 7f), heap.position, "the measured centre goes to cell 2,3's centre");

            Assert.IsFalse(heap.GetComponentInChildren<MeshCollider>().enabled, "the overhanging mesh collider is disabled");
            var fitted = heap.Find(LayoutApplier.FootprintColliderName).GetComponent<BoxCollider>();
            Physics.SyncTransforms();
            var bounds = fitted.bounds;
            Assert.AreEqual(4.1f, bounds.min.x, 1e-3f);
            Assert.AreEqual(5.9f, bounds.max.x, 1e-3f);
            Assert.AreEqual(6.1f, bounds.min.z, 1e-3f);
            Assert.AreEqual(7.9f, bounds.max.z, 1e-3f);
            Assert.AreEqual(0.5f, bounds.min.y, 1e-3f, "full height of the replaced collider");
            Assert.AreEqual(1.5f, bounds.max.y, 1e-3f);
            Assert.IsTrue(adapter.Blocking.Contains(fitted), "the piece hook configured the fitted box");
            StringAssert.StartsWith("FIT_BOX 2,3 heap: MeshCollider", result.Notes.Single());

            var bake = LayoutBaker.Bake(scene, adapter, cards);
            Assert.AreEqual(0, bake.Issues.Count, string.Join("; ", bake.Issues));
            CollectionAssert.IsEmpty(LayoutDocument.Diff(document, bake.Document));
            CollectionAssert.IsEmpty(result.Expected.DiffOccupancy(bake.Physics), "the physics bake blocks only the claimed cell");
            Assert.AreEqual(1, bake.Physics.BlockedCount);
        }

        [Test]
        public void CardScaleReplacesThePrefabScaleAndBakeReportsOthers()
        {
            // Prefab root at scale 1, card at (1.7, 1, 1): a 1 m cube becomes a 1.7 m run, one cell.
            var guid = MakeMeshProp("SM_Test_Barrier", Vector3.one);
            var cards = Cards($"\"{guid}\":{{\"role\":\"cover_half\",\"placementScale\":[1.7,1,1]}}");
            var document = Doc($"[\"run\",\"{guid}\",1,1,1]");

            var result = LayoutApplier.Apply(document, scene, adapter, cards);
            Assert.AreEqual(0, result.Issues.Count, string.Join("; ", result.Issues));
            var run = Piece(result, "run");
            Assert.AreEqual(new Vector3(1.7f, 1f, 1f), run.localScale);
            CollectionAssert.IsEmpty(result.Notes, "1.7 m fits its cell inside the 0.1 m inset: nothing to fit");
            var bake = LayoutBaker.Bake(scene, adapter, cards);
            Assert.AreEqual(0, bake.Issues.Count, string.Join("; ", bake.Issues));
            CollectionAssert.IsEmpty(LayoutDocument.Diff(document, bake.Document));

            run.localScale = Vector3.one;
            var rescaled = LayoutBaker.Bake(scene, adapter, cards);
            Assert.AreEqual(1, rescaled.Issues.Count(i => i.StartsWith("OFFGRID_SCALE") && i.Contains("card's placementScale (1.7, 1, 1)")),
                string.Join("; ", rescaled.Issues));
            CollectionAssert.IsEmpty(rescaled.Document.Pieces);
        }

        [Test]
        public void CardCoverNoneReachesThePieceHookAndStaysApartFromHalf()
        {
            var guid = MakeMeshProp("SM_Test_Tyres", new Vector3(1.5f, 1f, 1.5f));
            var cards = Cards($"\"{guid}\":{{\"role\":\"blocker\",\"cover\":\"None\"}}");
            var document = Doc($"[\"tyres\",\"{guid}\",3,3,0]");

            var result = LayoutApplier.Apply(document, scene, adapter, cards);
            Assert.AreEqual(CoverLevel.None, adapter.Covers[Piece(result, "tyres").gameObject], "the hook got the card's cover, not the height rule");
            Assert.AreEqual(CoverLevel.None, result.Specs["tyres"].Cover);
            Assert.AreEqual(CoverLevel.None, result.Expected[new Cell(3, 3)].Cover);
            Assert.IsTrue(result.Expected.IsBlocked(new Cell(3, 3)), "movement-only blockers still block");
            var bake = LayoutBaker.Bake(scene, adapter, cards);
            Assert.AreEqual(CoverLevel.None, bake.Specs["tyres"].Cover);
            CollectionAssert.IsEmpty(result.Expected.DiffOccupancy(bake.Physics));

            var half = new LayoutGrid(6, 6, adapter.Rules);
            half.Block(new Cell(3, 3), "tyres", CoverLevel.Half);
            CollectionAssert.AreEqual(new[] { "COVER 3,3 expected None from tyres, got Half from tyres" }, result.Expected.DiffOccupancy(half));
        }

        [Test]
        public void GroundPiecesRiseByTheAdapterGroundLift()
        {
            var guid = MakeMeshProp("SM_Test_Road", new Vector3(4f, 0.05f, 4f));
            var cards = Cards($"\"{guid}\":{{\"role\":\"ground\"}}");
            var document = Doc("", $"[\"road\",\"{guid}\",0,0,0]");

            var result = LayoutApplier.Apply(document, scene, adapter, cards);
            Assert.AreEqual(0, result.Issues.Count, string.Join("; ", result.Issues));
            Assert.AreEqual(new Vector3(2f, TestAdapter.Lift, 2f), Piece(result, "road", LayoutApplier.DecorName).position);
            var bake = LayoutBaker.Bake(scene, adapter, cards);
            Assert.AreEqual(0, bake.Issues.Count, string.Join("; ", bake.Issues));
            CollectionAssert.IsEmpty(LayoutDocument.Diff(document, bake.Document), "bake accepts the lifted tile");
        }

        [Test]
        public void PiecesWithoutACardAreMeasuredAtTheirPrefabScale()
        {
            // No overrides entry: the prefab's own scale (2, 1, 1) and the instance measurement apply.
            var guid = MakeMeshProp("SM_Test_Plain", Vector3.one, new Vector3(2f, 1f, 1f));
            var ruleOnly = LevelCardSource.ForOverrides(LevelCatalogOverrides.Parse(
                "{\"v\":1,\"rules\":[{\"match\":\"**/SM_Test_Plain.prefab\",\"placementScale\":[3,1,1]}]}"));
            Assert.IsNull(ruleOnly.Find(guid), "family rules alone give no card");
            var document = Doc($"[\"plain\",\"{guid}\",1,1,0]");

            var result = LayoutApplier.Apply(document, scene, adapter, ruleOnly);
            Assert.AreEqual(0, result.CardCount);
            Assert.AreEqual(new Vector3(2f, 1f, 1f), Piece(result, "plain").localScale);
            Assert.AreEqual((1, 1), (result.Specs["plain"].Width, result.Specs["plain"].Depth));
            var bake = LayoutBaker.Bake(scene, adapter, ruleOnly);
            CollectionAssert.IsEmpty(LayoutDocument.Diff(document, bake.Document));
        }

        // A small game: 2 m cells on a 6 x 6 board at the origin. The piece hook marks every solid collider
        // as blocking (no game components needed), and the occupancy bake is an overlap box per cell.
        [Test]
        public void WalledBuildingKeepsItsCollidersAndGetsItsWalls()
        {
            // A 3.6 m square building whose card frames it as 2 x 2 cells, its bounds centre 0.5 m towards +x of the
            // frame centre (rotation 0), with a door on +y of (1,1). Placed at 2,2 turned once (clockwise).
            var guid = MakeMeshProp("SM_Bld_Test_House", new Vector3(3.6f, 3f, 3.6f));
            var cards = Cards($"\"{guid}\":{{\"role\":\"building\",\"placementScale\":[1,1,1],\"footprint\":[4,4],\"footprintOffset\":[0.5,0]," +
                              "\"walls\":{\"outline\":[\"+-+D+\",\"|   |\",\"+ + +\",\"|   |\",\"+-+-+\"]}}");
            var document = Doc($"[\"house\",\"{guid}\",2,2,1]");
            adapter.Rules.SideWalls = true;

            var result = LayoutApplier.Apply(document, scene, adapter, cards);
            Assert.AreEqual(0, result.Issues.Count, string.Join("; ", result.Issues));
            var spec = result.Specs["house"];
            Assert.IsNotNull(spec.Outline);
            Assert.AreEqual((2, 2), (spec.Width, spec.Depth));
            Assert.IsFalse(result.Expected.IsBlocked(new Cell(2, 2)) || result.Expected.IsBlocked(new Cell(3, 3)), "the frame stays walkable");
            Assert.AreEqual(SideKind.Door, result.Expected.SideAt(new Cell(3, 2), GridSide.PlusX).Kind, "turned once, the door is on +x of 3,2");
            Assert.AreEqual(SideKind.Wall, result.Expected.SideAt(new Cell(3, 3), GridSide.PlusX).Kind);

            var house = Piece(result, "house");
            Assert.AreEqual(new Vector3(6f, 0f, 5.5f), house.position, "bounds centre = frame centre (6, 6) + the offset turned once (0, -0.5)");
            Assert.IsTrue(house.GetComponentInChildren<MeshCollider>().enabled && house.Find(LayoutApplier.FootprintColliderName) == null,
                "a walled building keeps its collider as authored (sight), with no fitted box");
            CollectionAssert.IsEmpty(adapter.Blocking, "no piece hook, so nothing blocks cells");
            var walls = adapter.Walls[house.gameObject];
            Assert.AreEqual(new Vector3(6f, 0f, 6f), walls.frameCentre);
            Assert.AreEqual(1, walls.rotation);

            var bake = LayoutBaker.Bake(scene, adapter, cards);
            Assert.AreEqual(0, bake.Issues.Count, string.Join("; ", bake.Issues));
            CollectionAssert.IsEmpty(LayoutDocument.Diff(document, bake.Document));
            CollectionAssert.IsEmpty(result.Expected.DiffOccupancy(bake.Physics), "the game's walls equal the layout's");

            adapter.Walls[house.gameObject] = (WallOutline.FromLines(new[] { "+-+-+", "|   |", "+ + +", "|   |", "+-+-+" }), walls.frameCentre, walls.rotation);
            var edited = LayoutBaker.Bake(scene, adapter, cards);
            StringAssert.StartsWith("WALLS_DIFF 2,2 house: outline", edited.Issues.Single(i => i.StartsWith("WALLS_")));
            CollectionAssert.Contains(edited.OccupancyDiff, "WALL_KIND 3,2 +x expected Door from house, got Wall from house");
        }

        // Sight checks against colliders (SIGHT_NO_OBSTACLE, WALL_SIGHT_GAP, DOOR_COLLIDER) run only for a game whose
        // sight is physics; with grid sight (plan Phase S, D13) colliders decide no sight. The adapter's default
        // IgnoredBySight is used here.
        [Test]
        public void SightCollidersAreGatheredOnlyWithPhysicsSight()
        {
            var rock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rock.name = "rock";
            rock.transform.position = new Vector3(5f, 1f, 5f);
            var board = new GridBoard { Width = 6, Height = 6, FloorY = 0f };
            var sight = LayoutSceneValidator.GatherSight(scene, board, adapter);
            Assert.AreEqual(1, sight.Count, "physics sight: the rock is a sight collider");
            Assert.IsFalse(sight[0].IsObstacle);
            Assert.AreEqual(1, LayoutValidator.Consistency(new LayoutGrid(6, 6, adapter.Rules), new LayoutGrid(6, 6, adapter.Rules), sight)
                .Count(i => i.Code == "SIGHT_NO_OBSTACLE"));
            adapter.Rules.GridSight = true;
            Assert.IsEmpty(LayoutSceneValidator.GatherSight(scene, board, adapter), "grid sight: no sight colliders");
            Assert.IsEmpty(LayoutSceneValidator.WallSight(Array.Empty<PlacedCard>(), board, adapter));
        }

        private sealed class TestAdapter : IGridRulesAdapter
        {
            public const float Lift = 0.25f;
            public readonly HashSet<Collider> Blocking = new HashSet<Collider>();
            public readonly Dictionary<GameObject, CoverLevel> Covers = new Dictionary<GameObject, CoverLevel>();
            public readonly Dictionary<GameObject, (WallOutline outline, Vector3 frameCentre, int rotation)> Walls =
                new Dictionary<GameObject, (WallOutline, Vector3, int)>();

            public void ConfigureWalls(GameObject instance, WallOutline outline, Vector3 frameCentre, int layoutRotation) =>
                Walls[instance] = (outline, frameCentre, layoutRotation);

            public bool TryReadWalls(GameObject instance, out WallOutline outline)
            {
                outline = Walls.TryGetValue(instance, out var walls) ? walls.outline : null;
                return outline != null;
            }

            public GridRules Rules { get; } = new GridRules { CellSize = 2f };
            public float GroundLift => Lift;
            public LayerMask SightLayers => ~0;

            public bool TryGetBoard(Scene scene, out GridBoard board, out string error)
            {
                board = new GridBoard { Width = 6, Height = 6, FloorY = 0f };
                error = null;
                return true;
            }

            public Vector3 CellToWorld(Cell cell, GridBoard board) => new Vector3(cell.X * 2f + 1f, board.FloorY, cell.Y * 2f + 1f);
            public Cell WorldToCell(Vector3 position) => new Cell(Mathf.FloorToInt(position.x / 2f), Mathf.FloorToInt(position.z / 2f));
            public bool IsBlockingCollider(Collider collider) =>
                collider != null && collider.enabled && collider.gameObject.activeInHierarchy && Blocking.Contains(collider);

            public PieceRole Classify(GameObject root) =>
                root.GetComponentsInChildren<Collider>(true).Any(IsBlockingCollider) || Walls.ContainsKey(root) ? PieceRole.Blocker : PieceRole.Decor;

            public CoverLevel DescribeCover(GameObject root) => Covers.TryGetValue(root, out var cover) ? cover : CoverLevel.Full;

            public void ConfigurePiece(GameObject instance, CoverLevel cover)
            {
                Covers[instance] = cover;
                foreach (var collider in instance.GetComponentsInChildren<Collider>())
                    if (collider.enabled && !collider.isTrigger) Blocking.Add(collider);
            }

            public LayoutGrid BakeOccupancy(Scene scene, List<string> issues)
            {
                Physics.SyncTransforms();
                var grid = new LayoutGrid(6, 6, Rules);
                for (var x = 0; x < 6; x++)
                    for (var y = 0; y < 6; y++)
                    {
                        var hit = Physics.OverlapBox(new Vector3(x * 2f + 1f, 2f, y * 2f + 1f), new Vector3(1f, 2f, 1f))
                            .FirstOrDefault(IsBlockingCollider);
                        if (hit == null) continue;
                        var root = hit.transform;
                        while (!Covers.ContainsKey(root.gameObject) && root.parent != null) root = root.parent;
                        grid.Block(new Cell(x, y), root.name, DescribeCover(root.gameObject));
                    }
                // Walls where ConfigureWalls put them: the frame's min cell from its centre and turned size.
                foreach (var pair in Walls)
                {
                    var turned = pair.Value.outline.Rotate(pair.Value.rotation);
                    var anchor = WorldToCell(pair.Value.frameCentre - new Vector3(turned.Width - 1, 0f, turned.Height - 1));
                    foreach (var side in turned.Placed(anchor, 0))
                        grid.SetSide(side.Cell, side.Side, side.Kind, turned.CoverFor(side.Kind), pair.Key.name);
                }
                return grid;
            }

            public IReadOnlyList<Cell> ReadDeployCells(Scene scene) => new[] { new Cell(0, 0) };
            public void WriteDeployCells(Scene scene, IReadOnlyList<Cell> cells) { }
            public IReadOnlyList<LayoutEnemySpawn> ReadEnemySpawns(Scene scene) => new LayoutEnemySpawn[0];
            public void WriteEnemySpawns(Scene scene, IReadOnlyList<LayoutEnemySpawn> spawns) { }
        }
    }
}
