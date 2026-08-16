using System;

namespace Gamenami.UnitySemanticBridge
{
    // Minimal relay between EditorBridge and editor UI.
    public static class BridgeRelay
    {
        // Returns EditorBridge.IsConnected
        public static Func<bool> IsServerConnected = () => false;

        // Event to notify the UI Window to show a message
        public static Action<string> OnAgentMessage;
    }
}
