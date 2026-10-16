using System;
using System.Collections.Generic;
using Gamenami.UnitySemanticBridge.Layout;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>
    /// Bounds of a piece in its own quarter-turn frame: origin at the instance pivot,
    /// axes rotated by the piece's yaw, meters (all scales applied). Rotation 0 maps
    /// frame +X to grid +x and frame +Z to grid +y.
    /// </summary>
    public readonly struct PieceBounds
    {
        public readonly Vector3 Center;
        public readonly Vector3 Size;

        public PieceBounds(Vector3 center, Vector3 size)
        {
            Center = center;
            Size = size;
        }
    }

    public static class PieceGeometry
    {
        // A piece up to 5 cm longer than whole cells still counts as fitting them; the
        // applier then insets its colliders.
        public const float FootprintTolerance = 0.05f;

        public static Quaternion Frame(int rotation) => Quaternion.Euler(0f, 90f * rotation, 0f);

        /// <summary>Footprint cells at rotation 0 (width along frame X, depth along frame Z).</summary>
        public static (int width, int depth) Footprint(PieceBounds bounds, float cellSize) =>
            (CellsFor(bounds.Size.x, cellSize), CellsFor(bounds.Size.z, cellSize));

        private static int CellsFor(float meters, float cellSize) =>
            Math.Max(1, Mathf.CeilToInt((meters - FootprintTolerance) / cellSize));

        /// <summary>Blocking colliders' bounds, for a blocker.</summary>
        public static PieceBounds? MeasureColliders(GameObject root, Quaternion frame, Func<Collider, bool> include)
        {
            var points = new List<Vector3>();
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (!include(collider)) continue;
                points.AddRange(ColliderCorners(collider));
            }
            return BoundsInFrame(points, root.transform.position, frame);
        }

        /// <summary>Active renderers' bounds, for decor.</summary>
        public static PieceBounds? MeasureRenderers(GameObject root, Quaternion frame)
        {
            var points = new List<Vector3>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled) continue;
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                    : renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                if (mesh == null) continue;
                points.AddRange(BoxCorners(renderer.transform.localToWorldMatrix, mesh.bounds.center, mesh.bounds.size));
            }
            return BoundsInFrame(points, root.transform.position, frame);
        }

        /// <summary>World corners of a collider's own shape box (exact for boxes; local bounds for other shapes).</summary>
        public static IEnumerable<Vector3> ColliderCorners(Collider collider)
        {
            var matrix = collider.transform.localToWorldMatrix;
            switch (collider)
            {
                case BoxCollider box:
                    return BoxCorners(matrix, box.center, box.size);
                case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                    return BoxCorners(matrix, meshCollider.sharedMesh.bounds.center, meshCollider.sharedMesh.bounds.size);
                case SphereCollider sphere:
                    return BoxCorners(matrix, sphere.center, Vector3.one * sphere.radius * 2f);
                case CapsuleCollider capsule:
                    var size = Vector3.one * capsule.radius * 2f;
                    size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2f);
                    return BoxCorners(matrix, capsule.center, size);
                default:
                    // Physics bounds are world-axis aligned and need a synced transform.
                    var bounds = collider.bounds;
                    return BoxCorners(Matrix4x4.identity, bounds.center, bounds.size);
            }
        }

        public static IEnumerable<Vector3> BoxCorners(Matrix4x4 matrix, Vector3 center, Vector3 size)
        {
            var half = size * 0.5f;
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                yield return matrix.MultiplyPoint3x4(center + corner);
            }
        }

        private static PieceBounds? BoundsInFrame(List<Vector3> worldPoints, Vector3 pivot, Quaternion frame)
        {
            if (worldPoints.Count == 0) return null;
            var inverse = Quaternion.Inverse(frame);
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            foreach (var point in worldPoints)
            {
                var local = inverse * (point - pivot);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
            return new PieceBounds((min + max) * 0.5f, max - min);
        }

        /// <summary>
        /// The quarter turn of an instance relative to its prefab root, or null when it also
        /// tilts or sits between quarter turns (more than half a degree off).
        /// </summary>
        public static int? QuarterTurns(Quaternion instanceRotation, Quaternion prefabRootRotation)
        {
            var frame = instanceRotation * Quaternion.Inverse(prefabRootRotation);
            var up = frame * Vector3.up;
            if (Vector3.Angle(up, Vector3.up) > 0.5f) return null;
            var yaw = frame.eulerAngles.y;
            var turns = Mathf.RoundToInt(yaw / 90f);
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, turns * 90f)) > 0.5f) return null;
            return ((turns % 4) + 4) % 4;
        }
    }
}
