using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // get_console_logs(clear) and get_project_settings' physics section.
    public class ConsoleAndPhysicsTests
    {
        static string Call(string method, JObject request)
        {
            request["method"] = method;
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(request, completion);
            Assert.IsTrue(completion.Task.IsCompleted);
            return completion.Task.Result;
        }

        [Test]
        public void ClearReturnsTheLogsThenClearsThem()
        {
            var marker = "USB console test " + Guid.NewGuid().ToString("N");
            Debug.Log(marker);

            StringAssert.Contains(marker, Call("get_console_logs", new JObject()));
            var cleared = Call("get_console_logs", new JObject { ["clear"] = true });
            StringAssert.Contains(marker, cleared);
            StringAssert.Contains("Console cleared.", cleared);
            StringAssert.DoesNotContain(marker, Call("get_console_logs", new JObject()));
        }

        [Test]
        public void PhysicsSectionReportsIgnoredLayerPairs()
        {
            int water = LayerMask.NameToLayer("Water");
            Assert.GreaterOrEqual(water, 0, "builtin Water layer");
            bool wasIgnored = Physics.GetIgnoreLayerCollision(0, water);
            try
            {
                Physics.IgnoreLayerCollision(0, water, true);
                var collisions = JObject.Parse(Call("get_project_settings", new JObject { ["sections"] = new JArray("physics") }))
                    ["physics"]["layerCollisions"];
                var withDefault = collisions["Default"].Values<string>().ToArray();
                CollectionAssert.Contains(withDefault, "Default");
                CollectionAssert.DoesNotContain(withDefault, "Water");
                CollectionAssert.DoesNotContain(collisions["Water"].Values<string>().ToArray(), "Default");
            }
            finally { Physics.IgnoreLayerCollision(0, water, wasIgnored); }
        }
    }
}
