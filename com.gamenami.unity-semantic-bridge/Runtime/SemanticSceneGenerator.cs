using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Gamenami.UnitySemanticBridge
{
    public static class SemanticSceneGenerator
    {
        public static string Generate(SemanticSceneConfigSo settings)
        {
            var activeScene = SceneManager.GetActiveScene();
            var sceneName = string.IsNullOrEmpty(activeScene.name) ? "UntitledScene" : activeScene.name;
            var sceneData = new SemanticScene
            {
                sceneName = sceneName,
                sceneContext = "Each entry in the JSON represents a single interactable entity. " +
                               "To interact with a unit or obstacle, use the viewportPos of its root node.",
                // Initialize layer statistics if toggled true
                layerCounts = settings.includeLayerStats ? new Dictionary<string, int>() : null
            };

            foreach (GameObject root in activeScene.GetRootGameObjects())
            {
                AddNodesRecursively(root, sceneData, null, 0, settings);
            }

            return JsonConvert.SerializeObject(sceneData, new JsonSerializerSettings {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });
        }

        private static void AddNodesRecursively(GameObject obj, SemanticScene scene, string parentPath,
            int currentDepth, SemanticSceneConfigSo settings)
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
                scene.layerCounts.TryAdd(layerName, 0);
                scene.layerCounts[layerName]++;
            }
            
            // --- GENERALIZABLE CULLING LOGIC ---

            SimpleVec2? vPos = GetViewportPos(obj); // returns null if obj is outside the viewport
            
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
                
                scene.entities.Add(node);
            }
            
            // Continue recursion for child nodes
            foreach (Transform child in obj.transform)
            {
                var newDepth = HeuristicFilters.IsFolderObject(obj) ? currentDepth : currentDepth + 1;
                // Pass the currentPath as the parentPath for the next generation
                AddNodesRecursively(child.gameObject, scene, currentPath, newDepth, settings);
            }
        }
        
        private static SimpleVec2? GetViewportPos(GameObject obj) 
        {
            Camera cam = Camera.main;
            
            if (!cam) return null;
            Vector3 viewPoint = cam.WorldToViewportPoint(obj.transform.position);
            
            if (viewPoint is { z: > 0, x: >= 0 and <= 1, y: >= 0 and <= 1 })
                return new SimpleVec2(viewPoint.x, 1f - viewPoint.y);
            
            return null;
        }
    }
}
