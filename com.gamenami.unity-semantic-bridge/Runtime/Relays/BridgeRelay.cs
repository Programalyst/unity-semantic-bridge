using System;

namespace Gamenami.UnitySemanticBridge
{
    public static class BridgeRelay
    {
        // The Runtime Agent calls this
        // MpeBridge listens to this event
        public static Action<string, byte[]> OnRequestSendToServer;

        public static void Send(string json, byte[] image)
        {
            OnRequestSendToServer?.Invoke(json, image);
        }
    }
}