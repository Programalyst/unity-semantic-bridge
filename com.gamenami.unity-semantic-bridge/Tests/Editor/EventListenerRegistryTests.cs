using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    public class EventListenerRegistryTests
    {
        static readonly DateTime Now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        string _saved;

        [SetUp]
        public void Save()
        {
            _saved = EventListenerRegistry.Serialize();
            EventListenerRegistry.Clear();
        }

        [TearDown]
        public void Restore() => EventListenerRegistry.Load(_saved, DateTime.UtcNow);

        [TestCase("http://127.0.0.1:50123/rpc", "http://127.0.0.1:50123/rpc")]
        [TestCase("http://localhost:50123/rpc", "http://127.0.0.1:50123/rpc")]
        public void LoopbackRpcUrlsAreNormalized(string url, string expected)
        {
            Assert.AreEqual(expected, EventListenerRegistry.Normalize(url, out var error));
            Assert.IsNull(error);
        }

        [TestCase(null)]
        [TestCase("not a url")]
        [TestCase("https://127.0.0.1:50123/rpc")]
        [TestCase("http://example.com:50123/rpc")]
        [TestCase("http://127.0.0.1:50123/other")]
        [TestCase("http://127.0.0.1/rpc")]
        [TestCase("http://127.0.0.1:80/rpc")]
        public void OtherUrlsAreRejected(string url)
        {
            Assert.IsNotNull(EventListenerRegistry.Register(url, 60, Now));
            CollectionAssert.IsEmpty(EventListenerRegistry.Live(Now));
        }

        [Test]
        public void RegistrationsExpireUnlessRenewed()
        {
            Assert.IsNull(EventListenerRegistry.Register("http://127.0.0.1:50123/rpc", 60, Now));
            Assert.IsNull(EventListenerRegistry.Register("http://127.0.0.1:50124/rpc", 60, Now));
            Assert.IsNull(EventListenerRegistry.Register("http://127.0.0.1:50124/rpc", 60, Now.AddSeconds(50)));
            CollectionAssert.AreEquivalent(new[] { "http://127.0.0.1:50124/rpc" }, EventListenerRegistry.Live(Now.AddSeconds(70)));
            CollectionAssert.IsEmpty(EventListenerRegistry.Live(Now.AddSeconds(111)));
        }

        [Test]
        public void TtlIsClamped()
        {
            EventListenerRegistry.Register("http://127.0.0.1:50123/rpc", 1, Now);
            Assert.AreEqual(1, EventListenerRegistry.Live(Now.AddSeconds(EventListenerRegistry.MinTtlSeconds - 1)).Count);
            EventListenerRegistry.Register("http://127.0.0.1:50124/rpc", 1e9, Now);
            CollectionAssert.IsEmpty(EventListenerRegistry.Live(Now.AddSeconds(EventListenerRegistry.MaxTtlSeconds + 1)));
        }

        [Test]
        public void ListenerCountIsCappedButRenewalsStillSucceed()
        {
            for (var i = 0; i < EventListenerRegistry.MaxListeners; i++)
                Assert.IsNull(EventListenerRegistry.Register($"http://127.0.0.1:{50000 + i}/rpc", 60, Now));
            Assert.IsNotNull(EventListenerRegistry.Register("http://127.0.0.1:51000/rpc", 60, Now));
            Assert.IsNull(EventListenerRegistry.Register("http://127.0.0.1:50000/rpc", 60, Now));
        }

        [Test]
        public void RemovedListenersStopReceiving()
        {
            EventListenerRegistry.Register("http://127.0.0.1:50123/rpc", 60, Now);
            EventListenerRegistry.Remove("http://127.0.0.1:50123/rpc");
            CollectionAssert.IsEmpty(EventListenerRegistry.Live(Now));
        }

        [Test]
        public void SerializedRegistrationsSurviveReloadAndDropExpiredOrInvalidEntries()
        {
            EventListenerRegistry.Register("http://127.0.0.1:50123/rpc", 60, Now);
            EventListenerRegistry.Register("http://127.0.0.1:50124/rpc", 20, Now);
            var text = EventListenerRegistry.Serialize() + "http://example.com:1/rpc 1\ngarbage\n";
            EventListenerRegistry.Clear();
            EventListenerRegistry.Load(text, Now.AddSeconds(30));
            CollectionAssert.AreEquivalent(new[] { "http://127.0.0.1:50123/rpc" }, EventListenerRegistry.Live(Now.AddSeconds(30)));
        }

        [Test]
        public void RegisterRpcReportsRegistrationAndRejectsBadUrls()
        {
            var savedSession = SessionState.GetString("UnitySemanticBridge_EventListeners", "");
            try
            {
                var ok = JObject.Parse(EditorBridge.RegisterEventListener(new JObject { ["url"] = "http://localhost:50123/rpc", ["ttlSeconds"] = 30 }));
                Assert.AreEqual("http://127.0.0.1:50123/rpc", ok["registered"].ToString());
                Assert.AreEqual(1, ok["listeners"].Value<int>());
                StringAssert.StartsWith("Error:", EditorBridge.RegisterEventListener(new JObject { ["url"] = "http://example.com:50123/rpc" }));
            }
            finally
            {
                SessionState.SetString("UnitySemanticBridge_EventListeners", savedSession);
            }
        }
    }
}
