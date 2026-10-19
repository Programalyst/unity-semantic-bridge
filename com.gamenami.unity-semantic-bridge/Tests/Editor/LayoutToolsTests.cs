using System;
using System.IO;
using System.Linq;
using Gamenami.UnitySemanticBridge.Editor.Layout;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Phase 5 MCP tools: layout files (get/set, prefab paths for GUIDs), the scene-free grid tier against
    // apply-then-validate, and the top-down render. Prefabs and layout files live in a scratch folder; each
    // test gets a new empty scene. The test adapter is LayoutApplierCardTests' (6 x 6 board, 2 m cells).
    public class LayoutToolsTests
    {
        private string folder;
        private Scene scene;
        private LayoutApplierCardTests.TestAdapter adapter;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/__LayoutTools_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            adapter = new LayoutApplierCardTests.TestAdapter();
        }

        [TearDown]
        public void TearDown()
        {
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        // A cube child of the given size standing on the pivot; with a mesh collider (like Synty pieces) or its box.
        private string MakeProp(string name, Vector3 size, bool meshCollider = true)
        {
            var root = new GameObject(name);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            cube.transform.localPosition = new Vector3(0, size.y * 0.5f, 0);
            cube.transform.localScale = size;
            if (meshCollider)
            {
                Object.DestroyImmediate(cube.GetComponent<BoxCollider>());
                cube.AddComponent<MeshCollider>().sharedMesh = cube.GetComponent<MeshFilter>().sharedMesh;
            }
            var path = $"{folder}/{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return path;
        }

        private static LevelCardSource Cards(string piecesJson) =>
            LevelCardSource.ForOverrides(LevelCatalogOverrides.Parse("{\"v\":1,\"pieces\":{" + piecesJson + "}}"));

        private static LayoutDocument Doc(string pieces, string decor = "") =>
            LayoutJson.Parse("{\"v\":1,\"size\":[6,6],\"pieces\":[" + pieces + "],\"deploy\":[[0,0]],\"decor\":[" + decor + "]}");

        [Test]
        public void GridTierMatchesApplyThenValidateWithoutTouchingTheScene()
        {
            // A card sandbag run (1.9 x 1 x 0.8 m: one cell, Half), a measured 3.5 m wall without a card (2 cells,
            // Full from the test adapter), and the run again overlapping the wall.
            var run = AssetDatabase.AssetPathToGUID(MakeProp("SM_Test_Sandbags", new Vector3(1.9f, 1f, 0.8f)));
            var wall = AssetDatabase.AssetPathToGUID(MakeProp("SM_Test_Wall", new Vector3(3.5f, 2.5f, 0.5f), meshCollider: false));
            var cards = Cards($"\"{run}\":{{\"role\":\"cover_half\",\"placementScale\":[1,1,1]}}");
            var document = Doc($"[\"run\",\"{run}\",1,1,0],[\"wall\",\"{wall}\",1,3,0],[\"run2\",\"{wall}\",4,4,1],[\"clash\",\"{run}\",2,3,0]");
            var rootsBefore = scene.rootCount;

            var draft = LayoutPieceResolver.ValidateDraft(document, adapter, cards);
            Assert.AreEqual(rootsBefore, scene.rootCount, "the grid tier adds nothing to the open scene");
            Assert.AreEqual(CoverLevel.Half, draft.Grid[new Cell(1, 1)].Cover);
            Assert.AreEqual(CoverLevel.Full, draft.Grid[new Cell(1, 3)].Cover);
            Assert.IsTrue(draft.Grid.IsBlocked(new Cell(2, 3)), "the measured wall takes two cells");
            Assert.AreEqual(CoverLevel.Full, draft.Grid[new Cell(4, 5)].Cover, "rot 1 turns it along y");
            Assert.AreEqual(1, draft.WithCode("OVERLAP").Count(), draft.Format(0));

            var applied = LayoutApplier.Apply(document, scene, adapter, cards);
            var full = LayoutSceneValidator.ValidateApplied(document, applied, scene, adapter);
            CollectionAssert.IsEmpty(draft.Grid.DiffOccupancy(applied.Expected), "same footprints and cover as apply");
            CollectionAssert.AreEqual(
                full.Issues.Where(i => i.Code == "OVERLAP" || i.Code == "HEIGHT_BAND").Select(i => i.ToString()),
                draft.Issues.Where(i => i.Code == "OVERLAP" || i.Code == "HEIGHT_BAND").Select(i => i.ToString()));
        }

        [Test]
        public void GridTierReportsMissingPrefabs()
        {
            var document = Doc("[\"ghost\",\"0123456789abcdef0123456789abcdef\",0,0,0]");
            var e = Assert.Throws<BridgeToolException>(() => LayoutPieceResolver.ValidateDraft(document, adapter, LevelCardSource.None));
            StringAssert.Contains("ghost", e.Message);
        }

        [Test]
        public void PrefabPathsStandForGuids()
        {
            var path = MakeProp("SM_Test_Crate", Vector3.one);
            var json = "{\"v\":1,\"size\":[6,6],\"pieces\":[[\"crate\",\"" + path + "\",0,0,0]]}";
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(path), LayoutTools.Parse(json, "layout").Pieces[0].PrefabGuid);
            Assert.Throws<LayoutFormatException>(() => LayoutJson.Parse(json), "plain parsing still wants GUIDs");
            var e = Assert.Throws<BridgeToolException>(() => LayoutTools.Parse(json.Replace(path, folder + "/Nope.prefab"), "layout"));
            StringAssert.Contains("neither an asset GUID nor the path of an asset", e.Message);
        }

        [Test]
        public void SetLayoutWritesCanonicalJsonAndGetLayoutReadsIt()
        {
            var prefab = MakeProp("SM_Test_Crate", Vector3.one);
            var path = $"{folder}/draft.layout.json";
            var layout = JObject.Parse("{\"v\":1,\"size\":[6,6],\"pieces\":[[\"crate\",\"" + prefab + "\",2,2,1]],\"deploy\":[[0,0]]}");

            var reply = LayoutTools.SetLayout(new JObject { ["path"] = path, ["layout"] = layout, ["validate"] = false });
            StringAssert.StartsWith($"Wrote {path}: 6x6, 1 pieces", reply);
            var written = File.ReadAllText(path);
            StringAssert.Contains(AssetDatabase.AssetPathToGUID(prefab), written);
            StringAssert.DoesNotContain(".prefab", written);
            Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(path), "imported");

            var read = LayoutTools.GetLayout(new JObject { ["path"] = path });
            StringAssert.StartsWith($"{path}: 6x6, 1 pieces, 0 decor, 1 deploy cells, 0 enemies.", read);
            StringAssert.Contains(written.TrimEnd('\n'), read);

            StringAssert.StartsWith("Replaced", LayoutTools.SetLayout(new JObject { ["path"] = path, ["layout"] = layout.ToString(), ["validate"] = false }));
        }

        [Test]
        public void SetLayoutWritesNothingOnASchemaError()
        {
            var path = $"{folder}/broken.layout.json";
            var e = Assert.Throws<BridgeToolException>(() =>
                LayoutTools.SetLayout(new JObject { ["path"] = path, ["layout"] = "{\"v\":1,\"size\":[6,6],\"pieces\":[[\"a\",\"x\",0,0,5]]}" }));
            StringAssert.StartsWith("Error: layout: pieces[0]", e.Message);
            Assert.IsFalse(File.Exists(path));
            Assert.Throws<BridgeToolException>(() => LayoutTools.SetLayout(new JObject { ["path"] = "Assets/../x.json", ["layout"] = "{}" }));
        }

        [Test]
        public void ToolsWantOneDraftAndAnOpenScene()
        {
            var both = new JObject { ["path"] = $"{folder}/a.layout.json", ["layout"] = "{}" };
            StringAssert.Contains("exactly one", Assert.Throws<BridgeToolException>(() => LayoutTools.ReadDocument(both)).Message);
            StringAssert.Contains("exactly one", Assert.Throws<BridgeToolException>(() => LayoutTools.ReadDocument(new JObject())).Message);
            var closed = new JObject { ["scene"] = "Assets/Scenes/NotOpen.unity" };
            StringAssert.Contains("is not open", Assert.Throws<BridgeToolException>(() => LayoutTools.ResolveScene(closed)).Message);
            Assert.AreEqual(SceneManager.GetActiveScene(), LayoutTools.ResolveScene(new JObject()));
            var sceneTierWithDraft = new JObject { ["tier"] = "scene", ["path"] = $"{folder}/a.layout.json" };
            StringAssert.Contains("omit path and layout", Assert.Throws<BridgeToolException>(() => LayoutTools.ValidateLayout(sceneTierWithDraft)).Message);
        }

        [Test]
        public void RenderFramesTheRegionOnWholeCells()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.position = new Vector3(6f, 0f, 6f);
            var rootsBefore = scene.rootCount;

            var board = LayoutRender.Render(scene, adapter, null, 1, true, true, null, 256, 80);
            Assert.AreEqual((256, 256), (board.Width, board.Height), "6 x 6 board plus a 1-cell margin is square");
            Assert.AreEqual((new Cell(-1, -1), new Cell(6, 6)), (board.Min, board.Max));
            Assert.AreEqual(32f, board.PixelsPerCell, 1e-3f);
            Assert.Greater(board.Jpg.Length, 0);
            Assert.AreEqual(rootsBefore, scene.rootCount, "the camera is gone and nothing was added");

            var strip = LayoutRender.Render(scene, adapter, (new Cell(0, 0), new Cell(5, 2)), 0, false, false, 2.4f, 300, 80);
            Assert.AreEqual((300, 150), (strip.Width, strip.Height), "6 x 3 cells");

            var plain = LayoutRender.Render(scene, adapter, null, 1, false, false, null, 256, 80);
            CollectionAssert.AreNotEqual(plain.Jpg, board.Jpg, "the overlay changes the image");
            Assert.Throws<BridgeToolException>(() => LayoutRender.Render(scene, adapter, null, 1, true, true, -1f, 256, 80));
        }
    }
}
