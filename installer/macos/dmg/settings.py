# dmgbuild settings for the macOS disk image (build/build-portable.ps1 passes -D app=<Filee.app> -D dir=<this folder>).
# The window shows 600x456 points under its title bar; background.png is drawn for it and background@2x.png for Retina
# screens (dmgbuild combines the two). The design sits in the top 600x400: the bottom 56 points are plain background,
# covered when Finder shows its path and status bars (a Finder-wide choice the disk image can't turn off).
# Icon positions are the centres of the slots drawn on the background.
import filecmp
import os
import subprocess

here = defines["dir"]  # noqa: F821 (set by dmgbuild)
app = defines["app"]  # noqa: F821

format = "UDZO"
filesystem = "HFS+"
files = [app, (os.path.join(here, "INSTALL.txt"), "INSTALL.txt")]
symlinks = {"Applications": "/Applications"}
background = os.path.join(here, "background.png")
window_rect = ((200, 120), (600, 488))
default_view = "icon-view"
show_status_bar = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
icon_size = 56
text_size = 12

# dmgbuild doesn't check that its ditto copies succeeded: check them before the image is closed.
def create_hook(mount_point, options):
    subprocess.check_call(
        ["/usr/bin/codesign", "--verify", "--deep", "--strict", os.path.join(mount_point, "Filee.app")]
    )
    if not filecmp.cmp(os.path.join(here, "INSTALL.txt"), os.path.join(mount_point, "INSTALL.txt"), shallow=False):
        raise RuntimeError("INSTALL.txt was not copied into the disk image")


icon_locations = {
    "Filee.app": (124, 194),
    "Applications": (269, 194),
    "INSTALL.txt": (124, 312),
}
