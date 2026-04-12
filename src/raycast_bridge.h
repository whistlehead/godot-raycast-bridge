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
/// GetNode<GodotObject>("/root/RaycastBridge").
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
class RaycastBridge : public Object {
    GDCLASS(RaycastBridge, Object)

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

    /// Cast a ray and write results directly into out_buffer.
    /// out_buffer must be pre-allocated to exactly 8 elements by the caller;
    /// it is resized defensively if not, but that resize would itself allocate.
    /// No new managed objects are created on the C# side per call when used correctly.
    void intersect_ray_into(
        PackedFloat32Array&        out_buffer,
        PhysicsDirectSpaceState3D* space,
        Vector3                    from,
        Vector3                    to,
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
