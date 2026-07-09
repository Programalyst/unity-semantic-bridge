using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Linq;

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
            var rootGameObjects = activeScene.GetRootGameObjects();
            foreach (var go in rootGameObjects)
            {
                AddNodesRecursively(go, scene, "", 0, config, mainCamera);
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

        // Gameplay Mode overload. Uses Scriptableobject
        public static SemanticScene Generate(SemanticSceneConfigSo settings)
        {
            var activeScene = SceneManager.GetActiveScene();
            var sceneName = string.IsNullOrEmpty(activeScene.name) ? "UntitledScene" : activeScene.name;
            var sceneData = new SemanticScene
            {
                sceneName = sceneName,
                sceneContext = "Each entry in the JSON represents a single interactable entity. " +
                               "To interact with a unit or obstacle, use the viewportPos of its root node.",
                // Initialize layer statistics if toggled true
                LayerCounts = settings.includeLayerStats ? new Dictionary<string, int>() : null
            };
            
            var mainCamera = Camera.main;
            foreach (var rootGameObject in activeScene.GetRootGameObjects())
            {
                AddNodesRecursively(rootGameObject, sceneData, null, 0, settings, mainCamera);
            }

            return sceneData;
        }

        private static void AddNodesRecursively(GameObject obj, SemanticScene scene, string parentPath,
            int currentDepth, SemanticSceneConfigSo settings, Camera mainCamera)
        {
            // --- OPTIMIZATIONS ---
            // Ignore disabled objects and their entire children sub-hierarchy
            if (!obj.activeSelf) return; 
            
            // Check if the object's layer bit is toggled in exclusion mask
            if (((1 << obj.layer) & settings.excludeLayers) != 0) return;
            
            // Stop if _maxDepth exceeded
            if (currentDepth > settings.maxDepth) return;
            
            // Prune branch traversal if we hit a SkinnedMeshRenderer (Character Rig)
            // This ignores all bones, joints, and target points inside the character
            if (obj.GetComponent<SkinnedMeshRenderer>()) return;
            
            // Layer STATISTICS for debugging: Count objects per layer
            var layerName = LayerMask.LayerToName(obj.layer);
            if (settings.includeLayerStats)
            {
                scene.LayerCounts.TryAdd(layerName, 0);
                scene.LayerCounts[layerName]++;
            }
            
            // --- GENERALIZABLE CULLING LOGIC ---
            SimpleVec2? vPos = GetViewportPos(obj, mainCamera); // returns null if obj is outside the viewport
            
            // If it's a "Grid Tile" but NOT visible, skip it. 
            // This allows the LLM to see the 100 tiles on screen but ignore the 2,400 off-screen.
            if (layerName == "Grid Tiles" && vPos != null) return;
            
            // Build the breadcrumb path
            var currentPath = string.IsNullOrEmpty(parentPath) ? obj.name : $"{parentPath}/{obj.name}";

            // Use heuristics to determine if an object should be included
            if (HeuristicFilters.IsGameplayObject(obj))
            {
                var node = new SemanticNode {
                    name = obj.name,
                    path = currentPath,
                    viewportPos = vPos,
                };

                // For Editor time work such as changing object placements
                if (settings.includeTransforms)
                {
                    node.layer = layerName;
                    node.position = obj.transform.position;
                    node.rotation = obj.transform.eulerAngles;
                    node.scale = obj.transform.localScale == Vector3.one ? null : obj.transform.localScale; // exclude scale if it is 1.0, 1.0, 1.0
                }

                if (settings.includeComponents)
                {
                    // InstanceId only needed if operating on components in Editor mode
                    node.instanceId = obj.GetInstanceID();
                    
                    var uniqueComponents = new HashSet<string>();
                
                    foreach (var comp in obj.GetComponents<Component>()) 
                    {
                        // Use heuristics to determine if a component gives context to the LLM
                        if (HeuristicFilters.IsFunctionalComponent(comp))
                        {
                            uniqueComponents.Add(comp.GetType().Name);
                        }
                    }
                    
                    // Convert back to List for the SemanticNode (if there are any)
                    if (uniqueComponents.Count > 0)
                    {
                        node.components = new List<string>(uniqueComponents);
                    }
                }
                
                scene.nodes.Add(node);
            }
            
            // Continue recursion for child nodes
            foreach (Transform child in obj.transform)
            {
                var newDepth = HeuristicFilters.IsFolderObject(obj) ? currentDepth : currentDepth + 1;
                // Pass the currentPath as the parentPath for the next generation
                AddNodesRecursively(child.gameObject, scene, currentPath, newDepth, settings, mainCamera);
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
