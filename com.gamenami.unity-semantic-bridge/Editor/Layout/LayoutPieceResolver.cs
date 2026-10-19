using System;
using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>
    /// Footprints and cover for a layout draft without applying it: the grid tier of validate_layout. Card
    /// pieces take their card's footprint and cover, as apply does. Pieces without a card (and card blockers
    /// whose card leaves cover to the game) are instantiated into a hidden preview scene and measured with
    /// <see cref="LayoutApplier.MeasureInstance"/>, the code apply uses, then destroyed. No open scene changes.
    /// Fit problems need the real placement, so they stay apply and scene tier findings.
    /// </summary>
    public sealed class LayoutPieceResolver : IDisposable
    {
        private readonly IGridRulesAdapter _adapter;
        private readonly LevelCardSource _cards;
        private readonly Dictionary<(string guid, int rotation, bool blocker), PieceSpec> _measured =
            new Dictionary<(string, int, bool), PieceSpec>();
        private Scene _preview;

        /// <summary>Card pieces and decor, for the scale and plausibility checks.</summary>
        public readonly List<PlacedCard> Cards = new List<PlacedCard>();
        /// <summary>NO_BLOCKING_COLLIDER / NO_GEOMETRY lines for measured pieces, cell-addressed like apply's.</summary>
        public readonly List<string> Issues = new List<string>();

        public LayoutPieceResolver(IGridRulesAdapter adapter, LevelCardSource cards)
        {
            _adapter = adapter;
            _cards = cards;
        }

        /// <summary>
        /// The grid tier of a draft: structural, encounter and reachability checks on the resolved footprints, plus
        /// scale and plausibility for card pieces. Prefabs and enemy assets must exist, as for apply.
        /// </summary>
        public static LayoutReport ValidateDraft(LayoutDocument document, IGridRulesAdapter adapter = null, LevelCardSource cards = null)
        {
            adapter ??= LayoutAdapters.Find();
            cards ??= LevelCardSource.ForProject();
            var prefabs = LayoutApplier.ResolvePrefabs(document);
            LayoutApplier.CheckEnemyAssets(document);
            using var resolver = new LayoutPieceResolver(adapter, cards);
            var specs = new Dictionary<string, PieceSpec>();
            foreach (var piece in document.Pieces)
                specs[piece.LocalId] = resolver.Resolve(piece, prefabs[piece.PrefabGuid], true);
            foreach (var decor in document.Decor)
                resolver.Resolve(decor, prefabs[decor.PrefabGuid], false);

            var report = LayoutValidator.Validate(document, p => specs.TryGetValue(p.LocalId, out var s) ? s : (PieceSpec?)null, adapter.Rules);
            report.Issues.InsertRange(0, resolver.Issues.Select(line => LayoutIssue.FromLine(line)));
            report.Issues.AddRange(LayoutValidator.Scale(resolver.Cards, adapter.Rules.CoverHeights));
            report.Issues.AddRange(LayoutValidator.Plausibility(report.Grid, resolver.Cards));
            return report;
        }

        /// <summary>A piece's (or decor's) footprint and cover, as apply would place it.</summary>
        public PieceSpec Resolve(LayoutPiece piece, GameObject prefab, bool blocker)
        {
            var card = _cards.Find(piece.PrefabGuid);
            if (card == null) return Measure(piece, prefab, blocker);

            var footprint = LevelCards.Footprint(card, _adapter.Rules.CellSize, blocking: blocker);
            var size = footprint.RotatedSize(piece.Rotation);
            PieceSpec spec;
            CoverLevel cover;
            if (blocker && card.Walls != null && _adapter.Rules.SideWalls)
            {
                cover = card.Cover ?? CoverLevel.Full;
                spec = new PieceSpec(footprint.Width, footprint.Depth, cover, card.Walls);
            }
            else
            {
                cover = blocker ? card.Cover ?? DescribeCover(card, prefab, piece.Rotation) : CoverLevel.None;
                spec = footprint.ToSpec(cover);
            }
            Cards.Add(new PlacedCard
            {
                LocalId = piece.LocalId, Card = card, Anchor = piece.Anchor, Size = size, Rotation = piece.Rotation,
                Blocking = blocker, Cover = cover,
            });
            return spec;
        }

        private PieceSpec Measure(LayoutPiece piece, GameObject prefab, bool blocker)
        {
            var key = (piece.PrefabGuid, piece.Rotation, blocker);
            string problem = null;
            if (!_measured.TryGetValue(key, out var spec))
            {
                var frame = PieceGeometry.Frame(piece.Rotation);
                var instance = Instantiate(prefab, frame);
                try
                {
                    spec = LayoutApplier.MeasureInstance(instance, frame, blocker, _adapter, out _, out problem);
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
                if (problem == null) _measured[key] = spec;
            }
            if (problem != null) Issues.Add($"{problem} {piece.Anchor} {piece.LocalId}: {LayoutApplier.MeasureProblemText(problem)}");
            return spec;
        }

        // Cover the game gives a card piece whose card leaves it open, on an instance at the card's scale.
        private CoverLevel DescribeCover(LevelCard card, GameObject prefab, int rotation)
        {
            var instance = Instantiate(prefab, PieceGeometry.Frame(rotation));
            try
            {
                instance.transform.localScale = LayoutApplier.ToVector3(card.PlacementScale);
                return _adapter.DescribeCover(instance);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        private GameObject Instantiate(GameObject prefab, Quaternion frame)
        {
            if (!_preview.IsValid()) _preview = EditorSceneManager.NewPreviewScene();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _preview);
            instance.transform.SetPositionAndRotation(Vector3.zero, frame * prefab.transform.localRotation);
            return instance;
        }

        public void Dispose()
        {
            if (_preview.IsValid()) EditorSceneManager.ClosePreviewScene(_preview);
        }
    }
}
