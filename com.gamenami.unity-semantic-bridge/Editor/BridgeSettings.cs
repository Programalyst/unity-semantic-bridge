using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Per-project bridge ports, stored in UserSettings/ (per user, per project, not committed),
    /// so several Editors can run the bridge side by side. Each Editor pairs with its own
    /// MCP server started with matching <c>--unity-port</c>/<c>--event-port</c>.
    /// </summary>
    [FilePath("UserSettings/UnitySemanticBridgeSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class BridgeSettings : ScriptableSingleton<BridgeSettings>
    {
        public const int DefaultListenerPort = 1073;
        public const int DefaultEventPort = 1074;
        public const int MinPort = 1024;
        public const int MaxPort = 65535;

        [SerializeField] private int listenerPort = DefaultListenerPort;
        [SerializeField] private int eventPort = DefaultEventPort;

        // Python -> Unity JSON-RPC listener.
        public int ListenerPort => listenerPort;
        // Unity -> Python event server.
        public int EventPort => eventPort;

        /// <summary>Returns null when the pair is usable, otherwise an error message.</summary>
        public static string ValidatePorts(int listener, int events)
        {
            if (listener < MinPort || listener > MaxPort)
                return $"Listener port must be between {MinPort} and {MaxPort}.";
            if (events < MinPort || events > MaxPort)
                return $"Event port must be between {MinPort} and {MaxPort}.";
            if (listener == events)
                return "Listener and event ports must differ.";
            return null;
        }

        /// <summary>Validates, stores and saves the ports. Returns null on success, otherwise an error message.</summary>
        public string SetPorts(int listener, int events)
        {
            var error = ValidatePorts(listener, events);
            if (error != null) return error;
            if (listener == listenerPort && events == eventPort) return null;
            listenerPort = listener;
            eventPort = events;
            Save(true);
            return null;
        }
    }
}
