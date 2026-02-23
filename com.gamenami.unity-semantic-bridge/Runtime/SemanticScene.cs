using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

[System.Serializable]
public class SemanticScene
{
    public string sceneName;
    // Info for LLM on the limitations of the exported scene graph (e.g. excluded layers, etc)
    public string sceneContext;
    
    public List<SemanticNode> entities = new List<SemanticNode>();
    
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, int> layerCounts = new Dictionary<string, int>();
}

[System.Serializable]
public class SemanticNode 
{
    public string name;
    public string path; // New: e.g., "Obstacles/Wall"
    
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string layer;
    
    // ignore default values e.g. 0,0,0 to shrink JSON
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public SimpleVec3? position;
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public SimpleVec3? rotation;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public SimpleVec3? scale;
    
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public SimpleVec2? viewportPos; // 0.0 to 1.0 (Top-Left to Bottom-Right)
    
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string> components; // e.g. ["SphereCollider", "Rigidbody"]
    
}


// Vector3 is a struct containing properties like normalized and magnitude, which return another Vector3, 
// creating an infinite recursion during serialization. Thus, we use a custom struct instead.
[System.Serializable]
public struct SimpleVec3 
{
    public float x, y, z;
    public SimpleVec3(Vector3 v) 
    { 
        // 3 decimal places is usually enough for layout tasks
        x = (float)System.Math.Round(v.x, 3); 
        y = (float)System.Math.Round(v.y, 3); 
        z = (float)System.Math.Round(v.z, 3); 
    }
    
    // This allows: SimpleVec3 myVec = someGameObject.transform.position;
    public static implicit operator SimpleVec3(Vector3 v) => new SimpleVec3(v);
}

[System.Serializable]
public struct SimpleVec2 {
    public float x, y;
    public SimpleVec2(float x, float y) 
    { 
        // Primarily used for screen coordinates values 0-1, thus, 5 decimal places
        this.x = (float)System.Math.Round(x, 5); 
        this.y = (float)System.Math.Round(y, 5); 
    }
}