using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>What <see cref="WallOutlineSuggester"/> proposes for a building's overrides entry.</summary>
    public sealed class WallSuggestion
    {
        public string Name;
        public double CellSize;
        public Vector3 Scale;
        /// <summary>The frame (footprint) in cells at layout rotation 0.</summary>
        public int Width, Height;
        /// <summary>The colliders' bounds centre relative to the frame centre (meters, x and z).</summary>
        public Vector2 FootprintOffset;
        /// <summary>Wall centrelines relative to the frame lines (meters, + = outside): -x, +x, -y, +y.</summary>
        public float[] WallResidual = new float[4];
        /// <summary>Pivot height above the floor that puts the measured floor at 0 (negative sinks a slab).</summary>
        public double Lift;
        /// <summary>Lowest ceiling over the frame cells above 1 m, or null.</summary>
        public double? Ceiling;
        public WallOutline Outline;
        public readonly List<string> Notes = new List<string>();

        /// <summary>The overrides entry fields: footprint, footprintOffset, lift and walls.</summary>
        public JObject ToOverrideJson()
        {
            var json = new JObject
            {
                ["footprint"] = new JArray(Round(Width * CellSize), Round(Height * CellSize)),
                ["footprintOffset"] = new JArray(Round(FootprintOffset.x), Round(FootprintOffset.y)),
            };
            if (Math.Abs(Lift) >= 0.005) json["lift"] = Round(Lift);
            json["walls"] = Outline.ToJson();
            return json;
        }

        public string Format()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "WALLS_SUGGESTION {0} at scale ({1:0.###}, {2:0.###}, {3:0.###}): {4}x{5} cells of {6:0.##} m",
                Name, Scale.x, Scale.y, Scale.z, Width, Height, CellSize));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "wall centrelines vs frame lines (+ outside): -x {0:+0.00;-0.00}, +x {1:+0.00;-0.00}, -y {2:+0.00;-0.00}, +y {3:+0.00;-0.00}",
                WallResidual[0], WallResidual[1], WallResidual[2], WallResidual[3]));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "floor lift {0:0.00}, ceiling {1}", Lift,
                Ceiling.HasValue ? Ceiling.Value.ToString("0.00", CultureInfo.InvariantCulture) : "none"));
            foreach (var line in Outline.ToLines()) sb.AppendLine("  " + line);
            foreach (var note in Notes) sb.AppendLine("note: " + note);
            sb.Append(ToOverrideJson().ToString(Newtonsoft.Json.Formatting.None));
            return sb.ToString();
        }

        private static double Round(double value) => Math.Round(value, 2);
    }

    /// <summary>
    /// Proposes a building's walls on cell sides (plan Phase W) from its colliders: the frame whose lines
    /// the walls follow, the model's offset from it, each side's kind (wall, window, door, open), door
    /// points, sight gaps and the floor lift. Rays, not overlap boxes (those miss thin faces between
    /// samples). An agent runs it; the owner approves the result into the overrides file.
    /// </summary>
    public static class WallOutlineSuggester
    {
        public const float SampleStep = 0.1f;
        public const float MinOpening = 0.6f;
        /// <summary>A wall centreline further than this from its frame line gets a note (the frame keeps walls within half a cell).</summary>
        public const float MaxWallResidual = 0.3f;
        private static readonly float[] Heights = { 0.3f, 1.0f, 1.7f };

        /// <summary>Measures the prefab at <paramref name="scale"/> (its root's own scale when null) on isolated prefab contents.</summary>
        public static WallSuggestion Suggest(string prefabPath, double cellSize, Vector3? scale = null)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                if (scale.HasValue) root.transform.localScale = scale.Value;
                root.transform.position = Vector3.zero;
                return Suggest(root, cellSize);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        /// <summary>Measures a building already placed with its pivot on the floor (root's Y = floor) in a scene.</summary>
        public static WallSuggestion Suggest(GameObject root, double cellSize)
        {
            var hitBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                Physics.SyncTransforms();
                return Measure(root, (float)cellSize);
            }
            finally { Physics.queriesHitBackfaces = hitBackfaces; }
        }

        private static WallSuggestion Measure(GameObject root, float cell)
        {
            var physics = root.scene.GetPhysicsScene();
            var colliders = root.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToList();
            if (colliders.Count == 0) throw new InvalidOperationException($"{root.name} has no enabled solid colliders to measure.");
            var bounds = colliders[0].bounds;
            foreach (var c in colliders) bounds.Encapsulate(c.bounds);
            var floor = root.transform.position.y;
            var result = new WallSuggestion { Name = root.name, CellSize = cell, Scale = root.transform.localScale };
            const int mask = Physics.DefaultRaycastLayers;

            bool Hit(Vector3 origin, Vector3 direction, float length, out RaycastHit hit) =>
                physics.Raycast(origin, direction, out hit, length, mask, QueryTriggerInteraction.Ignore);

            // Wall centrelines: median outer and inner faces across the middle 60 % of each side at 1 m.
            var centre = new Vector3(bounds.center.x, floor + 1f, bounds.center.z);
            var sides = new[] { (GridSide.MinusX, Vector3.left, Vector3.forward), (GridSide.PlusX, Vector3.right, Vector3.forward),
                                (GridSide.MinusY, Vector3.back, Vector3.right), (GridSide.PlusY, Vector3.forward, Vector3.right) };
            var wallCentre = new float[4];
            for (var i = 0; i < 4; i++)
            {
                var (side, normal, along) = sides[i];
                var half = Vector3.Dot(bounds.extents, Abs(along));
                var reach = Vector3.Dot(bounds.extents, Abs(normal));
                var outers = new List<float>(); var inners = new List<float>();
                for (var s = -half * 0.6f; s <= half * 0.6f; s += 0.05f)
                {
                    var line = centre + along * s;
                    if (Hit(line + normal * (reach + 3f), -normal, reach + 3f, out var outer) && Hit(line, normal, reach + 3f, out var inner))
                    {
                        outers.Add(Vector3.Dot(outer.point - centre, normal));
                        inners.Add(Vector3.Dot(inner.point - centre, normal));
                    }
                }
                if (outers.Count == 0) throw new InvalidOperationException($"{root.name}: no wall found on its {GridSides.Name(side)} side at 1 m.");
                wallCentre[i] = (Median(outers) + Median(inners)) / 2f;
            }
            // Centrelines as coordinates along x (-x, +x) and z (-y, +y), relative to the bounds centre.
            float xMin = -wallCentre[0], xMax = wallCentre[1], zMin = -wallCentre[2], zMax = wallCentre[3];
            result.Width = Math.Max(1, Mathf.RoundToInt((xMax - xMin) / cell));
            result.Height = Math.Max(1, Mathf.RoundToInt((zMax - zMin) / cell));
            var frameCentre = new Vector2((xMin + xMax) / 2f, (zMin + zMax) / 2f); // relative to the bounds centre
            result.FootprintOffset = -frameCentre;
            var halfW = result.Width * cell / 2f; var halfH = result.Height * cell / 2f;
            result.WallResidual[0] = -(xMin - frameCentre.x) - halfW;
            result.WallResidual[1] = (xMax - frameCentre.x) - halfW;
            result.WallResidual[2] = -(zMin - frameCentre.y) - halfH;
            result.WallResidual[3] = (zMax - frameCentre.y) - halfH;
            for (var i = 0; i < 4; i++)
                if (Math.Abs(result.WallResidual[i]) > MaxWallResidual)
                    result.Notes.Add($"the {GridSides.Name(sides[i].Item1)} wall is {result.WallResidual[i]:+0.00;-0.00} m off its frame line: rescale or check the frame");

            Vector3 CellCentre(int x, int y) => new Vector3(bounds.center.x + frameCentre.x + (x + 0.5f) * cell - halfW, floor,
                bounds.center.z + frameCentre.y + (y + 0.5f) * cell - halfH);

            // Each side of each frame cell: rays from the cell centre (spread along the side) through the side.
            var outlineSides = new List<OutlineSide>();
            var doorPoints = new List<DoorPoint>();
            var sightFill = new List<(Cell, GridSide)>();
            for (var x = 0; x < result.Width; x++)
                for (var y = 0; y < result.Height; y++)
                    foreach (var (side, normal, along) in sides)
                    {
                        var neighbour = new Cell(x, y) + GridSides.Step(side);
                        // Interior sides once, from the cell on their -x/-y side.
                        if ((side == GridSide.MinusX || side == GridSide.MinusY) && neighbour.X >= 0 && neighbour.Y >= 0) continue;
                        var samples = new List<(float s, bool[] open)>();
                        for (var s = -cell / 2f + SampleStep; s <= cell / 2f - SampleStep + 1e-4f; s += SampleStep)
                        {
                            var open = new bool[Heights.Length];
                            for (var h = 0; h < Heights.Length; h++)
                                open[h] = !Hit(CellCentre(x, y) + Vector3.up * Heights[h] + along * s, normal, cell / 2f + 0.6f, out _);
                            samples.Add((s, open));
                        }
                        var door = LongestRun(samples, o => o[0] && o[1] && o[2]);
                        var window = LongestRun(samples, o => !o[0] && o[2]);
                        var blockedAtWaist = samples.Count(p => !p.open[1]);
                        var onEdge = neighbour.X < 0 || neighbour.Y < 0 || neighbour.X >= result.Width || neighbour.Y >= result.Height;
                        // An interior side's ends touch the outer walls meeting there, so only its middle counts as solid.
                        var solidSomewhere = samples.Any(p => (onEdge || Math.Abs(p.s) <= cell / 2f - 0.3f) && p.open.Any(open => !open));
                        SideKind kind;
                        if (!solidSomewhere)
                        {
                            // Nothing on the side at all: open inside; on the outline an opening as wide as the cell.
                            kind = onEdge ? SideKind.Door : SideKind.Open;
                            if (onEdge) result.Notes.Add($"outer side {new Cell(x, y)} {GridSides.Name(side)} is open across the whole cell: a door, or a gap in the building?");
                        }
                        else if (door.length >= MinOpening - 1e-4f)
                        {
                            kind = SideKind.Door;
                            if (Math.Abs(door.centre) >= SampleStep - 1e-4f)
                                doorPoints.Add(new DoorPoint(new Cell(x, y), side, Math.Round(door.centre, 2)));
                        }
                        else if (window.length >= MinOpening - 1e-4f) kind = SideKind.Window;
                        else if (blockedAtWaist * 2 >= samples.Count)
                        {
                            kind = SideKind.Wall;
                            // Gaps at a side's ends belong to the neighbouring side's opening (a door frame), not this wall.
                            if (samples.Any(p => p.open[1] && Math.Abs(p.s) <= cell / 2f - 0.3f)) sightFill.Add((new Cell(x, y), side));
                        }
                        else kind = SideKind.Open;
                        if (kind != SideKind.Open) outlineSides.Add(new OutlineSide(new Cell(x, y), side, kind));
                    }

            // Floor: the first surface below 1 m at each cell centre; ceiling: the first above it.
            var floors = new List<float>(); var ceilings = new List<float>();
            for (var x = 0; x < result.Width; x++)
                for (var y = 0; y < result.Height; y++)
                {
                    var from = CellCentre(x, y) + Vector3.up;
                    if (Hit(from, Vector3.down, 1.5f, out var down) && down.point.y - floor <= 0.5f) floors.Add(down.point.y - floor);
                    if (Hit(from, Vector3.up, 20f, out var up)) ceilings.Add(up.point.y - floor);
                }
            if (floors.Count > 0) result.Lift = -Math.Round(Median(floors), 2);
            if (floors.Count < result.Width * result.Height)
                result.Notes.Add($"{result.Width * result.Height - floors.Count} frame cells have no floor below 1 m (holes show the ground)");
            if (ceilings.Count > 0) result.Ceiling = Math.Round(ceilings.Min(), 2);
            result.Outline = new WallOutline(result.Width, result.Height, outlineSides, doorPoints: doorPoints, sightFill: sightFill);
            return result;
        }

        private static (float length, float centre) LongestRun(List<(float s, bool[] open)> samples, Func<bool[], bool> test)
        {
            int best = 0, bestStart = 0, run = 0, start = 0;
            for (var i = 0; i < samples.Count; i++)
            {
                if (test(samples[i].open)) { if (run == 0) start = i; run++; if (run > best) { best = run; bestStart = start; } }
                else run = 0;
            }
            if (best == 0) return (0f, 0f);
            var centre = (samples[bestStart].s + samples[bestStart + best - 1].s) / 2f;
            return (best * SampleStep, centre);
        }

        private static float Median(List<float> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>
        /// Writes a top-down cutaway (camera at <paramref name="cutHeight"/> looking down) of the prefab at the
        /// suggestion's scale, with the frame (yellow), the ring of cells around it (red) and cell centres (blue).
        /// </summary>
        public static void RenderTopDown(string prefabPath, WallSuggestion suggestion, string pngPath, float cutHeight = 1.9f, int pixelsPerMeter = 40)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), scene);
                go.transform.localScale = suggestion.Scale;
                go.transform.position = Vector3.zero;
                var colliders = go.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToList();
                var bounds = colliders[0].bounds;
                foreach (var c in colliders) bounds.Encapsulate(c.bounds);
                var cell = (float)suggestion.CellSize;
                var frameCentre = new Vector3(bounds.center.x - suggestion.FootprintOffset.x, 0f, bounds.center.z - suggestion.FootprintOffset.y);
                float widthM = (suggestion.Width + 2) * cell, heightM = (suggestion.Height + 2) * cell;
                int w = Mathf.RoundToInt(widthM * pixelsPerMeter), h = Mathf.RoundToInt(heightM * pixelsPerMeter);
                var camera = new GameObject("Cutaway Camera").AddComponent<Camera>();
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(camera.gameObject, scene);
                camera.scene = scene;
                camera.orthographic = true;
                camera.orthographicSize = heightM / 2f;
                camera.aspect = widthM / heightM;
                camera.transform.SetPositionAndRotation(frameCentre + Vector3.up * cutHeight, Quaternion.Euler(90f, 0f, 0f));
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = cutHeight + 1f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.55f, 0.6f, 0.5f);
                var light = new GameObject("Light").AddComponent<Light>();
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(light.gameObject, scene);
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
                var target = new RenderTexture(w, h, 24);
                camera.targetTexture = target;
                camera.Render();
                var previous = RenderTexture.active;
                RenderTexture.active = target;
                var texture = new Texture2D(w, h, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                RenderTexture.active = previous;
                for (var i = 0; i <= suggestion.Width + 2; i++)
                {
                    var px = Mathf.Clamp(Mathf.RoundToInt(i * cell * pixelsPerMeter), 0, w - 1);
                    var frame = i == 1 || i == suggestion.Width + 1;
                    for (var y = 0; y < h; y++) texture.SetPixel(px, y, frame ? Color.yellow : Color.red);
                }
                for (var j = 0; j <= suggestion.Height + 2; j++)
                {
                    var py = Mathf.Clamp(Mathf.RoundToInt(j * cell * pixelsPerMeter), 0, h - 1);
                    var frame = j == 1 || j == suggestion.Height + 1;
                    for (var x = 0; x < w; x++) texture.SetPixel(x, py, frame ? Color.yellow : Color.red);
                }
                for (var i = 0; i < suggestion.Width + 2; i++)
                    for (var j = 0; j < suggestion.Height + 2; j++)
                    {
                        int cx = Mathf.RoundToInt((i + 0.5f) * cell * pixelsPerMeter), cy = Mathf.RoundToInt((j + 0.5f) * cell * pixelsPerMeter);
                        for (var d = -3; d <= 3; d++) { texture.SetPixel(cx + d, cy, Color.blue); texture.SetPixel(cx, cy + d, Color.blue); }
                    }
                texture.Apply();
                File.WriteAllBytes(pngPath, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                target.Release();
                Object.DestroyImmediate(target);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
