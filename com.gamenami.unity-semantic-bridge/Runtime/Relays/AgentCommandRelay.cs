using System;
using UnityEngine;

public static class AgentCommandRelay
{
    // General events for the game to subscribe to
    public static Action<Vector2> OnScreenClickReceived;
    public static Action<string> OnButtonClickReceived;
    public static Action<string> OnCommandReceived;

    public static void ExecuteScreenClick(Vector2 screenPosition) => OnScreenClickReceived?.Invoke(screenPosition);
    public static void ExecuteButtonClick(string buttonName) => OnButtonClickReceived?.Invoke(buttonName);
    public static void CommandReceived(string agentIntent) => OnCommandReceived?.Invoke(agentIntent);
}