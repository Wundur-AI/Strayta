# dmgbuild settings for Strayta's disk image: a fixed 660×400 window with the background art, Strayta on the left and
# an Applications shortcut on the right (packaging/macos/make-dmg-background.cs draws the arrow between them).
# tools/package-mac.sh passes the app and background paths with -D app=... -D background=...
import os

app = defines["app"]
format = "UDZO"
files = [app]
symlinks = {"Applications": "/Applications"}
background = defines["background"]

window_rect = ((200, 140), (660, 400))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
icon_size = 112
text_size = 13
icon_locations = {
    os.path.basename(app): (180, 190),
    "Applications": (480, 190),
}
