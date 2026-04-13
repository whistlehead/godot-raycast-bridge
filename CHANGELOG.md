# Changelog

## [v0.1.0] — 2026-04-13

Initial experimental release.

- `intersect_ray_packed` — low-allocation raycast returning a new `PackedFloat32Array`
- `intersect_ray_into` — zero-allocation raycast writing into a caller-supplied buffer
- Windows x86-64 and ARM64 builds via GitHub Actions (MSVC)
- macOS Universal build via GitHub Actions (`lipo` fat binary)
- `setup.py` helper to clone the correct `godot-cpp` version
