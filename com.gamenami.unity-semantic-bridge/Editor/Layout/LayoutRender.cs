using System;
using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>
    /// render_layout_topdown: an orthographic capture straight down onto a board region, framed on whole cells,
    /// with the grid drawn over it (every 5th line stronger, the board edge and cell 0,0 marked) and the level's
    /// deploy and enemy cells outlined. Image +x is grid +x and image up is grid +y, like the validators' ASCII
    /// map. A temporary hidden camera renders it; the scene is not changed. With a cut height the camera sits that
    /// far above the floor, so roofs and upper floors are clipped by its near plane and interiors show; renderers
    /// reaching above the cut cast no shadow for that capture (restored afterwards), so the clipped roof does not
    /// darken the floor it no longer covers.
    /// </summary>
    public static class LayoutRender
    {
        public const int MaxPixels = 4096;
        private static readonly Color32 GridLine = new Color32(255, 255, 255, 70);
        private static readonly Color32 GridLineFifth = new Color32(255, 230, 90, 150);
        private static readonly Color32 BoardEdge = new Color32(255, 80, 60, 230);
        private static readonly Color32 OriginMark = new Color32(60, 230, 255, 255);
        private static readonly Color32 DeployMark = new Color32(70, 140, 255, 255);
        private static readonly Color32 EnemyMark = new Color32(255, 50, 50, 255);
        private static readonly Color Background = new Color(0.12f, 0.12f, 0.12f, 1f);

        public sealed class Capture
        {
            public byte[] Jpg;
            public int Width;
            public int Height;
            public Cell Min;
            public Cell Max;
            public float PixelsPerCell;
        }

        // MCP entry point (method "render_layout_topdown"): {image (base64 JPEG), caption}.
        public static string RenderTopDown(JObject mcpMessage)
        {
            mcpMessage ??= new JObject();
            var scene = LayoutTools.ResolveScene(mcpMessage);
            var adapter = LayoutAdapters.Find();
            var capture = Render(scene, adapter,
                ReadRegion(mcpMessage["cells"]),
                mcpMessage["margin_cells"]?.Value<int?>() ?? 1,
                mcpMessage["grid"]?.Value<bool?>() ?? true,
                mcpMessage["markers"]?.Value<bool?>() ?? true,
                mcpMessage["cut_height"]?.Value<float?>(),
                mcpMessage["max_width"]?.Value<int?>() ?? 1280,
                mcpMessage["jpg_quality"]?.Value<int?>() ?? 70);
            var name = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;
            var caption = $"Top-down {name}: cells {capture.Min}..{capture.Max} (outside the board where the margin reaches past it), " +
                          $"{capture.Width}x{capture.Height} px, {capture.PixelsPerCell:0.#} px per cell; image right = grid +x, up = grid +y.";
            return new JObject { ["image"] = Convert.ToBase64String(capture.Jpg), ["caption"] = caption }.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <param name="region">Cells to frame, inclusive (min, max); the whole board when null.</param>
        public static Capture Render(Scene scene, IGridRulesAdapter adapter, (Cell min, Cell max)? region, int marginCells, bool grid,
            bool markers, float? cutHeight, int maxWidth, int jpgQuality)
        {
            if (!adapter.TryGetBoard(scene, out var board, out var boardError)) throw new BridgeToolException(boardError);
            if (marginCells < 0 || marginCells > 50) throw new BridgeToolException("margin_cells must be 0-50.");
            if (maxWidth < 64 || maxWidth > MaxPixels) throw new BridgeToolException($"max_width must be 64-{MaxPixels}.");
            if (jpgQuality < 1 || jpgQuality > 100) throw new BridgeToolException("jpg_quality must be 1-100.");
            if (cutHeight.HasValue && cutHeight.Value <= 0) throw new BridgeToolException("cut_height must be above the floor (meters > 0).");

            var (min, max) = region ?? (new Cell(0, 0), new Cell(board.Width - 1, board.Height - 1));
            if (min.X > max.X || min.Y > max.Y) throw new BridgeToolException("cells must be [x0, y0, x1, y1] with x0 <= x1 and y0 <= y1.");
            min = new Cell(min.X - marginCells, min.Y - marginCells);
            max = new Cell(max.X + marginCells, max.Y + marginCells);

            var cellSize = adapter.Rules.CellSize;
            var first = adapter.CellToWorld(min, board);
            var last = adapter.CellToWorld(max, board);
            var left = Mathf.Min(first.x, last.x) - cellSize / 2f;
            var right = Mathf.Max(first.x, last.x) + cellSize / 2f;
            var bottom = Mathf.Min(first.z, last.z) - cellSize / 2f;
            var top = Mathf.Max(first.z, last.z) + cellSize / 2f;
            var worldWidth = right - left;
            var worldHeight = top - bottom;

            var width = maxWidth;
            var height = Mathf.RoundToInt(width * worldHeight / worldWidth);
            if (height > MaxPixels)
            {
                height = MaxPixels;
                width = Mathf.Max(1, Mathf.RoundToInt(height * worldWidth / worldHeight));
            }
            height = Mathf.Max(1, height);

            var pixels = RenderPixels(scene, board, left, bottom, worldWidth, worldHeight, width, height, cutHeight);
            var pixelsPerCell = width / (worldWidth / cellSize);
            if (grid || markers)
            {
                var overlay = new Overlay(pixels, width, height, left, bottom, worldWidth, worldHeight, cellSize, adapter, board);
                if (grid) overlay.DrawGrid(min, max);
                if (markers)
                {
                    foreach (var cell in adapter.ReadDeployCells(scene)) overlay.OutlineCell(cell, DeployMark);
                    foreach (var enemy in adapter.ReadEnemySpawns(scene)) overlay.OutlineCell(enemy.Cell, EnemyMark);
                }
            }

            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply();
                return new Capture
                {
                    Jpg = texture.EncodeToJPG(jpgQuality), Width = width, Height = height, Min = min, Max = max,
                    PixelsPerCell = pixelsPerCell,
                };
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static Color32[] RenderPixels(Scene scene, GridBoard board, float left, float bottom, float worldWidth, float worldHeight,
            int width, int height, float? cutHeight)
        {
            var cameraObject = new GameObject("USB Top-down Camera") { hideFlags = HideFlags.HideAndDontSave };
            var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previousActive = RenderTexture.active;
            var readback = new Texture2D(width, height, TextureFormat.RGB24, false);
            var shadowless = new List<(Renderer renderer, ShadowCastingMode mode)>();
            try
            {
                if (cutHeight.HasValue)
                    foreach (var renderer in RenderersOver(scene, left, bottom, worldWidth, worldHeight))
                        if (renderer.bounds.max.y > board.FloorY + cutHeight.Value && renderer.shadowCastingMode != ShadowCastingMode.Off)
                        {
                            shadowless.Add((renderer, renderer.shadowCastingMode));
                            renderer.shadowCastingMode = ShadowCastingMode.Off;
                        }
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = worldHeight / 2f;
                camera.aspect = worldWidth / worldHeight;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Background;
                // Looking straight down with world +Z as the image's up: grid +y is up, as in the ASCII maps.
                float eye;
                if (cutHeight.HasValue)
                {
                    eye = board.FloorY + cutHeight.Value;
                    camera.nearClipPlane = 0.01f;
                }
                else
                {
                    eye = Mathf.Min(HighestTop(scene, left, bottom, worldWidth, worldHeight, board.FloorY) + 1f, board.FloorY + 200f);
                    camera.nearClipPlane = 0.1f;
                }
                camera.farClipPlane = eye - board.FloorY + 20f;
                cameraObject.transform.SetPositionAndRotation(
                    new Vector3(left + worldWidth / 2f, eye, bottom + worldHeight / 2f), Quaternion.Euler(90f, 0f, 0f));
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply();
                return readback.GetPixels32();
            }
            finally
            {
                foreach (var (renderer, mode) in shadowless)
                    if (renderer != null) renderer.shadowCastingMode = mode;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(cameraObject);
            }
        }

        // The top of the highest renderer over the region, so the camera sits just above everything (shadow
        // distance counts from the camera, so a camera far above the board would lose the shadows).
        private static float HighestTop(Scene scene, float left, float bottom, float worldWidth, float worldHeight, float floorY)
        {
            var top = floorY + 5f;
            foreach (var renderer in RenderersOver(scene, left, bottom, worldWidth, worldHeight))
                top = Mathf.Max(top, renderer.bounds.max.y);
            return top;
        }

        // Enabled renderers of the scene whose bounds overlap the region from above.
        private static IEnumerable<Renderer> RenderersOver(Scene scene, float left, float bottom, float worldWidth, float worldHeight)
        {
            var area = new Rect(left, bottom, worldWidth, worldHeight);
            foreach (var root in scene.GetRootGameObjects())
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled) continue;
                var bounds = renderer.bounds;
                if (area.Overlaps(new Rect(bounds.min.x, bounds.min.z, bounds.size.x, bounds.size.z))) yield return renderer;
            }
        }

        private static (Cell min, Cell max)? ReadRegion(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JArray values) || values.Count != 4 || values.Any(v => v.Type != JTokenType.Integer))
                throw new BridgeToolException("cells must be [x0, y0, x1, y1] (whole cells, inclusive).");
            return (new Cell((int)values[0], (int)values[1]), new Cell((int)values[2], (int)values[3]));
        }

        // Draws into the read-back pixels (row 0 is the image's bottom, i.e. lowest world Z).
        private sealed class Overlay
        {
            private readonly Color32[] _pixels;
            private readonly int _width, _height;
            private readonly float _left, _bottom, _scaleX, _scaleY, _cellSize;
            private readonly IGridRulesAdapter _adapter;
            private readonly GridBoard _board;

            public Overlay(Color32[] pixels, int width, int height, float left, float bottom, float worldWidth, float worldHeight,
                float cellSize, IGridRulesAdapter adapter, GridBoard board)
            {
                _pixels = pixels;
                _width = width;
                _height = height;
                _left = left;
                _bottom = bottom;
                _scaleX = width / worldWidth;
                _scaleY = height / worldHeight;
                _cellSize = cellSize;
                _adapter = adapter;
                _board = board;
            }

            public void DrawGrid(Cell min, Cell max)
            {
                // Cell boundaries: x = index of the cell to the boundary's right (or above it).
                for (var x = min.X; x <= max.X + 1; x++)
                {
                    var px = PixelX(CellMinCorner(new Cell(x, min.Y)).x);
                    var color = x == 0 || x == _board.Width ? BoardEdge : x % 5 == 0 ? GridLineFifth : GridLine;
                    var thick = color.Equals(GridLine) ? 1 : 2;
                    for (var t = 0; t < thick; t++) VerticalLine(px + t - thick / 2, 0, _height - 1, color);
                }
                for (var y = min.Y; y <= max.Y + 1; y++)
                {
                    var py = PixelY(CellMinCorner(new Cell(min.X, y)).z);
                    var color = y == 0 || y == _board.Height ? BoardEdge : y % 5 == 0 ? GridLineFifth : GridLine;
                    var thick = color.Equals(GridLine) ? 1 : 2;
                    for (var t = 0; t < thick; t++) HorizontalLine(0, _width - 1, py + t - thick / 2, color);
                }
                // Cell 0,0: an L in its lower-left corner.
                var origin = CellMinCorner(new Cell(0, 0));
                int ox = PixelX(origin.x) + 2, oy = PixelY(origin.z) + 2;
                var arm = Mathf.Max(4, Mathf.RoundToInt(_cellSize * _scaleX * 0.45f));
                for (var t = 0; t < 2; t++)
                {
                    HorizontalLine(ox, ox + arm, oy + t, OriginMark);
                    VerticalLine(ox + t, oy, oy + arm, OriginMark);
                }
            }

            public void OutlineCell(Cell cell, Color32 color)
            {
                var corner = CellMinCorner(cell);
                var inset = Mathf.Max(2, Mathf.RoundToInt(_cellSize * _scaleX * 0.12f));
                int x0 = PixelX(corner.x) + inset, x1 = PixelX(corner.x + _cellSize) - inset;
                int y0 = PixelY(corner.z) + inset, y1 = PixelY(corner.z + _cellSize) - inset;
                if (x1 <= x0 || y1 <= y0) return;
                for (var t = 0; t < 2; t++)
                {
                    HorizontalLine(x0, x1, y0 + t, color);
                    HorizontalLine(x0, x1, y1 - t, color);
                    VerticalLine(x0 + t, y0, y1, color);
                    VerticalLine(x1 - t, y0, y1, color);
                }
            }

            private Vector3 CellMinCorner(Cell cell)
            {
                var center = _adapter.CellToWorld(cell, _board);
                return new Vector3(center.x - _cellSize / 2f, 0f, center.z - _cellSize / 2f);
            }

            private int PixelX(float worldX) => Mathf.RoundToInt((worldX - _left) * _scaleX);
            private int PixelY(float worldZ) => Mathf.RoundToInt((worldZ - _bottom) * _scaleY);

            private void HorizontalLine(int x0, int x1, int y, Color32 color)
            {
                if (y < 0 || y >= _height) return;
                for (var x = Mathf.Max(0, x0); x <= Mathf.Min(_width - 1, x1); x++) Blend(x, y, color);
            }

            private void VerticalLine(int x, int y0, int y1, Color32 color)
            {
                if (x < 0 || x >= _width) return;
                for (var y = Mathf.Max(0, y0); y <= Mathf.Min(_height - 1, y1); y++) Blend(x, y, color);
            }

            private void Blend(int x, int y, Color32 color)
            {
                var index = y * _width + x;
                var under = _pixels[index];
                var a = color.a / 255f;
                _pixels[index] = new Color32(
                    (byte)(under.r + (color.r - under.r) * a),
                    (byte)(under.g + (color.g - under.g) * a),
                    (byte)(under.b + (color.b - under.b) * a), 255);
            }
        }
    }
}
