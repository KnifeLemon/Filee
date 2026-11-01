#!/usr/bin/env python3
"""Builds the Rust native pieces of Filee's packages that upstream doesn't build for a platform Filee supports.

Linux packages must start on Ubuntu 22.04 (glibc 2.35). The rhwp CLI (HWP/HWPX -> PDF) release binary is linked on
Ubuntu 24.04 (GLIBC_2.39), so CI builds it from its pinned source on Ubuntu 22.04 and build/build-portable.ps1 puts it
into the package. SharpHook's native libraries (libuiohook) come from its NuGet package: from 8.0.1 on they need
glibc 2.34 at most.

Windows on ARM64: rhwp has no ARM64 release. It is built on a Windows ARM64 runner
(.github/workflows/windows-arm64-native.yml), hosted on dl.filee.sh and pinned by SHA-256 in build/fetch-engines.ps1.
It links the C runtime statically (+crt-static): it imports no Visual C++ runtime DLL, so no ARM64 copy of it has to
ship next to it.

Subcommands:
  check                     verify the pins below still match engines.json
  rhwp --output DIR         build the rhwp CLI into DIR (rhwp[.exe], LICENSE, README files, like its release archive)

Builds for the machine it runs on. Needs git, a C toolchain (Visual Studio's on Windows) and rustup. Standard library
only.
"""

import argparse
import json
import os
import platform
import re
import shutil
import struct
import subprocess
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
WINDOWS = sys.platform == "win32"

# The rhwp release pinned in engines.json (`check` compares the versions). RUST_TOOLCHAIN is the one in the tag's
# rust-toolchain.toml. Cargo.lock in the tag pins every dependency (--locked).
RHWP_VERSION = "0.8.7"
RHWP_REPOSITORY = "https://github.com/edwardkim/rhwp"
RHWP_COMMIT = "1a76570e833917d15817415a53c09ad61ab3203f"
RUST_TOOLCHAIN = "1.93.1"

# Visual C++ runtime DLLs, as src/Filee.Setup/Installation/RuntimeCheck.cs matches them.
VC_RUNTIME_DLL = re.compile(r"^(vcruntime|msvcp|concrt|vcomp|vccorlib|mfc|mfcm)140[a-z0-9_]*\.dll$", re.IGNORECASE)
PE_MACHINES = {0x8664: "x64", 0xAA64: "arm64"}


class BuildError(Exception):
    pass


def run(command, cwd=None, env=None, capture=False):
    print("+ " + " ".join(command), flush=True)
    result = subprocess.run(command, cwd=cwd, env=env, check=False, text=True,
                            stdout=subprocess.PIPE if capture else None)
    if result.returncode != 0:
        raise BuildError(f"{command[0]} failed with exit code {result.returncode}")
    return result.stdout if capture else None


def pinned_rhwp_versions():
    path = os.path.join(ROOT, "src", "Filee.Engines", "Infrastructure", "engines.json")
    with open(path, encoding="utf-8") as stream:
        manifest = json.load(stream)
    versions = {rid: manifest["platforms"][rid]["components"]["rhwp"]["version"] for rid in ("linux-x64", "linux-arm64")}
    # Windows ARM64 gets the version of the win-x64 download.
    versions["win-x64"] = manifest["components"]["rhwp"]["version"]
    return versions


def check():
    problems = []
    for rid, rhwp in pinned_rhwp_versions().items():
        if rhwp != RHWP_VERSION:
            problems.append(
                f"engines.json pins rhwp {rhwp} for {rid}, but build/build-native.py builds rhwp {RHWP_VERSION}. "
                "Update RHWP_VERSION, RHWP_COMMIT and RUST_TOOLCHAIN (from the tag's rust-toolchain.toml).")
    if problems:
        raise BuildError("\n".join(problems))
    print(f"Pins match: rhwp {RHWP_VERSION} -> {RHWP_COMMIT} (Rust {RUST_TOOLCHAIN}).")


def fetch(repository, commit, destination):
    """A shallow checkout of exactly one commit (GitHub serves any reachable commit by its hash)."""
    os.makedirs(destination)
    run(["git", "init", "-q", destination])
    run(["git", "-C", destination, "fetch", "-q", "--depth", "1", repository, commit])
    run(["git", "-C", destination, "checkout", "-q", "--detach", "FETCH_HEAD"])
    head = run(["git", "-C", destination, "rev-parse", "HEAD"], capture=True).strip()
    if head != commit:
        raise BuildError(f"{repository}: expected commit {commit}, got {head}")


def verify_rhwp_tag():
    """Warns when the tag no longer points at the pinned commit (the pinned commit is built either way)."""
    lines = run(["git", "ls-remote", RHWP_REPOSITORY, f"refs/tags/v{RHWP_VERSION}", f"refs/tags/v{RHWP_VERSION}^{{}}"],
                capture=True).splitlines()
    refs = dict(reversed(line.split("\t", 1)) for line in lines if "\t" in line)
    tagged = refs.get(f"refs/tags/v{RHWP_VERSION}^{{}}") or refs.get(f"refs/tags/v{RHWP_VERSION}")
    if tagged != RHWP_COMMIT:
        print(f"::warning::rhwp tag v{RHWP_VERSION} points at {tagged}, the build uses the pinned {RHWP_COMMIT}.")


def cargo(source, work, name, arguments):
    """Runs cargo build with the pinned toolchain; returns the target folder's release directory."""
    if shutil.which("rustup") is None:
        raise BuildError(f"{name} needs rustup (https://rustup.rs).")
    toolchain = RUST_TOOLCHAIN
    if WINDOWS:
        # The toolchain of this machine by name: an x64 rustup on Windows on ARM would otherwise build x64 programs.
        toolchain += {"ARM64": "-aarch64", "AMD64": "-x86_64"}[platform.machine().upper()] + "-pc-windows-msvc"
    run(["rustup", "toolchain", "install", toolchain, "--profile", "minimal", "--no-self-update"])
    # "+toolchain" takes precedence over a repository's rust-toolchain.toml (rhwp's adds a wasm target).
    env = dict(os.environ, CARGO_TARGET_DIR=os.path.join(work, f"{name}-target"))
    if WINDOWS:
        # The C runtime linked in (/MT): nothing to import from the Visual C++ Redistributable.
        env["RUSTFLAGS"] = "-C target-feature=+crt-static"
    run(["cargo", f"+{toolchain}", "build", "--release", "--locked"] + arguments, cwd=source, env=env)
    return os.path.join(env["CARGO_TARGET_DIR"], "release")


def build_rhwp(output, work):
    verify_rhwp_tag()
    source = os.path.join(work, "rhwp")
    fetch(RHWP_REPOSITORY, RHWP_COMMIT, source)
    program_name = "rhwp.exe" if WINDOWS else "rhwp"
    program = os.path.join(cargo(source, work, "rhwp", ["--bin", "rhwp"]), program_name)
    check_windows_binary(program)
    version = run([program, "--version"], capture=True)
    print(version.strip())
    if RHWP_VERSION not in version:
        raise BuildError(f"rhwp --version printed {version.strip()!r}, expected {RHWP_VERSION}.")
    # The same layout as rhwp's release archive, which build/fetch-portable-engines.ps1 and build/fetch-engines.ps1
    # unpack.
    os.makedirs(output, exist_ok=True)
    shutil.copy2(program, os.path.join(output, program_name))
    copy_notices(source, output, ("LICENSE", "README.md", "README_EN.md"))
    write_source_note(output, "rhwp", RHWP_REPOSITORY, RHWP_COMMIT,
                      f"v{RHWP_VERSION}; {build_flags()}cargo +{RUST_TOOLCHAIN} build --release --locked --bin rhwp; "
                      f"{platform.machine()}")


def build_flags():
    return "RUSTFLAGS=\"-C target-feature=+crt-static\" " if WINDOWS else ""


def copy_notices(source, output, names):
    for name in names:
        if os.path.isfile(os.path.join(source, name)):
            shutil.copy2(os.path.join(source, name), os.path.join(output, name))
    if not os.path.isfile(os.path.join(output, "LICENSE")):
        raise BuildError(f"{source} has no LICENSE file to ship with the build.")


def check_windows_binary(path):
    """A Windows build must be for this machine and must not need the Visual C++ Redistributable."""
    if not WINDOWS:
        return
    machine, imports = pe_imports(path)
    expected = {"AMD64": "x64", "ARM64": "arm64"}.get(platform.machine().upper())
    if machine != expected:
        raise BuildError(f"{os.path.basename(path)} is built for {machine}, expected {expected}.")
    runtime = [name for name in imports if VC_RUNTIME_DLL.match(name)]
    if runtime:
        raise BuildError(f"{os.path.basename(path)} imports {', '.join(runtime)}; it must link the C runtime statically.")
    print(f"{os.path.basename(path)}: {machine}, imports {', '.join(sorted(imports))}")


def pe_imports(path):
    """The machine (x64, arm64) and the imported DLL names of a PE file (its import directory)."""
    with open(path, "rb") as stream:
        data = stream.read()
    if data[:2] != b"MZ":
        raise BuildError(f"{path} is not a PE file.")
    header = struct.unpack_from("<I", data, 0x3C)[0]
    if data[header:header + 4] != b"PE\0\0":
        raise BuildError(f"{path} is not a PE file.")
    machine, sections, _, _, _, optional_size, _ = struct.unpack_from("<HHIIIHH", data, header + 4)
    optional = header + 24
    if struct.unpack_from("<H", data, optional)[0] != 0x20B:
        raise BuildError(f"{path} is not a 64-bit PE file.")
    import_rva = struct.unpack_from("<I", data, optional + 112 + 8)[0]
    table = optional + optional_size
    section_list = [struct.unpack_from("<IIII", data, table + 40 * index + 8) for index in range(sections)]

    def offset(rva):
        for virtual_size, address, raw_size, raw_offset in section_list:
            if address <= rva < address + max(virtual_size, raw_size):
                return raw_offset + rva - address
        raise BuildError(f"{path}: RVA {rva:#x} is outside every section.")

    names = []
    if import_rva:
        entry = offset(import_rva)
        while True:
            descriptor = struct.unpack_from("<IIIII", data, entry)
            if not any(descriptor):
                break
            start = offset(descriptor[3])
            names.append(data[start:data.index(b"\0", start)].decode("ascii"))
            entry += 20
    return PE_MACHINES.get(machine, hex(machine)), names


def write_source_note(output, name, repository, commit, details):
    with open(os.path.join(output, "SOURCE.txt"), "w", encoding="utf-8") as stream:
        stream.write(f"{name} built from source by Filee's CI on {distribution()}.\n"
                     f"Source: {repository}/tree/{commit}\n{details}\n")


def distribution():
    try:
        with open("/etc/os-release", encoding="utf-8") as stream:
            for line in stream:
                if line.startswith("PRETTY_NAME="):
                    return line.split("=", 1)[1].strip().strip('"')
    except OSError:
        pass
    return platform.platform()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("component", choices=["check", "rhwp"])
    parser.add_argument("--output", help="folder for the built files (must not exist yet)")
    parser.add_argument("--work", help="scratch folder (default: a new temporary folder)")
    options = parser.parse_args(argv)
    try:
        check()
        if options.component == "check":
            return 0
        if not (sys.platform.startswith("linux") or WINDOWS):
            raise BuildError("Build the native pieces on Linux or Windows.")
        if not options.output:
            raise BuildError("--output is required.")
        output = os.path.abspath(options.output)
        if os.path.exists(output):
            raise BuildError(f"{output} already exists. Use a fresh output folder.")
        work = os.path.abspath(options.work) if options.work else tempfile.mkdtemp(prefix=f"filee-{options.component}-")
        os.makedirs(work, exist_ok=True)
        build_rhwp(output, work)
        print(f"{options.component} ready in {output}: {', '.join(sorted(os.listdir(output)))}")
        return 0
    except BuildError as error:
        print(f"::error::{error}" if os.environ.get("GITHUB_ACTIONS") else f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
