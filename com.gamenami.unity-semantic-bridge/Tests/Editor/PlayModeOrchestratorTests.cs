using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    /// <summary>
    /// Deterministic orchestration tests. Live Play Mode transitions are
    /// manual acceptance only (GridBattle path); nothing here toggles isPlaying.
    /// </summary>
    public class PlayModeOrchestratorTests
    {
        static readonly string[] Keys =
            { PlayModeOrchestrator.KindKey, PlayModeOrchestrator.TokenKey, PlayModeOrchestrator.DeadlineKey };
        readonly Dictionary<string, string> saved = new Dictionary<string, string>();

        [SetUp]
        public void SetUp()
        {
            saved.Clear();
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                Assert.Ignore("Run in Edit Mode when idle.");
            foreach (var key in Keys)
            {
                saved[key] = SessionState.GetString(key, "");
                SessionState.EraseString(key);
            }
            SessionState.SetString(PlayModeOrchestrator.KindKey, "none");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var entry in saved) SessionState.SetString(entry.Key, entry.Value);
        }

        [Test]
        public void PollReturnsSnapshotWithoutStartingOp()
        {
            var first = JObject.Parse(PlayModeOrchestrator.PollPlayState(new JObject()));
            Assert.IsNotNull(first["isPlaying"]);
            Assert.IsNotNull(first["transitioning"]);
            Assert.IsNotNull(first["isCompiling"]);
            Assert.IsNotNull(first["isUpdating"]);
            Assert.AreEqual(JTokenType.Null, first["pendingOp"].Type);
            // Agents use this to spot a throttled background Editor before a transition.
            Assert.IsFalse(string.IsNullOrEmpty(first["interactionMode"]?.Value<string>()));

            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "poll_play_state" }, completion);
            var second = JObject.Parse(completion.Task.Result);
            Assert.AreEqual(JTokenType.Null, second["pendingOp"].Type);
            Assert.AreEqual("none", SessionState.GetString(PlayModeOrchestrator.KindKey, "none"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Assets/Nope.txt")]
        [TestCase("../outside.unity")]
        [TestCase("Assets/Does-Not-Exist-Usb-Probe.unity")]
        public void OpenSceneRejectsBadPathsWithoutSideEffects(string path)
        {
            var msg = new JObject { ["method"] = "open_scene" };
            if (path != null) msg["path"] = path;
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(msg, completion);
            StringAssert.StartsWith("Error:", completion.Task.Result);
            Assert.AreEqual("none", SessionState.GetString(PlayModeOrchestrator.KindKey, "none"));
        }

        [Test]
        public void OpenSceneRejectsUnknownDirtyPolicy()
        {
            // Policy is validated before asset existence, so no scene is touched.
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(new JObject
            {
                ["method"] = "open_scene",
                ["path"] = "Assets/Probe.unity",
                ["dirty_policy"] = "bogus",
            }, completion);
            StringAssert.Contains("dirty_policy", completion.Task.Result);
            Assert.AreEqual("none", SessionState.GetString(PlayModeOrchestrator.KindKey, "none"));
        }

        [Test]
        public void ConcurrentEnterIsRejectedWhilePendingOpActive()
        {
            // Simulate a pending op started before a (real or reload) pump.
            SessionState.SetString(PlayModeOrchestrator.KindKey, "enter");
            SessionState.SetString(PlayModeOrchestrator.TokenKey, "probe-token");
            SessionState.SetString(PlayModeOrchestrator.DeadlineKey,
                System.DateTime.UtcNow.AddMinutes(5).Ticks.ToString());

            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "enter_play" }, completion);
            StringAssert.StartsWith("BUSY:", completion.Task.Result);
            StringAssert.Contains("probe-token", completion.Task.Result);

            var poll = JObject.Parse(PlayModeOrchestrator.PollPlayState(new JObject()));
            Assert.AreEqual("enter", poll["pendingOp"]["kind"].ToString());

            var exitCompletion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "exit_play" }, exitCompletion);
            StringAssert.StartsWith("BUSY:", exitCompletion.Task.Result);
        }

        [TestCase("0", 1.0)]
        [TestCase("-5", 1.0)]
        [TestCase("1000", 55.0)]
        [TestCase(null, 30.0)]
        [TestCase("bogus", 30.0)]
        [TestCase("12.5", 12.5)]
        public void TimeoutParsingClampsToTransportWindow(string raw, double expected)
        {
            var msg = new JObject();
            if (raw != null) msg["timeout_s"] = raw;
            Assert.AreEqual(expected, PlayModeOrchestrator.ReadTimeoutS(msg), 1e-9);
        }

        [Test]
        public void OpenSceneWithUnsavedChangesAndDefaultPolicyFailsWithoutDiscarding()
        {
            const string dir = "Assets/__BridgePlayModeProbes__";
            const string sceneA = dir + "/UsbProbeA.unity";
            const string sceneB = dir + "/UsbProbeB.unity";
            AssetDatabase.DeleteAsset(dir);
            AssetDatabase.CreateFolder("Assets", "__BridgePlayModeProbes__");
            try
            {
                EditorSceneManager.SaveScene(
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single), sceneA);
                EditorSceneManager.SaveScene(
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single), sceneB);
                EditorSceneManager.OpenScene(sceneA);
                Assert.IsFalse(EditorSceneManager.GetActiveScene().isDirty);
                var probe = new GameObject("UsbDirtyProbe");
                Undo.RegisterCreatedObjectUndo(probe, "Usb dirty probe");
                Assert.IsTrue(EditorSceneManager.GetActiveScene().isDirty);

                var completion = new TaskCompletionSource<string>();
                McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "open_scene", ["path"] = sceneB }, completion);
                StringAssert.StartsWith("Error:", completion.Task.Result);
                StringAssert.Contains("unsaved changes", completion.Task.Result);
                Assert.AreEqual(sceneA, EditorSceneManager.GetActiveScene().path);
                Assert.IsTrue(EditorSceneManager.GetActiveScene().isDirty);

                Object.DestroyImmediate(probe);
                Assert.IsTrue(EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), sceneA));
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            finally
            {
                AssetDatabase.DeleteAsset(dir);
            }
        }

        [Test]
        public void UnknownMethodsStillReportError()
        {
            LogAssert.Expect(LogType.Error, "Unhandled MCP command received: enter_play_typo");
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "enter_play_typo" }, completion);
            StringAssert.Contains("Unhandled action", completion.Task.Result);
        }
    }
}
