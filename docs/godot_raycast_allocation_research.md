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
the Godot wrapper layer sitting above Jolt, not by Jolt itself.

This means there is no smarter C++ approach available from a GDExtension — the
`DictionaryPrivate` per call is irreducible without forking the engine. It also means the
C++ heap cost is not the dominant remaining cost: benchmarking (see README) shows the
output `float[]` copy across the `Call()` boundary accounts for ~93% of remaining
allocations after the managed Dictionary is eliminated. That is a .NET/Variant boundary
problem, addressed in `pinvoke_zero_alloc_spec.md`.
