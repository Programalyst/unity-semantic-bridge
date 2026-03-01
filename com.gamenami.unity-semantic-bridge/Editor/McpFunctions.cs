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
            return "Not implemented";
        }
        
        public static string GetFolderStructure(JObject mcpMessage)
        {
            return "Not implemented";
        }
        
        public static string FindFiles(JObject mcpMessage)
        {
            return "Not implemented";
        }
    }
}