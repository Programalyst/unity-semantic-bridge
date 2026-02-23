using UnityEngine;

namespace Gamenami.UnitySemanticBridge
{
    public class AgentSingleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;

        public static T Instance 
        {
            get
            {
                if (_instance) return _instance;
                
                // Check if one was already placed in the scene manually
                _instance = FindObjectOfType<T>();
                if (_instance) return _instance;
                
                var go = new GameObject($"(Auto-Generated) {typeof(T).Name}");
                _instance = go.AddComponent<T>();
                DontDestroyOnLoad(go);
                
                return _instance;
            }
        }

        protected virtual void Awake() 
        {
            if (!_instance)
            {
                _instance = this as T;
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
            }
        }
    }
}
