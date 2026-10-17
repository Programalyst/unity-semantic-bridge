using System;
using System.Collections.Generic;
using System.IO;
using Gamenami.UnitySemanticBridge.Layout;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>
    /// Where apply and bake get a piece's level catalog card, by prefab GUID. A project's cards are
    /// built on demand from the prefab and the project's overrides file
    /// (<c>AssetCatalog/LevelCatalogOverrides.json</c>), the same way the level catalog generator builds
    /// them, so nothing generated has to be committed or kept fresh. Only reviewed pieces (a per-piece
    /// entry in the overrides file) have a card; other prefabs have none and are measured on the
    /// instance at their prefab scale, as before cards existed. Cards are cached for the source's life.
    /// </summary>
    public sealed class LevelCardSource
    {
        private readonly Func<string, LevelCard> _lookup;
        private readonly Dictionary<string, LevelCard> _cache = new Dictionary<string, LevelCard>(StringComparer.Ordinal);

        public LevelCardSource(Func<string, LevelCard> lookup) => _lookup = lookup ?? (_ => null);

        /// <summary>No cards: every piece is measured on its instance.</summary>
        public static LevelCardSource None => new LevelCardSource(null);

        /// <summary>Cards from the given cards, keyed by GUID (tests, or a loaded level-catalog.json).</summary>
        public static LevelCardSource From(IReadOnlyDictionary<string, LevelCard> cards) =>
            new LevelCardSource(guid => cards.TryGetValue(guid, out var card) ? card : null);

        /// <summary>
        /// The project's reviewed pieces: cards built from each prefab and the project's overrides file.
        /// A missing overrides file means no cards; a malformed one stops the apply or bake.
        /// </summary>
        public static LevelCardSource ForProject()
        {
            var path = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                AssetCatalogGenerator.CatalogDirName, LevelCatalogOverrides.FileName);
            if (!File.Exists(path)) return None;
            LevelCatalogOverrides overrides;
            try
            {
                overrides = LevelCatalogOverrides.Parse(File.ReadAllText(path));
            }
            catch (FormatException e)
            {
                throw new BridgeToolException($"{e.Message} Fix it before applying or baking layouts (pieces take their scale and footprint from it).");
            }
            return ForOverrides(overrides);
        }

        /// <summary>Cards built on demand for prefabs with a per-piece entry in <paramref name="overrides"/>.</summary>
        public static LevelCardSource ForOverrides(LevelCatalogOverrides overrides) => new LevelCardSource(guid =>
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return null;
            LevelCatalogGenerator.PackAndId(path, out var id);
            if (!overrides.HasPieceEntry(guid, id)) return null;
            return LevelCatalogGenerator.BuildCard(path, overrides, out _);
        });

        /// <summary>The card for a prefab GUID, or null when the piece has none.</summary>
        public LevelCard Find(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (!_cache.TryGetValue(guid, out var card))
                _cache[guid] = card = _lookup(guid);
            return card;
        }
    }
}
