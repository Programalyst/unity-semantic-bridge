using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static partial class UiAuthoring
    {
        static string lastToken, lastUndoName;
        static int lastGroup, expectedGroup;
        static bool undoInvalid;

        static UiAuthoring()
        {
            Undo.undoRedoPerformed += () => undoInvalid = true;
            Undo.postprocessModifications += modifications => { undoInvalid = true; return modifications; };
        }

        static string RememberUndo(int group, Scene scene)
        {
            lastToken = Guid.NewGuid().ToString("N"); lastGroup = group;
            expectedGroup = group + 1; lastUndoName = Undo.GetCurrentGroupName(); undoInvalid = false;
            return lastToken;
        }

        public static string UndoBatch(JObject message)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return "Error: Wait for an idle Edit Mode Editor.";
            Undo.FlushUndoRecordObjects();
            if (lastToken == null || message["undoToken"]?.Value<string>() != lastToken || undoInvalid ||
                Undo.GetCurrentGroup() != expectedGroup || Undo.GetCurrentGroupName() != lastUndoName)
                return "Error: This UI batch is no longer the untouched latest Undo group (or the Editor reloaded). Use Unity's Undo history; USB will not undo intervening edits.";
            Undo.RevertAllDownToGroup(lastGroup); lastToken = null;
            Canvas.ForceUpdateCanvases();
            return "UI batch undone. Nothing was saved. Scene may remain dirty.";
        }

        public static string SaveContext(JObject message)
        {
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) throw new ArgumentException("Wait for an idle Edit Mode Editor.");
                var go = EditorIdLookup.FromInstanceId(message["instanceId"]?.Value<int>() ?? 0) as GameObject;
                EnsureEditable(go);
                var stage = PrefabStageUtility.GetPrefabStage(go);
                var requested = message["path"]?.Value<string>();
                string path;
                if (stage != null)
                {
                    path = stage.assetPath;
                    if (requested != null && requested != path) throw new ArgumentException("Prefab Stage saves only to its existing asset path.");
                    CheckWritablePath(path, ".prefab");
                    PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, path, out bool ok);
                    if (!ok) throw new IOException("Unity could not save the prefab; inspect the Console.");
                }
                else
                {
                    path = go.scene.path;
                    if (string.IsNullOrEmpty(path)) path = requested;
                    else if (requested != null && requested != path) throw new ArgumentException("This scene already has a path; save_ui_context will not silently Save As another scene.");
                    if (string.IsNullOrEmpty(path)) throw new ArgumentException("Unsaved scene: supply a new project-owned Assets/.../*.unity path.");
                    CheckWritablePath(path, ".unity");
                    if (!Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException("Save directory does not exist. Create a project-owned folder first.");
                    if (string.IsNullOrEmpty(go.scene.path) && File.Exists(path)) throw new ArgumentException("Refusing to overwrite another scene. Choose a new path.");
                    if (!EditorSceneManager.SaveScene(go.scene, path)) throw new IOException("Unity could not save the scene.");
                }
                lastToken = null;
                return new JObject { ["saved"] = true, ["path"] = path, ["scope"] = stage != null ? "entire current prefab contents" : "entire containing scene, including other pending edits",
                    ["object"] = AuthoringReferences.Identity(go) }.ToString();
            }
            catch (Exception e) { return "Error: " + e.Message; }
        }
    }
}
