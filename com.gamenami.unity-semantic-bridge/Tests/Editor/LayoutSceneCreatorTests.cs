using System;
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
    // create_layout_scene: a template level scene (a "Level Holder" renderer whose material stands for the level
    // asset, as the test adapter's scene-level mode reads it, plus a [Generated Layout]) copied into a new level.
    public class LayoutSceneCreatorTests
    {
        private string folder;
        private LayoutApplierCardTests.TestAdapter adapter;
        private string template;
        private Material level;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/__LayoutScenes_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            adapter = new LayoutApplierCardTests.TestAdapter { LevelFromScene = true };

            level = new Material(Shader.Find("Standard")) { name = "Template Level" };
            AssetDatabase.CreateAsset(level, $"{folder}/Template Level.mat");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var holder = GameObject.CreatePrimitive(PrimitiveType.Cube);
            holder.name = "Level Holder";
            holder.GetComponent<MeshRenderer>().sharedMaterial = level;
            new GameObject(LayoutApplier.RootName);
            new GameObject("Main Camera").AddComponent<Camera>();
            template = $"{folder}/Template.unity";
            EditorSceneManager.SaveScene(scene, template);
            adapter.Sizes[level] = (20, 20);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        [Test]
        public void CopiesTheSceneAndItsLevelAndRepointsTheCopy()
        {
            var path = $"{folder}/New Level.unity";
            var reply = LayoutSceneCreator.Create(template, path, null, new Cell(12, 8), adapter);
            StringAssert.StartsWith($"Created {path} from {template}: board 12x8 (template 20x20).", reply);
            StringAssert.Contains($"Level {folder}/New Level.mat (copy of {folder}/Template Level.mat; 1 reference repointed).", reply);
            StringAssert.Contains($"Removed the template's {LayoutApplier.RootName}.", reply);
            Assert.AreEqual(1, SceneManager.sceneCount, "the new scene was closed again");

            var copy = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/New Level.mat");
            Assert.IsNotNull(copy);
            Assert.AreEqual((12, 8), adapter.Sizes[copy], "the size went to the copy");
            Assert.AreEqual((20, 20), adapter.Sizes[level], "the template's level keeps its size");

            var opened = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Assert.AreEqual(copy, opened.GetRootGameObjects().Single(go => go.name == "Level Holder").GetComponent<MeshRenderer>().sharedMaterial);
            Assert.IsFalse(opened.GetRootGameObjects().Any(go => go.name == LayoutApplier.RootName));
            Assert.IsTrue(opened.GetRootGameObjects().Any(go => go.name == "Main Camera"), "the rest of the template comes along");

            var original = EditorSceneManager.OpenScene(template, OpenSceneMode.Single);
            Assert.AreEqual(level, original.GetRootGameObjects().Single(go => go.name == "Level Holder").GetComponent<MeshRenderer>().sharedMaterial);
            Assert.IsTrue(original.GetRootGameObjects().Any(go => go.name == LayoutApplier.RootName), "the template is unchanged");
        }

        [Test]
        public void RefusesWithoutLeavingAnythingBehind()
        {
            var path = $"{folder}/New Level.unity";
            Assert.Throws<BridgeToolException>(() => LayoutSceneCreator.Create(template, template, null, null, adapter), "the path exists");
            Assert.Throws<BridgeToolException>(() => LayoutSceneCreator.Create($"{folder}/Nope.unity", path, null, null, adapter));
            Assert.Throws<BridgeToolException>(() => LayoutSceneCreator.Create(template, "Assets/../x.unity", null, null, adapter));

            File.Copy($"{folder}/Template Level.mat", $"{folder}/Taken.mat");
            AssetDatabase.ImportAsset($"{folder}/Taken.mat");
            var e = Assert.Throws<BridgeToolException>(() => LayoutSceneCreator.Create(template, path, $"{folder}/Taken.mat", null, adapter));
            StringAssert.Contains("already exists", e.Message);
            Assert.IsFalse(File.Exists(path), "the scene copy was removed again");
            Assert.AreEqual(1, SceneManager.sceneCount);

            adapter.LevelFromScene = false; // a scene the adapter finds no level asset in, but a level path is given
            e = Assert.Throws<BridgeToolException>(() => LayoutSceneCreator.Create(template, path, $"{folder}/Other.mat", null, adapter));
            StringAssert.Contains("omit level", e.Message);
            Assert.IsFalse(File.Exists(path));
        }
    }
}
