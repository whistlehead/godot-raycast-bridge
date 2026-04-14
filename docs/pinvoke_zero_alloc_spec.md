# Zero-alloc batch dispatch via P/Invoke + Call() split

## Status

Not implemented. Retained as a design reference in case GC pressure from the current
batch API becomes measurable. Based on benchmark data, Mode F (batch 200) produces
~2 gen0 collections per 600 ticks with ~4.6 MB allocated — approximately one gen0
collection every 5 seconds. This is well below the threshold for perceptible frame
pacing issues.

If that changes — larger batches, higher tick rates, tighter platforms — this is the
next step.

---

## The problem this solves

Every call to `GodotObject.Call()` in C# goes through Godot's `Variant` dispatch system.
Arguments are boxed into `Variant` objects, the method is looked up by `StringName`, and
the return value is unboxed back into a managed type. When the return value is a
`PackedFloat32Array`, the interop layer copies all the floats into a new managed `float[]`.

That copy is the allocation. At batch size 200 it is `200 × 9 × 4 = 7,200 bytes` per tick,
which accounts for roughly 93% of the total bytes allocated in Mode F. There is no way to
eliminate it within the `Call()` dispatch model — `Call()` always returns a new value.

---

## Why P/Invoke is a separate mechanism

This is important. **P/Invoke and `GodotObject.Call()` are completely independent.**

- `Call()` goes through Godot's `ClassDB` reflection system. It boxes arguments into
  `Variant`, dispatches to a registered method, and wraps the return value.
- P/Invoke calls a native export directly via the OS dynamic linker (`LoadLibrary` /
  `dlopen`). It has no knowledge of Godot, `Variant`, or `ClassDB`. Arguments map directly
  to C types via the platform ABI. There is no boxing, no return value copy.

When Godot loads `RaycastBridge.dll` as a GDExtension, the OS maps it into the process.
When the .NET runtime later resolves a `[DllImport("RaycastBridge")]` declaration, the OS
loader finds the DLL **already in the loaded module list** and returns the same handle —
no second copy is loaded. This means P/Invoke functions and `ClassDB`-registered methods
share the same static memory, including any `static` or `thread_local` variables.

This shared memory is the side channel that makes the split mechanism work.

---

## The mechanism

The approach splits one logical operation across two calls:

### Call 1 — P/Invoke: register buffer pointers

```csharp
[DllImport("RaycastBridge")]
private static extern unsafe void RaycastSetBuffers(
    float* inBuf,
    float* outBuf,
    int    rayCount,
    uint   collisionMask);
```

This writes the four values into `thread_local` statics on the C++ side. No Godot types
are involved. No allocation occurs. The `fixed` statement that pins the managed arrays
must remain open across both calls.

### Call 2 — `Call()`: pass the space state and execute

```csharp
Native.Call(_methodIntersectRaysDirect, space);
```

`intersect_rays_direct` is a void `ClassDB`-registered method that takes only the
`PhysicsDirectSpaceState3D*`. It reads the buffer pointers and ray count from the
`thread_local` statics, performs all raycasts, and writes results directly into `outBuf`.
It returns `void` — no `PackedFloat32Array` is constructed, no return value crosses the
boundary.

### C# caller — complete pattern

```csharp
private GCHandle _inHandle, _outHandle;
private unsafe float* _inPtr, _outPtr;
private float[] _inBuffer, _outBuffer;

public override void _Ready()
{
    _inBuffer  = new float[RayCount * 7];
    _outBuffer = new float[RayCount * 9];

    // Pin for the lifetime of the node. Free in _ExitTree.
    _inHandle  = GCHandle.Alloc(_inBuffer,  GCHandleType.Pinned);
    _outHandle = GCHandle.Alloc(_outBuffer, GCHandleType.Pinned);
    _inPtr  = (float*)_inHandle.AddrOfPinnedObject();
    _outPtr = (float*)_outHandle.AddrOfPinnedObject();
}

public override void _ExitTree()
{
    _inHandle.Free();
    _outHandle.Free();
}

private unsafe void DispatchRays(PhysicsDirectSpaceState3D space)
{
    // Pack ray definitions into _inBuffer (no allocation).
    for (int i = 0; i < RayCount; i++)
        RaycastBridge.PackRay(_inBuffer, i, origins[i], direction, maxDist);

    // fixed is a no-op when the array is already pinned, but required by the compiler.
    fixed (float* unused = _inBuffer)
    {
        RaycastBridge.SetBuffers(_inPtr, _outPtr, RayCount, collisionMask); // P/Invoke
        Native.Call(_methodIntersectRaysDirect, space);                     // Call()
    }

    // Read from _outBuffer — zero allocations from this point.
    for (int i = 0; i < RayCount; i++)
    {
        if (RaycastBridge.GetHit(_outBuffer, i)) { ... }
    }
}
```

---

## C++ side

### thread_local statics

```cpp
// raycast_bridge.cpp
thread_local static float*   t_in_buf        = nullptr;
thread_local static float*   t_out_buf       = nullptr;
thread_local static int      t_ray_count     = 0;
thread_local static uint32_t t_collision_mask = 0;
```

`thread_local` is correct here. Godot's physics callbacks run on the physics thread.
If this is always called from `_PhysicsProcess`, a plain `static` would also work in
practice, but `thread_local` is safer if the API is ever used from worker threads.

### Exported registration function

```cpp
extern "C" __declspec(dllexport)  // Windows; use __attribute__((visibility("default"))) on macOS/Linux
void raycast_set_buffers(float* in_buf, float* out_buf, int ray_count, uint32_t collision_mask)
{
    t_in_buf         = in_buf;
    t_out_buf        = out_buf;
    t_ray_count      = ray_count;
    t_collision_mask = collision_mask;
}
```

This must use `extern "C"` to prevent C++ name mangling, which would break the
`[DllImport]` name lookup.

### ClassDB-registered dispatch method

```cpp
void RaycastBridgeNative::intersect_rays_direct(PhysicsDirectSpaceState3D* space)
{
    if (!space || !t_in_buf || !t_out_buf || t_ray_count <= 0) return;

    const float* in  = t_in_buf;
    float*       dst = t_out_buf;

    for (int i = 0; i < t_ray_count; ++i)
    {
        const float* src = in + i * 7;
        Vector3 origin   (src[0], src[1], src[2]);
        Vector3 direction(src[3], src[4], src[5]);
        float   max_dist  = src[6];
        fill_result(dst + i * 9, space, origin, origin + direction * max_dist, t_collision_mask);
    }
}
```

Register in `_bind_methods`:

```cpp
ClassDB::bind_method(
    D_METHOD("intersect_rays_direct", "space"),
    &RaycastBridgeNative::intersect_rays_direct);
```

### Platform export annotation

The `extern "C"` export needs a platform guard:

```cpp
#if defined(_WIN32) || defined(_WIN64)
  #define RAYCAST_EXPORT extern "C" __declspec(dllexport)
#else
  #define RAYCAST_EXPORT extern "C" __attribute__((visibility("default")))
#endif
```

---

## What this eliminates

| Cost | Current (Call() batch) | With P/Invoke split |
|---|---|---|
| Output `float[]` allocation | 1 per `Call()` | 0 — written directly into pinned buffer |
| `PackedFloat32Array` construction | 1 per `Call()` | 0 — return is `void` |
| Argument Variant boxing | ~4 Variants per `Call()` | 1 Variant (`space` only, in `Call()`) |
| Input buffer copy (float[]→PackedFloat32Array) | 1 per `Call()` | 0 — pointer passed directly |

The `space` argument still crosses via `Call()` as a `Variant`. That is unavoidable: the
`PhysicsDirectSpaceState3D*` is a Godot engine object whose pointer is only meaningful
inside the Godot runtime, and there is no safe way to obtain or pass it via P/Invoke
without engine cooperation.

---

## What this does NOT eliminate

- The `fill_result` inner loop: one `PhysicsRayQueryParameters3D` mutation and one
  `Dictionary` allocation per ray on the C++ heap. These are irreducible without
  engine-level access. They do not affect the .NET GC.
- The single remaining `Variant` boxing for the `space` argument on each `Call()`.
- The `Call()` method dispatch overhead (StringName lookup, virtual dispatch).

---

## Cautions

**Do not attempt to pass `PhysicsDirectSpaceState3D` via P/Invoke.** It is a Godot engine
object. Its pointer is only valid within the engine's object system. Passing it as an
`IntPtr` to native code and casting it back to `PhysicsDirectSpaceState3D*` would be
undefined behaviour — the pointer may be tagged, the object may be relocated, and there
is no stable ABI guarantee. Always pass Godot objects through `Call()`.

**Do not store the pinned pointers in ordinary C++ member variables** (`_query_params`
style). The GC pins the arrays but can still move other objects. Only `thread_local` or
`static` storage in the C++ DLL is safe for cross-call state of this kind.

**The `fixed` block must span both calls.** If the `fixed` block closes between the
P/Invoke and the `Call()`, the GC is free to move the arrays before `intersect_rays_direct`
runs. In practice with `GCHandleType.Pinned` this cannot happen, but the `fixed` statement
communicates intent and satisfies the compiler's unsafe pointer rules.
