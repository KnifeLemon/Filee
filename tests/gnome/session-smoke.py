#!/usr/bin/env python3
"""Load the real extension in an isolated headless GNOME session, including Shell's metadata compatibility check.

Requires gnome-shell, Xwayland, dbus-run-session, gsettings and Python GObject bindings. No changes to the desktop
or the installed Filee. Unlike extension-smoke.js, this checks real Shell loading rather than mocking the Shell.
"""

import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import time


UUID = "filee-donut@filee.sh"
ROOT = Path(__file__).resolve().parents[2]


def probe(bus):
    from gi.repository import Gio, GLib

    result = bus.call_sync(
        "org.gnome.Shell", "/org/gnome/Shell", "org.gnome.Shell.Extensions", "GetExtensionInfo",
        GLib.Variant("(s)", (UUID,)),
        None, Gio.DBusCallFlags.NONE, 2000, None,
    ).unpack()[0]
    return {key: value.unpack() if hasattr(value, "unpack") else value for key, value in result.items()}


def inside(output, extension):
    from gi.repository import Gio, GLib

    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    # Unix socket paths have a 108-byte limit; keep the runtime short even when reports live deep in a checkout.
    runtime = Path(tempfile.mkdtemp(prefix="filee-runtime-"))
    for key, folder in [("XDG_RUNTIME_DIR", "runtime"), ("XDG_CONFIG_HOME", "config"),
                        ("XDG_DATA_HOME", "data"), ("XDG_CACHE_HOME", "cache")]:
        path = runtime if key == "XDG_RUNTIME_DIR" else output / folder
        path.mkdir(mode=0o700, parents=True, exist_ok=True)
        os.environ[key] = str(path)
    os.environ.update(GSETTINGS_BACKEND="keyfile", XDG_CURRENT_DESKTOP="GNOME", XDG_SESSION_TYPE="wayland")
    for key in ("DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY"):
        os.environ.pop(key, None)
    shutil.copytree(extension, output / "data/gnome-shell/extensions" / UUID)
    subprocess.run(["gsettings", "set", "org.gnome.shell", "enabled-extensions", f"['{UUID}']"], check=True)
    version = subprocess.check_output(["gnome-shell", "--version"], text=True).strip()
    address = runtime / "filee-gnome/pointer.sock"
    with (output / "gnome-shell.log").open("w") as log:
        shell = subprocess.Popen(
            ["gnome-shell", "--headless", "--wayland", "--virtual-monitor", "1280x720",
             "--wayland-display=filee-smoke-wayland"], stdout=log, stderr=subprocess.STDOUT,
        )
        try:
            last = {}
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                if shell.poll() is not None:
                    raise RuntimeError("Headless GNOME exited; see gnome-shell.log")
                try:
                    last = probe(bus)
                    if address.exists():
                        break
                    # GNOME ExtensionState: OUT_OF_DATE = 4. This is the Fedora 45 upgrade regression.
                    if last.get("state") == 4:
                        raise RuntimeError(f"{version} rejected the extension as OUT OF DATE: check metadata shell-version")
                    if last.get("error"):
                        raise RuntimeError(f"GNOME extension failed: {last['error']}")
                except GLib.Error:
                    pass  # Shell has not acquired its session bus name yet.
                time.sleep(0.1)
            else:
                raise RuntimeError(f"No extension socket after 25 seconds; last state: {last}")
            assert stat.S_IMODE(address.stat().st_mode) == 0o600, "pointer socket must be private"
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(3)
                client.connect(str(address))
                with client.makefile("r") as stream:
                    first = json.loads(stream.readline())
                    second = json.loads(stream.readline())
                assert first["Version"] == second["Version"] == 1, "unexpected pointer protocol"
                assert 0 <= first["Modifiers"] <= 15 and 0 <= first["Buttons"] <= 31, "invalid pointer state"
            last = probe(bus)
            assert last.get("state") == 1, f"extension is not active: {last}"
            result = {"shell": version, "metadata": json.loads((extension / "metadata.json").read_text()),
                      "extensionInfo": last, "socketMode": "0600", "firstFrame": first,
                      "result": "PASS: real GNOME loaded extension and delivered pointer frames"}
            (output / "result.json").write_text(json.dumps(result, indent=2) + "\n")
            print(result["result"], f"({version})", flush=True)
        finally:
            shell.terminate()
            try:
                shell.wait(timeout=8)
            except subprocess.TimeoutExpired:
                shell.kill()
                shell.wait()
            shutil.rmtree(runtime)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="New directory for isolated session files and logs")
    parser.add_argument("--extension", type=Path, default=ROOT / "installer/linux/gnome" / UUID)
    parser.add_argument("--inside-session", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.inside_session:
        inside(args.output.resolve(), args.extension.resolve())
        return
    output = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix="filee-gnome-smoke-"))
    if args.output:
        output.mkdir(parents=True, exist_ok=False)
    print(f"Session evidence: {output}", flush=True)
    child = subprocess.Popen(
        ["dbus-run-session", "--", sys.executable, str(Path(__file__).resolve()), "--inside-session",
         "--output", str(output), "--extension", str(args.extension.resolve())],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, start_new_session=True,
    )
    try:
        text, _ = child.communicate(timeout=45)
    except subprocess.TimeoutExpired:
        os.killpg(child.pid, signal.SIGTERM)
        try:
            text, _ = child.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(child.pid, signal.SIGKILL)
            text, _ = child.communicate()
        (output / "runner.log").write_text(text)
        raise RuntimeError(f"Isolated session exceeded 45 seconds; see {output}")
    (output / "runner.log").write_text(text)
    if child.returncode:
        errors = [line for line in text.splitlines() if "Error:" in line or "AssertionError" in line]
        print("\n".join(errors) or f"Session failed; see {output / 'runner.log'}", file=sys.stderr)
        raise SystemExit(child.returncode)
    print(json.loads((output / "result.json").read_text())["result"])


if __name__ == "__main__":
    main()
