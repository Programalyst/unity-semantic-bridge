using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    public class ModelInspectionTests
    {
        private string folder;
        private string modelPath;

        [SetUp]
        public void SetUp()
        {
            // Only tests create assets: unique scratch directory, never touch user fixtures.
            folder = "Assets/__ModelInspection_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            modelPath = folder + "/Triangle.obj";
            File.WriteAllText(modelPath, "o Triangle\nv 0 0 0\nv 2 0 0\nv 0 3 0\nusemtl Surface\nf 1 2 3\n");
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport);
        }

        [TearDown]
        public void TearDown() { if (folder != null) AssetDatabase.DeleteAsset(folder); }

        private string Inspect(bool details = false) => InspectThroughDispatcher(modelPath, details);

        private static string InspectThroughDispatcher(string path, bool details)
        {
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(new JObject
            {
                ["method"] = "inspect_model_asset", ["path"] = path, ["include_details"] = details,
            }, completion);
            Assert.IsTrue(completion.Task.IsCompleted, "Read-only inspection must complete synchronously on the Editor thread.");
            return completion.Task.GetAwaiter().GetResult();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("/tmp/model.fbx")]
        [TestCase("Assets/../model.fbx")]
        [TestCase("Assets//model.fbx")]
        public void RejectsInvalidPaths(string path)
        {
            StringAssert.StartsWith("Error: Provide a project-relative model path", ModelInspectionFunctions.InspectModelAsset(new JObject { ["path"] = path }));
        }

        [Test]
        public void MissingFolderAndUnsupportedAssetsAreActionable()
        {
            StringAssert.Contains("Check the path and connected project", ModelInspectionFunctions.InspectModelAsset(new JObject { ["path"] = folder + "/Missing.fbx" }));
            StringAssert.Contains("Path is a folder", ModelInspectionFunctions.InspectModelAsset(new JObject { ["path"] = folder }));
            var meshPath = folder + "/Mesh.asset";
            AssetDatabase.CreateAsset(new Mesh(), meshPath);
            StringAssert.Contains("not ModelImporter", ModelInspectionFunctions.InspectModelAsset(new JObject { ["path"] = meshPath }));
        }

        [Test]
        public void ImportedModelReportsRealIdsBoundsHierarchyAndDoesNotMutate()
        {
            var mesh = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Mesh>().First();
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out var guid, out long fileId);
            var importer = AssetImporter.GetAtPath(modelPath);
            var beforeImporter = EditorJsonUtility.ToJson(importer);
            var beforeMeta = File.ReadAllBytes(modelPath + ".meta");
            var beforeSource = File.ReadAllBytes(modelPath);
            var beforeSelection = Selection.instanceIDs;
            var scenes = Enumerable.Range(0, EditorSceneManager.sceneCount).Select(EditorSceneManager.GetSceneAt).ToArray();
            var sceneState = scenes.Select(s => (s.handle, s.isDirty, s.rootCount)).ToArray();
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var transforms = root.GetComponentsInChildren<Transform>(true);
            var beforeTransforms = transforms.Select(t => (t.localPosition, t.localRotation, t.localScale)).ToArray();
            var dirty = EditorUtility.IsDirty(root);
            var compact = Inspect();
            StringAssert.Contains($"GUID {guid}, fileID {fileId}", compact);
            StringAssert.Contains($"vertices={mesh.vertexCount} submeshes={mesh.subMeshCount}", compact);
            StringAssert.Contains("mesh-local bounds center=", compact);
            StringAssert.Contains("Combined placement AABB:", compact);
            StringAssert.Contains("Triangle[0]", compact);
            StringAssert.Contains("bakeAxisConversion=", compact);
            StringAssert.Contains("direct", compact);
            StringAssert.DoesNotContain("submesh[0]:", compact);
            var detail = Inspect(true);
            StringAssert.Contains("submesh[0]: topology=Triangles indices=3", detail);
            StringAssert.Contains("recursive", detail);
            Assert.AreEqual(beforeImporter, EditorJsonUtility.ToJson(importer));
            CollectionAssert.AreEqual(beforeMeta, File.ReadAllBytes(modelPath + ".meta"));
            CollectionAssert.AreEqual(beforeSource, File.ReadAllBytes(modelPath));
            CollectionAssert.AreEqual(beforeSelection, Selection.instanceIDs);
            CollectionAssert.AreEqual(beforeTransforms, transforms.Select(t => (t.localPosition, t.localRotation, t.localScale)).ToArray());
            CollectionAssert.AreEqual(sceneState, scenes.Select(s => (s.handle, s.isDirty, s.rootCount)).ToArray());
            Assert.AreEqual(dirty, EditorUtility.IsDirty(root));
        }

        [Test]
        public void MaterialSlotsDistinguishEmbeddedFromExternalRemap()
        {
            var embedded = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Material>().FirstOrDefault();
            Assert.IsNotNull(embedded, "OBJ fixture must import an embedded material.");
            StringAssert.Contains(embedded.name + " (embedded subasset;", Inspect());
            var materialPath = folder + "/External.mat";
            var material = new Material(Shader.Find("Standard")) { name = "External" };
            AssetDatabase.CreateAsset(material, materialPath);
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(embedded), material);
            importer.SaveAndReimport(); // Fixture setup only; inspection must never reimport.
            var report = Inspect();
            StringAssert.Contains("[0] External (external asset " + materialPath, report);
            StringAssert.Contains(materialPath + " (GUID ", report);
        }

        [Test]
        public void TransformedAabbMatchesEightCornersWithNestedTrs()
        {
            var bounds = new Bounds(new Vector3(1, 2, 3), new Vector3(2, 4, 6));
            var matrix = Matrix4x4.TRS(new Vector3(4, 5, 6), Quaternion.Euler(0, 30, 0), new Vector3(-2, 3, 4)) *
                Matrix4x4.TRS(new Vector3(1, 0, 0), Quaternion.Euler(20, 0, 45), new Vector3(1, 2, 1));
            var expected = new Bounds(matrix.MultiplyPoint3x4(bounds.min), Vector3.zero);
            for (var x = 0; x < 2; x++) for (var y = 0; y < 2; y++) for (var z = 0; z < 2; z++)
                expected.Encapsulate(matrix.MultiplyPoint3x4(new Vector3(x == 0 ? bounds.min.x : bounds.max.x,
                    y == 0 ? bounds.min.y : bounds.max.y, z == 0 ? bounds.min.z : bounds.max.z)));
            var actual = (Bounds)typeof(ModelInspectionFunctions).GetMethod("TransformBounds", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { bounds, matrix });
            Assert.Less(Vector3.Distance(expected.center, actual.center), 0.0001f);
            Assert.Less(Vector3.Distance(expected.size, actual.size), 0.0001f);
        }

        [Test]
        public void OptionalBulletFbxIntegration()
        {
            const string fixture = "Assets/Synty/PolygonPoliceStation/Models/SM_Wep_Bullet_01.fbx";
            if (!(AssetImporter.GetAtPath(fixture) is ModelImporter))
                Assert.Ignore("Optional Synty bullet fixture is not installed in this test project.");
            var report = InspectThroughDispatcher(fixture, true);
            StringAssert.StartsWith("Model: " + fixture, report);
            foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(fixture).OfType<Mesh>())
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out var guid, out long fileId);
                StringAssert.Contains($"GUID {guid}, fileID {fileId}", report);
                StringAssert.Contains($"vertices={mesh.vertexCount} submeshes={mesh.subMeshCount}", report);
            }
            TestContext.WriteLine(report);
        }
    }
}
