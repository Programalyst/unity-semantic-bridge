using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Gamenami.UnitySemanticBridge
{
    public class CanvasButtonFinder : AgentSingleton<CanvasButtonFinder>
    {
        public string GetButtonNames()
        {
            var buttons = GetAllInteractableButtons();
            var names = buttons.Select(b => b.name).ToList();
            
            string result = names.Count > 0 ? string.Join(", ", names) : "None";
            Debug.Log($"[Agent] Found {buttons.Count} interactable buttons: {result}");
            
            return result;
        }

        public void ClickButton(string buttonName)
        {
            // Dynamic Discovery: Find the button across ALL active canvases
            var target = GetAllInteractableButtons()
                .FirstOrDefault(b => b.name.Equals(buttonName, System.StringComparison.OrdinalIgnoreCase));

            if (target != null)
            {
                BridgeRelay.OnNotifyAgentLogWindow?.Invoke($"Invoking UI Button: <b>{target.name}</b>");
                target.onClick.Invoke();
            }
            else
            {
                BridgeRelay.OnNotifyAgentLogWindow?.Invoke($"Button '{buttonName}' not found or not interactable.");
            }
        }

        private List<Button> GetAllInteractableButtons()
        {
            // 1. Find all active Canvases in the scene
            var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .Where(c => c.enabled && c.gameObject.activeInHierarchy);

            var interactableButtons = new List<Button>();

            foreach (var canvas in canvases)
            {
                // 2. Get all buttons under this canvas
                var buttons = canvas.GetComponentsInChildren<Button>(false);
                
                foreach (var btn in buttons)
                {
                    // 3. Ensure button is interactable and not blocked by a disabled group
                    if (btn.interactable && btn.gameObject.activeInHierarchy)
                    {
                        interactableButtons.Add(btn);
                    }
                }
            }

            return interactableButtons;
        }
    }
}