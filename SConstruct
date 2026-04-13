#!/usr/bin/env python

# Build script for the godot-raycast-bridge GDExtension.
#
# Prerequisites:
#   Run the setup script to clone godot-cpp, or do it manually:
#     git clone https://github.com/godotengine/godot-cpp \
#               --branch 4.3 godot-cpp/4.3
#   Match the branch to your Godot version (4.3, 4.4, etc.).
#   Install SCons:  pip install scons
#
# Usage:
#   scons                                      # debug build, host platform
#   scons target=template_release              # release build
#   scons godot_version=4.4                    # use a different godot-cpp clone
#
# Output lands in bin/ and is referenced by RaycastBridge.gdextension.
# Build intermediates (.obj/.o) land next to source files and are gitignored.

import os
from SCons.Script import SConscript, Glob, ARGUMENTS

godot_version = ARGUMENTS.get("godot_version", "4.3")
godot_cpp_path = "godot-cpp/{}".format(godot_version)

if not os.path.isdir(godot_cpp_path):
    print("ERROR: godot-cpp not found at '{}'.".format(godot_cpp_path))
    print("Run:  python setup.py --godot-version {}".format(godot_version))
    print("  or: git clone https://github.com/godotengine/godot-cpp "
          "--branch {} {}".format(godot_version, godot_cpp_path))
    Exit(1)

env = SConscript("{}/SConstruct".format(godot_cpp_path))

env.Append(CPPPATH=["src/"])

sources = Glob("src/*.cpp")

# Final binaries go in bin/ — paths must match RaycastBridge.gdextension.
output = env.SharedLibrary(
    "bin/RaycastBridge{}{}".format(env["suffix"], env["SHLIBSUFFIX"]),
    source=sources,
)

env.Default(output)
