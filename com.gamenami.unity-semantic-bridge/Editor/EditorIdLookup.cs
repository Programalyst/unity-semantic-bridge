using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Version-safe wrapper for Editor object-ID lookups.
    /// EditorUtility.InstanceIDToObject(int) is obsolete in Unity 6.3+ (CS0618, use
    /// EntityIdToObject), but EntityIdToObject doesn't exist on older Unity versions
    /// this package supports (see package.json). Int-based IDs keep working on 6.3+
    /// because EntityId converts implicitly to/from int.
    /// </summary>
    internal static class EditorIdLookup
    {
        public static UnityEngine.Object FromInstanceId(int instanceId)
        {
#if UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(instanceId);
#else
            return EditorUtility.InstanceIDToObject(instanceId);
#endif
        }
    }
}
