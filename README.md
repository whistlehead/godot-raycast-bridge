# godot-raycast-bridge

A GDExtension for Godot 4 (.NET) that provides allocation-reduced raycasting, eliminating
the .NET GC pressure caused by `PhysicsDirectSpaceState3D.IntersectRay` at high call rates.

## Why this exists

Calling `IntersectRay` from C# causes the Mono glue layer to wrap the native result in a
`Godot.Collections.Dictionary` — a finalizable managed object — on every call. At high
call rates (e.g. vehicle suspension raycasts: 12+ per wheel at 60 Hz), this generates
thousands of finalizable objects per second and measurable GC pressure.

This extension intercepts the result in C++ before it crosses the managed/unmanaged
boundary, extracts the values into a flat `PackedFloat32Array`, and returns that to C#
instead. The managed Dictionary wrapper is never created. See
`docs/raycast_allocation_research.md` for the full investigation.

## API

Two methods are registered on the `RaycastBridge` singleton:

**`intersect_ray_packed(space, from, to, collision_mask) → PackedFloat32Array`**  
Returns a newly allocated `PackedFloat32Array`. One managed object per call; no finalizer.
Simpler to use; acceptable at lower call rates.

**`intersect_ray_into(out_buffer, space, from, to, collision_mask) → void`**  
Writes into a caller-supplied `PackedFloat32Array`. Zero managed allocations per call when
the buffer is pre-allocated. Use this on the hot path.

**Result buffer layout** (8 floats, both methods):

| Index | Content |
|---|---|
| `[0]` | Hit flag: `1.0` = hit, `0.0` = miss |
| `[1–3]` | Position (x, y, z) — world space, metres |
| `[4–6]` | Normal (x, y, z) — world space, unit vector |
| `[7]` | Collider instance ID (cast to float) |

## Building

### Prerequisites

- [SCons](https://scons.org/) — `pip install scons`
- A C++17-capable compiler:
  - **Windows:** Visual Studio 2019 or later (MSVC), or MinGW-w64
  - **Linux:** GCC or Clang
- The `godot-cpp` bindings, cloned as a subdirectory of this folder (see step 1 below)

### Steps

**1. Clone godot-cpp**

Clone the Godot C++ bindings into this folder. The branch must match your Godot version.

```bash
cd GDExtension/RaycastBridge
git clone https://github.com/godotengine/godot-cpp --branch 4.3 godot-cpp
```

Check your Godot version in the editor (`Help → About Godot`) and use the matching branch:
`4.1`, `4.2`, `4.3`, `4.4`, etc. The branch name is just the minor version — no patch number.

**2. Build**

From inside `GDExtension/RaycastBridge/`:

```bash
# Debug build (default)
scons

# Release build
scons target=template_release
```

SCons will compile `godot-cpp` on the first run (slow — a few minutes). Subsequent builds
are incremental and fast.

The output `.dll` (Windows) or `.so` (Linux) is written directly to your Godot project's
`bin/` directory as configured in `SConstruct`.

**3. Verify**

Open the Godot editor. If the extension loaded correctly, `RaycastBridge` will appear as an
available class in the editor's class reference. If not, check the Godot output panel for
extension load errors.

### Compiler notes — Windows

If you are using MSVC (recommended on Windows):

- Open a **Developer Command Prompt** (or run `vcvars64.bat`) before running SCons, so
  that `cl.exe` is on your PATH.
- Alternatively, install the `mingw` SCons tool and pass `use_mingw=yes`.

If SCons cannot find your compiler it will print `No C++ compiler found` and exit.

### Compiler notes — Linux

GCC and Clang both work. Install via your package manager:

```bash
# Debian/Ubuntu
sudo apt install build-essential scons
```

## Project setup

**1. Add the autoload singleton**

In Godot: `Project → Project Settings → Autoload`  
Add a new entry pointing to a scene or script that instantiates a `RaycastBridge` node.

The simplest approach: create a `RaycastBridge.tscn` containing a single `RaycastBridge`
node (the class registered by this extension), and add that scene as an autoload with the
name `RaycastBridge`. It will then be accessible from C# as:

```csharp
GetNode<GodotObject>("/root/RaycastBridge")
```

**2. Use from C#**

```csharp
// Pre-allocate (once, e.g. in _Ready):
var buffer = new PackedFloat32Array();
buffer.Resize(8);
var raycastBridge = GetNode<GodotObject>("/root/RaycastBridge");

// Per raycast (hot path — zero managed allocations):
raycastBridge.Call("intersect_ray_into", buffer, spaceState, from, to, collisionMask);

bool hit      = buffer[0] > 0.5f;
var  position = new Vector3(buffer[1], buffer[2], buffer[3]);
var  normal   = new Vector3(buffer[4], buffer[5], buffer[6]);
```

Or use the `SuspensionRaycaster` / `IGroundQuery` wrapper from the MoVeSim project, which
handles the buffer lifecycle and unpacking.

## Godot version compatibility

Tested against Godot 4.3. Should work on any 4.x version with a matching `godot-cpp`
branch. The extension uses no version-specific APIs beyond `PhysicsDirectSpaceState3D`.
