using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge
{
    public class SceneGenerateSettings
    {
        public int MaxDepth = 2;
        public int MaxNodes = 300; // node cap to avoid overwhelming LLM context / freezing Editor on huge scenes
        public bool IncludeLayers = true; // if true, will return name of the layer the gameobject is in
        public bool IncludeComponents = true;
        public bool IncludePositions = false; // include transform.position
        public bool OnlyMainCamVisible = false; // cull objects out of the main camera's view
        public bool IgnoreDisabled = false; // ignore disabled objects in Unity
        public int? RootInstanceId;
    }

    public static class SemanticSceneGenerator
    {
        public static SemanticScene Generate(SceneGenerateSettings config)
        {
            var activeScene = SceneManager.GetActiveScene();
            var scene = new SemanticScene
            {
                sceneName = activeScene.name,
                sceneContext = "Each entry in the JSON represents a gameObject."
            };
            
            if (config.IncludeLayers)
            {
                scene.LayerCounts = new Dictionary<string, int>();
            }

            var mainCamera = Camera.main;

            // LLM opted to specify a root object to start the traversal from
            if (config.RootInstanceId.HasValue)
            {
                var rootId = config.RootInstanceId.Value;
                var rootObj = EditorUtility.InstanceIDToObject(rootId) as GameObject;

                if (rootObj == null) // rootObj can still be null if instanceId is invalid
                    throw new BridgeToolException($"No GameObject found for instance_id {rootId}.");
                
                AddNodesRecursively(rootObj, scene, "", config.MaxDepth, config, mainCamera);
            }
            else // traverse from all scene root objects
            {
                var rootGameObjects = activeScene.GetRootGameObjects();
                foreach (var go in rootGameObjects)
                {
                    AddNodesRecursively(go, scene, "", 0, config, mainCamera);
                }
            }
            
            if (scene.truncated)
            {
                scene.sceneContext += $" NOTE: Result truncated at {config.MaxNodes} nodes ({scene.totalNodesVisited} total encountered). " +
                                      "Use a smaller 'depth' or query a specific subtree path to see more.";
            }

            return scene;
        }
        
        private static void AddNodesRecursively(GameObject go, SemanticScene scene, string parentPath, 
            int currentDepth, SceneGenerateSettings config, Camera mainCamera)
        {
            scene.totalNodesVisited++;
            if (scene.nodes.Count >= config.MaxNodes)
            {
                scene.truncated = true;
                return; // stop adding. Still increment totalNodesVisited for recursive calls that reach here
            }

            // Ignore disabled objects and their entire children sub-hierarchy
            if (config.IgnoreDisabled && !go.activeSelf) { return; }

            // Ignore objects out of the main camera view
            SimpleVec2? vPos = null;
            if (config.OnlyMainCamVisible)
            {
                vPos = GetViewportPos(go, mainCamera);
                if (vPos == null) return;
            }

            // Prune branch traversal if we hit a SkinnedMeshRenderer (Character Rig)
            // This ignores all bones, joints, and target points inside the character's rig
            if (go.GetComponent<SkinnedMeshRenderer>()) return;

            var currentPath = string.IsNullOrEmpty(parentPath) ? go.name : $"{parentPath}/{go.name}";
    
            var node = new SemanticNode
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = currentPath,
                viewportPos = vPos // populated for free if OnlyMainCamVisible was on; null otherwise
            };

            if (config.IncludeLayers)
            {
                node.layer = LayerMask.LayerToName(go.layer);
            }
            
            if (config.IncludeComponents)
            {
                node.components = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToList();
            }

            if (config.IncludePositions)
            {
                node.position = new SimpleVec3(go.transform.position);
            }

            scene.nodes.Add(node);

            // Recursion check
            if (currentDepth >= config.MaxDepth) return;
            
            foreach (Transform child in go.transform)
            {
                if (scene.nodes.Count >= config.MaxNodes)
                {
                    scene.truncated = true; 
                    break; // early exit before recursing further
                } 
                AddNodesRecursively(child.gameObject, scene, currentPath, currentDepth + 1, config, mainCamera);
            }
        }


        
        private static SimpleVec2? GetViewportPos(GameObject obj, Camera cam) 
        {
            if (!cam) return null;
            var viewPoint = cam.WorldToViewportPoint(obj.transform.position);
            
            if (viewPoint is { z: > 0, x: >= 0 and <= 1, y: >= 0 and <= 1 })
                return new SimpleVec2(viewPoint.x, 1f - viewPoint.y);
            
            return null;
        }
    }
}
