#!/usr/bin/env python

# Build script for the godot-raycast-bridge GDExtension.
#
# Prerequisites:
#   1. Clone godot-cpp as a subdirectory of this folder:
#        git clone https://github.com/godotengine/godot-cpp --branch 4.3 godot-cpp
#      Match the branch to your Godot version (4.3, 4.4, etc.).
#   2. Install SCons:  pip install scons
#
# Usage:
#   scons                          # debug build for the host platform
#   scons target=template_release  # release build
#   scons platform=linux           # cross-target (if toolchain is configured)
#
# Output lands in Godot/bin/ and is referenced by RaycastBridge.gdextension.

import os
from SCons.Script import SConscript, Glob

env = SConscript("godot-cpp/SConstruct")

env.Append(CPPPATH=["src/"])

sources = Glob("src/*.cpp")

# Output path mirrors the paths declared in RaycastBridge.gdextension.
# SCons provides env["suffix"] (e.g. ".windows.template_debug.x86_64")
# and env["SHLIBSUFFIX"] (e.g. ".dll" / ".so").
output = env.SharedLibrary(
    "../../Godot/bin/RaycastBridge{}{}".format(
        env["suffix"], env["SHLIBSUFFIX"]
    ),
    source=sources,
)

env.Default(output)
