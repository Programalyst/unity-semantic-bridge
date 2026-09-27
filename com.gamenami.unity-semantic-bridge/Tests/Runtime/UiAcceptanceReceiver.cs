using UnityEngine;
using UnityEngine.UI;

namespace Gamenami.UnitySemanticBridge.Tests
{
    // Test-only stand-in for a game's existing HUD/gameplay component. Tools never add it.
    public class UiAcceptanceReceiver : MonoBehaviour
    {
        public Button[] movementButtons;
        public Button weaponButton;
        public Graphic statusLabel;
        public int weaponIndex = -1;
        public string preserved = "keep me";
        public void SelectWeapon(int index) { weaponIndex = index; }
        public void Click() { weaponIndex++; }
        public void SetLabel(Graphic label) { statusLabel = label; }
        public float fraction;
        public bool armed;
        public string caption;
        public void SetFraction(float value) { fraction = value; }
        public void SetArmed(bool value) { armed = value; }
        public void SetCaption(string value) { caption = value; }
        public int UnsupportedReturn() => 1;
    }
}
