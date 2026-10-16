using System.IO;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>MCP entry points for layouts.</summary>
    public static class LayoutTools
    {
        /// <summary>
        /// bake_layout: reads the active scene back into layout JSON. With "path"
        /// (project-relative, under Assets/, ending .json) it also writes the layout there.
        /// </summary>
        public static string BakeLayout(JObject mcpMessage)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new BridgeToolException("Exit Play Mode before baking a layout; the bake reads the authored scene.");
            var result = LayoutBaker.Bake(SceneManager.GetActiveScene());
            var path = mcpMessage?["path"]?.Value<string>();
            if (string.IsNullOrEmpty(path)) return result.ToString();

            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/") || path.Contains("..") || !path.EndsWith(".json"))
                throw new BridgeToolException($"path must be project-relative under Assets/ and end in .json (was '{path}').");
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                throw new BridgeToolException($"Folder '{directory}' does not exist.");
            File.WriteAllText(path, LayoutJson.Serialize(result.Document));
            AssetDatabase.ImportAsset(path);
            return $"Wrote {path}\n{result}";
        }
    }
}
