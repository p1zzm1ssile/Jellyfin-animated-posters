# Changelog

All notable changes to Jellyfin Animated Posters are recorded here. The newest version is at the top.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [semantic
versioning](https://semver.org): given a version X.Y.Z, Z changes for fixes, Y for new features, X for changes that
break compatibility.

To release a version, for example 1.0.0:

1. Rename the Unreleased heading to the version in brackets and the date, for example `[1.0.0] (2026-10-20)`, and put a
   new empty Unreleased section above it.
2. Raise `<Version>` in `Jellyfin.Plugin.LibraryCoverMaker/Jellyfin.Plugin.LibraryCoverMaker.csproj` to the same version
   with a fourth part of 0 (`1.0.0.0`), and `<AssemblyVersion>` and `<FileVersion>` with it.
3. Merge, then tag the merged commit `v1.0.0`.

The release workflow refuses a tag whose version is not the project's `<Version>` (without its fourth part), a tag that
is not on main, and a version with no section here. It publishes that section as the release notes and as the changelog
in `manifest.json`.

## [Unreleased]

### Added
- **An animated cover for every library, made for you.** Once the plugin is on, each movie, TV show, music video, home
  video and mixed library gets a cover with no setup: it is drawn on the server and saved as the library's Primary
  image. A scheduled task, Refresh library covers, makes new ones every Sunday at 03:00 by default, so the covers change
  over time. Libraries of music, books and photos can have a cover uploaded or pinned.
- **Scenes and Art modes.** Scenes, the default, cuts between short clips from the videos themselves, picked by score
  for brightness, contrast, color, sharpness, little letterboxing and no cut inside the clip, and never from an intro,
  outro, recap, preview or commercial that Jellyfin has marked, nor from the first or last tenth of a title. HDR videos
  are tone mapped, so their clips are not darker than the rest. Scenes reads short pieces of the videos on each
  renewal, so it costs more disk reads than Art, which moves through the backdrop images of the library's titles with
  a slow zoom. A library with no artwork, such as home videos, uses Scenes by itself, and one with no playable video
  uses Art.
- **Your own covers.** Upload a cover, still or animated, pin the one a library has so it leaves the rotation, or hand
  the library back to automatic. A cover you uploaded or pinned is not replaced by the weekly task.
- **History.** Each library keeps its last 8 covers and the Primary image it had before the plugin first replaced it.
  Click one to restore it; a restored cover is pinned. A cover the plugin made that now shows a title some user who can
  see the library may not see is not restored, and the page says why.
- **Five slots you can lock.** Search the library for a title, choose the moment of a video with a slider that shows the
  frame (or one of the title's backdrops in Art mode), and lock the slot. Locked slots keep your choice, and free slots
  get new picks every time.
- **Covers that follow parental controls.** A cover only shows titles that every user who can see the library may see.
  When a user is created, gets access to a library or has their parental controls changed, the covers that user can see
  are checked again, and one that breaks the rule is made again right away with the reason on its card. Pinned covers
  made by the plugin are replaced too; covers you uploaded are left alone, because the plugin cannot know what they
  show. Every 15 minutes the plugin also checks every library's cover again, so a change no event announces (a title's
  rating changed by a metadata refresh, or a user saved by another plugin) or a replacement that failed is caught too.
- **A Dashboard page.** One card per library shows its live cover, its mode (Auto: art, Auto: scenes or Manual: pinned),
  when it was made and when it will be renewed, with buttons to generate it now, edit it or upload a cover. A failed
  render shows its reason on the card. The editor below the grid has the mode, content rules, style, a renewal override,
  the history and the slots, and "Generate all now" sits at the top.
- **Animated or still.** Every render makes both an animated WebP and a still picture of the same cover. One setting
  decides which of the two becomes each library's Primary image (animated by default), for clients that cannot play an
  animation; changing it applies the current covers again without rendering them.
- **The Jellyfin look, by default.** The library name is written the way Jellyfin writes it on the library images it
  draws itself: in Jellyfin's own bold sans-serif font (whatever the server's font configuration gives Jellyfin, so
  nothing ships for it), in the name's own case, at the same size and on the same line, with no frame, over a lightly
  darkened picture and with a soft shadow, so it stays readable over moving pictures. A library whose style still had
  the old defaults (Bodoni Moda at 120 in the white frame) moves to the Jellyfin look; one whose font, size or frame
  was changed keeps it.
- **The look of the original page, as an option.** A rounded white frame and the library name in uppercase
  in one of five bundled fonts, with text size, darkening, shadow, length (2 to 30 seconds) and fade. The bundled fonts
  ship with the plugin under the SIL Open Font License, with their license texts, and nothing is fetched from Google
  Fonts or any other service while it runs. A title in a script the chosen font lacks is drawn with an installed font
  that has the glyphs.
- **Gentle on the server.** Covers are rendered one at a time at low priority, and the automatic ones wait while someone
  is watching; a cover you ask for with Generate now starts at once. A failed or stopped render leaves the current cover
  untouched.
