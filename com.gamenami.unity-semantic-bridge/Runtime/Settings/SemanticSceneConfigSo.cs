using UnityEngine;

namespace Gamenami.UnitySemanticBridge
{
    [CreateAssetMenu(fileName = "New SemanticSceneConfig", menuName = "Gamenami/Semantic Scene Config")]
    public class SemanticSceneConfigSo : ScriptableObject
    {
        public int maxDepth = 0;
        public bool includeComponents = true;
        public bool includeTransforms = false;
        public LayerMask excludeLayers = 0;
        public bool includeLayerStats = false;
    }
}