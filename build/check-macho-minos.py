#!/usr/bin/env python3
"""Lists the minimum macOS every Mach-O file under the given paths was built for, and fails when one needs a newer
macOS than the app says it supports.

dyld refuses to load a library built for a newer macOS than the one running (LC_BUILD_VERSION minos, or the older
LC_VERSION_MIN_MACOSX). The app still starts, but the first conversion that needs the library fails (#600: the
ImageMagick library in Magick.NET 14.17.2 needs macOS 14 on Apple silicon and 15 on Intel). The limit is
LSMinimumSystemVersion in installer/macos/Info.plist unless --max is given. Universal (fat) files are checked per
architecture. Standard library only, so it runs on any CI image.

Usage: check-macho-minos.py [--max 13.0] PATH [PATH ...]
Exit codes: 0 every file is within the limit, 1 a file needs a newer macOS, 2 no Mach-O files or a file can't be read.
"""

import argparse
import os
import plistlib
import struct
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
INFO_PLIST = os.path.join(ROOT, "installer", "macos", "Info.plist")

MH_MAGIC = 0xFEEDFACE
MH_MAGIC_64 = 0xFEEDFACF
FAT_MAGIC = 0xCAFEBABE
FAT_MAGIC_64 = 0xCAFEBABF
LC_VERSION_MIN_MACOSX = 0x24
LC_BUILD_VERSION = 0x32
PLATFORM_MACOS = 1
CPU_NAMES = {7: "i386", 0x01000007: "x86_64", 12: "arm", 0x0100000C: "arm64"}


class MachOError(Exception):
    pass


def decode_version(packed):
    """xxxx.yy.zz nibbles, as in LC_BUILD_VERSION and LC_VERSION_MIN_MACOSX."""
    return (packed >> 16, (packed >> 8) & 0xFF, packed & 0xFF)


def thin_minos(data, offset):
    """Returns (architecture, minimum macOS) of the thin Mach-O image at offset."""
    magic_le = struct.unpack_from("<I", data, offset)[0]
    if magic_le not in (MH_MAGIC, MH_MAGIC_64):
        raise MachOError("not a little-endian Mach-O image")
    is64 = magic_le == MH_MAGIC_64
    cputype, _, _, ncmds, sizeofcmds = struct.unpack_from("<iiIII", data, offset + 4)
    position = offset + (32 if is64 else 28)
    end = position + sizeofcmds
    found = None
    for _ in range(ncmds):
        if position + 8 > end:
            raise MachOError("load commands run past their size")
        command, size = struct.unpack_from("<II", data, position)
        if size < 8:
            raise MachOError("load command too small")
        if command == LC_BUILD_VERSION:
            platform, minos = struct.unpack_from("<II", data, position + 8)
            if platform == PLATFORM_MACOS:
                found = decode_version(minos)
        elif command == LC_VERSION_MIN_MACOSX and found is None:
            found = decode_version(struct.unpack_from("<I", data, position + 8)[0])
        position += size
    return CPU_NAMES.get(cputype & 0xFFFFFFFF, hex(cputype & 0xFFFFFFFF)), found


def macho_minos(data):
    """Returns [(architecture, minimum macOS or None)] for a thin or universal Mach-O file."""
    if len(data) < 8:
        raise MachOError("file too small")
    magic_be = struct.unpack_from(">I", data, 0)[0]
    if magic_be in (FAT_MAGIC, FAT_MAGIC_64):
        count = struct.unpack_from(">I", data, 4)[0]
        entry, layout = (32, ">iiQQI") if magic_be == FAT_MAGIC_64 else (20, ">iiIII")
        offsets = [struct.unpack_from(layout, data, 8 + i * entry)[2] for i in range(count)]
        return [thin_minos(data, offset) for offset in offsets]
    return [thin_minos(data, 0)]


def is_macho(path):
    try:
        with open(path, "rb") as stream:
            head = stream.read(8)
    except OSError:
        return False
    if len(head) < 8:
        return False
    if struct.unpack_from("<I", head)[0] in (MH_MAGIC, MH_MAGIC_64):
        return True
    # 0xCAFEBABE is also a Java class file; those have a major version >= 45 where a fat header has a small count.
    return struct.unpack_from(">I", head)[0] in (FAT_MAGIC, FAT_MAGIC_64) and struct.unpack_from(">I", head, 4)[0] < 20


def macho_files(paths):
    for path in paths:
        if os.path.isdir(path):
            for directory, _, names in os.walk(path):
                for name in sorted(names):
                    full = os.path.join(directory, name)
                    if not os.path.islink(full) and is_macho(full):
                        yield path, full
        elif is_macho(path):
            yield os.path.dirname(path), path


def parse_version(text):
    parts = [int(part) for part in text.split(".")]
    return tuple((parts + [0, 0])[:3])


def format_version(version):
    if version is None:
        return "none"
    return ".".join(str(part) for part in (version if version[2] else version[:2]))


def default_limit():
    with open(INFO_PLIST, "rb") as stream:
        return plistlib.load(stream)["LSMinimumSystemVersion"]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--max", help="newest minimum macOS allowed (default: LSMinimumSystemVersion in Info.plist)")
    parser.add_argument("paths", nargs="+")
    options = parser.parse_args(argv)
    limit_text = options.max or default_limit()
    limit = parse_version(limit_text)

    rows = []
    unreadable = []
    for base, path in macho_files(options.paths):
        shown = os.path.relpath(path, base) if base else path
        try:
            with open(path, "rb") as stream:
                images = macho_minos(stream.read())
        except (MachOError, OSError, struct.error) as error:
            unreadable.append((shown, str(error)))
            continue
        for architecture, minos in images:
            rows.append((f"{shown} ({architecture})", minos))

    if not rows and not unreadable:
        print("No Mach-O files found in: " + ", ".join(options.paths), file=sys.stderr)
        return 2

    width = max([len(shown) for shown, _ in rows] + [len(shown) for shown, _ in unreadable] + [4])
    print(f"{'File':<{width}}  {'Needs':<8}  Result")
    print(f"{'-' * width}  {'-' * 8}  ------")
    failed = 0
    for shown, minos in rows:
        too_new = minos is not None and minos > limit
        failed += too_new
        result = f"needs a newer macOS than {limit_text}" if too_new else "ok"
        print(f"{shown:<{width}}  {format_version(minos):<8}  {result}")
    for shown, error in unreadable:
        print(f"{shown:<{width}}  {'?':<8}  cannot read: {error}")

    newest = max((minos for _, minos in rows if minos), default=None)
    print()
    print(f"{len(rows)} Mach-O images, newest requirement macOS {format_version(newest)}, limit macOS {limit_text}.")
    sys.stdout.flush()
    if failed:
        print(f"{failed} image(s) need a macOS newer than {limit_text}: they would not load on the oldest supported "
              "macOS. Rebuild them for it or raise LSMinimumSystemVersion.", file=sys.stderr)
        return 1
    if unreadable:
        print(f"{len(unreadable)} Mach-O file(s) could not be read.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
