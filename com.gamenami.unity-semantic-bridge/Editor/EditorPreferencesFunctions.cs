using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class EditorPreferencesFunctions
    {
        // Matches Unity's PreferencesProvider.DrawInteractionModeOptions. These
        // are user preferences, not project assets or UnityEditor.InteractionMode.
        const string ModeKey = "InteractionMode";
        const string IdleKey = "ApplicationIdleTime";
        const string RestoreKey = "UnitySemanticBridge.InteractionMode.Restore";
        static readonly MethodInfo ApplyMethod = typeof(EditorApplication).GetMethod(
            "UpdateInteractionModeSettings", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);

        public static JObject GetPreferences()
        {
            int mode = EditorPrefs.GetInt(ModeKey, 0);
            string name = mode switch
            {
                0 => "default",
                1 => "no_throttling",
                2 => "monitor_refresh_rate",
                3 => "custom",
                _ => "unknown"
            };
            string label = mode switch
            {
                0 => "Default",
                1 => "No Throttling",
                2 => "Monitor Refresh Rate",
                3 => "Custom",
                _ => $"Unknown ({mode})"
            };
            return new JObject
            {
                ["interactionMode"] = name,
                ["interactionModeLabel"] = label,
                ["interactionModeRaw"] = mode,
                ["applicationIdleTimeMs"] = EditorPrefs.GetInt(IdleKey, 4),
                ["restoreAvailable"] = !string.IsNullOrEmpty(SessionState.GetString(RestoreKey, "")),
                ["canSetInteractionMode"] = ApplyMethod != null,
                ["scope"] = "editor_user"
            };
        }

        public static string SetEditorThrottling(JObject message)
        {
            var modeToken = message["mode"];
            if (modeToken?.Type != JTokenType.String)
                return "Error: 'mode' must be no_throttling, default, or restore.";
            var mode = modeToken.Value<string>();
            if (mode != "no_throttling" && mode != "default" && mode != "restore")
                return "Error: 'mode' must be no_throttling, default, or restore.";
            if (ApplyMethod == null)
                return "Error: This Unity version does not expose UpdateInteractionModeSettings; preferences were not changed.";

            var saved = SessionState.GetString(RestoreKey, "");
            if (mode == "restore" && string.IsNullOrEmpty(saved))
                return "Error: No previous Interaction Mode is saved in this Editor session.";

            var before = Capture();
            JObject target;
            try
            {
                target = mode == "restore" ? JObject.Parse(saved) : new JObject
                {
                    ["modePresent"] = mode == "no_throttling",
                    ["mode"] = mode == "no_throttling" ? 1 : 0,
                    ["idlePresent"] = mode == "no_throttling",
                    ["idle"] = mode == "no_throttling" ? 0 : 4
                };
                // Validate the entire saved snapshot before changing either key.
                ValidateSnapshot(target);
            }
            catch (Exception e)
            {
                return $"Error: Cannot read saved Interaction Mode: {e.Message}";
            }

            // Save once, before changing settings; SessionState survives domain reload.
            bool newSnapshot = mode != "restore" && string.IsNullOrEmpty(saved);
            if (newSnapshot) SessionState.SetString(RestoreKey, before.ToString(Newtonsoft.Json.Formatting.None));
            try
            {
                Write(target);
                ApplyMethod.Invoke(null, null);
            }
            catch (Exception e)
            {
                // Keep the original restore point if applying a later override fails.
                string rollbackError = "";
                try
                {
                    Write(before);
                    ApplyMethod.Invoke(null, null);
                }
                catch (Exception rollback) { rollbackError = $" Rollback could not be applied: {rollback.GetBaseException().Message}"; }
                if (newSnapshot && string.IsNullOrEmpty(rollbackError)) SessionState.EraseString(RestoreKey);
                return $"Error: Could not apply Interaction Mode: {e.GetBaseException().Message}.{rollbackError}";
            }

            if (mode == "restore") SessionState.EraseString(RestoreKey);
            return new JObject
            {
                ["requestedMode"] = mode,
                ["changed"] = !JToken.DeepEquals(before, target),
                ["editor_prefs"] = GetPreferences()
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        static JObject Capture() => new JObject
        {
            ["modePresent"] = EditorPrefs.HasKey(ModeKey),
            ["mode"] = EditorPrefs.GetInt(ModeKey, 0),
            ["idlePresent"] = EditorPrefs.HasKey(IdleKey),
            ["idle"] = EditorPrefs.GetInt(IdleKey, 4)
        };

        static void ValidateSnapshot(JObject snapshot)
        {
            if (snapshot["modePresent"]?.Type != JTokenType.Boolean ||
                snapshot["idlePresent"]?.Type != JTokenType.Boolean ||
                snapshot["mode"]?.Type != JTokenType.Integer || snapshot["idle"]?.Type != JTokenType.Integer)
                throw new InvalidOperationException("Invalid preference snapshot.");
            // Check Int32 conversion before Write can perform a partial mutation.
            snapshot["mode"].Value<int>();
            snapshot["idle"].Value<int>();
        }

        static void Write(JObject snapshot)
        {
            if (snapshot["modePresent"].Value<bool>()) EditorPrefs.SetInt(ModeKey, snapshot["mode"].Value<int>());
            else EditorPrefs.DeleteKey(ModeKey);
            if (snapshot["idlePresent"].Value<bool>()) EditorPrefs.SetInt(IdleKey, snapshot["idle"].Value<int>());
            else EditorPrefs.DeleteKey(IdleKey);
        }
    }
}
