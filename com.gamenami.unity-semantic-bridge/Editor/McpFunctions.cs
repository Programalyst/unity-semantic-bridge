using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class McpFunctions
    {
        public static string SearchAssets(JObject mcpMessage)
        {
            var filter = mcpMessage["filter"]?.ToString();
            var limit = Convert.ToInt32(mcpMessage["limit"]?.ToString());
            var searchInFolders = mcpMessage["folders"]?.ToObject<string[]>() ?? new[] { "Assets" };
            
            var guids = AssetDatabase.FindAssets(filter, searchInFolders);
            var paths = guids.Select(AssetDatabase.GUIDToAssetPath).ToList();
            
            // Limit results to prevent context overflow (mimicking 'head -n 10')
            var resultList = paths.Count > limit ? paths.GetRange(0, limit) : paths;
            var resultText = resultList.Count > 0 
                ? string.Join("\n", resultList) 
                : "No assets found matching that query.";
            return resultText;
        }

        public static string GetSemanticScene(JObject mcpMessage)
        {
            return SemanticBridgeWindow.Instance.GetEditorHierarchy();
        }

        public static string FindAssetReferences(JObject mcpMessage)
        {
            var assetPath = mcpMessage["path"]?.ToString();
            // Finds everything this asset uses (dependencies)
            string[] deps = UnityEditor.AssetDatabase.GetDependencies(assetPath, false);
            var responseContent = deps.Length > 0 ? string.Join("\n", deps) : "No references found.";
            return responseContent;
        }
        
        public static string GetFolderStructure(JObject mcpMessage)
        {
            var folderPath = mcpMessage["path"]?.ToString() ?? "Assets";
            var dirs = System.IO.Directory.GetDirectories(folderPath);
            var files = System.IO.Directory.GetFiles(folderPath);
            var responseContent = $"Folders in {folderPath}:\n" + string.Join("\n", dirs) + 
                                        $"\nFiles in {folderPath}:\n" + string.Join("\n", files);
            return responseContent;
        }
    }
}