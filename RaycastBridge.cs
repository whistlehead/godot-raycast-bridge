using System;
using Godot;

namespace PhysicsQueryBridge;

/// <summary>
/// Typed C# wrapper around the RaycastBridgeNative GDExtension.
///
/// No autoload or scene tree setup required. The native instance is created
/// on the first call and reused for the lifetime of the application.
///
/// Buffer layouts — see README.md for full documentation.
/// </summary>
public static class RaycastBridge
{
    private static GodotObject _native;
    private static GodotObject Native =>
        _native ??= ClassDB.Instantiate("RaycastBridgeNative").AsGodotObject();

    // Cached to avoid a StringName allocation on every Call() invocation.
    private static readonly StringName MethodIntersectRayPacked  = "intersect_ray_packed";
    private static readonly StringName MethodIntersectRaysBatch  = "intersect_rays_batch";

    // -------------------------------------------------------------------------
    // Single ray
    // -------------------------------------------------------------------------

    /// <summary>
    /// Casts a single ray. Returns a PackedFloat32Array of 9 floats.
    /// One managed allocation per call. Use for low-frequency raycasts.
    /// </summary>
    public static PackedFloat32Array IntersectRay(
        PhysicsDirectSpaceState3D space,
        Vector3 from,
        Vector3 to,
        uint collisionMask)
    {
        return (PackedFloat32Array)Native.Call(
            MethodIntersectRayPacked, space, from, to, collisionMask);
    }

    // -------------------------------------------------------------------------
    // Batch rays
    // -------------------------------------------------------------------------

    /// <summary>
    /// Casts rayCount rays in a single call.
    /// Returns a PackedFloat32Array of rayCount * 9 floats.
    ///
    /// inBuffer must be pre-allocated to rayCount * 7 floats by the caller.
    /// Use PackRay to fill it before calling this method.
    /// One managed allocation per call regardless of ray count.
    /// </summary>
    public static PackedFloat32Array IntersectRaysBatch(
        PackedFloat32Array inBuffer,
        PhysicsDirectSpaceState3D space,
        int rayCount,
        uint collisionMask)
    {
        return (PackedFloat32Array)Native.Call(
            MethodIntersectRaysBatch, inBuffer, space, rayCount, collisionMask);
    }

    // -------------------------------------------------------------------------
    // Buffer helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes one ray into the batch input buffer at the given index.
    /// inBuffer must be pre-allocated to at least (index + 1) * 7 floats.
    /// direction need not be normalised; the ray endpoint is origin + direction * maxDist.
    /// </summary>
    public static void PackRay(
        PackedFloat32Array inBuffer,
        int index,
        Vector3 origin,
        Vector3 direction,
        float maxDist)
    {
        int o = index * 7;
        inBuffer[o + 0] = origin.X;    inBuffer[o + 1] = origin.Y;    inBuffer[o + 2] = origin.Z;
        inBuffer[o + 3] = direction.X; inBuffer[o + 4] = direction.Y; inBuffer[o + 5] = direction.Z;
        inBuffer[o + 6] = maxDist;
    }

    /// <summary>
    /// Returns true if the ray at the given index hit something.
    /// </summary>
    public static bool GetHit(PackedFloat32Array results, int index)
        => results[index * 9] > 0.5f;

    /// <summary>
    /// Returns the hit position for the ray at the given index.
    /// Check GetHit first — position is zero if there was no hit.
    /// </summary>
    public static Vector3 GetPosition(PackedFloat32Array results, int index)
    {
        int o = index * 9;
        return new Vector3(results[o + 1], results[o + 2], results[o + 3]);
    }

    /// <summary>
    /// Returns the hit normal for the ray at the given index.
    /// Check GetHit first — normal is zero if there was no hit.
    /// </summary>
    public static Vector3 GetNormal(PackedFloat32Array results, int index)
    {
        int o = index * 9;
        return new Vector3(results[o + 4], results[o + 5], results[o + 6]);
    }

    /// <summary>
    /// Returns the full 64-bit collider instance ID for the ray at the given index, or 0 if no hit.
    /// The ID is stored as two raw-bit-reinterpreted float32 slots — no precision loss.
    /// </summary>
    public static ulong GetColliderId(PackedFloat32Array results, int index)
    {
        int o = index * 9 + 7;
        uint lo = BitConverter.SingleToUInt32Bits(results[o]);
        uint hi = BitConverter.SingleToUInt32Bits(results[o + 1]);
        return ((ulong)hi << 32) | lo;
    }
}
