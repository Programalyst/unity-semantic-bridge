using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    public static class UsbInvokeProbe
    {
        public static string Ping() => "pong-usb";
        public static void Boom() => throw new InvalidOperationException("probe failure");
    }

    /// <summary>
    /// Mechanism tests for generic menu/static invocation. Live game-validator
    /// runs and Play Mode refusal are manual acceptance on a game project.
    /// </summary>
    public class InvocationTests
    {
        static string Invoke(string method, JObject extra = null)
        {
            var msg = new JObject { ["method"] = method };
            if (extra != null)
                foreach (var property in extra.Properties())
                    msg[property.Name] = property.Value;
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(msg, completion);
            return completion.Task.Result;
        }

        [Test]
        public void IdleGuardHoldsInTestEditor()
        {
            Assert.IsTrue(InvocationFunctions.IsIdle());
        }

        [Test]
        public void UnknownMenuPathReturnsNotFound()
        {
            // Unity logs its own error for unknown paths alongside returning false.
            LogAssert.Expect(LogType.Error, "ExecuteMenuItem failed because there is no menu named 'Tools/Usb/No Such Item'");
            var result = Invoke("invoke_menu_item", new JObject { ["menuPath"] = "Tools/Usb/No Such Item" });
            StringAssert.StartsWith("Error:", result);
            StringAssert.Contains("not found", result);
        }

        [Test]
        public void MissingMenuPathIsRejected()
        {
            StringAssert.Contains("menuPath", Invoke("invoke_menu_item"));
        }

        [Test]
        public void BogusTypeReturnsNotFoundInsteadOfThrowing()
        {
            var result = Invoke("invoke_static", new JObject
            {
                ["typeName"] = "Usb.NoSuchType",
                ["methodName"] = "Nope",
            });
            StringAssert.StartsWith("Error:", result);
            StringAssert.Contains("Type not found", result);
        }

        [Test]
        public void BogusMethodReturnsNotFoundInsteadOfThrowing()
        {
            var result = Invoke("invoke_static", new JObject
            {
                ["typeName"] = "System.Guid",
                ["methodName"] = "NoSuchMethodUsb",
            });
            StringAssert.StartsWith("Error:", result);
            StringAssert.Contains("Static method not found", result);
        }

        [Test]
        public void MethodRequiringArgsIsNotMatched()
        {
            // string.IsNullOrEmpty exists but takes an argument: zero-arg only.
            var result = Invoke("invoke_static", new JObject
            {
                ["typeName"] = "System.String",
                ["methodName"] = "IsNullOrEmpty",
            });
            StringAssert.StartsWith("Error:", result);
        }

        [Test]
        public void GenericMethodIsRejected()
        {
            var result = Invoke("invoke_static", new JObject
            {
                ["typeName"] = "System.Array",
                ["methodName"] = "Empty",
            });
            StringAssert.StartsWith("Error:", result);
        }

        [Test]
        public void VoidStaticReturnsOk()
        {
            Assert.AreEqual("ok", Invoke("invoke_static", new JObject
            {
                ["typeName"] = "System.GC",
                ["methodName"] = "Collect",
            }));
        }

        [Test]
        public void ValueStaticReturnsString()
        {
            var result = Invoke("invoke_static", new JObject
            {
                ["typeName"] = "System.Guid",
                ["methodName"] = "NewGuid",
            });
            Assert.IsTrue(Guid.TryParse(result, out _), $"Expected a GUID string, got: {result}");
        }

        [Test]
        public void ProbePingReturnsValue()
        {
            Assert.AreEqual("pong-usb", Invoke("invoke_static", new JObject
            {
                ["typeName"] = typeof(UsbInvokeProbe).FullName,
                ["methodName"] = "Ping",
            }));
        }

        [Test]
        public void ThrownExceptionSurfacesAsTypedError()
        {
            Assert.AreEqual("Error: InvalidOperationException: probe failure",
                Invoke("invoke_static", new JObject
                {
                    ["typeName"] = typeof(UsbInvokeProbe).FullName,
                    ["methodName"] = "Boom",
                }));
        }

        [Test]
        public void SnakeCaseParamsAreAccepted()
        {
            Assert.AreEqual("pong-usb", Invoke("invoke_static", new JObject
            {
                ["type_name"] = typeof(UsbInvokeProbe).FullName,
                ["method_name"] = "Ping",
            }));
        }
    }
}
