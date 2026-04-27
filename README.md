# godot-raycast-bridge

## Disclaimer

Long story short: this library works well at reducing - but not eliminating - Garbage
Collector (GC) pressure if, and only if:
- You are using a LOT of raycasts
- You are able to batch your raycast queries together and dispatch them in one go

In particular, it totally eliminates Gen2 GC collection in my testing setup
(10,000 rays/second), eliminating a very real cause of microstutter. At batch sizes of
20 or more, Gen1 collections were also eliminated. This matters more than raw allocation
numbers — Gen1/2 pauses are longer and less predictable than Gen0, and less likely to be
drowned out by other scene activity.

Reading hit results from the native dictionary also allocates — each field access boxes a
new managed value. At high call rates this compounds the base allocation cost significantly.
The bridge returns a plain struct; field reads are zero-allocation regardless of how many
fields you read.

In a typical use scenario, if you can reasonably batch 10 or more rays together for one dispatch, this will measurably help reduce Gen1 and Gen2 pressure. If you are reading 2 or more fields from every cast, the native p99 spikes above 5 ms even with optimised code — the bridge reduces that to under 4.5 ms at batch 2, and under 3.5 ms at batch 10. If you have a natural batch of 200+ rays you can group together, even better, but don't go overboard and try to force together every raycast in your scene — you won't see any practical returns beyond batch 20.

*This library is objectively worse than native in all cases at batch size 1.* The crossover point depends on how many fields you read: batch 20 if reading 0–1 fields, batch 2 if reading 2–3 fields. Only use it if your project meets the above criteria.

To be clear - the Godot implementation is easy to use and works great if you have a more typical
number of raycasts per frame, a few dozen is hardly going to tickle the garbage collector, plus
it has some under-the-hood optimisations which make it more efficient than it may first appear.
In my case though, I'm shooting 15 rays per wheel, 10 times per tick, 60 ticks per second, on a
minimum of four wheels. GC allocations add up *fast*.

This extension has been largely coded by Claude Code (Sonnet 4.6) under heavy direction. It
behaves as expected in all circumstances I've tested.

From beyond this point, you are in Magical Claude-land. I have become a copy editor for the robot.

This feels wrong.

## What this is and why it exists

A GDExtension for Godot 4 (.NET) that reduces .NET GC pressure when calling
`PhysicsDirectSpaceState3D.IntersectRay` at high rates.

Calling `IntersectRay` from C# causes the Mono glue layer to wrap the native result in a
`Godot.Collections.Dictionary` — a finalizable managed object — on every call. At high
call rates this generates thousands of finalizable objects per second and measurable GC
pressure.

This extension intercepts the result in C++ before it crosses the managed/unmanaged
boundary, extracts the values into a flat `float[]`, and returns that to C#
instead. The managed Dictionary wrapper is never created. See
`docs/godot_raycast_allocation_research.md` for the full investigation.

## API

One method is exposed from C#:

**`intersect_rays_batch(in_buffer, space, ray_count, collision_mask) → float[]`**  
Casts N rays in a single GDExtension call. Returns a `float[]` of `ray_count × 9` floats.
One managed allocation per call regardless of ray count. The per-ray C++ heap work
(`PhysicsRayQueryParameters3D` + `DictionaryPrivate`) still occurs N times — that cost is
irreducible without engine-level access and does not affect the .NET GC. The one managed
allocation can itself be eliminated via a P/Invoke side-channel (see
`docs/pinvoke_zero_alloc_spec.md`), though that approach is unimplemented and unvalidated.

> **Single-ray use is not exposed.** Each GDExtension dispatch carries fixed overhead
> (Variant boxing of arguments, return-value copy across the boundary). How quickly the
> bridge pays for that overhead depends on how many fields you read — native dictionary
> reads allocate per field, bridge struct reads do not. Benchmarking shows the bridge
> outperforms *optimised* native code (cached `PhysicsRayQueryParameters3D`, mutated per
> ray, cached `StringName` keys) at a batch size of roughly **20 rays** when reading 0–1
> fields, **2 rays** when reading 2–3 fields (native p99 spikes sharply above 5 ms at that
> point). Below those thresholds, call `PhysicsDirectSpaceState3D.IntersectRay` directly —
> and make sure you are caching your params object. See
> [Benchmark results](#benchmark-results) below.

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

`collision_mask` applies uniformly to all rays in the batch. If `in_buffer.Length` does
not equal `ray_count × 7`, all results are returned as miss.

---

## Installation

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
            ├── RaycastBatchExample.cs
            └── RaycastGCBenchmark.cs
```

The `bin/` folder is not committed to this repository. Releases are the distribution
mechanism; build from source if you need binaries outside of a tagged release.

**2. Use from C#**

`RaycastBridge` is a static class — no autoload or scene tree setup required. Drop
`RaycastBridge.cs` into your project and call it directly.

> The GDExtension native class is registered internally as `RaycastBridgeNative` to avoid
> a name collision with the C# wrapper. You do not need to interact with it directly.

#### Example - raycast controller for a vehicle

```csharp
using Godot;
using PhysicsQueryBridge;

public partial class RaycastOrchestrator : Node
{
    private const int RayCount = 60; // e.g. 15 rays × 4 wheels — well above the ~15–20-ray break-even

    private float[] _batchIn;

    public override void _Ready()
    {
        _batchIn = new float[RayCount * 7];
    }

    private void DispatchAndRead(PhysicsDirectSpaceState3D spaceState, uint collisionMask)
    {
        // Pack rays from wheel poses before calling:
        for (int i = 0; i < RayCount; i++)
        {
            // origin/direction/maxDist come from your wheel pose data:
            Vector3 origin    = /* wheel[i].GlobalPosition */ default;
            Vector3 direction = /* wheel[i].SuspensionDir  */ Vector3.Down;
            float   maxDist   = /* wheel[i].TravelLength   */ 0.5f;
            RaycastBridge.PackRay(_batchIn, i, origin, direction, maxDist);
        }

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

Further samples are included in the samples/ folder.

---

## Benchmark results

Measured with `samples/RaycastGCBenchmark.cs`: 200 rays/tick, 600 ticks (10 s at 60 Hz
physics), all rays hitting geometry. Platform: Godot 4.6.2 stable mono, Windows 11, NVIDIA
GeForce RTX 2060.

Each dispatch mode is measured four times: no result reads, position only, position + normal,
and all fields. This isolates the cost of the cross-boundary mechanism itself from the cost of
consuming the result — a distinction that matters because native dictionary reads allocate per
field accessed, while bridge struct reads do not.

**Mode descriptions**

| Label | Description |
|---|---|
| Z — No-op | No raycasts — scene GC floor |
| A — Native naive | New `PhysicsRayQueryParameters3D` per ray; plain string keys on read |
| B — Native optimised | Single params object cached, `From`/`To` mutated per ray; cached `StringName` keys on read |
| C — Bridge batch 1 | 200 `Call()` dispatches per tick — full batch machinery at minimum size |
| H — Bridge batch 2 | 100 `Call()` dispatches per tick |
| D — Bridge batch 5 | 40 `Call()` dispatches per tick |
| E — Bridge batch 10 | 20 `Call()` dispatches per tick |
| F — Bridge batch 20 | 10 `Call()` dispatches per tick |
| G — Bridge batch 200 | 1 `Call()` dispatch per tick |

### Table 1 — No reads (mechanism cost only)

| Mode | Bytes allocated | GC gen0/1/2 | Bytes/ray | Mean (ms) | p99 (ms) | Stddev (ms) |
|---|---|---|---|---|---|---|
| Z — No-op | 8,200 | 0/0/0 | — | ~0 | ~0 | ~0 |
| A — Native naive | 28,817,728 | 4/2/1 | ~240 | 1.774 | 4.718 | 2.495 |
| B — Native optimised | 14,446,720 | 2/1/0 | ~120 | 1.164 | 2.978 | 1.322 |
| C — Bridge batch 1 | 70,078,576 | 11/10/0 | ~584 | 2.343 | 21.719 | 2.766 |
| H — Bridge batch 2 | 36,948,888 | 5/4/0 | ~308 | 1.653 | 4.416 | 1.694 |
| D — Bridge batch 5 | 17,465,832 | 2/1/0 | ~146 | 1.343 | 3.757 | 1.110 |
| E — Bridge batch 10 | 10,839,512 | 1/0/0 | ~90 | 1.151 | 3.221 | 0.520 |
| F — Bridge batch 20 | 7,576,760 | 1/0/0 | ~63 | 1.132 | 3.254 | 0.520 |
| G — Bridge batch 200 | 4,644,896 | 0/0/0 | ~39 | 1.032 | 2.722 | 0.391 |

### Table 2 — Read position only (1 field)

| Mode | Bytes allocated | GC gen0/1/2 | Bytes/ray | Mean (ms) | p99 (ms) | Stddev (ms) |
|---|---|---|---|---|---|---|
| Z — No-op | 8,200 | 0/0/0 | — | ~0 | ~0 | ~0 |
| A — Native naive | 37,429,768 | 5/4/0 | ~312 | 1.891 | 5.360 | 1.813 |
| B — Native optimised | 24,948,864 | 3/2/0 | ~208 | 1.320 | 3.396 | 1.311 |
| C — Bridge batch 1 | 70,070,640 | 11/10/0 | ~584 | 2.397 | 21.155 | 2.901 |
| H — Bridge batch 2 | 36,948,888 | 5/4/0 | ~308 | 1.710 | 4.818 | 1.696 |
| D — Bridge batch 5 | 17,465,832 | 2/1/0 | ~146 | 1.297 | 3.760 | 0.819 |
| E — Bridge batch 10 | 10,839,512 | 1/0/0 | ~90 | 1.166 | 3.354 | 0.533 |
| F — Bridge batch 20 | 7,576,760 | 1/0/0 | ~63 | 1.053 | 2.851 | 0.363 |
| G — Bridge batch 200 | 4,644,896 | 0/0/0 | ~39 | 0.990 | 2.677 | 0.295 |

### Table 3 — Read position + normal (2 fields)

| Mode | Bytes allocated | GC gen0/1/2 | Bytes/ray | Mean (ms) | p99 (ms) | Stddev (ms) |
|---|---|---|---|---|---|---|
| Z — No-op | 8,200 | 0/0/0 | — | ~0 | ~0 | ~0 |
| A — Native naive | 50,873,280 | 8/7/0 | ~424 | 2.170 | 20.521 | 2.391 |
| B — Native optimised | 38,393,664 | 6/5/0 | ~320 | 1.577 | 5.173 | 1.827 |
| C — Bridge batch 1 | 70,070,640 | 11/10/0 | ~584 | 2.347 | 21.289 | 2.611 |
| H — Bridge batch 2 | 36,949,208 | 5/4/0 | ~308 | 1.665 | 4.315 | 1.710 |
| D — Bridge batch 5 | 17,465,832 | 2/1/0 | ~146 | 1.264 | 3.450 | 0.799 |
| E — Bridge batch 10 | 10,839,512 | 1/0/0 | ~90 | 1.177 | 3.503 | 0.535 |
| F — Bridge batch 20 | 7,576,760 | 1/0/0 | ~63 | 1.071 | 2.951 | 0.404 |
| G — Bridge batch 200 | 4,644,896 | 0/0/0 | ~39 | 0.979 | 2.689 | 0.266 |

### Table 4 — Read all fields (position, normal, collider ID — 3 fields)

| Mode | Bytes allocated | GC gen0/1/2 | Bytes/ray | Mean (ms) | p99 (ms) | Stddev (ms) |
|---|---|---|---|---|---|---|
| Z — No-op | 8,200 | 0/0/0 | — | ~0 | ~0 | ~0 |
| A — Native naive | 64,310,872 | 10/9/0 | ~536 | 2.453 | 21.544 | 2.712 |
| B — Native optimised | 51,832,272 | 8/7/0 | ~432 | 1.785 | 17.622 | 2.101 |
| C — Bridge batch 1 | 70,070,640 | 11/10/0 | ~584 | 2.426 | 21.030 | 2.632 |
| H — Bridge batch 2 | 36,948,888 | 5/4/0 | ~308 | 1.706 | 4.670 | 1.727 |
| D — Bridge batch 5 | 17,465,832 | 2/1/0 | ~146 | 1.299 | 3.548 | 0.852 |
| E — Bridge batch 10 | 10,839,512 | 1/0/0 | ~90 | 1.158 | 3.306 | 0.548 |
| F — Bridge batch 20 | 7,576,760 | 1/0/0 | ~63 | 1.059 | 3.139 | 0.415 |
| G — Bridge batch 200 | 4,644,896 | 0/0/0 | ~39 | 0.991 | 2.657 | 0.301 |

---

### When is the bridge worthwhile?

"Worthwhile" here means low *and consistent* frame time: p99 below the native baseline, gen1
collections eliminated, and stddev tight. These are more meaningful than mean time alone
because gen1/2 pauses are the direct cause of visible microstutter.

**"Probably worthwhile"** — p99 is already below native optimised; gen1 fires less often or
not at all; meaningful jitter reduction over native.

**"Definitely worthwhile"** — p99 is well clear of native; gen1 is fully eliminated; stddev
is tight and stable regardless of field count.

| | 0 fields read | 1 field (position) | 2 fields (pos + normal) | 3 fields (all) |
|---|---|---|---|---|
| **Native optimised p99** | 2.978 ms | 3.396 ms | 5.173 ms | 17.622 ms |
| **Probably worthwhile at** | batch 20 | batch 20 | batch 2 | batch 2 |
| **Definitely worthwhile at** | batch 20 | batch 20 | batch 10 | batch 10 |

At 0–1 fields the bridge p99 only drops below native at batch 20 — the GDExtension dispatch
overhead isn't offset by dictionary savings until the batch is large enough. At 2 fields the
native p99 spikes to 5.2 ms (gen1 firing 5×); batch 2 already beats that at 4.3 ms with half
the gen1 activity. At 3 fields native p99 hits 17.6 ms; batch 2 brings it to 4.7 ms
immediately. The "definitely" threshold is batch 10 for 2–3 fields because that is where gen1
is fully eliminated and p99 stabilises around 3.3–3.5 ms.

**The crossover point moves sharply with field count because native dictionary reads allocate
per field,** while bridge reads do not. Native optimised bytes/ray: ~120 (0 fields) → ~208 (1
field) → ~320 (2 fields) → ~432 (3 fields) — a linear ~112 bytes per field, confirmed by the
new 2-field run. Bridge bytes/ray are identical across all four tables (~308 at batch 2, ~146
at batch 5, ~90 at batch 10), so every additional field you read from the native dictionary
moves the break-even to a smaller batch size.

**Before reaching for this library, cache your `PhysicsRayQueryParameters3D`.** Table 1,
Mode A vs Mode B: simply reusing the params object cuts allocations by 50% and eliminates
gen2 collections — no extension required.

**Reading 2+ fields from the native dictionary causes p99 spikes even with the optimised
path.** At 2 fields, native optimised p99 is 5.2 ms (gen1 fires 5×); at 3 fields it reaches
17.6 ms (gen1 fires 7×). These are the pauses that produce visible stutter. The bridge
eliminates them entirely at batch 10+, and reduces them sharply at batch 2.

**The bridge never triggers gen2 collections at any batch size,** because the returned
`float[]` is short-lived and never promoted. Naive native code triggers gen2 even with no
reads (the `Dictionary` wrapper has a finalizer); optimised native avoids gen2 but still
triggers gen1 consistently. Whether avoiding gen1/2 matters depends on your platform — on
consoles and mobile, higher-generation pauses can be significant even at low frequency.

**Mode C (batch size 1) is included to show the cost floor of the batch machinery** and is
not a recommended usage pattern. It is worse than a dedicated single-ray dispatch would be
because it also packs a 7-float input buffer on every call. Its bytes/ray are unchanged
across all four tables, confirming that bridge reads add no allocation overhead.

### Further reduction via P/Invoke

The remaining ~7.7 KB/tick in Mode F is almost entirely the output `float[]` allocated
on each `Call()` return. `docs/pinvoke_zero_alloc_spec.md` contains a complete
implementation spec for eliminating it by splitting the dispatch: buffer pointers are
registered via P/Invoke (no Variant boxing, no allocation), then `Call()` passes only the
space state and writes results directly into the pre-pinned output buffer, returning
`void`. This would reduce allocations to near-zero at the cost of added complexity.
This is not warranted: the remaining allocation at batch 20+ is small enough that ordinary
scene activity (string formatting, signal dispatch, UI, audio) will dominate Gen0 pressure
regardless. The design is documented in case that assumption changes.

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
├── src/                    # C++ source
├── samples/                # C# usage examples
├── SConstruct
├── setup.py                # clones godot-cpp at the right version
├── RaycastBridge.cs        # C#-side wrapper to simplify communication with the extension
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

> **Tested on Godot 4.6.2** (using the 4.5 `godot-cpp` bindings, which are compatible for
> this extension). Builds against 4.3 and 4.4 are provided by the CI matrix but have not
> been runtime-tested.

The extension uses no version-specific APIs beyond `PhysicsDirectSpaceState3D`, so it
should continue to work on future 4.x releases. When a new `godot-cpp` stable branch
appears, adding support is a one-line change to the matrix in
`.github/workflows/build.yml`.

**Adding a new Godot version to the build matrix:**
1. Verify the branch exists at [github.com/godotengine/godot-cpp/branches](https://github.com/godotengine/godot-cpp/branches)
2. Add the version string to the three `matrix.godot_version` lists in `.github/workflows/build.yml`
3. Note: if godot-cpp adopts a new branch naming convention (e.g. `godot-4.6-stable`
   instead of `4.6`), update both the matrix value and the `--branch` flag in the clone step
