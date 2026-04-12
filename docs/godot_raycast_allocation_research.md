# Godot Raycast Allocation Research

**Date:** 2026-04-12  
**Context:** MoVeSim suspension raycasting hot path — 12+ raycasts per wheel, at 60 Hz physics.  
**Question:** Does `PhysicsDirectSpaceState3D.IntersectRay` cause .NET GC pressure? Can it be avoided?

---

## Finding: The managed wrapper is the problem, not the engine Dictionary

Tracing through the Godot 4 source reveals a two-layer allocation story.

### Layer 1 — The C++ Dictionary (unmanaged heap, harmless to .NET GC)

`PhysicsDirectSpaceState3D::intersect_ray` is implemented in `servers/physics_server_3d.cpp`.
After the Jolt backend completes its raycast (returning a flat `RayResult` struct with no
allocation), the Godot wrapper creates a `Dictionary` to hand the result back:

```cpp
// physics_server_3d.cpp
Dictionary d;              // allocates DictionaryPrivate via memnew() — C++ heap
d["position"] = result.position;
d["normal"]   = result.normal;
// ... 5 more entries
return d;
```

`Dictionary` is backed by `DictionaryPrivate`, which holds a `HashMap<Variant, Variant>`.
Both are allocated via `memnew()` — Godot's custom allocator wrapping `malloc`. This memory
lives on the **unmanaged C++ heap** and is managed by Godot's own reference counting. The
.NET garbage collector is entirely unaware of it.

**Source files:**
- `servers/physics_server_3d.cpp` — wrapper creating the Dictionary
- `core/variant/dictionary.cpp` — `Dictionary()` constructor calling `memnew(DictionaryPrivate)`
- `core/variant/dictionary.h` — `DictionaryPrivate` containing the `HashMap`

### Layer 2 — The C# wrapper (managed heap, causes GC pressure)

When `intersect_ray` is called from C#, the Mono glue layer in
`modules/mono/glue/GodotSharp/GodotSharp/Core/Dictionary.cs` wraps the returned native
`Dictionary*` in a managed `Godot.Collections.Dictionary` object. This object:

- Lives on the **.NET managed heap**
- Has a **finalizer** to release the native side when collected
- Is created **fresh every call** — no pooling

A finalizable managed object per raycast call is the source of the GC pressure. At 60 Hz
with 12+ raycasts per wheel, this means thousands of finalizable objects queued per second.

### The Jolt backend's actual floor

The Jolt backend (`modules/jolt_physics/spaces/jolt_physics_direct_space_state_3d.cpp`)
fills a native `RayResult` struct with no allocation. The Dictionary is created solely by
the Godot wrapper layer sitting above Jolt, not by Jolt itself. The minimum possible
allocation for any GDExtension-accessible raycast is therefore: one `DictionaryPrivate`
on the C++ heap per call.

---

## Solution: RaycastBridge GDExtension

The RaycastBridge GDExtension (`godot-raycast-bridge`, separate repository) intercepts the
result in C++ before it crosses the managed/unmanaged boundary:

```cpp
Dictionary hit = space->intersect_ray(params);  // C++ Dictionary, unmanaged heap
// Extract values into a flat float array — Dictionary freed when it goes out of scope.
// The managed Godot.Collections.Dictionary wrapper is never created.
```

The result is returned to C# as a `PackedFloat32Array`, which:
- Carries no finalizer
- In `intersect_ray_into` mode, is pre-allocated once and reused — eliminating the
  per-call managed allocation entirely

### Allocation accounting per raycast call

| Allocation | Calling from C# directly | Via GDExtension `intersect_ray_into` |
|---|---|---|
| Jolt `RayResult` struct | Stack — none | Stack — none |
| `DictionaryPrivate` + `HashMap` (C++ heap) | Yes | Yes — unavoidable |
| `PhysicsRayQueryParameters3D` (C++ heap) | Yes | Yes |
| `Godot.Collections.Dictionary` managed wrapper | **Yes — finalizable** | **No** |
| Per-key `Variant` boxing in C# | **Yes — per key** | **No** |
| `PackedFloat32Array` managed wrapper | No | No — reused |
| `float[]` scratch array | No | No — reused |

The two bold rows are the source of GC pressure. Both are eliminated by the GDExtension.

---

## What would eliminate the remaining C++ allocations

The `DictionaryPrivate` allocation cannot be removed from GDExtension. To go further:

- **Fork Godot** and add a struct-return overload to `PhysicsDirectSpaceState3D` that
  bypasses the Dictionary entirely.
- **Call Jolt directly** from a GDExtension, managing your own physics world — enormous
  undertaking, incompatible with using Godot's scene physics.

Neither is warranted here. The C++ heap allocations do not cause .NET GC pressure.

---

## API

### `IGroundQuery` — `Godot/Vehicle/IGroundQuery.cs`

Approved hot-path interface for ground surface queries. Implementations must be
allocation-audited before use inside the substep loop (see `conventions.md §4.1`).

```csharp
public interface IGroundQuery
{
    // Result type — stack-allocated, no reference fields.
    public readonly struct GroundHit
    {
        public readonly bool    DidHit;
        public readonly Vector3 Position;   // world space, metres
        public readonly Vector3 Normal;     // world space, unit vector
        public readonly ulong   ColliderId; // Godot instance ID; 0 if no hit

        public static GroundHit Miss => default; // no-hit sentinel
    }

    /// Cast a ray from `from` to `to`. Returns a GroundHit; check DidHit before
    /// reading Position or Normal.
    GroundHit Query(
        PhysicsDirectSpaceState3D space,
        Vector3 from,
        Vector3 to,
        uint collisionMask = 0xFFFF_FFFF);
}
```

**Important:** obtain `PhysicsDirectSpaceState3D` from `PhysicsDirectBodyState3D` at tick
start and pass it through to substep components. Do not call `GetDirectSpaceState()` inside
the substep loop.

---

### `SuspensionRaycaster` — `Godot/Vehicle/SuspensionRaycaster.cs`

Implements `IGroundQuery` via the RaycastBridge GDExtension.

**Construction:**

```csharp
// Recommended for the substep hot path:
var raycaster = new SuspensionRaycaster(
    GetNode<GodotObject>("/root/RaycastBridge"),
    SuspensionRaycaster.EMode.InPlaceBuffer);

// Simpler, one allocation per call, acceptable at lower call rates:
var raycaster = new SuspensionRaycaster(
    GetNode<GodotObject>("/root/RaycastBridge"),
    SuspensionRaycaster.EMode.ReturnBuffer);
```

**Usage:**

```csharp
IGroundQuery.GroundHit hit = raycaster.Query(
    spaceState,
    attachmentPointWorld,
    attachmentPointWorld + Vector3.Down * (rideHeight + margin),
    collisionMask: 1u);

if (hit.DidHit)
{
    float groundY = hit.Position.Y;
    // ... suspension compression calculation
}
```

**Modes:**

| Mode | Per-call managed allocations | Notes |
|---|---|---|
| `EMode.InPlaceBuffer` | Zero | Buffer pre-allocated at construction, reused each call |
| `EMode.ReturnBuffer` | One `PackedFloat32Array` | Simpler; no finalizer overhead |

---

### GDExtension — `intersect_ray_packed` / `intersect_ray_into`

Both methods are on the `RaycastBridge` singleton node (`/root/RaycastBridge`).
GDScript and C# callable. Direct use from C# is via `SuspensionRaycaster`; only call
these methods directly if implementing a different `IGroundQuery`.

**Result buffer layout** (8 floats, both methods):

| Index | Content |
|---|---|
| `[0]` | Hit flag: `1.0` = hit, `0.0` = miss |
| `[1]` | Position X (world space, metres) |
| `[2]` | Position Y |
| `[3]` | Position Z |
| `[4]` | Normal X (world space, unit vector) |
| `[5]` | Normal Y |
| `[6]` | Normal Z |
| `[7]` | Collider instance ID (cast to float; valid for IDs up to ~16 million) |

**`intersect_ray_packed(space, from, to, collision_mask) → PackedFloat32Array`**  
Returns a freshly allocated array. One managed object allocated per call.

**`intersect_ray_into(out_buffer, space, from, to, collision_mask) → void`**  
Writes into `out_buffer`. Caller must pre-allocate to exactly 8 elements. Zero managed
allocations on the C# side when used correctly.
