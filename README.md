# godot-raycast-bridge

## Disclaimer

This extension has been coded (or hallucinated, yet to be fully verified) by Claude Code
(Sonnet 4.6) at my direction. I simply guided it to look into the Godot source code to see
if there was any way around the horrendously inefficient method Godot uses to return the
results of its raycasts. To be clear - the Godot implementation is easy to use and works
great if you have a more typical number of raycasts per frame, a few dozen is hardly going
to tickle the garbage collector. In my case though, I'm shooting 15 rays per wheel, 10 times
per tick, 60 ticks per second, on a minimum of four wheels.

From beyond this point, you are in Magical Claude-land. Good luck.

## What this is and why it exists

A GDExtension for Godot 4 (.NET) that reduces .NET GC pressure when calling
`PhysicsDirectSpaceState3D.IntersectRay` at high rates.

Calling `IntersectRay` from C# causes the Mono glue layer to wrap the native result in a
`Godot.Collections.Dictionary` — a finalizable managed object — on every call. At high
call rates this generates thousands of finalizable objects per second and measurable GC
pressure.

This extension intercepts the result in C++ before it crosses the managed/unmanaged
boundary, extracts the values into a flat `PackedFloat32Array`, and returns that to C#
instead. The managed Dictionary wrapper is never created. See
`docs/godot_raycast_allocation_research.md` for the full investigation.

## API

Two methods are registered on the `RaycastBridge` singleton:

**`intersect_ray_packed(space, from, to, collision_mask) → PackedFloat32Array`**  
Casts a single ray. Returns a `PackedFloat32Array` of 9 floats. One managed object per
call; no finalizer. Use for low-frequency raycasts.

**`intersect_rays_batch(in_buffer, space, ray_count, collision_mask) → PackedFloat32Array`**  
Casts N rays in a single call. Returns a `PackedFloat32Array` of `ray_count × 9` floats.
Compared to calling `intersect_ray_packed` N times: produces one managed allocation instead
of N, and one GDExtension dispatch instead of N. The per-ray C++ heap work
(`PhysicsRayQueryParameters3D` + `DictionaryPrivate`) still occurs N times — that cost is
irreducible without engine-level access and does not affect the .NET GC.

### Result buffer layout (9 floats per ray)

| Index | Content |
|---|---|
| `[0]` | Hit flag: `1.0` = hit, `0.0` = miss |
| `[1–3]` | Position (x, y, z) — world space, metres |
| `[4–6]` | Normal (x, y, z) — world space, unit vector |
| `[7]` | Collider instance ID — low 32 bits, raw bit reinterpret (no precision loss) |
| `[8]` | Collider instance ID — high 32 bits, raw bit reinterpret (no precision loss) |

For the batch method the output is `ray_count` of these records laid end to end (stride 9).
Use `RaycastBridge.GetColliderId(results, i)` to reconstruct the full 64-bit ID.

### Batch input buffer layout (`ray_count × 7` floats, stride 7 per ray)

| Offset | Content |
|---|---|
| `+0..+2` | Origin (x, y, z) — world space |
| `+3..+5` | Direction (x, y, z) — world space, need not be normalised |
| `+6` | Max distance — ray endpoint = origin + direction × max_dist |

`collision_mask` applies uniformly to all rays in the batch. If `in_buffer.Size()` does
not equal `ray_count × 7`, all results are returned as miss.

---

## Project setup

**1. Get the binaries**

Pre-built binaries for Windows (x86-64, ARM64) and macOS (Universal) are attached to each
[GitHub Release](../../releases). Download the zip for your Godot version and drag the
`addons/` folder directly into your Godot project root. The layout after extraction:

```
YourGodotProject/
└── addons/
    └── RaycastBridge/
        ├── RaycastBridge.gdextension
        ├── RaycastBridge.cs
        ├── bin/
        │   ├── RaycastBridge.windows.template_release.x86_64.dll
        │   ├── RaycastBridge.windows.template_release.arm64.dll
        │   └── RaycastBridge.macos.template_release.universal.dylib
        └── samples/
            ├── RaycastExample.cs
            └── RaycastBatchExample.cs
```

The `bin/` folder is not committed to this repository. Releases are the distribution
mechanism; build from source if you need binaries outside of a tagged release.

**2. Use from C#**

`RaycastBridge` is a static class — no autoload or scene tree setup required. Drop
`RaycastBridge.cs` into your project and call it directly.

> The GDExtension native class is registered internally as `RaycastBridgeNative` to avoid
> a name collision with the C# wrapper. You do not need to interact with it directly.

#### `IntersectRay` — single ray

```csharp
using Godot;
using PhysicsQueryBridge;

public partial class MyNode : Node
{
    private void CastRay(Vector3 from, Vector3 to, uint collisionMask)
    {
        var spaceState = GetWorld3D().DirectSpaceState;
        var result = RaycastBridge.IntersectRay(spaceState, from, to, collisionMask);

        bool    hit      = RaycastBridge.GetHit(result, 0);
        Vector3 position = RaycastBridge.GetPosition(result, 0);
        Vector3 normal   = RaycastBridge.GetNormal(result, 0);
    }
}
```

#### `IntersectRaysBatch` — N rays, one call

```csharp
using Godot;
using PhysicsQueryBridge;

public partial class RaycastOrchestrator : Node
{
    private const int RayCount = 12; // e.g. 3 rays × 4 wheels

    private readonly PackedFloat32Array _batchIn = new PackedFloat32Array();

    public override void _Ready()
    {
        _batchIn.Resize(RayCount * 7);
    }

    private void DispatchAndRead(PhysicsDirectSpaceState3D spaceState, uint collisionMask)
    {
        // Pack rays from wheel poses before calling:
        for (int i = 0; i < RayCount; i++)
            RaycastBridge.PackRay(_batchIn, i, origin, direction, maxDist);

        // Single GDExtension call for all rays:
        var results = RaycastBridge.IntersectRaysBatch(_batchIn, spaceState, RayCount, collisionMask);

        // Read results by ray index:
        for (int i = 0; i < RayCount; i++)
        {
            bool    hit      = RaycastBridge.GetHit(results, i);
            Vector3 position = RaycastBridge.GetPosition(results, i);
            Vector3 normal   = RaycastBridge.GetNormal(results, i);
            // ... distribute to wheels
        }
    }
}
```

### Platform limitations

#### macOS

The binaries in releases are **ad-hoc signed** (using `-` as the identity, a zero-cost
placeholder). This is sufficient for:
- Running in the Godot editor on your own Mac
- Distribution to other developers who will run it themselves

It is **not sufficient** for:
- Submitting to the **Mac App Store** — requires a paid Apple Developer account, a
  distribution certificate, and App Store provisioning. The `.dylib` must be signed with
  your distribution identity and the app submitted through Xcode or `altool`.
- **Notarised distribution** outside the App Store (e.g. a `.dmg` or `.zip` you host
  yourself) — Apple requires notarisation for software distributed to users on macOS 10.15+
  who have Gatekeeper enabled. This needs a paid Developer ID certificate, signing with
  `codesign --deep --options runtime`, and submission to Apple's notary service via
  `notarytool`. Without it, users will see "Apple cannot check it for malicious software"
  and may be blocked from opening the app.

If you are publishing a game that embeds this extension, you will need to re-sign the
`.dylib` with your own Apple Developer credentials as part of your Godot export pipeline.
Godot's export documentation covers this for GDExtensions.

#### Windows

Fewer restrictions apply:
- The binaries are **unsigned**. Windows SmartScreen may show a warning the first time a
  user runs an app that contains them, particularly if the app itself is also unsigned.
  For most development and internal use this is not an issue.
- For **commercial distribution via Steam, Epic, or direct download**, signing with a
  code-signing certificate (EV or OV, from a CA like DigiCert or Sectigo) removes the
  SmartScreen warning and is expected by users. This is done at the Godot export stage,
  not at the GDExtension level — you sign the final `.exe`, not the `.dll` individually.
- The **Microsoft Store** (MSIX packaging) requires the package to be signed, but again
  this is handled at the app level by Godot's export pipeline.

---

## Building from source

### Repository layout

```
RaycastBridge/
├── godot-cpp/              # gitignored — created by setup.py
│   └── 4.3/                # one sub-folder per Godot version
├── bin/                    # compiled output (.dll / .dylib)
├── src/                    # C++ source
├── samples/                # C# usage examples
├── SConstruct
├── setup.py                # clones godot-cpp at the right version
└── RaycastBridge.gdextension
```

`godot-cpp` is never committed. `setup.py` clones it on demand. Multiple Godot versions
can coexist under `godot-cpp/` without interfering with each other.

### Prerequisites

- Python 3 and [SCons](https://scons.org/): `pip install scons`
- A C++17-capable compiler:
  - **Windows:** Visual Studio 2019+ (MSVC) — open a Developer Command Prompt so `cl.exe` is on PATH, or pass `use_mingw=yes` for MinGW-w64
  - **macOS:** Xcode Command Line Tools (`xcode-select --install`)
  - **Linux:** GCC or Clang (`sudo apt install build-essential scons`)

### Steps

**1. Clone this repo**

```bash
git clone <this-repo-url>
cd RaycastBridge
```

**2. Fetch godot-cpp**

```bash
python setup.py                      # clones for Godot 4.3 (default)
python setup.py --godot-version 4.4  # or a different version
python setup.py --list               # show what's already cloned
```

This clones `https://github.com/godotengine/godot-cpp` into `godot-cpp/4.3/`. Match the
version to the Godot editor you are targeting (`Help → About Godot` shows the version).

**3. Build**

```bash
# Debug build for the host platform (default)
scons

# Release build
scons target=template_release

# Different Godot version
scons godot_version=4.4

# Explicit platform / architecture (usually not needed — SCons detects the host)
scons platform=windows arch=x86_64 target=template_release
scons platform=macos arch=arm64 target=template_release
```

SCons compiles `godot-cpp` on the first run (a few minutes). Subsequent builds are
incremental. Output lands in `bin/` with names like:

```
bin/RaycastBridge.windows.template_release.x86_64.dll
bin/RaycastBridge.macos.template_release.arm64.dylib
```

**4. Verify**

Open the Godot editor. If the extension loaded, `RaycastBridge` will appear in the class
reference. If not, check the Godot output panel for extension load errors.

---

## CI / automated builds (GitHub Actions)

The workflow at `.github/workflows/build.yml` runs automatically and produces
ready-to-use binaries without any local toolchain setup.

### What triggers a build

| Event | What happens |
|---|---|
| Push a version tag (`v*`) | Builds all platforms **and** creates a GitHub Release with all binaries attached as downloadable assets |
| Manual trigger | `Actions → Build RaycastBridge → Run workflow` — useful for testing the pipeline itself |

### Platforms built

| Job | Runner | Output |
|---|---|---|
| `windows-x86_64` | `windows-latest` | `.windows.template_debug.x86_64.dll` + release variant |
| `windows-arm64` | `windows-latest` | `.windows.template_debug.arm64.dll` + release (MSVC cross-compiler) |
| `macos` | `macos-latest` | Both slices (x86-64 + arm64) merged into `.universal.dylib` via `lipo` |

The release job produces one zip per Godot version (e.g. `RaycastBridge-v1.0.0-godot4.3.zip`), each containing all platform binaries staged under `addons/RaycastBridge/` — drag the `addons/` folder into your project root to install.

### Releasing a new version

```bash
git tag v1.0.0
git push origin v1.0.0
```

GitHub Actions will build all platforms and publish a Release at
`https://github.com/<you>/<repo>/releases/tag/v1.0.0` with the binaries attached. No
manual upload needed.

### godot-cpp caching

godot-cpp is cached between runs (keyed by version + platform). The first build for a
given version clones and compiles it; subsequent pushes reuse the cache and are much
faster.

---

## Godot version compatibility

Release zips are built against **Godot 4.3, 4.4, and 4.5**. Each zip is compiled against
the matching `godot-cpp` branch and is not interchangeable — use the zip that matches your
editor version.

The extension uses no version-specific APIs beyond `PhysicsDirectSpaceState3D`, so it
should continue to work on future 4.x releases. When a new `godot-cpp` stable branch
appears, adding support is a one-line change to the matrix in
`.github/workflows/build.yml`.

**Adding a new Godot version to the build matrix:**
1. Verify the branch exists at [github.com/godotengine/godot-cpp/branches](https://github.com/godotengine/godot-cpp/branches)
2. Add the version string to the three `matrix.godot_version` lists in `.github/workflows/build.yml`
3. Note: if godot-cpp adopts a new branch naming convention (e.g. `godot-4.6-stable`
   instead of `4.6`), update both the matrix value and the `--branch` flag in the clone step
