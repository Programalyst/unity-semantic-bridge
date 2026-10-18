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
    // The outline suggester (plan Phase W) on a box-built room of 2 x 2 cells (4 m, walls 0.2 m thick on the
    // cell lines): a door 1 m wide 0.4 m left of its cell's centre on +y, a window (sill 1.2 m) on -x of the
    // upper-left cell, a 0.3 m slot in the back wall (a wall whose collider lets sight through), a 0.1 m floor
    // slab and a ceiling at 2.6 m.
    public class WallOutlineSuggesterTests
    {
        private Scene scene;
        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            root = new GameObject("Room");
            Box(new Vector3(-2.2f, 0f, -2.2f), new Vector3(2.2f, 0.1f, 2.2f));     // floor slab
            Box(new Vector3(-2.2f, 2.6f, -2.2f), new Vector3(2.2f, 2.7f, 2.2f));   // ceiling
            Box(new Vector3(1.9f, 0f, -2.1f), new Vector3(2.1f, 2.7f, 2.1f));      // +x wall
            Box(new Vector3(-2.1f, 0f, -2.1f), new Vector3(0.5f, 2.7f, -1.9f));    // -y wall, left of the slot
            Box(new Vector3(0.8f, 0f, -2.1f), new Vector3(2.1f, 2.7f, -1.9f));     // -y wall, right of the slot
            Box(new Vector3(-2.1f, 0f, -2.1f), new Vector3(-1.9f, 1.2f, 2.1f));    // -x wall up to the sill
            Box(new Vector3(-2.1f, 1.2f, -2.1f), new Vector3(-1.9f, 2.7f, 0.3f));  // -x wall beside the window
            Box(new Vector3(-2.1f, 1.2f, 1.7f), new Vector3(-1.9f, 2.7f, 2.1f));
            Box(new Vector3(-2.1f, 2.2f, 0.3f), new Vector3(-1.9f, 2.7f, 1.7f));   // window lintel
            Box(new Vector3(-2.1f, 0f, 1.9f), new Vector3(-1.9f, 2.7f, 2.1f));     // +y wall left of the door
            Box(new Vector3(-0.9f, 0f, 1.9f), new Vector3(2.1f, 2.7f, 2.1f));      // +y wall right of the door
            Box(new Vector3(-1.9f, 2.2f, 1.9f), new Vector3(-0.9f, 2.7f, 2.1f));   // door lintel
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private void Box(Vector3 min, Vector3 max)
        {
            var box = new GameObject("Box").AddComponent<BoxCollider>();
            box.transform.SetParent(root.transform, false);
            box.transform.localPosition = (min + max) / 2f;
            box.size = max - min;
        }

        [Test]
        public void ProposesFrameSidesDoorPointSightFillAndLift()
        {
            var suggestion = WallOutlineSuggester.Suggest(root, 2.0);
            Assert.AreEqual(2, suggestion.Width);
            Assert.AreEqual(2, suggestion.Height);
            Assert.AreEqual(0f, suggestion.FootprintOffset.x, 0.02f);
            Assert.AreEqual(0f, suggestion.FootprintOffset.y, 0.02f);
            Assert.IsTrue(suggestion.WallResidual.All(r => Mathf.Abs(r) < 0.02f), string.Join(", ", suggestion.WallResidual));
            CollectionAssert.AreEqual(new[] { "+D+-+", "w   |", "+ + +", "|   |", "+-+-+" }, suggestion.Outline.ToLines(), suggestion.Format());
            var door = suggestion.Outline.DoorPoints.Single();
            Assert.AreEqual(new Cell(0, 1), door.Cell);
            Assert.AreEqual(GridSide.PlusY, door.Side);
            Assert.AreEqual(-0.4, door.Offset, 0.11, "the doorway's centre is 0.4 m left of the cell's");
            CollectionAssert.AreEqual(new[] { (new Cell(1, 0), GridSide.MinusY) }, suggestion.Outline.SightFill, "the slot lets sight through a wall");
            Assert.AreEqual(-0.1, suggestion.Lift, 0.005, "the floor slab sinks to the ground");
            Assert.AreEqual(2.6, suggestion.Ceiling.Value, 0.005);
            CollectionAssert.IsEmpty(suggestion.Notes);

            var json = suggestion.ToOverrideJson();
            Assert.AreEqual(4.0, (double)json["footprint"][0]);
            var outline = WallOutline.FromJson(json["walls"]);
            CollectionAssert.AreEqual(suggestion.Outline.ToLines(), outline.ToLines(), "the proposal reads back as an overrides entry");
        }

        [Test]
        public void ReportsAWallOffItsFrameLine()
        {
            // Push the +x wall 0.7 m out: the frame still rounds to 2 cells, but the wall is far from its line.
            root.transform.GetChild(2).localPosition += new Vector3(0.7f, 0f, 0f);
            var suggestion = WallOutlineSuggester.Suggest(root, 2.0);
            Assert.IsTrue(suggestion.Notes.Any(n => n.Contains("+x wall")), suggestion.Format());
        }
    }
}
