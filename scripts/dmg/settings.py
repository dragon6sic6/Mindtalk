# dmgbuild settings for Mindtalk's DMG window.
#   dmgbuild -s scripts/dmg/settings.py -D app=path/to/Mindtalk.app -D background=bg.tiff Mindtalk out.dmg
import os

app = defines["app"]  # noqa: F821 — provided by dmgbuild
app_name = os.path.basename(app)

# The app's own icon on the mounted volume.
icns = os.path.join(app, "Contents", "Resources", "AppIcon.icns")
if os.path.exists(icns):
    icon = icns

format = "UDZO"
filesystem = "APFS"
files = [app]
symlinks = {"Applications": "/Applications"}
hide_extension = [app_name]

background = defines["background"]  # noqa: F821
# The frame includes the title bar: 400 pt of background plus 28.
window_rect = ((200, 140), (660, 428))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
icon_size = 112
text_size = 13
icon_locations = {
    app_name: (180, 232),
    "Applications": (480, 232),
    # Far outside the window, for Finders set to show hidden files.
    ".background.tiff": (900, 900),
    ".VolumeIcon.icns": (1000, 900),
    ".fseventsd": (1100, 900),
}
