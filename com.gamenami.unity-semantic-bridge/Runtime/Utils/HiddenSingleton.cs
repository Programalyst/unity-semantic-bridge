using UnityEngine;

namespace Gamenami.UnitySemanticBridge
{
    // Creates hidden gameobject at Runtime
    // Allows access to MonoBehaviours such as WaitForEndOfFrame
    public abstract class HiddenSingleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;

        public static T Instance 
        {
            get
            {
                if (_instance) return _instance;
                
                var go = new GameObject($"(Auto-Generated) {typeof(T).Name}")
                {
                    // This makes it invisible in the Hierarchy and 
                    // prevents it from being saved into the Scene file
                    hideFlags = HideFlags.HideAndDontSave
                };

                _instance = go.AddComponent<T>();
                
                // Keep it alive across scene loads
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