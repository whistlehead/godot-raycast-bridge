#!/usr/bin/env python3
"""
Clone godot-cpp into godot-cpp/<version>/ so SConstruct can find it.

Usage:
    python setup.py                      # clones default version (4.3)
    python setup.py --godot-version 4.4  # clone a specific version
    python setup.py --list               # show what's already cloned
"""

import argparse
import os
import subprocess
import sys

DEFAULT_VERSION = "4.3"
GODOT_CPP_REPO = "https://github.com/godotengine/godot-cpp"


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--godot-version", default=DEFAULT_VERSION,
                        metavar="VERSION",
                        help="Godot version to clone godot-cpp for (default: %(default)s)")
    parser.add_argument("--list", action="store_true",
                        help="List already-cloned godot-cpp versions and exit")
    args = parser.parse_args()

    base = os.path.join(os.path.dirname(__file__), "godot-cpp")

    if args.list:
        if not os.path.isdir(base):
            print("No godot-cpp versions cloned yet.")
            return
        versions = [d for d in os.listdir(base)
                    if os.path.isdir(os.path.join(base, d))]
        if versions:
            print("Cloned godot-cpp versions:")
            for v in sorted(versions):
                print("  {}".format(v))
        else:
            print("No godot-cpp versions cloned yet.")
        return

    version = args.godot_version
    dest = os.path.join(base, version)

    if os.path.isdir(dest):
        print("godot-cpp/{} already exists — nothing to do.".format(version))
        print("To re-clone, delete '{}' and run again.".format(dest))
        return

    os.makedirs(base, exist_ok=True)

    cmd = [
        "git", "clone",
        GODOT_CPP_REPO,
        "--branch", version,
        "--depth", "1",
        "--recurse-submodules",
        dest,
    ]
    print("Cloning godot-cpp {} into {} ...".format(version, dest))
    print("  " + " ".join(cmd))
    result = subprocess.run(cmd)
    if result.returncode != 0:
        print("\nERROR: git clone failed.")
        print("Make sure branch '{}' exists in {}.".format(version, GODOT_CPP_REPO))
        sys.exit(result.returncode)

    print("\nDone. Build with:")
    print("  scons                          # debug, host platform")
    print("  scons target=template_release  # release, host platform")
    if version != DEFAULT_VERSION:
        print("  scons godot_version={}         # if you have multiple versions".format(version))


if __name__ == "__main__":
    main()
