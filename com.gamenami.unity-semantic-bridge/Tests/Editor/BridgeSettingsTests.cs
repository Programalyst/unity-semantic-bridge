using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    public class BridgeSettingsTests
    {
        [TestCase(1073, 1074)]
        [TestCase(1075, 1076)]
        [TestCase(BridgeSettings.MinPort, BridgeSettings.MaxPort)]
        public void ValidPortPairsAreAccepted(int listener, int events)
        {
            Assert.IsNull(BridgeSettings.ValidatePorts(listener, events));
        }

        [TestCase(1073, 1073)]
        [TestCase(0, 1074)]
        [TestCase(1023, 1074)]
        [TestCase(1073, 65536)]
        [TestCase(-1, 1074)]
        public void InvalidPortPairsAreRejected(int listener, int events)
        {
            Assert.IsNotNull(BridgeSettings.ValidatePorts(listener, events));
        }

        [Test]
        public void RejectedPortsLeaveStoredSettingsUnchanged()
        {
            var settings = BridgeSettings.instance;
            var listener = settings.ListenerPort;
            var events = settings.EventPort;
            Assert.IsNotNull(settings.SetPorts(listener, listener));
            Assert.AreEqual(listener, settings.ListenerPort);
            Assert.AreEqual(events, settings.EventPort);
        }
    }
}
