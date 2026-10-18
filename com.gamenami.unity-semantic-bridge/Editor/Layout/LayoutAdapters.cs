using System;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using UnityEditor;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>Finds the project's single IGridRulesAdapter implementation.</summary>
    public static class LayoutAdapters
    {
        public static IGridRulesAdapter Find()
        {
            var types = TypeCache.GetTypesDerivedFrom<IGridRulesAdapter>()
                .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition)
                .ToList();
            if (types.Count == 0)
                throw new BridgeToolException("No IGridRulesAdapter implementation in this project. The game must implement Gamenami.UnitySemanticBridge.Layout.IGridRulesAdapter.");
            if (types.Count > 1)
                throw new BridgeToolException($"Several IGridRulesAdapter implementations: {string.Join(", ", types.Select(t => t.FullName))}. Keep exactly one.");
            if (types[0].GetConstructor(Type.EmptyTypes) == null)
                throw new BridgeToolException($"{types[0].FullName} needs a public parameterless constructor.");
            return (IGridRulesAdapter)Activator.CreateInstance(types[0]);
        }

        /// <summary>The project's adapter, or null when it has none (or several): for optional uses such as catalog role detection.</summary>
        public static IGridRulesAdapter TryFind()
        {
            try
            {
                return Find();
            }
            catch (BridgeToolException)
            {
                return null;
            }
        }

        /// <summary>The project's cover height bands (its adapter's GridRules.CoverHeights), else USB's defaults.</summary>
        public static CoverHeightBands CoverHeights() => TryFind()?.Rules?.CoverHeights ?? CoverHeightBands.Default;
    }
}
