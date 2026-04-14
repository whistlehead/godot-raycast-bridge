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
    /// Performance note: each GDExtension dispatch carries fixed overhead from Variant
    /// boxing of arguments and the return-value copy across the C#/C++ boundary.
    /// Benchmarking at 200 rays/tick (60 Hz, AMD integrated GPU) shows:
    /// <list type="table">
    ///   <item><term>Batch size 1  </term><description>~584 bytes/ray — 6× worse than optimised native</description></item>
    ///   <item><term>Batch size 5  </term><description>~146 bytes/ray — ~52% worse than optimised native</description></item>
    ///   <item><term>Batch size 10 </term><description>~90 bytes/ray  — roughly equal to optimised native (~96)</description></item>
    ///   <item><term>Batch size 20 </term><description>~63 bytes/ray  — ~34% better than optimised native</description></item>
    ///   <item><term>Batch size 200</term><description>~39 bytes/ray  — ~59% better than optimised native</description></item>
    /// </list>
    /// Break-even against optimised native (cached <c>PhysicsRayQueryParameters3D</c>,
    /// mutated per ray) is around batch size 15–20. Below that, prefer Godot's built-in
    /// <c>PhysicsDirectSpaceState3D.IntersectRay</c> directly.
    /// </para>
    ///
    /// <para>
    /// Note: even at batch size 1 the bridge avoids gen2 collections entirely
    /// (the returned float[] is short-lived and collected at gen0/1). Whether that
    /// matters depends on how expensive your gen2 collections are relative to the
    /// increased gen0/1 pressure.
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
