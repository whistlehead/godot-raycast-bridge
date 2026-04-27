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
    private static readonly StringName _methodIntersectRaysBatch = "intersect_rays_batch";

    // -------------------------------------------------------------------------
    // Batch rays
    // -------------------------------------------------------------------------

    /// <summary>
    /// Casts rayCount rays in a single GDExtension call.
    /// Returns a float[] of rayCount * 9 floats. One managed allocation per call
    /// regardless of ray count.
    ///
    /// <para>
    /// When to use this instead of <c>PhysicsDirectSpaceState3D.IntersectRay</c>:
    /// each GDExtension dispatch has fixed overhead, so small batches can be worse than
    /// optimised native. The break-even batch size depends on how many result fields you
    /// read — native dictionary reads allocate per field, bridge struct reads do not.
    /// "Worth it" matrix (ballpark — see README for full data):
    ///   3 fields per ray (position + normal + ID): batch 2+
    ///   2 fields per ray (eg position + normal):   batch 2+
    ///   1 field  per ray (eg position only):       batch 20+
    ///   0 fields per ray (no reads):               batch 20+
    /// The primary benefit is p99 frame time (microstutter), not mean time. Native
    /// dictionary reads cause gen1 collection spikes at 2+ fields; the bridge avoids
    /// these entirely at batch 10+, and reduces them at batch 2+.
    /// See https://github.com/whistlehead/godot-raycast-bridge/blob/main/README.md for
    /// full benchmark data.
    /// </para>
    ///
    /// inBuffer must be pre-allocated to rayCount * 7 floats by the caller.
    /// Use PackRay to fill it before calling this method.
    /// </summary>
    public static float[] IntersectRaysBatch(
        float[] inBuffer,
        PhysicsDirectSpaceState3D space,
        int rayCount,
        uint collisionMask)
    {
        return (float[])Native.Call(
            _methodIntersectRaysBatch, inBuffer, space, rayCount, collisionMask);
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
        float[] inBuffer,
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
    public static bool GetHit(float[] results, int index)
        => results[index * 9] > 0.5f;

    /// <summary>
    /// Returns the hit position for the ray at the given index.
    /// Check GetHit first — position is zero if there was no hit.
    /// </summary>
    public static Vector3 GetPosition(float[] results, int index)
    {
        int o = index * 9;
        return new Vector3(results[o + 1], results[o + 2], results[o + 3]);
    }

    /// <summary>
    /// Returns the hit normal for the ray at the given index.
    /// Check GetHit first — normal is zero if there was no hit.
    /// </summary>
    public static Vector3 GetNormal(float[] results, int index)
    {
        int o = index * 9;
        return new Vector3(results[o + 4], results[o + 5], results[o + 6]);
    }

    /// <summary>
    /// Returns the full 64-bit collider instance ID for the ray at the given index, or 0 if no hit.
    /// The ID is stored as two raw-bit-reinterpreted float32 slots — no precision loss.
    /// </summary>
    public static ulong GetColliderId(float[] results, int index)
    {
        int o = index * 9 + 7;
        uint lo = BitConverter.SingleToUInt32Bits(results[o]);
        uint hi = BitConverter.SingleToUInt32Bits(results[o + 1]);
        return ((ulong)hi << 32) | lo;
    }
}
