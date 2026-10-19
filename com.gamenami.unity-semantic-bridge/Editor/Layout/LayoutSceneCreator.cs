using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>
    /// create_layout_scene: a new level from a template level scene. The template's managers, cameras, UI and
    /// lighting come along; its level asset (GridBoard.LevelOwner, e.g. NK's Level Info) is copied too and the
    /// copy's references repointed, so applying a layout to the new scene never rewrites the template's level.
    /// The template's [Generated Layout] is removed and the board can be resized (IGridRulesAdapter.TryWriteBoardSize).
    /// The copy is opened additively only to edit it, then saved and closed; Build Settings and other scenes are
    /// left alone.
    /// </summary>
    public static class LayoutSceneCreator
    {
        // MCP entry point (method "create_layout_scene").
        public static string CreateLayoutScene(JObject mcpMessage)
        {
            mcpMessage ??= new JObject();
            Cell? size = null;
            var sizeToken = mcpMessage["size"];
            if (sizeToken != null && sizeToken.Type != JTokenType.Null)
            {
                if (!(sizeToken is JArray pair) || pair.Count != 2 || pair.Any(t => t.Type != JTokenType.Integer))
                    throw new BridgeToolException("size must be [width, height] in cells.");
                size = new Cell((int)pair[0], (int)pair[1]);
            }
            return Create(mcpMessage["template"]?.Value<string>(), mcpMessage["path"]?.Value<string>(),
                mcpMessage["level"]?.Value<string>(), size, LayoutAdapters.Find());
        }

        /// <param name="levelPath">Where the level asset copy goes; default: beside the template's level asset, named after the new scene.</param>
        /// <param name="size">New board size in cells; the template's when null.</param>
        public static string Create(string templatePath, string scenePath, string levelPath, Cell? size, IGridRulesAdapter adapter)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new BridgeToolException("Exit Play Mode before creating a level scene.");
            templatePath = CheckScenePath(templatePath, "template");
            scenePath = CheckScenePath(scenePath, "path");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(templatePath) == null) throw new BridgeToolException($"No scene at {templatePath}.");
            if (File.Exists(scenePath)) throw new BridgeToolException($"{scenePath} already exists; pick a new path.");
            CheckFolder(scenePath);
            if (size.HasValue && (size.Value.X <= 0 || size.Value.Y <= 0))
                throw new BridgeToolException($"size must be positive (was {size.Value.X}x{size.Value.Y}).");
            var openTemplate = SceneManager.GetSceneByPath(templatePath);
            if (openTemplate.isLoaded && openTemplate.isDirty)
                throw new BridgeToolException($"{templatePath} is open with unsaved changes; save it first (the copy is taken from the file).");

            if (!AssetDatabase.CopyAsset(templatePath, scenePath)) throw new BridgeToolException($"Could not copy {templatePath} to {scenePath}.");
            var previousActive = SceneManager.GetActiveScene();
            var scene = default(Scene);
            string copiedLevel = null;
            try
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                if (!adapter.TryGetBoard(scene, out var board, out var boardError))
                    throw new BridgeToolException($"{templatePath} is not a level scene: {boardError}");
                var templateSize = new Cell(board.Width, board.Height);
                var lines = new List<string>();

                var owner = board.LevelOwner;
                if (owner != null && AssetDatabase.Contains(owner))
                {
                    var ownerPath = AssetDatabase.GetAssetPath(owner);
                    if (!AssetDatabase.IsMainAsset(owner))
                        throw new BridgeToolException($"The level ({owner.name}) is a sub-asset of {ownerPath}; only main assets can be copied.");
                    levelPath = string.IsNullOrEmpty(levelPath)
                        ? $"{Path.GetDirectoryName(ownerPath)?.Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(scenePath)}{Path.GetExtension(ownerPath)}"
                        : CheckAssetPath(levelPath, Path.GetExtension(ownerPath));
                    if (File.Exists(levelPath)) throw new BridgeToolException($"{levelPath} already exists; pass a new level path.");
                    CheckFolder(levelPath);
                    if (!AssetDatabase.CopyAsset(ownerPath, levelPath)) throw new BridgeToolException($"Could not copy {ownerPath} to {levelPath}.");
                    copiedLevel = levelPath;
                    var level = AssetDatabase.LoadMainAssetAtPath(levelPath);
                    var repointed = Repoint(scene, owner, level);
                    if (repointed == 0) throw new BridgeToolException($"Nothing in the scene references {ownerPath}, so the copy could not be wired in.");
                    lines.Add($"Level {levelPath} (copy of {ownerPath}; {repointed} reference{(repointed == 1 ? "" : "s")} repointed).");
                }
                else if (!string.IsNullOrEmpty(levelPath))
                {
                    throw new BridgeToolException("The template's level lives in the scene itself, so there is no level asset to copy; omit level.");
                }
                else
                {
                    lines.Add("The level lives in the scene, so it was copied with it.");
                }

                var generated = scene.GetRootGameObjects().Where(go => go.name == LayoutApplier.RootName).ToList();
                foreach (var root in generated) Object.DestroyImmediate(root);
                if (generated.Count > 0) lines.Add($"Removed the template's {LayoutApplier.RootName}.");

                if (size.HasValue && size.Value != templateSize)
                {
                    if (!adapter.TryWriteBoardSize(scene, size.Value.X, size.Value.Y, out var sizeError)) throw new BridgeToolException(sizeError);
                    if (!adapter.TryGetBoard(scene, out board, out boardError)) throw new BridgeToolException(boardError);
                    if (board.Width != size.Value.X || board.Height != size.Value.Y)
                        throw new BridgeToolException($"The adapter set the board to {board.Width}x{board.Height}, not {size.Value.X}x{size.Value.Y}.");
                }
                lines.Insert(0, $"Created {scenePath} from {templatePath}: board {board.Width}x{board.Height}" +
                                (templateSize.X != board.Width || templateSize.Y != board.Height ? $" (template {templateSize.X}x{templateSize.Y})." : "."));

                var deploy = adapter.ReadDeployCells(scene);
                var enemies = adapter.ReadEnemySpawns(scene);
                lines.Add($"The level keeps the template's {deploy.Count} deploy cells and {enemies.Count} enemy spawns; apply_layout replaces them.");
                bool OnBoard(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < board.Width && c.Y < board.Height;
                foreach (var cell in deploy.Where(c => !OnBoard(c))) lines.Add($"DEPLOY_OUT_OF_BOUNDS {cell} is off the new board");
                foreach (var enemy in enemies.Where(e => !OnBoard(e.Cell))) lines.Add($"ENEMY_OUT_OF_BOUNDS {enemy.Cell} is off the new board");

                if (board.LevelOwner != null && AssetDatabase.Contains(board.LevelOwner))
                {
                    EditorUtility.SetDirty(board.LevelOwner);
                    AssetDatabase.SaveAssetIfDirty(board.LevelOwner);
                }
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new BridgeToolException($"Could not save {scenePath}.");
                lines.Add("Saved; not opened and not added to Build Settings. Open it with open_scene before apply_layout.");
                return string.Join("\n", lines);
            }
            catch
            {
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                scene = default;
                AssetDatabase.DeleteAsset(scenePath);
                if (copiedLevel != null) AssetDatabase.DeleteAsset(copiedLevel);
                throw;
            }
            finally
            {
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        // Every serialized reference to `from` in the scene's components now points at `to`.
        private static int Repoint(Scene scene, Object from, Object to)
        {
            var count = 0;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue; // missing script
                var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                var changed = false;
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue != from) continue;
                    property.objectReferenceValue = to;
                    changed = true;
                    count++;
                }
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return count;
        }

        private static string CheckScenePath(string path, string name)
        {
            if (string.IsNullOrEmpty(path)) throw new BridgeToolException($"{name} is required (a project path under Assets/ ending .unity).");
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/") || path.Contains("..") || !path.EndsWith(".unity"))
                throw new BridgeToolException($"{name} must be a project path under Assets/ ending .unity (was '{path}').");
            return path;
        }

        private static string CheckAssetPath(string path, string extension)
        {
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/") || path.Contains("..") || !path.EndsWith(extension))
                throw new BridgeToolException($"level must be a project path under Assets/ ending {extension} (was '{path}').");
            return path;
        }

        private static void CheckFolder(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                throw new BridgeToolException($"Folder '{directory}' does not exist.");
        }
    }
}
