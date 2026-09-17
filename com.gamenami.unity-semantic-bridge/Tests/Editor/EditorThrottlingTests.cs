using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    public class EditorThrottlingReadTests
    {
        [Test]
        public void EditorPrefsSectionReportsStoredModeWithoutMutation()
        {
            var result = JObject.Parse(ProjectSettingsFunctions.GetProjectSettings(
                new JObject { ["sections"] = new JArray("editor_prefs") }));
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(EditorPrefs.GetInt("InteractionMode", 0), result["editor_prefs"]["interactionModeRaw"].Value<int>());
            Assert.AreEqual(EditorPrefs.GetInt("ApplicationIdleTime", 4), result["editor_prefs"]["applicationIdleTimeMs"].Value<int>());
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("custom")]
        [TestCase("NO_THROTTLING")]
        public void InvalidModeDoesNotChangePreferences(string mode)
        {
            var before = EditorPreferencesFunctions.GetPreferences().ToString();
            StringAssert.StartsWith("Error:", EditorPreferencesFunctions.SetEditorThrottling(new JObject { ["mode"] = mode }));
            Assert.AreEqual(before, EditorPreferencesFunctions.GetPreferences().ToString());
        }
    }

    // These tests temporarily modify user-wide preferences. Opt in only after
    // coordinating with other sessions/Editors, and restore even on assertion failure.
    [Explicit("Temporarily changes Unity user preferences; run in a coordinated test Editor.")]
    public class EditorThrottlingMutationTests
    {
        const string RestoreKey = "UnitySemanticBridge.InteractionMode.Restore";
        bool hadMode, hadIdle;
        int mode, idle;
        string saved;

        [SetUp]
        public void SavePreferences()
        {
            hadMode = EditorPrefs.HasKey("InteractionMode");
            hadIdle = EditorPrefs.HasKey("ApplicationIdleTime");
            mode = EditorPrefs.GetInt("InteractionMode", 0);
            idle = EditorPrefs.GetInt("ApplicationIdleTime", 4);
            saved = SessionState.GetString(RestoreKey, "");
            SessionState.EraseString(RestoreKey);
        }

        [TearDown]
        public void RestorePreferences()
        {
            if (hadMode) EditorPrefs.SetInt("InteractionMode", mode); else EditorPrefs.DeleteKey("InteractionMode");
            if (hadIdle) EditorPrefs.SetInt("ApplicationIdleTime", idle); else EditorPrefs.DeleteKey("ApplicationIdleTime");
            SessionState.SetString(RestoreKey, saved);
            typeof(EditorApplication).GetMethod("UpdateInteractionModeSettings",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null);
        }

        static JObject Set(string mode)
        {
            var result = EditorPreferencesFunctions.SetEditorThrottling(new JObject { ["mode"] = mode });
            Assert.IsFalse(result.StartsWith("Error:"), result);
            return JObject.Parse(result);
        }

        [TestCase(2, 16)]
        [TestCase(3, 27)]
        public void RepeatedOverridesRestoreOriginalModeAndIdleTime(int originalMode, int originalIdle)
        {
            EditorPrefs.SetInt("InteractionMode", originalMode);
            EditorPrefs.SetInt("ApplicationIdleTime", originalIdle);
            Set("no_throttling");
            Assert.AreEqual(1, EditorPrefs.GetInt("InteractionMode"));
            Assert.AreEqual(0, EditorPrefs.GetInt("ApplicationIdleTime"));
            var snapshot = SessionState.GetString(RestoreKey, "");
            Assert.IsFalse(Set("no_throttling")["changed"].Value<bool>());
            Set("default");
            Assert.IsFalse(EditorPrefs.HasKey("InteractionMode"));
            Assert.IsFalse(EditorPrefs.HasKey("ApplicationIdleTime"));
            Assert.AreEqual(snapshot, SessionState.GetString(RestoreKey, ""));
            Set("restore");
            Assert.AreEqual(originalMode, EditorPrefs.GetInt("InteractionMode"));
            Assert.AreEqual(originalIdle, EditorPrefs.GetInt("ApplicationIdleTime"));
            Assert.IsFalse(EditorPreferencesFunctions.GetPreferences()["restoreAvailable"].Value<bool>());
        }

        [Test]
        public void RestorePreservesAbsentKeysAndIsNotAToggle()
        {
            EditorPrefs.DeleteKey("InteractionMode");
            EditorPrefs.DeleteKey("ApplicationIdleTime");
            Set("no_throttling");
            Set("restore");
            Assert.IsFalse(EditorPrefs.HasKey("InteractionMode"));
            Assert.IsFalse(EditorPrefs.HasKey("ApplicationIdleTime"));
            StringAssert.StartsWith("Error:", EditorPreferencesFunctions.SetEditorThrottling(new JObject { ["mode"] = "restore" }));
        }

        [Test]
        public void RestoreReadsSessionSnapshotAndRejectsCorruptionWithoutMutation()
        {
            // No static in-memory backup: simulate a snapshot left across domain reload.
            SessionState.SetString(RestoreKey, "{\"modePresent\":true,\"mode\":3,\"idlePresent\":true,\"idle\":22}");
            Set("restore");
            Assert.AreEqual(3, EditorPrefs.GetInt("InteractionMode"));
            Assert.AreEqual(22, EditorPrefs.GetInt("ApplicationIdleTime"));
            SessionState.SetString(RestoreKey, "{}");
            StringAssert.StartsWith("Error:", EditorPreferencesFunctions.SetEditorThrottling(new JObject { ["mode"] = "restore" }));
            Assert.AreEqual(3, EditorPrefs.GetInt("InteractionMode"));
            Assert.AreEqual(22, EditorPrefs.GetInt("ApplicationIdleTime"));
        }
    }
}
