using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class AssetFunctions
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
                if (Path.GetDirectoryName(path)?.Replace("\\", "/") == folderPath)
                {
                    filesInFolder.Add(Path.GetFileName(path));
                }
            }

            // 4. Format for Claude
            var sb = new StringBuilder();
            sb.AppendLine($"--- Contents of {folderPath} ---");
    
            sb.AppendLine("\n[Directories]:");
            foreach (var dir in subFolders) sb.AppendLine($"  > {Path.GetFileName(dir)}/");
    
            sb.AppendLine("\n[Files]:");
            foreach (var file in filesInFolder) sb.AppendLine($"  - {file}");

            return sb.ToString();
        }
        
        public static string WriteScript(JObject mcpMessage)
        {
            var path = mcpMessage["path"]?.ToString();
            var content = mcpMessage["content"]?.ToString();
            var confirm = mcpMessage["confirm"]?.ToObject<bool>() ?? false;

            if (string.IsNullOrEmpty(path))
                return "Failed: 'path' is required.";

            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                    return "Refused: path escapes project root.";

                var exists = File.Exists(fullPath);
                if (exists && !confirm)
                {
                    var existing = File.ReadAllText(fullPath);
                    return $"CONFIRM_REQUIRED: '{path}' already exists ({existing.Length} chars). " +
                           $"Re-call with confirm=true to overwrite.";
                }

                var directory = Path.GetDirectoryName(fullPath);
                if (directory != null && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(fullPath, content);

                var token = CompileWatcher.BeginWrite();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();

                return $"Wrote {path}. Compilation triggered (token={token}). " +
                       $"Call get_compilation_status to get the result.";
            }
            catch (Exception e)
            {
                return $"Failed to write script: {e.Message}";
            }
        }

        // Separate tool — the client calls this after a short delay / in a poll loop.
        public static string GetCompilationStatus(JObject mcpMessage)
        {
            var (status, errors) = CompileWatcher.Poll();
            switch (status)
            {
                case "compiling" or "pending":
                    return "PENDING: still compiling, poll again shortly.";
                case "failed":
                    return $"FAILED:\n{errors}";
                default:
                    return "SUCCESS: compiled cleanly.";
            }
        }
    }
}