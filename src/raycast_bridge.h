#pragma once

#include <godot_cpp/classes/object.hpp>
#include <godot_cpp/classes/physics_direct_space_state3d.hpp>
#include <godot_cpp/variant/packed_float32_array.hpp>
#include <godot_cpp/variant/vector3.hpp>

namespace godot {

/// Exposes allocation-reduced raycasting to C# via two methods:
///
///   intersect_ray_packed  — returns a new PackedFloat32Array per call (one allocation).
///   intersect_ray_into    — writes into a caller-supplied PackedFloat32Array (zero C#
///                           allocations per call; the buffer is owned and reused by the caller).
///
/// Result buffer layout (both methods, 8 floats):
///   [0]   hit flag  (1.0 = hit, 0.0 = miss)
///   [1-3] position  (x, y, z) — world space, metres
///   [4-6] normal    (x, y, z) — world space, unit vector
///   [7]   collider instance ID cast to float (precision sufficient for ID ranges in practice)
///
/// Register as an autoload singleton in project.godot so C# can reach it via
/// GetNode<GodotObject>("/root/RaycastBridgeNative").
///
/// ## Why this wrapper eliminates the .NET GC problem
///
/// Godot's intersect_ray (physics_server_3d.cpp) creates a Dictionary per call:
///   Dictionary d;           // allocates DictionaryPrivate on the C++ heap via memnew()
///   d["position"] = ...;    // populates a HashMap<Variant, Variant>
///   return d;
///
/// That DictionaryPrivate lives on the *C++ heap* (unmanaged), not the .NET managed heap.
/// The problem for C# callers is that the Mono glue layer wraps it in a managed
/// Godot.Collections.Dictionary object (Dictionary.cs in the Godot C# bindings), which:
///   - lives on the .NET managed heap
///   - has a finalizer to release the native side
///   - generates a finalizable object per raycast → GC pressure at high call rates
///
/// By consuming the Dictionary here in C++ and extracting its values into a flat float
/// array, we prevent that managed wrapper from ever being created. The C++ Dictionary
/// is freed immediately when it goes out of scope at the end of fill_result().
/// The PackedFloat32Array that crosses the boundary carries no finalizer overhead
/// and, in intersect_ray_into mode, is reused rather than reallocated each call.
class RaycastBridgeNative : public Object {
    GDCLASS(RaycastBridgeNative, Object)

protected:
    static void _bind_methods();

public:
    /// Cast a ray and return results as a newly allocated PackedFloat32Array.
    /// One PackedFloat32Array is allocated per call as the return value.
    /// Prefer intersect_ray_into on the hot path.
    PackedFloat32Array intersect_ray_packed(
        PhysicsDirectSpaceState3D* space,
        Vector3                    from,
        Vector3                    to,
        uint32_t                   collision_mask);

    /// Cast N rays in a single call. Returns a PackedFloat32Array owned by the
    /// caller; pass the same array back each tick and Godot's ref-counting avoids
    /// a deep copy on return.
    ///
    /// in_buffer layout  (7 floats per ray, stride 7):
    ///   [i*7 + 0..2]  origin     (x, y, z) — world space
    ///   [i*7 + 3..5]  direction  (x, y, z) — world space, does not need to be normalised
    ///                             ray endpoint = origin + direction * max_dist
    ///   [i*7 + 6]     max_dist   scalar — length of the ray
    ///
    /// Return buffer layout (8 floats per ray, stride 8):
    ///   [i*8 + 0]     hit flag   (1.0 = hit, 0.0 = miss)
    ///   [i*8 + 1..3]  position   (x, y, z) — world space
    ///   [i*8 + 4..6]  normal     (x, y, z) — world space, unit vector
    ///   [i*8 + 7]     collider instance ID cast to float
    ///
    /// collision_mask applies uniformly to all rays in the batch.
    PackedFloat32Array intersect_rays_batch(
        PackedFloat32Array         in_buffer,
        PhysicsDirectSpaceState3D* space,
        int                        ray_count,
        uint32_t                   collision_mask);

private:
    /// Shared implementation. Writes 8 floats into dest[0..7].
    /// dest must point to at least 8 writable floats.
    static void fill_result(
        float*                     dest,
        PhysicsDirectSpaceState3D* space,
        Vector3                    from,
        Vector3                    to,
        uint32_t                   collision_mask);
};

} // namespace godot
