#include "raycast_bridge.h"

#include <godot_cpp/classes/physics_ray_query_parameters3d.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/dictionary.hpp>

using namespace godot;

// ---------------------------------------------------------------------------
// Shared implementation
// ---------------------------------------------------------------------------

void RaycastBridgeNative::fill_result(
    float*                     dest,
    PhysicsDirectSpaceState3D* space,
    Vector3                    from,
    Vector3                    to,
    uint32_t                   collision_mask)
{
    // Zero-initialise to "miss" before any early return.
    for (int i = 0; i < 8; ++i) dest[i] = 0.f;

    if (!space) return;

    // PhysicsRayQueryParameters3D is a ref-counted heap object — one allocation
    // per call here. Promoting this to a cached member variable would eliminate
    // it; deferred until profiling confirms it is worth the added state.
    Ref<PhysicsRayQueryParameters3D> params =
        PhysicsRayQueryParameters3D::create(from, to, collision_mask);

    // intersect_ray allocates a DictionaryPrivate on the C++ heap (via memnew) and
    // populates a HashMap<Variant,Variant> with 7 entries. That allocation is on the
    // *unmanaged* C++ heap — it does not touch the .NET GC. The problem with calling
    // this from C# directly is that the Mono glue layer wraps the result in a managed
    // Godot.Collections.Dictionary with a finalizer, creating GC pressure per call.
    // By consuming it here in C++ and never returning it across the boundary, we
    // prevent that managed wrapper from being created. The Dictionary is freed when
    // it goes out of scope at the end of this function.
    Dictionary hit = space->intersect_ray(params);

    if (hit.is_empty()) return;

    dest[0] = 1.f;

    Vector3 pos  = hit["position"];
    dest[1] = pos.x;
    dest[2] = pos.y;
    dest[3] = pos.z;

    Vector3 nrm  = hit["normal"];
    dest[4] = nrm.x;
    dest[5] = nrm.y;
    dest[6] = nrm.z;

    // Instance ID fits in a float for all IDs that Godot generates in practice
    // (Godot ObjectIDs are 64-bit but low-valued; float has 24 bits of mantissa
    // which covers IDs up to ~16 million without loss). If precise IDs are needed
    // for large scenes, split across two floats or pass via RID instead.
    Object* collider = Object::cast_to<Object>(hit["collider"]);
    if (collider) dest[7] = static_cast<float>(collider->get_instance_id());
}

// ---------------------------------------------------------------------------
// Version 1 — one PackedFloat32Array allocated per call as the return value
// ---------------------------------------------------------------------------

PackedFloat32Array RaycastBridgeNative::intersect_ray_packed(
    PhysicsDirectSpaceState3D* space,
    Vector3                    from,
    Vector3                    to,
    uint32_t                   collision_mask)
{
    PackedFloat32Array out;
    out.resize(8);
    fill_result(out.ptrw(), space, from, to, collision_mask);
    return out; // ref-counted; no deep copy on return
}

// ---------------------------------------------------------------------------
// Version 2 — batch: N rays in one call, result returned by value
//
// PackedFloat32Array is ref-counted. Godot does not support mutating a
// caller-supplied array across the GDExtension boundary (ClassDB passes
// Variant arguments by value, so a ref parameter is not visible to C#).
// Returning the output array is the correct pattern — the caller holds the
// only other reference, so no deep copy occurs on return.
// ---------------------------------------------------------------------------

PackedFloat32Array RaycastBridgeNative::intersect_rays_batch(
    PackedFloat32Array         in_buffer,
    PhysicsDirectSpaceState3D* space,
    int                        ray_count,
    uint32_t                   collision_mask)
{
    PackedFloat32Array out;
    out.resize(ray_count * 8);

    // Early out — if space is null every ray is a miss; buffer is already zeroed.
    if (!space) return out;

    const int expected_in = ray_count * 7;
    if (in_buffer.size() != expected_in) return out; // malformed input; return all-miss

    const float* in  = in_buffer.ptr();
    float*       dst = out.ptrw();

    for (int i = 0; i < ray_count; ++i) {
        const float* src = in + i * 7;

        Vector3 origin   (src[0], src[1], src[2]);
        Vector3 direction(src[3], src[4], src[5]);
        float   max_dist  = src[6];

        // fill_result allocates one PhysicsRayQueryParameters3D (C++ heap, not GC)
        // and one DictionaryPrivate per ray — irreducible without engine-level access.
        fill_result(dst + i * 8, space, origin, origin + direction * max_dist, collision_mask);
    }

    return out;
}

// ---------------------------------------------------------------------------
// Binding registration
// ---------------------------------------------------------------------------

void RaycastBridgeNative::_bind_methods()
{
    ClassDB::bind_method(
        D_METHOD("intersect_ray_packed", "space", "from", "to", "collision_mask"),
        &RaycastBridgeNative::intersect_ray_packed);

    ClassDB::bind_method(
        D_METHOD("intersect_rays_batch", "in_buffer", "space", "ray_count", "collision_mask"),
        &RaycastBridgeNative::intersect_rays_batch);
}
