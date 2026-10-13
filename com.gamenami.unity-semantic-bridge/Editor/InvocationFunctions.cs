using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Generic invocation mechanism only: exact menu paths and exact zero-arg
    /// public static methods. No game-specific knowledge lives here; project
    /// mappings (e.g. validation suites) belong to the consuming project and
    /// call these tools. Same trust level as write_unity_script: callers can
    /// already execute arbitrary code, so no allowlist is attempted here.
    /// </summary>
    public static class InvocationFunctions
    {
        public static bool IsIdle() =>
            !(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating);

        static void RequireIdle()
        {
            if (!IsIdle())
                throw new BridgeToolException("Wait for an idle Edit Mode Editor (not playing, compiling, or updating).");
        }

        static string RequiredString(JObject message, string camel, string snake, string label)
        {
            var raw = message[camel]?.ToString() ?? message[snake]?.ToString();
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 256)
                throw new BridgeToolException($"'{label}' must be a nonempty string of at most 256 characters.");
            return raw;
        }

        public static string InvokeMenuItem(JObject mcpMessage)
        {
            var path = RequiredString(mcpMessage, "menuPath", "menu_path", "menuPath");
            RequireIdle();
            // Exact match only: ExecuteMenuItem itself returns false for anything
            // else, so there is no fuzzy matching and no partial execution.
            Debug.Log($"[Bridge] invoke_menu_item '{path}'");
            bool executed;
            try
            {
                executed = EditorApplication.ExecuteMenuItem(path);
            }
            catch (Exception e)
            {
                return $"Error: {e.GetType().Name}: {e.Message}";
            }
            if (!executed)
                return $"Error: Menu item not found: '{path}'. Pass the exact menu path.";
            Debug.Log($"[Bridge] invoke_menu_item '{path}' executed.");
            return "ok";
        }

        public static string InvokeStatic(JObject mcpMessage)
        {
            var typeName = RequiredString(mcpMessage, "typeName", "type_name", "typeName");
            var methodName = RequiredString(mcpMessage, "methodName", "method_name", "methodName");
            RequireIdle();
            Type type;
            MethodInfo method;
            try
            {
                type = ResolveType(typeName);
                method = type == null ? null : FindZeroArgStatic(type, methodName);
            }
            catch (Exception e)
            {
                return $"Error: {e.GetType().Name}: {e.Message}";
            }
            if (type == null)
                return $"Error: Type not found: '{typeName}'. Pass the exact full type name.";
            if (method == null)
                return $"Error: Static method not found: '{type.FullName}.{methodName}()'. Expected exactly one public static zero-arg non-generic method; no overload guessing, no instance construction.";
            Debug.Log($"[Bridge] invoke_static {method.DeclaringType.FullName}.{method.Name}()");
            object result;
            try
            {
                result = method.Invoke(null, null);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? (Exception)tie;
                return $"Error: {inner.GetType().Name}: {inner.Message}";
            }
            catch (Exception e)
            {
                return $"Error: {e.GetType().Name}: {e.Message}";
            }
            if (method.ReturnType == typeof(void))
                return "ok";
            try
            {
                return result?.ToString() ?? "null";
            }
            catch (Exception e)
            {
                return $"Error: {e.GetType().Name}: {e.Message}";
            }
        }

        static Type ResolveType(string fullName)
        {
            // Exact match only: full name first, then an exact full-name scan of
            // loaded assemblies (game types are rarely assembly-qualified).
            var direct = Type.GetType(fullName, throwOnError: false);
            if (direct != null)
                return direct;
            Type match = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate;
                try
                {
                    candidate = assembly.GetType(fullName, throwOnError: false);
                }
                catch
                {
                    continue;
                }
                if (candidate == null || Equals(candidate, match))
                    continue;
                if (match != null)
                    throw new InvalidOperationException($"Ambiguous type name '{fullName}' across assemblies.");
                match = candidate;
            }
            return match;
        }

        static MethodInfo FindZeroArgStatic(Type type, string methodName)
        {
            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == methodName
                    && !m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 0)
                .ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }
    }
}
