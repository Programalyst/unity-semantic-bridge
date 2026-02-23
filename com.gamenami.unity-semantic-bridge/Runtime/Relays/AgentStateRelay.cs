namespace Gamenami.UnitySemanticBridge
{
    public static class AgentStateRelay
    {
        // The Game Project sets this
        public static System.Func<bool> OnCheckCanAct;
        public static System.Func<bool> OnCheckIsProcessing;

        public static bool CanAgentAct() => OnCheckCanAct?.Invoke() ?? false;
        public static bool IsProcessing() => OnCheckIsProcessing?.Invoke() ?? false;
    }
}