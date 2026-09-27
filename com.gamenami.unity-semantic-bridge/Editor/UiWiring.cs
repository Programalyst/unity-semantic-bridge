using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static partial class UiAuthoring
    {
        static bool PrepareWiring(Plan plan, JObject op)
        {
            switch (op["op"]?.Value<string>())
            {
                case "set_fields": PrepareFields(plan, op); return true;
                case "button_on_click": PrepareClicks(plan, op); return true;
                default: return false;
            }
        }

        static void ValidateFieldReference(Plan plan, Type type, JToken value)
        {
            if (typeof(Object).IsAssignableFrom(type)) { plan.ReferenceType(value, type); return; }
            var element = AuthoringReferences.ElementType(type);
            if (!typeof(Object).IsAssignableFrom(element) || !(value is JArray array)) throw new ArgumentException("set_fields accepts typed Object references or arrays/lists of Object references. Use existing set_field_values for primitive/gameplay data.");
            if (array.Count > 1000) throw new ArgumentException("Reference arrays are limited to 1000 elements per assignment.");
            foreach (var item in array) plan.ReferenceType(item, element);
        }

        static void PrepareFields(Plan plan, JObject op)
        {
            UiProperties.Keys(op, "op target fields"); plan.Required(op["target"], typeof(Component));
            var type = plan.ReferenceType(op["target"], typeof(Component));
            if (!(op["fields"] is JObject fields) || fields.Count == 0) throw new ArgumentException("fields must be a nonempty map of serialized property paths to references.");
            foreach (var field in fields.Properties())
            {
                if (field.Name == "m_Script") throw new ArgumentException("Changing component scripts is not supported.");
                ValidateFieldReference(plan, AuthoringReferences.FieldType(type, field.Name), field.Value);
            }
            if (!(op["target"] is JObject t && t["ref"] != null))
            {
                // Added components on existing objects are checked after addition, with rollback on failure.
                try
                {
                    var existing = (Component)AuthoringReferences.Resolve(op["target"], typeof(Component));
                    plan.Use(existing.gameObject);
                    using (var serialized = new SerializedObject(existing))
                        foreach (var f in fields.Properties()) if (serialized.FindProperty(f.Name) == null) throw new ArgumentException($"'{f.Name}' is not a serialized field on {type.Name}.");
                }
                catch (ArgumentException) { if (plan.added.Count == 0) throw; }
            }
            plan.actions.Add((locals, warnings) =>
            {
                var target = (Component)AuthoringReferences.Resolve(op["target"], typeof(Component), locals); EnsureEditable(target.gameObject);
                using (var serialized = new SerializedObject(target))
                {
                    foreach (var field in fields.Properties())
                    {
                        var property = serialized.FindProperty(field.Name) ?? throw new ArgumentException($"'{field.Name}' is not serialized on {type.Name}.");
                        var expected = AuthoringReferences.FieldType(type, field.Name);
                        if (typeof(Object).IsAssignableFrom(expected))
                        {
                            if (property.propertyType != SerializedPropertyType.ObjectReference) throw new ArgumentException("Field must be an Object reference.");
                            property.objectReferenceValue = AuthoringReferences.Resolve(field.Value, expected, locals);
                        }
                        else
                        {
                            if (!property.isArray) throw new ArgumentException("Field must be a serialized reference array/list.");
                            var values = (JArray)field.Value; var element = AuthoringReferences.ElementType(expected);
                            property.arraySize = values.Count;
                            for (var i = 0; i < values.Count; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = AuthoringReferences.Resolve(values[i], element, locals);
                        }
                    }
                    Undo.RegisterCompleteObjectUndo(target, "Wire uGUI references");
                    serialized.ApplyModifiedProperties(); Touch(target);
                }
            });
        }

        sealed class Click
        {
            internal JToken target, argument;
            internal string method, mode;
            internal Type argumentType;
            internal MethodInfo methodInfo;
            internal UnityEventCallState state;
        }

        static void PrepareClicks(Plan plan, JObject op)
        {
            UiProperties.Keys(op, "op target mode listeners"); plan.Required(op["target"], typeof(Button));
            var mode = op["mode"]?.Value<string>() ?? "append";
            if (mode != "append" && mode != "replace") throw new ArgumentException("button_on_click.mode must be append or replace (replace with [] clears listeners).");
            if (!(op["listeners"] is JArray listeners) || listeners.Count > 100) throw new ArgumentException("listeners must be an array of at most 100 persistent calls.");
            var calls = new List<Click>();
            foreach (var item in listeners)
            {
                if (!(item is JObject listener)) throw new ArgumentException("Listener must be an object.");
                UiProperties.Keys(listener, "target method mode argument argumentType state"); plan.Required(listener["target"], typeof(Object));
                var targetType = plan.ReferenceType(listener["target"], typeof(Object));
                var call = new Click { target = listener["target"], method = listener["method"]?.Value<string>(),
                    mode = listener["mode"]?.Value<string>() ?? "void", argument = listener["argument"], state = UnityEventCallState.RuntimeOnly };
                switch (call.mode)
                {
                    case "void": if (call.argument != null) throw new ArgumentException("void callbacks take no argument."); break;
                    case "int": call.argumentType = typeof(int); break;
                    case "float": call.argumentType = typeof(float); break;
                    case "bool": call.argumentType = typeof(bool); break;
                    case "string": call.argumentType = typeof(string); break;
                    case "object":
                        call.argumentType = AuthoringReferences.TypeNamed(listener["argumentType"]?.Value<string>());
                        if (!typeof(Object).IsAssignableFrom(call.argumentType)) throw new ArgumentException("object argumentType must derive from UnityEngine.Object.");
                        break;
                    default: throw new ArgumentException("Listener mode must be void, int, float, bool, string or object.");
                }
                if (string.IsNullOrWhiteSpace(call.method)) throw new ArgumentException("Listener method is required.");
                var parameters = call.argumentType == null ? Type.EmptyTypes : new[] { call.argumentType };
                call.methodInfo = targetType.GetMethod(call.method, BindingFlags.Instance | BindingFlags.Public, null, parameters, null);
                if (call.methodInfo == null || call.methodInfo.ReturnType != typeof(void) || call.methodInfo.IsGenericMethod)
                    throw new ArgumentException($"{targetType.Name}.{call.method} must be a public instance void method with exactly ({string.Join(",", parameters.Select(t => t.Name))}).");
                if (call.argumentType != null) UiProperties.ConvertValue(call.argument, call.argumentType, (t, e) => { plan.ReferenceType(t, e); return null; });
                if (listener["state"] != null) call.state = (UnityEventCallState)UiProperties.ConvertValue(listener["state"], typeof(UnityEventCallState), null);
                calls.Add(call);
            }
            plan.actions.Add((locals, warnings) =>
            {
                var button = (Button)AuthoringReferences.Resolve(op["target"], typeof(Button), locals); EnsureEditable(button.gameObject);
                Undo.RegisterCompleteObjectUndo(button, "Wire Button.onClick");
                if (mode == "replace") while (button.onClick.GetPersistentEventCount() > 0) UnityEventTools.RemovePersistentListener(button.onClick, 0);
                foreach (var call in calls)
                {
                    var target = AuthoringReferences.Resolve(call.target, typeof(Object), locals);
                    var arg = call.argumentType == null ? null : UiProperties.ConvertValue(call.argument, call.argumentType, (t, e) => AuthoringReferences.Resolve(t, e, locals));
                    var delegateType = call.argumentType == null ? typeof(UnityAction) : typeof(UnityAction<>).MakeGenericType(call.argumentType);
                    var callback = Delegate.CreateDelegate(delegateType, target, call.methodInfo);
                    switch (call.mode)
                    {
                        case "void": UnityEventTools.AddVoidPersistentListener(button.onClick, (UnityAction)callback); break;
                        case "int": UnityEventTools.AddIntPersistentListener(button.onClick, (UnityAction<int>)callback, (int)arg); break;
                        case "float": UnityEventTools.AddFloatPersistentListener(button.onClick, (UnityAction<float>)callback, (float)arg); break;
                        case "bool": UnityEventTools.AddBoolPersistentListener(button.onClick, (UnityAction<bool>)callback, (bool)arg); break;
                        case "string": UnityEventTools.AddStringPersistentListener(button.onClick, (UnityAction<string>)callback, (string)arg); break;
                        case "object":
                            typeof(UnityEventTools).GetMethods().Single(m => m.Name == "AddObjectPersistentListener" && m.IsGenericMethodDefinition)
                                .MakeGenericMethod(call.argumentType).Invoke(null, new[] { button.onClick, callback, arg }); break;
                    }
                    button.onClick.SetPersistentListenerState(button.onClick.GetPersistentEventCount() - 1, call.state);
                }
                Touch(button);
            });
        }
    }
}
