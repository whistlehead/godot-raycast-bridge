using Godot;

/// <summary>
/// Typed C# wrapper around the RaycastBridge GDExtension.
///
/// Register this script as an autoload singleton in Project Settings → Autoload
/// (name it "RaycastBridge"). Access it from any node with:
///   var bridge = GetNode&lt;RaycastBridge&gt;("/root/RaycastBridge");
///
/// The caller is responsible for allocating and reusing the input and output
/// buffers. This wrapper provides typed dispatch and result reading only.
///
/// Buffer layouts — see README.md for full documentation.
/// </summary>
public partial class RaycastBridge : GodotObject
{
    private GodotObject _native;

    public override void _Ready()
    {
        // The native GDExtension class shares the same name. Retrieve it by
        // finding the node registered under this autoload path, which Godot
        // will have instantiated from the extension.
        _native = (GodotObject)ClassDB.Instantiate("RaycastBridgeNative");
    }

    // -------------------------------------------------------------------------
    // Single ray
    // -------------------------------------------------------------------------

    /// <summary>
    /// Casts a single ray. Returns a PackedFloat32Array of 8 floats.
    /// One managed allocation per call. Use for low-frequency raycasts.
    /// </summary>
    public PackedFloat32Array IntersectRay(
        PhysicsDirectSpaceState3D space,
        Vector3 from,
        Vector3 to,
        uint collisionMask)
    {
        return (PackedFloat32Array)_native.Call(
            "intersect_ray_packed", space, from, to, collisionMask);
    }

    // -------------------------------------------------------------------------
    // Batch rays
    // -------------------------------------------------------------------------

    /// <summary>
    /// Casts rayCount rays in a single call.
    /// Returns a PackedFloat32Array of rayCount * 8 floats.
    ///
    /// inBuffer must be pre-allocated to rayCount * 7 floats by the caller.
    /// Use PackRay to fill it before calling this method.
    /// One managed allocation per call regardless of ray count.
    /// </summary>
    public PackedFloat32Array IntersectRaysBatch(
        PackedFloat32Array inBuffer,
        PhysicsDirectSpaceState3D space,
        int rayCount,
        uint collisionMask)
    {
        return (PackedFloat32Array)_native.Call(
            "intersect_rays_batch", inBuffer, space, rayCount, collisionMask);
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
        => results[index * 8] > 0.5f;

    /// <summary>
    /// Returns the hit position for the ray at the given index.
    /// Check GetHit first — position is zero if there was no hit.
    /// </summary>
    public static Vector3 GetPosition(PackedFloat32Array results, int index)
    {
        int o = index * 8;
        return new Vector3(results[o + 1], results[o + 2], results[o + 3]);
    }

    /// <summary>
    /// Returns the hit normal for the ray at the given index.
    /// Check GetHit first — normal is zero if there was no hit.
    /// </summary>
    public static Vector3 GetNormal(PackedFloat32Array results, int index)
    {
        int o = index * 8;
        return new Vector3(results[o + 4], results[o + 5], results[o + 6]);
    }

    /// <summary>
    /// Returns the collider instance ID for the ray at the given index, or 0 if no hit.
    /// </summary>
    public static ulong GetColliderId(PackedFloat32Array results, int index)
        => (ulong)results[index * 8 + 7];
}
