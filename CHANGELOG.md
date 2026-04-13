# Changelog

## [v0.1.0] — 2026-04-13

Initial experimental release.

- `intersect_ray_packed` — low-allocation single raycast returning a new `PackedFloat32Array`
- `intersect_rays_batch` — batch raycast for N rays in one GDExtension dispatch
- Windows x86-64 and ARM64 builds via GitHub Actions (MSVC)
- macOS Universal build via GitHub Actions (`lipo` fat binary, ad-hoc signed)
- Separate release zips for Godot 4.3, 4.4, and 4.5
- `setup.py` helper to clone the correct `godot-cpp` version
- Fixed: `set_minimum_initialization_level` renamed to `set_minimum_library_initialization_level` in godot-cpp 4.3+
