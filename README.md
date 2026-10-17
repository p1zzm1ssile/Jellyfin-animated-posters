<img src="Jellyfin.Plugin.LibraryCoverMaker/logo.png" width="96" alt="">

# Jellyfin Animated Posters

A Jellyfin plugin that keeps an animated cover on every library. The covers are drawn on the server, saved as each
library's Primary image, and made again on a schedule, so the home screen changes over time without anyone
uploading anything.

The project and its original look (the rounded white frame, the big serif name, the darkened picture) are by
p1zzm1ssile (Graham). It started as Library Cover Maker, a page in a container, and is now a plugin called Jellyfin
Animated Posters; the original look is still one click away in the style settings.

The plugin needs Jellyfin 12. It runs on a household server with 22 plugins installed; the client table below says
what has been checked on real devices.

## Install

No release is published yet, so the catalog stays empty until the first tagged version.

1. In Jellyfin, open **Dashboard**, then **Plugins**, then **Repositories**, and add this repository URL:

   ```
   https://raw.githubusercontent.com/p1zzm1ssile/Jellfin-animated-posters/main/manifest.json
   ```

2. Open the **Catalog**, install **Jellyfin Animated Posters**, and restart Jellyfin.
3. Open **Jellyfin Animated Posters** in the Dashboard menu.

## What it does

- Every movie, TV show, music video, home video and mixed library gets a cover with no setup. Libraries of music,
  books and photos can have a cover uploaded or pinned.
- A scheduled task, **Refresh library covers**, makes new covers. The default is every Sunday at 03:00 server time.
  Each cover looks different from the last unless you pinned something.
- Covers are made one at a time and yield to playback (a render waits, or runs at the lowest priority, while
  someone is watching). If a render fails or is stopped, the current cover stays.
- Every render makes an animated WebP and a still picture of the same cover (see Animated or still).

### Modes

- **Scenes** (the default): short clips from the videos themselves, picked by score for brightness, contrast, color,
  sharpness, little letterboxing and no cut inside the clip. Clips never come from an intro, outro, recap, preview or
  commercial that Jellyfin has marked, nor from the first or last tenth of a title. HDR videos are tone mapped so
  they are not darker than the rest. For TV shows, episodes from the first season are preferred. Scenes reads short
  pieces of the videos on each renewal, so it costs more disk reads than Art. A library with no playable video falls
  back to artwork, and then to the name-only cover.
- **Art**: a slow zoom through the backdrop images of the library's titles. A library with no artwork uses Scenes.
- **Manual**: covers are only made when you ask (Generate now or a change to the slots), never on a schedule.

Pinning is separate from the mode: a pinned library keeps its cover whatever the mode, until you go back to
automatic.

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
- Change the style: the name on the cover (default: the library name), font, text size, darkening, rounded frame,
  text shadow, length (2 to 30 seconds) and fade. The default font, Jellyfin, writes the name the way Jellyfin
  writes it on the library images it draws itself, in the name's own case and with no frame; the bundled fonts write
  it in uppercase, as the original page did.

Changing a library's Primary image in Jellyfin's own "Edit images" dialog counts as an upload and pins the library.

### Parental controls

A cover only shows titles that every user who can see the library is allowed to see under their parental controls
(maximum rating, unrated items, blocked tags). When a user is created, gets access to a library, or has their
parental controls changed, the covers that user can see are checked again, and one that breaks the rule is made
queued at once, with the reason on its card. A pinned or restored cover that the plugin composed is replaced the
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

Checked on 2026-10-17 on a Jellyfin 12.2 server, with an animated cover in the plugin's own format (partial
frames with transparency) and a still JPEG of the same cover:

| Client | Animated cover | Still cover |
| --- | --- | --- |
| Web (browser) | animates | shown |
| LG webOS app | animates | shown |
| Jellyfin Media Player (PC) | animates | shown |
| Jellyfin for Android (phone) | animates | shown |
| Jellyfin for Android TV (Fire TV) | animates | not checked yet |
| Samsung Tizen | not checked yet | not checked yet |
| iOS | not checked yet | not checked yet |

Use the still version for any client that does not animate.

## Known limits

- A title that mixes text directions or scripts is shaped as one run.
- A script with no installed font shows boxes. In the stock `jellyfin/jellyfin:12.2` image no Thai or
  Devanagari font is installed, and those titles were observed rendering as boxes.
- A cover is 640 by 360 at 12 frames a second, and its size grows with its length. Measured with jellyfin-ffmpeg 8.1.3
  on synthetic clips: a 30 second cover (the longest) came to 2.4 MB with one slot and 3.5 MB with five slots, and
  took 7 to 10 s of processor time; a noisy 720p source gave 2.6 MB and 23 s. The default of 6 seconds is a fifth of
  that. Real film grain was not measured, so a long cover of grainy video may be larger. A title shorter than 37.5
  seconds cannot hold the one 30 second clip of a single-slot cover and is replaced by another title.
- The plugin needs Jellyfin 12. Older versions are not supported.
- The unit tests run on Linux only: they need Fontconfig and fonts with Cyrillic, Hebrew and Arabic.

## If you ran the container

The old container version is retired. Stop and remove it. Covers you made with it can be uploaded on the plugin's
Dashboard page (**Upload cover** on the library's card). If you added custom CSS in Dashboard, Branding, Custom CSS
code to show a cover, remove it: the cover is now the library's Primary image and needs no CSS.

## Fonts

The default font, Jellyfin, is not shipped: it is the typeface Jellyfin asks the server's font configuration for when
it draws a library's image (bold sans-serif, DejaVu Sans Bold in the `jellyfin/jellyfin:12.2` image), asked for the
same way. Five fonts ship with the plugin, with their license texts (in
`Jellyfin.Plugin.LibraryCoverMaker/Rendering/Fonts/` and in the release zip), all under the SIL Open Font License:
Bodoni Moda, Cover Serif, Cinzel, Bebas Neue and Oswald. Cover Serif is a static ExtraBold instance of Playfair
Display by Claus Eggers Sorensen, under the SIL Open Font License, renamed as the license requires for modified
versions. Nothing is fetched from Google Fonts or any other service while the plugin runs.

## License

See [LICENSE](LICENSE).
