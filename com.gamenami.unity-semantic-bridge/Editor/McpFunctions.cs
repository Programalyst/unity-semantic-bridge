using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
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
            var sceneData = SemanticBridgeWindow.Instance.GetEditorHierarchy();
            var sceneJson = JsonConvert.SerializeObject(sceneData, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });
            return sceneJson;
        }

        public static string FindAssetReferences(JObject mcpMessage)
        {
            var assetPath = mcpMessage["path"]?.ToString();
            // Finds everything this asset uses (dependencies)
            string[] deps = AssetDatabase.GetDependencies(assetPath, false);
            var responseContent = deps.Length > 0 ? string.Join("\n", deps) : "No references found.";
            return responseContent;
        }
        
        public static string GetFolderStructure(JObject mcpMessage)
        {
            // 1. Get the path and ensure it's Unity-friendly (forward slashes)
            var folderPath = mcpMessage["path"]?.ToString() ?? "Assets";
            folderPath = folderPath.Replace("\\", "/").TrimEnd('/');

            // 2. Get Sub-folders (using AssetDatabase is much faster)
            string[] subFolders = AssetDatabase.GetSubFolders(folderPath);
    
            // 3. Get Files in this specific folder (depth = false to avoid recursion)
            // We use a filter to ignore .meta files and system files
            string[] assets = AssetDatabase.FindAssets("", new[] { folderPath });
            var filesInFolder = new List<string>();

            foreach (var guid in assets)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                // Only include files DIRECTLY in this folder (not in subfolders)
                if (System.IO.Path.GetDirectoryName(path)?.Replace("\\", "/") == folderPath)
                {
                    filesInFolder.Add(System.IO.Path.GetFileName(path));
                }
            }

            // 4. Format for Claude
            var sb = new StringBuilder();
            sb.AppendLine($"--- Contents of {folderPath} ---");
    
            sb.AppendLine("\n[Directories]:");
            foreach (var dir in subFolders) sb.AppendLine($"  > {System.IO.Path.GetFileName(dir)}/");
    
            sb.AppendLine("\n[Files]:");
            foreach (var file in filesInFolder) sb.AppendLine($"  - {file}");

            return sb.ToString();
        }
    }
}