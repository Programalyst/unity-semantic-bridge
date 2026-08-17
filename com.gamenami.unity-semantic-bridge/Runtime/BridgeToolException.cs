using System;

namespace Gamenami.UnitySemanticBridge
{
    public class BridgeToolException : Exception
    {
        public BridgeToolException(string message) : base($"Error: {message}")
        {
        }
    }
}