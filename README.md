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

## What this is

A GDExtension for Godot 4 (.NET) that provides allocation-reduced raycasting, eliminating
the .NET GC pressure caused by `PhysicsDirectSpaceState3D.IntersectRay` at high call rates.

## Why this exists

Calling `IntersectRay` from C# causes the Mono glue layer to wrap the native result in a
`Godot.Collections.Dictionary` — a finalizable managed object — on every call. At high
call rates, this generates thousands of finalizable objects per second and measurable GC
pressure.

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

---

## Using pre-built binaries

Pre-built binaries for Windows (x86-64, ARM64) and macOS (Universal) are attached to each
[GitHub Release](../../releases). Download the zip from the latest release and extract it —
you'll get a `bin/` folder and `RaycastBridge.gdextension`. Copy both into your Godot
project root.

The `bin/` folder is not committed to this repository. Releases are the distribution
mechanism; build from source if you need binaries outside of a tagged release.

See [Project setup](#project-setup) below for how to wire it up in Godot.

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
├── build/                  # gitignored — SCons intermediates
│   └── windows-x86_64/
├── bin/                    # compiled output (.dll / .dylib)
├── src/                    # C++ source
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
| Push to `main` | Builds all platforms; binaries attached to the workflow run as artifacts (available for 90 days, useful for testing) |
| Pull request to `main` | Same — lets you verify a PR builds cleanly before merging |
| Push a version tag (`v*`) | Builds all platforms **and** creates a GitHub Release with all binaries attached as downloadable assets |
| Manual trigger | `Actions → Build RaycastBridge → Run workflow` — useful for testing the pipeline itself |

### Platforms built

| Job | Runner | Output |
|---|---|---|
| `windows-x86_64` | `windows-latest` | `.windows.template_debug.x86_64.dll` + release variant |
| `windows-arm64` | `windows-latest` | `.windows.template_debug.arm64.dll` + release (MSVC cross-compiler) |
| `macos` | `macos-latest` | Both slices (x86-64 + arm64) merged into `.universal.dylib` via `lipo` |

A final `collect` job gathers all three artifacts into a single `RaycastBridge-all` zip.

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

## Project setup

**1. Copy files into your Godot project**

Download the zip from the [latest Release](../../releases/latest) and extract it directly
into your Godot project root. The layout should look like this:

```
YourGodotProject/
├── bin/
│   ├── RaycastBridge.windows.template_release.x86_64.dll
│   ├── RaycastBridge.windows.template_release.arm64.dll
│   └── RaycastBridge.macos.template_release.universal.dylib
└── RaycastBridge.gdextension
```

**2. Add the autoload singleton**

In Godot: `Project → Project Settings → Autoload`  
Add a new entry pointing to a scene or script that instantiates a `RaycastBridge` node.

The simplest approach: create a `RaycastBridge.tscn` containing a single `RaycastBridge`
node (the class registered by this extension), and add that scene as an autoload with the
name `RaycastBridge`. It will then be accessible from C# as:

```csharp
GetNode<GodotObject>("/root/RaycastBridge")
```

**3. Use from C#**

```csharp
// Pre-allocate once (e.g. in _Ready):
var buffer = new PackedFloat32Array();
buffer.Resize(8);
var raycastBridge = GetNode<GodotObject>("/root/RaycastBridge");

// Per raycast (hot path — zero managed allocations):
raycastBridge.Call("intersect_ray_into", buffer, spaceState, from, to, collisionMask);

bool hit      = buffer[0] > 0.5f;
var  position = new Vector3(buffer[1], buffer[2], buffer[3]);
var  normal   = new Vector3(buffer[4], buffer[5], buffer[6]);
```

---

## Godot version compatibility

Tested against Godot 4.3. Should work on any 4.x version with a matching `godot-cpp`
branch. The extension uses no version-specific APIs beyond `PhysicsDirectSpaceState3D`.
