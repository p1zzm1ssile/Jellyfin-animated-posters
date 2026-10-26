<img src="Jellyfin.Plugin.LibraryCoverMaker/logo.png" width="96" alt="">

# Jellyfin Animated Posters

A Jellyfin plugin that keeps an animated cover on every library. The covers are drawn on the server, saved as each
library's Primary image, and made again on a schedule, so the home screen changes over time without anyone
uploading anything.

The project and its original look (the rounded white frame, the big serif name, the darkened picture) are by
p1zzm1ssile (Graham). It started as Library Cover Maker, a page in a container, and is now a plugin called Jellyfin
Animated Posters; the original look is still one click away in the style settings.

This repository also holds **[Animated Film Posters](#animated-film-posters)** (formerly Medic Posters), animated posters for every film
and series, which moved here from Jellyfin Medic.

The plugin needs Jellyfin 12. It runs on a household server with 22 plugins installed; the client table below says
what has been checked on real devices.

## Install

Jellyfin Animated Posters has no release yet, so the catalog only lists Animated Film Posters until its first tagged
version.

1. In Jellyfin, open **Dashboard**, then **Plugins**, then **Repositories**, and add this repository URL:

   ```
   https://raw.githubusercontent.com/p1zzm1ssile/Jellfin-animated-posters/main/manifest.json
   ```

2. Open the **Catalog**, install **Jellyfin Animated Posters** and/or **Animated Film Posters**, and restart Jellyfin.
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

## Animated Film Posters

An animated poster for every film and series, and an animated cover for every library, in the style of the
[Library Cover Maker](https://github.com/p1zzm1ssile/Library-Cover-Maker)'s library covers. Set up under
**Plugins → Animated Film Posters** (admins only); everyone sees the posters.

> [!CAUTION]
> **Animated posters for your whole library are not recommended on Fire TV sticks, smart TVs and other low-end
> devices.** They have a small processor and little memory, and a screen full of animated posters can make them slow,
> stutter or crash. Animated library covers are fine, as there are only a few of them. Try a few posters with **Use
> this one** on your slowest device before making them all. The plugin's page shows this warning too.

- **Made from the video itself.** Each poster cuts between a few moments of the film, spread from 15% to 75% of the
  way through so it skips the opening titles and the credits (for a series, a moment from each of a few episodes, or more than one from each when it has fewer episodes than moments),
  with the title in capitals and a rounded white frame. Choose from ten fonts, the text's size and position (top,
  middle or bottom), its shadow and the dark band behind it, and fades between moments or at the end. HDR films are
  converted to normal colour first. It uses Jellyfin's own FFmpeg, so there's nothing else to install.
- **They stay animated.** Jellyfin resizes posters before sending them to an app, which turns an animated WebP into a
  still picture, but it never resizes a GIF. So the posters are small GIFs, and they move in the Jellyfin website,
  Jellyfin Desktop and the apps that can play a GIF poster. Apps that can't show the first frame.
- **Library covers too.** Each library with videos in it (films, TV, home videos, music videos) gets a wide cover,
  the shape of Jellyfin's library cards, cut from moments of a few random things in it with the library's name on it.
  It's a GIF as well, so it moves on the home screen. Covers have their own settings, separate from the posters'.
- **Your own posters are kept.** Nothing in your media folders is changed or deleted: the GIFs live in the server's
  data folder, and each film's own poster is remembered. **Put back** under any poster, or **Put back the original
  posters**, brings them back. Putting them all back carries on on the server even if you leave the page, which shows
  how far it has got.
- **New films get one each night** from the **Make animated posters** scheduled task. A poster you change yourself,
  or that a metadata refresh replaces, is left alone. **Make all posters again** and **Make all covers again** redo
  them all. While a run is going, the page shows which one it's on and roughly how much time is left.
- **A preview** shows a poster or a cover with the settings on screen, saved or not, and its size, without changing
  anything. Pick the film, series or library, or let it choose one. **Use this one** puts that preview on it, so you
  can do them one at a time.
- **Posters and covers separately.** **Make posters now** and **Make library covers now** each make only their own
  kind, and each says how many there are to make and roughly how much space they'll take, worked out from the
  settings on screen and the ones made so far.
- **Action and fight scenes.** Tick **Prefer action and fight scenes** (posters and covers each have their own box)
  and, instead of evenly spaced moments, it tries several spots in each video and keeps the ones where the picture
  moves most. Fast movement and quick cuts are mostly action and fights. Each poster takes a little longer to make.
  Moments you picked yourself are kept as they are.
- **Leave libraries out**, such as IPTV ones, by unticking them. Libraries you add later are included.
- **Your own text and moments.** Give any library's cover or title's poster its own text, and pick the films and the
  exact moments it's cut from, with a still of each frame. Pick a title from **Show list** or by typing any words of
  its name (only real video files are offered, never streams or libraries left out such as IPTV ones; typing finds any title, and **Show list** shows the first 5000 from A to Z), and set each moment with the slider or by typing its time, such as `1:23:45`. For a series, choose a random episode or a particular one, and then its time. A library of streams, such
  as IPTV, can take its cover from another library's films and keep its own name.

Animated Film Posters works on Jellyfin 12.0 and later. What's new in each version is in
[its changelog](Jellyfin.Plugin.MedicPosters/CHANGELOG.md).

### Size

Because Jellyfin sends GIF posters at their full size, every poster on a page is downloaded whole. The default,
240 × 360 with three one-second moments at 8 frames a second and 96 colours, comes to anything from under 100 KB for
a plain picture to about 750 KB for a busy one, such as a field of grass. The settings page has smaller sizes, fewer
moments and colours, and the preview shows what each choice comes to.

### Setting it up

1. Add this repository's catalogue (see [Install](#install)), install **Animated Film Posters** and restart Jellyfin.
2. Open **Plugins → Animated Film Posters**, untick any library to leave alone, and press **Preview a poster** or **Preview
   a library cover** to try the settings.
3. Press **Make posters now**, or wait for the nightly run.

### If you had Medic Posters from Jellyfin Medic

Animated Film Posters used to be called Medic Posters and lived in the [Jellyfin Medic](https://github.com/p1zzm1ssile/Jellyfin-Medic) repository. It
keeps its ID here, so add this repository's catalogue and the next version arrives as an ordinary update,
with your posters, covers and settings kept. Versions up to 1.3.1 came from Jellyfin Medic; 1.3.2 and later come
from here, and 1.3.3 renamed it.

### Building and releasing

Requires the .NET 10 SDK.

```
dotnet publish Jellyfin.Plugin.MedicPosters/Jellyfin.Plugin.MedicPosters.csproj -c Release -o publish/MedicPosters
```

It can also be built for Jellyfin 10.11, where it targets .NET 9 (the .NET 10 SDK builds that too). Releases are only
built for Jellyfin 12, so a 10.11 server needs one built by hand:

```
dotnet publish Jellyfin.Plugin.MedicPosters/Jellyfin.Plugin.MedicPosters.csproj -c Release -o publish/MedicPosters-10.11 -p:JellyfinTarget=10.11
```

To release a version, for example 1.4.4: raise `<Version>` in the project file to `1.4.4.0`, give
[its changelog](Jellyfin.Plugin.MedicPosters/CHANGELOG.md) a `## [1.4.4]` section, merge, then tag the merged commit
`posters-v1.4.4` (or make the release with that tag on GitHub's Releases page). The **Release Animated Film Posters**
workflow runs the tests, builds the zip, fills in the release and adds the version to `manifest.json`.

## If you ran the container

The old container version is still in place, however if you are using it for only Jellfin the plugin it a easier option and can do alot more. Covers you made with it can be uploaded on the plugin's
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
