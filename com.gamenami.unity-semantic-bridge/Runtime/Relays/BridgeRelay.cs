using System;
using System.Collections.Generic;

namespace Gamenami.UnitySemanticBridge
{
    // Bridge between EditorBridge and SemanticBridgeWindow
    public static class BridgeRelay
    {
        // EditorBridge listens to this event
        public static Action<List<string>, SemanticScene, byte[]> OnRequestSendToServer;
        
        // Returns EditorBridge.IsConnected
        public static Func<bool> IsServerConnected = () => false;

        // Event to notify the UI Window to show a message
        public static Action<string> OnAgentMessage;

        public static void Send(List<string> agentActions, SemanticScene sceneData, byte[] image)
        {
            OnRequestSendToServer?.Invoke(agentActions, sceneData, image);
        }
    }
}