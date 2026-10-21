using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>A 3D vector in meters or degrees. Pure (no UnityEngine) so cards work in NUnit.</summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 One = new Vec3(1, 1, 1);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);

        /// <summary>Component-wise product (applying a scale).</summary>
        public Vec3 Scale(Vec3 s) => new Vec3(X * s.X, Y * s.Y, Z * s.Z);

        public static Vec3 Min(Vec3 a, Vec3 b) => new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        public static Vec3 Max(Vec3 a, Vec3 b) => new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

        public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is Vec3 other && Equals(other);
        public override int GetHashCode() => unchecked((X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode());

        public bool ApproximatelyEquals(Vec3 other, double epsilon = 1e-4) =>
            Math.Abs(X - other.X) <= epsilon && Math.Abs(Y - other.Y) <= epsilon && Math.Abs(Z - other.Z) <= epsilon;

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }

    /// <summary>An axis-aligned box (center and size), in a prefab root's space.</summary>
    public readonly struct Box3
    {
        public readonly Vec3 Center;
        public readonly Vec3 Size;

        public Box3(Vec3 center, Vec3 size)
        {
            Center = center;
            Size = size;
        }

        public Vec3 Min => Center - Size * 0.5;
        public Vec3 Max => Center + Size * 0.5;

        public static Box3 FromMinMax(Vec3 min, Vec3 max) => new Box3((min + max) * 0.5, max - min);

        /// <summary>The eight corners, in a fixed order.</summary>
        public IEnumerable<Vec3> Corners()
        {
            var min = Min;
            var max = Max;
            for (var i = 0; i < 8; i++)
                yield return new Vec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        }

        public override string ToString() => $"{Center} {Size}";
    }

    /// <summary>A side of a footprint on the grid: +x, -x, +y (world +Z) or -y.</summary>
    public enum GridSide { PlusX, MinusX, PlusY, MinusY }

    public static class GridSides
    {
        // Clockwise seen from above, the way layout quarter turns go: +y, +x, -y, -x.
        private static readonly GridSide[] Clockwise = { GridSide.PlusY, GridSide.PlusX, GridSide.MinusY, GridSide.MinusX };

        /// <summary>The four sides, clockwise from +y.</summary>
        public static IReadOnlyList<GridSide> All => Clockwise;

        /// <summary>The side after <paramref name="quarterTurns"/> clockwise layout rotations.</summary>
        public static GridSide Rotate(GridSide side, int quarterTurns) =>
            Clockwise[(((Array.IndexOf(Clockwise, side) + quarterTurns) % 4) + 4) % 4];

        /// <summary>One step off this side, in cells.</summary>
        public static Cell Step(GridSide side) => side switch
        {
            GridSide.PlusX => new Cell(1, 0),
            GridSide.MinusX => new Cell(-1, 0),
            GridSide.PlusY => new Cell(0, 1),
            _ => new Cell(0, -1),
        };

        public static string Name(GridSide side) => side switch
        {
            GridSide.PlusX => "+x",
            GridSide.MinusX => "-x",
            GridSide.PlusY => "+y",
            _ => "-y",
        };

        public static GridSide Parse(string text)
        {
            foreach (GridSide side in Enum.GetValues(typeof(GridSide)))
                if (string.Equals(Name(side), text, StringComparison.Ordinal))
                    return side;
            throw new FormatException($"Unknown side '{text}' (+x, -x, +y, -y: grid axes at layout rotation 0, y = world Z).");
        }
    }

    /// <summary>What a piece is for in a level. Serialized as snake_case (see LevelCards.RoleName).</summary>
    public enum LevelRole
    {
        /// <summary>Blocks movement; its cover class needs review (between the half and full cover bands) or comes from the overrides file.</summary>
        Blocker,
        /// <summary>Blocks movement, half cover (below the half-cover maximum, USB default 1.2 m).</summary>
        CoverHalf,
        /// <summary>Blocks movement, full cover and sight (from the full-cover minimum, USB default 2 m).</summary>
        CoverFull,
        /// <summary>Flat ground pieces (roads, sidewalks); never blocks.</summary>
        Ground,
        /// <summary>Scenery that never blocks.</summary>
        Decor,
        /// <summary>A solid building blocker (no interiors), with storeys.</summary>
        Building,
        /// <summary>No rule decided it; left for review (later: vision on the thumbnail).</summary>
        Unknown,
    }

    /// <summary>
    /// A level catalog card: measured once on the isolated prefab, at root scale 1 with the
    /// root at the origin unrotated (child transforms count). Footprints are not stored;
    /// LevelCards.Footprint computes them for any cell size and scale.
    /// </summary>
    public sealed class LevelCard
    {
        public string Guid;
        /// <summary>The asset catalog id (pack-relative path), e.g. "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab".</summary>
        public string Id;
        public string Name;
        public string Pack;
        public LevelRole Role = LevelRole.Unknown;
        /// <summary>Which rule set the role: "override", "name" or "geometry".</summary>
        public string RoleSource;
        /// <summary>Cover the piece grants when it blocks; null when the game's adapter should decide (e.g. by height).</summary>
        public CoverLevel? Cover;
        /// <summary>Active renderers' mesh bounds in root space, meters at root scale 1.</summary>
        public Box3 VisualBounds;
        /// <summary>Enabled solid colliders' bounds in root space, meters at root scale 1; null when the prefab has none.</summary>
        public Box3? ColliderBounds;
        /// <summary>Bottom centre of the footprint bounds (colliders for blocking roles, else visual) relative to the pivot, root space at scale 1.</summary>
        public Vec3 PivotOffset;
        /// <summary>
        /// Top of the footprint bounds above the floor at PlacementScale and RootRotation (meters): the top above
        /// the pivot plus <see cref="Lift"/>.
        /// </summary>
        public double Height;
        /// <summary>
        /// Height of the pivot above the floor when placed (meters, 0 by default; an override sets it for pieces
        /// modelled below their pivot, e.g. barricades pivoted at mid-height). Layouts place the pivot at floor + Lift.
        /// </summary>
        public double Lift;
        /// <summary>
        /// The horizontal size (meters, X along grid x and Z along grid y at layout rotation 0) the piece claims on
        /// the grid instead of its measured bounds; null to use the bounds. Set by an override for pieces that may
        /// visually overhang their cells, e.g. a tyre heap scaled past one cell. Its cells come from this size.
        /// </summary>
        public FootprintSize? FootprintOverride;
        /// <summary>
        /// Buildings only: the side the front faces on the grid at layout rotation 0, root rotation applied; null when
        /// unknown. From the overrides file, else the model's forward, +Z (FrontSource "override" or "forward").
        /// </summary>
        public GridSide? Front;
        public string FrontSource;
        /// <summary>
        /// Buildings only: walls on cell sides at layout rotation 0 (plan Phase W), from the overrides file; null for a
        /// solid building. Its frame is the footprint, which stays walkable when the game supports side walls.
        /// </summary>
        public WallOutline Walls;
        /// <summary>
        /// Walled buildings only: the top of the walkable floor inside the walls, meters above the floor the piece is
        /// placed on (lift included), from the overrides file; null when not measured yet. The applier builds the
        /// building's floor box at this height (plan H1a).
        /// </summary>
        public double? FloorHeight;
        /// <summary>
        /// The footprint bounds' centre relative to the footprint centre (meters, X along grid x and Z along grid y at
        /// layout rotation 0); null for centred. Lets a building's walls sit on cell lines while porches overhang.
        /// </summary>
        public FootprintSize? FootprintOffset;
        /// <summary>Buildings only: storeys at PlacementScale (height / 3 m, at least 1).</summary>
        public int? Storeys;
        /// <summary>A measured child is rotated off 90° steps, so the boxes overstate its size.</summary>
        public bool Skewed;
        /// <summary>The approved root scale for layouts; defaults to the prefab root's own scale until reviewed.</summary>
        public Vec3 PlacementScale = Vec3.One;
        /// <summary>"prefab" (the root's own scale) or "override".</summary>
        public string ScaleSource = "prefab";
        /// <summary>The prefab root's own local rotation (Unity Euler degrees); layout quarter turns go on top of it.</summary>
        public Vec3 RootRotation = Vec3.Zero;
        /// <summary>Thumbnail path relative to the catalog folder, or null.</summary>
        public string Thumbnail;

        /// <summary>The bounds footprints use: colliders for blocking roles when present, visual otherwise.</summary>
        public Box3 FootprintBounds => BoundsFor(LevelCards.IsBlocking(Role));

        /// <summary>Colliders (when present) for a piece placed as a blocker, visual bounds for decor.</summary>
        public Box3 BoundsFor(bool blocking) => blocking && ColliderBounds.HasValue ? ColliderBounds.Value : VisualBounds;
    }

    /// <summary>A footprint size override in meters at layout rotation 0: X along grid x, Z along grid y.</summary>
    public readonly struct FootprintSize : IEquatable<FootprintSize>
    {
        public readonly double X;
        public readonly double Z;

        public FootprintSize(double x, double z)
        {
            X = x;
            Z = z;
        }

        public bool Equals(FootprintSize other) => X == other.X && Z == other.Z;
        public override bool Equals(object obj) => obj is FootprintSize other && Equals(other);
        public override int GetHashCode() => (X, Z).GetHashCode();
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "[{0}, {1}]", X, Z);
    }

    /// <summary>A card's footprint for one cell size and scale, at layout rotation 0.</summary>
    public readonly struct CardFootprint
    {
        /// <summary>Cells along grid +x at rotation 0.</summary>
        public readonly int Width;
        /// <summary>Cells along grid +y at rotation 0.</summary>
        public readonly int Depth;
        /// <summary>Extent of the footprint bounds in meters: X along grid x, Y up, Z along grid y (rotation 0, scale and root rotation applied).</summary>
        public readonly Vec3 Size;
        /// <summary>Bounds centre relative to the pivot, same frame (the applier subtracts it to centre the piece on its cells).</summary>
        public readonly Vec3 Center;
        /// <summary>Cells minus meters per axis (X, Z): the slack left in the footprint; slightly negative within the 5 cm tolerance.</summary>
        public readonly double FitErrorX;
        public readonly double FitErrorZ;
        /// <summary>Top of the bounds above the floor (meters): above the pivot, plus the card's lift when built from a card.</summary>
        public readonly double Height;
        /// <summary>The root rotation is not a quarter turn about Y (or the card is skewed), so the box overstates the size.</summary>
        public readonly bool Approximate;
        /// <summary>Width and Depth come from the card's footprint override, not the bounds; FitError is then negative where the piece overhangs.</summary>
        public readonly bool Overridden;

        public CardFootprint(int width, int depth, Vec3 size, Vec3 center, double fitErrorX, double fitErrorZ, double height, bool approximate, bool overridden = false)
        {
            Overridden = overridden;
            Width = width;
            Depth = depth;
            Size = size;
            Center = center;
            FitErrorX = fitErrorX;
            FitErrorZ = fitErrorZ;
            Height = height;
            Approximate = approximate;
        }

        /// <summary>The layout piece spec (rotation 0 footprint plus cover) this footprint gives.</summary>
        public PieceSpec ToSpec(CoverLevel cover) => new PieceSpec(Width, Depth, cover);

        /// <summary>Footprint size on the board after quarter turns (odd turns swap the axes).</summary>
        public Cell RotatedSize(int rotation) => rotation % 2 == 0 ? new Cell(Width, Depth) : new Cell(Depth, Width);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0}x{1} cells ({2:0.00} x {3:0.00} m, fit {4:0.00}/{5:0.00}, h {6:0.00})",
                Width, Depth, Size.X, Size.Z, FitErrorX, FitErrorZ, Height);
    }

    /// <summary>
    /// Pure card rules: footprints on request, role detection and the card JSON shape.
    /// </summary>
    public static class LevelCards
    {
        /// <summary>A piece up to 5 cm longer than whole cells still counts as fitting them (same as the applier).</summary>
        public const double FootprintTolerance = 0.05;
        /// <summary>USB's default: below this a cover-family piece is half cover (a game sets its own in GridRules.CoverHeights).</summary>
        public const double HalfCoverMaxHeight = 1.2;
        /// <summary>USB's default: from this height up a piece is full cover (a game sets its own in GridRules.CoverHeights).</summary>
        public const double FullCoverMinHeight = 2.0;
        /// <summary>Unnamed pieces lower than this are ground (floor tiles, walkways: about 0.25-0.31 m in the Apocalypse packs).</summary>
        public const double GroundMaxHeight = 0.35;
        /// <summary>Unnamed pieces whose longest side is shorter than this are decor.</summary>
        public const double DecorMaxExtent = 0.5;
        /// <summary>Bld_ pieces need at least this on both horizontal sides (and this height) to count as buildings.</summary>
        public const double BuildingMinExtent = 2.0;
        public const double StoreyHeight = 3.0;

        public static bool IsBlocking(LevelRole role) =>
            role == LevelRole.Blocker || role == LevelRole.CoverHalf || role == LevelRole.CoverFull || role == LevelRole.Building;

        // ---- Footprints ----

        /// <summary>
        /// Footprint of a card at layout rotation 0: the footprint bounds scaled by <paramref name="scale"/>
        /// (the card's PlacementScale when null, e.g. a per-piece exception otherwise), turned by the root
        /// rotation, rounded up to whole cells after the 5 cm tolerance. <paramref name="blocking"/> picks the
        /// bounds by how the piece is placed (a layout blocker measures its colliders, decor its renderers);
        /// null follows the card's role.
        /// </summary>
        public static CardFootprint Footprint(LevelCard card, double cellSize, Vec3? scale = null, bool? blocking = null)
        {
            var bounds = blocking.HasValue ? card.BoundsFor(blocking.Value) : card.FootprintBounds;
            var measured = Lifted(Footprint(bounds, card.RootRotation, scale ?? card.PlacementScale, cellSize, card.Skewed), card.Lift);
            if (!card.FootprintOverride.HasValue) return measured;
            // The override claims a size on the grid; the measured size and centre stay, so a negative fit error shows the overhang.
            var claimed = card.FootprintOverride.Value;
            var width = CellsFor(claimed.X, cellSize);
            var depth = CellsFor(claimed.Z, cellSize);
            return new CardFootprint(width, depth, measured.Size, measured.Center,
                Round(width * cellSize - measured.Size.X), Round(depth * cellSize - measured.Size.Z), measured.Height, measured.Approximate, true);
        }

        /// <summary>The same footprint with its height measured from a pivot <paramref name="lift"/> meters above the floor.</summary>
        public static CardFootprint Lifted(CardFootprint f, double lift) => lift == 0 ? f :
            new CardFootprint(f.Width, f.Depth, f.Size, f.Center, f.FitErrorX, f.FitErrorZ, f.Height + lift, f.Approximate, f.Overridden);

        public static CardFootprint Footprint(Box3 bounds, Vec3 rootRotation, Vec3 scale, double cellSize, bool skewed = false)
        {
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");
            var rotation = EulerMatrix(rootRotation);
            var min = new Vec3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
            var max = new Vec3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
            foreach (var corner in bounds.Corners())
            {
                var point = Multiply(rotation, corner.Scale(scale));
                min = Vec3.Min(min, point);
                max = Vec3.Max(max, point);
            }
            var size = max - min;
            var width = CellsFor(size.X, cellSize);
            var depth = CellsFor(size.Z, cellSize);
            return new CardFootprint(width, depth, size, (min + max) * 0.5,
                Round(width * cellSize - size.X), Round(depth * cellSize - size.Z), max.Y,
                skewed || !IsSignedPermutation(rotation) || !IsYawOnly(rotation));
        }

        public static int CellsFor(double meters, double cellSize) =>
            Math.Max(1, (int)Math.Ceiling(Math.Round((meters - FootprintTolerance) / cellSize, 9)));

        private static double Round(double value) => Math.Round(value, 6);

        /// <summary>Unity's Euler convention: Z, then X, then Y (R = Ry · Rx · Rz), left-handed.</summary>
        public static double[,] EulerMatrix(Vec3 eulerDegrees)
        {
            double Rad(double d) => d * Math.PI / 180.0;
            var (cx, sx) = (Math.Cos(Rad(eulerDegrees.X)), Math.Sin(Rad(eulerDegrees.X)));
            var (cy, sy) = (Math.Cos(Rad(eulerDegrees.Y)), Math.Sin(Rad(eulerDegrees.Y)));
            var (cz, sz) = (Math.Cos(Rad(eulerDegrees.Z)), Math.Sin(Rad(eulerDegrees.Z)));
            var ry = new[,] { { cy, 0, sy }, { 0, 1, 0 }, { -sy, 0, cy } };
            var rx = new[,] { { 1, 0, 0 }, { 0, cx, -sx }, { 0, sx, cx } };
            var rz = new[,] { { cz, -sz, 0 }, { sz, cz, 0 }, { 0, 0, 1 } };
            return Multiply(Multiply(ry, rx), rz);
        }

        public static Vec3 Rotate(Vec3 eulerDegrees, Vec3 point) => Multiply(EulerMatrix(eulerDegrees), point);

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            var result = new double[3, 3];
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    result[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
            return result;
        }

        private static Vec3 Multiply(double[,] m, Vec3 v) => new Vec3(
            m[0, 0] * v.X + m[0, 1] * v.Y + m[0, 2] * v.Z,
            m[1, 0] * v.X + m[1, 1] * v.Y + m[1, 2] * v.Z,
            m[2, 0] * v.X + m[2, 1] * v.Y + m[2, 2] * v.Z);

        // Every axis maps onto an axis (90° steps): the rotated box is still exact.
        private static bool IsSignedPermutation(double[,] m)
        {
            const double eps = 1e-4;
            for (var j = 0; j < 3; j++)
            {
                var ones = 0;
                for (var i = 0; i < 3; i++)
                {
                    var v = Math.Abs(m[i, j]);
                    if (Math.Abs(v - 1) <= eps) ones++;
                    else if (v > eps) return false;
                }
                if (ones != 1) return false;
            }
            return true;
        }

        // Up stays up: a root tilt (e.g. a model lying on its side) is reported as approximate
        // because the layout's "height" then comes from another prefab axis.
        private static bool IsYawOnly(double[,] m) => Math.Abs(m[1, 1] - 1) <= 1e-4;

        // ---- Role detection ----

        /// <summary>The family a name or folder suggests, before geometry refines it.</summary>
        public enum NameHint { None, Decor, Ground, Cover, Building }

        // Checked in this order, against the file name first and then the folder path. The
        // decor tokens come first so a "Greeble_Wall" or a "Sign_Road" is not a wall or a road.
        private static readonly (string token, NameHint hint)[] NameTokens =
        {
            ("Greeble", NameHint.Decor),
            ("Decal", NameHint.Decor),
            ("Sign", NameHint.Decor),
            ("Road", NameHint.Ground),
            ("Sidewalk", NameHint.Ground),
            ("Sandbag", NameHint.Cover),
            ("Barrier", NameHint.Cover),
            ("Wall", NameHint.Cover),
            ("Fence", NameHint.Cover),
            ("Bld_", NameHint.Building),
        };

        public static NameHint HintFromName(string name, string path)
        {
            foreach (var text in new[] { name ?? "", FolderOf(path) })
                foreach (var (token, hint) in NameTokens)
                    if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        return hint;
            return NameHint.None;
        }

        private static string FolderOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path.Substring(0, slash);
        }

        /// <summary>
        /// Role from a name hint and the placed footprint (scale and root rotation applied).
        /// Cover-family names split by height; Bld_ needs a building-sized box; everything
        /// else falls to geometry (small → decor, flat → ground, then the cover heights).
        /// </summary>
        public static LevelRole RoleFrom(NameHint hint, CardFootprint placed, out string source, CoverHeightBands bands = null)
        {
            bands ??= CoverHeightBands.Default;
            var height = placed.Height;
            source = "name";
            switch (hint)
            {
                case NameHint.Decor:
                    return LevelRole.Decor;
                case NameHint.Ground:
                    return LevelRole.Ground;
                case NameHint.Cover:
                    return CoverByHeight(height, LevelRole.Blocker, bands);
                case NameHint.Building when Math.Min(placed.Size.X, placed.Size.Z) >= BuildingMinExtent && height >= BuildingMinExtent:
                    return LevelRole.Building;
            }
            source = "geometry";
            if (Math.Max(placed.Size.X, placed.Size.Z) < DecorMaxExtent) return LevelRole.Decor;
            if (height < GroundMaxHeight) return LevelRole.Ground;
            return CoverByHeight(height, LevelRole.Unknown, bands);
        }

        private static LevelRole CoverByHeight(double height, LevelRole between, CoverHeightBands bands)
        {
            if (height < bands.HalfMax) return LevelRole.CoverHalf;
            if (height >= bands.FullMin) return LevelRole.CoverFull;
            return between;
        }

        /// <summary>
        /// The grid side a model's forward (+Z) faces at layout rotation 0, turned by the root rotation; null when
        /// the root is tilted so forward points mostly up or down. Synty buildings are modelled facing +Z.
        /// </summary>
        public static GridSide? ForwardSide(Vec3 rootRotation)
        {
            var forward = Rotate(rootRotation, new Vec3(0, 0, 1));
            if (Math.Abs(forward.Y) >= Math.Max(Math.Abs(forward.X), Math.Abs(forward.Z))) return null;
            return Math.Abs(forward.X) > Math.Abs(forward.Z)
                ? (forward.X > 0 ? GridSide.PlusX : GridSide.MinusX)
                : (forward.Z > 0 ? GridSide.PlusY : GridSide.MinusY);
        }

        /// <summary>Cover implied by a role; null for Blocker and Unknown (the game adapter decides).</summary>
        public static CoverLevel? CoverFor(LevelRole role)
        {
            switch (role)
            {
                case LevelRole.CoverHalf: return CoverLevel.Half;
                case LevelRole.CoverFull:
                case LevelRole.Building: return CoverLevel.Full;
                case LevelRole.Ground:
                case LevelRole.Decor: return CoverLevel.None;
                default: return null;
            }
        }

        public static int StoreysFor(double height) => Math.Max(1, (int)Math.Round(height / StoreyHeight, MidpointRounding.AwayFromZero));

        /// <summary>
        /// Fills role, cover, placement scale and storeys from the overrides, the name and the
        /// measured bounds (card bounds, root rotation, skew and root scale must be set).
        /// </summary>
        /// <param name="bands">The game's cover heights for role detection (GridRules.CoverHeights); USB's defaults when null.</param>
        public static void Classify(LevelCard card, Vec3 rootScale, LevelOverride entry, CoverHeightBands bands = null)
        {
            card.PlacementScale = entry?.PlacementScale ?? rootScale;
            card.ScaleSource = entry?.PlacementScale != null ? "override" : "prefab";
            card.Lift = entry?.Lift ?? 0;
            card.FootprintOverride = entry?.Footprint;
            card.FootprintOffset = entry?.FootprintOffset;
            // Role from the overrides, else from the name and the blocking (collider-first) bounds.
            var blocking = card.ColliderBounds ?? card.VisualBounds;
            var placed = Lifted(Footprint(blocking, card.RootRotation, card.PlacementScale, 1.0, card.Skewed), card.Lift);
            if (entry?.Role != null)
            {
                card.Role = entry.Role.Value;
                card.RoleSource = "override";
            }
            else
            {
                card.Role = RoleFrom(HintFromName(card.Name, card.Id), placed, out var source, bands);
                card.RoleSource = source;
            }
            card.Cover = entry?.Cover ?? CoverFor(card.Role);
            var bounds = card.FootprintBounds;
            card.PivotOffset = new Vec3(bounds.Center.X, bounds.Min.Y, bounds.Center.Z);
            card.Height = Footprint(bounds, card.RootRotation, card.PlacementScale, 1.0, card.Skewed).Height + card.Lift;
            card.Storeys = entry?.Storeys ?? (card.Role == LevelRole.Building ? StoreysFor(card.Height) : (int?)null);
            card.Front = null;
            card.FrontSource = null;
            card.Walls = null;
            card.FloorHeight = null;
            if (card.Role == LevelRole.Building)
            {
                card.Walls = entry?.Walls;
                if (card.Walls != null) card.FloorHeight = entry?.FloorHeight;
                card.Front = entry?.Front ?? ForwardSide(card.RootRotation);
                if (card.Front.HasValue) card.FrontSource = entry?.Front != null ? "override" : "forward";
            }
        }

        // ---- Names ----

        public static string RoleName(LevelRole role)
        {
            switch (role)
            {
                case LevelRole.Blocker: return "blocker";
                case LevelRole.CoverHalf: return "cover_half";
                case LevelRole.CoverFull: return "cover_full";
                case LevelRole.Ground: return "ground";
                case LevelRole.Decor: return "decor";
                case LevelRole.Building: return "building";
                default: return "unknown";
            }
        }

        public static LevelRole ParseRole(string text)
        {
            foreach (LevelRole role in Enum.GetValues(typeof(LevelRole)))
                if (string.Equals(RoleName(role), text, StringComparison.Ordinal))
                    return role;
            throw new FormatException($"Unknown level role '{text}' (blocker, cover_half, cover_full, ground, decor, building, unknown).");
        }

        public static CoverLevel ParseCover(string text)
        {
            if (Enum.TryParse(text, false, out CoverLevel cover)) return cover;
            throw new FormatException($"Unknown cover '{text}' (None, Half, Full).");
        }

        // ---- JSON ----

        /// <summary>The card as written in level-catalog.json (the GUID is the key, not a field).</summary>
        public static JObject ToJson(LevelCard card)
        {
            var json = new JObject
            {
                ["id"] = card.Id,
                ["name"] = card.Name,
                ["pack"] = card.Pack,
                ["levelRole"] = RoleName(card.Role),
                ["roleSource"] = card.RoleSource,
                ["cover"] = card.Cover.HasValue ? new JValue(card.Cover.Value.ToString()) : JValue.CreateNull(),
                ["visualBounds"] = BoxJson(card.VisualBounds),
                ["colliderBounds"] = card.ColliderBounds.HasValue ? BoxJson(card.ColliderBounds.Value) : null,
                ["pivotOffset"] = VecJson(card.PivotOffset),
                ["height"] = Mm(card.Height),
                ["lift"] = Mm(card.Lift),
                ["footprintOverride"] = card.FootprintOverride.HasValue
                    ? new JArray(Mm(card.FootprintOverride.Value.X), Mm(card.FootprintOverride.Value.Z)) : null,
                ["storeys"] = card.Storeys,
                ["front"] = card.Front.HasValue ? new JValue(GridSides.Name(card.Front.Value)) : JValue.CreateNull(),
                ["frontSource"] = card.FrontSource,
                ["skewed"] = card.Skewed,
                ["placementScale"] = VecJson(card.PlacementScale),
                ["scaleSource"] = card.ScaleSource,
                ["rootRotation"] = VecJson(card.RootRotation),
            };
            // Only walled buildings carry these, so other cards keep their shape.
            if (card.FootprintOffset.HasValue)
                json["footprintOffset"] = new JArray(Mm(card.FootprintOffset.Value.X), Mm(card.FootprintOffset.Value.Z));
            if (card.Walls != null) json["walls"] = card.Walls.ToJson();
            if (card.FloorHeight.HasValue) json["floorHeight"] = Mm(card.FloorHeight.Value);
            if (card.Thumbnail != null) json["thumbnail"] = card.Thumbnail;
            return json;
        }

        public static LevelCard FromJson(string guid, JObject json)
        {
            var collider = json["colliderBounds"];
            var storeys = json["storeys"];
            var cover = json["cover"];
            var front = json["front"];
            return new LevelCard
            {
                Guid = guid,
                Id = (string)json["id"],
                Name = (string)json["name"],
                Pack = (string)json["pack"],
                Role = ParseRole((string)json["levelRole"]),
                RoleSource = (string)json["roleSource"],
                Cover = cover == null || cover.Type == JTokenType.Null ? (CoverLevel?)null : ParseCover((string)cover),
                VisualBounds = ParseBox(json["visualBounds"]),
                ColliderBounds = collider == null || collider.Type == JTokenType.Null ? (Box3?)null : ParseBox(collider),
                PivotOffset = ParseVec(json["pivotOffset"]),
                Height = (double)json["height"],
                Lift = (double?)json["lift"] ?? 0,
                FootprintOverride = ParseFootprint(json["footprintOverride"]),
                Storeys = storeys == null || storeys.Type == JTokenType.Null ? (int?)null : (int)storeys,
                Front = front == null || front.Type == JTokenType.Null ? (GridSide?)null : GridSides.Parse((string)front),
                FrontSource = (string)json["frontSource"],
                Walls = json["walls"] == null || json["walls"].Type == JTokenType.Null ? null : WallOutline.FromJson(json["walls"]),
                FootprintOffset = ParseOffset(json["footprintOffset"]),
                FloorHeight = (double?)json["floorHeight"],
                Skewed = (bool?)json["skewed"] ?? false,
                PlacementScale = ParseVec(json["placementScale"]),
                ScaleSource = (string)json["scaleSource"] ?? "prefab",
                RootRotation = ParseVec(json["rootRotation"]),
                Thumbnail = (string)json["thumbnail"],
            };
        }

        /// <summary>A "footprint" value: [x, z] meters, both positive; null for a missing or null token.</summary>
        public static FootprintSize? ParseFootprint(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JArray a) || a.Count != 2) throw new FormatException("Expected [x, z] in meters.");
            var size = new FootprintSize((double)a[0], (double)a[1]);
            if (!(size.X > 0) || !(size.Z > 0)) throw new FormatException("footprint sides must be positive");
            return size;
        }

        /// <summary>A "footprintOffset" value: [x, z] meters, any sign; null for a missing or null token.</summary>
        public static FootprintSize? ParseOffset(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JArray a) || a.Count != 2) throw new FormatException("Expected [x, z] in meters.");
            var offset = new FootprintSize((double)a[0], (double)a[1]);
            if (double.IsNaN(offset.X) || double.IsNaN(offset.Z) || double.IsInfinity(offset.X) || double.IsInfinity(offset.Z))
                throw new FormatException("footprintOffset must be finite");
            return offset;
        }

        /// <summary>Reads level-catalog.json into cards keyed by GUID.</summary>
        public static Dictionary<string, LevelCard> ParseCatalog(string text)
        {
            var root = JObject.Parse(text);
            var result = new Dictionary<string, LevelCard>(StringComparer.Ordinal);
            if (root["cards"] is JObject cards)
                foreach (var property in cards.Properties())
                    result[property.Name] = FromJson(property.Name, (JObject)property.Value);
            return result;
        }

        private static double Mm(double value) => Math.Round(value, 3);

        private static JArray VecJson(Vec3 v) => new JArray(Mm(v.X), Mm(v.Y), Mm(v.Z));

        private static JObject BoxJson(Box3 box) => new JObject { ["center"] = VecJson(box.Center), ["size"] = VecJson(box.Size) };

        public static Vec3 ParseVec(JToken token)
        {
            if (token is JArray array && array.Count == 3)
                return new Vec3((double)array[0], (double)array[1], (double)array[2]);
            if (token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer))
            {
                var s = (double)token;
                return new Vec3(s, s, s);
            }
            throw new FormatException($"Expected [x, y, z] (or one number for a uniform scale), got {token?.ToString(Newtonsoft.Json.Formatting.None) ?? "nothing"}.");
        }

        private static Box3 ParseBox(JToken token) => new Box3(ParseVec(token?["center"]), ParseVec(token?["size"]));
    }
}
