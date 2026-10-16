<img src="Jellyfin.Plugin.LibraryCoverMaker/logo.png" width="96" alt="">

# Library Cover Maker

A Jellyfin plugin that keeps an animated cover on every library. The covers are drawn on the server, saved as each
library's Primary image, and made again on a schedule, so the home screen changes over time without anyone
uploading anything.

The project and its look (the rounded white frame, the big serif name, the darkened picture) are by p1zzm1ssile
(Graham). It started as a page in a container and is now a plugin.

The plugin needs Jellyfin 12. It is still being built: the sections below describe what it is designed to do, and
the client table says what has been checked on real devices (nothing yet).

## Install

No release is published yet, so the catalog stays empty until the first tagged version.

1. In Jellyfin, open **Dashboard**, then **Plugins**, then **Repositories**, and add this repository URL:

   ```
   https://raw.githubusercontent.com/p1zzm1ssile/Library-Cover-Maker/main/manifest.json
   ```

2. Open the **Catalog**, install **Library Cover Maker**, and restart Jellyfin.
3. Open **Library Cover Maker** in the Dashboard menu.

## What it does

- Every movie, TV show, music video, home video and mixed library gets a cover with no setup. Libraries of music,
  books and photos can have a cover uploaded or pinned.
- A scheduled task, **Refresh library covers**, makes new covers. The default is every Sunday at 03:00 server time.
  Each cover looks different from the last unless you pinned something.
- Covers are made one at a time and yield to playback (a render waits, or runs at the lowest priority, while
  someone is watching). If a render fails or is stopped, the current cover stays.
- Every render makes an animated WebP and a still picture of the same cover (see Animated or still).

### Modes

- **Art** (the default): a slow zoom through the backdrop images of the library's titles.
- **Scenes**: short clips from the videos themselves, picked by score for brightness, contrast, color, sharpness,
  little letterboxing and no cut inside the clip. Clips never come from an intro, outro, recap, preview or
  commercial that Jellyfin has marked, nor from the first or last tenth of a title. HDR videos are tone mapped so
  they are not darker than the rest. For TV shows, episodes from the first season are preferred.
- **Manual**: you uploaded or pinned a cover, and the library leaves the rotation.

A library with no artwork, such as home videos, uses Scenes by itself.

### Manual controls

The Dashboard page shows one card per library with its live cover, its mode, when it was made and when it will be
renewed. From a card or the editor below the grid you can:

- **Generate now** for one library, or **Generate all now** for all of them.
- **Upload a cover**, still or animated. The library becomes Manual (pinned). The plugin cannot check what an
  uploaded cover shows against users' parental controls, so that is up to you.
- **Pin** the current cover so it is kept.
- **Restore** an earlier cover from the history: the last 8 covers, plus the Primary image the library had before
  the plugin first replaced it. A restored cover is pinned.
- **Lock slots.** A cover has 5 slots. Search the library for a title, pick the moment with a slider that shows the
  frame (or one of the title's backdrops in Art mode), and lock the slot. Locked slots keep your choice and free
  slots get new picks on every render.
- **Back to automatic**, which unpins the library.
- Change the style: the name on the cover (default: the library name in uppercase), font, text size, darkening,
  rounded frame, text shadow, length (2 to 12 seconds) and fade.

Changing a library's Primary image in Jellyfin's own "Edit images" dialog counts as an upload and pins the library.

### Parental controls

A cover only shows titles that every user who can see the library is allowed to see under their parental controls
(maximum rating, unrated items, blocked tags). When a user is created, gets access to a library, or has their
parental controls changed, the covers that user can see are checked again, and one that breaks the rule is made
again right away, with the reason on its card. A pinned or restored cover that the plugin composed is replaced the
same way, and a locked title that breaks the rule is unlocked. Covers you uploaded are left alone.

When fewer titles pass the rule than there are slots, the cover uses fewer slots. With no title at all it shows
only the library name on a neutral background.

### Animated or still

One setting decides whether each library's Primary image is the animated or the still version of its cover
(animated by default). Changing it applies every library's current cover again in the other version without
rendering. Use the still version for clients that cannot show an animation.

The plugin answers the image requests for an animated library cover itself, so Jellyfin does not log
`Cannot decode images with multiple frames` when a client asks for a resized copy.

## Clients

How each client shows an animated Primary image is checked on real devices before release. Until then every
client is listed as not checked.

| Client | Animated cover | Still cover |
| --- | --- | --- |
| Web | not checked yet | not checked yet |
| Android TV and Fire TV | not checked yet | not checked yet |
| Samsung Tizen | not checked yet | not checked yet |
| Android phone | not checked yet | not checked yet |
| iOS | not checked yet | not checked yet |
| Others | not checked yet | not checked yet |

## Known limits

- A title that mixes text directions or scripts is shaped as one run.
- A script with no installed font shows boxes. In the stock `jellyfin/jellyfin:12.2` image no Thai or
  Devanagari font is installed, and those titles were observed rendering as boxes.
- The plugin needs Jellyfin 12. Older versions are not supported.
- The unit tests run on Linux only: they need Fontconfig and fonts with Cyrillic, Hebrew and Arabic.

## If you ran the container

The old container version is retired. Stop and remove it. Covers you made with it can be uploaded on the plugin's
Dashboard page (**Upload cover** on the library's card). If you added custom CSS in Dashboard, Branding, Custom CSS
code to show a cover, remove it: the cover is now the library's Primary image and needs no CSS.

## Fonts

Five fonts ship with the plugin, with their license texts (in `Jellyfin.Plugin.LibraryCoverMaker/Rendering/Fonts/`
and in the release zip), all under the SIL Open Font License: Bodoni Moda, Cover Serif, Cinzel, Bebas Neue and
Oswald. Cover Serif is a static ExtraBold instance of Playfair Display by Claus Eggers Sorensen, under the SIL Open
Font License, renamed as the license requires for modified versions. Nothing is fetched from Google Fonts or any
other service while the plugin runs.

## License

See [LICENSE](LICENSE).
