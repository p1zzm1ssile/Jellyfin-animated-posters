# Animated Film Posters changelog

All notable changes to Animated Film Posters (called Medic Posters until 1.3.2) are recorded here. The newest version is at the top.
Jellyfin Animated Posters, the library covers plugin in this repository, has its own changelog.

## [1.4.2] – 2026-10-26

### Fixed
- **Typing a title finds it in big libraries.** The list of titles holds the first 5000 from A to Z, so on servers with more, such as ones with IPTV libraries, a title further down the alphabet like Solo Leveling showed "Nothing matches what you typed". What you type is now also searched for on the server, so every title can be found.
- **No IPTV titles in the lists.** Libraries left out under Libraries, such as IPTV ones, and streams (web links and .strm files) are no longer offered when picking titles, since there's nothing to cut moments from. This also keeps the lists much shorter.

## [1.4.1] – 2026-10-25

1.4.0 was never released, so everything below is new in this version.

### Added
- **A list of titles to pick from.** Next to each box for choosing a film or series there's **Show list**, which lists them all, and typing in the box narrows the list as you type.
- **Type the time of a moment.** Each picked moment has a box for its start time, such as `1:23:45` or `42:10`, as well as the slider. A time past the end of the film stops at its last second.
- **Prefer action and fight scenes.** A new setting, for posters and covers separately, that tries three spots for each moment in a video and keeps the ones where the picture moves most, which are mostly action and fights. It makes each poster a little slower. Moments you picked yourself are kept.

### Changed
- **Finding a title is quicker and finds more.** The titles are fetched once and searched on the page, so the list answers as you type. A title is found when every word you type is anywhere in its name or year, in any order, ignoring capitals, accents and punctuation, so `knight dark` finds The Dark Knight and `amelie` finds Amélie.

### Fixed
- **A series poster gets all its moments.** A series with fewer episodes than moments, such as three episodes and six moments, used to get one moment per episode and so fewer than asked for. It now takes the rest from the same episodes at other spots. Library covers with only a few videos in them do the same.

## [1.3.3] – 2026-10-23

### Changed
- **Medic Posters is now called Animated Film Posters**, in the catalogue, the Dashboard menu, its page and Scheduled Tasks. It is the same plugin with the same ID, so it updates in place and keeps your posters, covers and settings.

## [1.3.2] – 2026-10-20

### Changed
- **Medic Posters has moved** from the Jellyfin Medic repository to [Jellyfin Animated Posters](https://github.com/p1zzm1ssile/Jellyfin-animated-posters). It keeps its name and ID, so servers that have it keep their posters and settings. Add the new repository's catalogue in Jellyfin to get this version and the ones after it.

### Added
- **A warning about low-end devices** on the settings page and in the README: animated posters for a whole library aren't recommended on Fire TV sticks, smart TVs and other low-end devices, while animated library covers are fine.

## [1.3.1] – 2026-10-18

### Added
- **Time left.** While posters or covers are being made, the progress line says roughly how long the rest will take, once one has been made to time.

### Fixed
- **Put back the original posters** carries on to the end on the server. Before, reloading or leaving the page, or a reverse proxy giving up waiting on a big library, stopped it partway and left the rest of the animated posters in place. The page now shows how far it has got, and one title that can't be put back no longer stops the others.

## [1.3.0] – 2026-10-17

### Added
- **Your own text on a cover or poster.** Under **Your own text and moments**, choose a library or a title and type the text to show instead of its name.
- **Covers for IPTV libraries.** A library's cover can take its random moments from another library, so an IPTV library of streams can get a cover cut from your films library and still show its own name, or any text you like. It gets a cover even when it's unticked under **Libraries**.
- **Pick the films and the moments.** Pick up to six films or series for a library cover, or up to six moments of a film for its poster, and slide each one to where it should start, with a still of that frame to see what you'll get. A moment can stay random, and a series gives a moment from a random episode.
- **Ten fonts**: Bodoni Moda, Playfair Display, Cinzel, Cormorant Garamond, Abril Fatface, Bebas Neue, Anton, Oswald, Montserrat and Lobster. All are free fonts under the SIL Open Font License, and they travel inside the plugin.
- **Text size** from 60% to 200% of the size that fits the title, and **text position** at the top, middle or bottom.
- **Shadow on or off**: the shadow under the letters and the darkened band behind them can each be turned off.
- **Fades**: through black between moments, and out at the end (and in at the start, so the loop starts again from black), with how long each fade takes.
- **Preview it** next to a library's or title's own text and moments, then **Use this one**.

Posters and covers each have their own font, text and fade settings. Ones already made keep their old look until they're made again.

### Fixed
- Medic Posters is back in this repository and the catalogue, so servers that have it get updates again.

## [1.2.0] – 2026-10-17

### Added
- **Leave libraries out.** Every library is ticked; untick one, such as an IPTV library, and nothing in it gets a poster and it gets no cover. Libraries added later are included. The libraries chosen in 1.0 and 1.1 are kept: the ones not chosen show unticked.
- **How much space they'll take.** The page says how much the animated posters and covers use now, and for each kind how many are left to make and roughly what they'll come to, and what making them all again would. It works the size out from the settings on screen, so it changes as you change them, and from the ones made so far.
- **Posters and library covers on their own.** **Make posters now** and **Make library covers now**, and **Make all posters again** and **Make all covers again**, each do only their own kind. The nightly task still does both, covers only when **Make library covers each night too** is on.
- **One at a time.** Pick a film or series, or a library, for the preview, then **Use this one** to put that preview on it.

### Changed
- **Library covers have their own settings**: moments, how long each plays, frames per second, colours, the name and the frame. They start at the defaults, so if you changed the poster settings in 1.1, set the cover ones to match.

### Fixed
- **The preview uses the settings on screen.** It used the saved ones, so a change didn't show until it was saved.

## [1.1.0] – 2026-10-17

### Added
- **Every library with videos in it, not only films.** TV, home video, music video and mixed libraries are offered too. A series gets a poster cut from moments of a few of its episodes, and home videos and music videos get one like a film.
- **Animated library covers.** Each library gets a wide 16:9 cover, the shape of Jellyfin's library cards, cut from moments of a few random things in it, with the library's name on it, like the Library Cover Maker's covers. It's a GIF too, so it stays animated on the home screen. Turn it off, or pick its size (320 × 180 up to 640 × 360), under **Library covers**. **Put back** and **Put back the original posters** bring back a library's own picture as well.
- **Preview a library cover** next to **Preview a poster**.

### Changed
- Streams (such as IPTV films added as .strm files) still get no poster: cutting moments from one opens several connections to the provider at once, which many providers don't allow. They're listed with the reason, as before.

## [1.0.0] – 2026-10-17

### Added
- **Animated posters for your films.** Each film gets a short animated poster cut from a few moments of the film (15% to 75% of the way through, past the opening titles and before the credits), with its title in capitals in Bodoni Moda and a rounded white frame, like the Library Cover Maker's library covers.
- **They stay animated in the apps.** Posters are GIFs because Jellyfin never resizes a GIF: other formats are turned into a still picture before an app sees them. They move in the Jellyfin website and Jellyfin Desktop, and in apps that can play a GIF poster.
- **Your own posters are kept.** Nothing in your media folders is changed or deleted. Medic Posters keeps its posters in its own folder and remembers each film's own poster. **Put back** on any poster, or **Put back the original posters** for all of them, brings them back.
- **New films get one each night** from the **Make animated posters** scheduled task (4 am to begin with; Jellyfin Medic can move it into your quiet hours). A poster changed by hand, or by a metadata refresh that replaces images, is left alone. **Make them all again** redoes every film, those included.
- **A preview** of a random film with the current settings, without changing the film, with its file size.
- **Settings** for which film libraries, the size (160 × 240 up to 300 × 450), how many moments and how long each plays, frames per second and the number of colours (all of which change the file size), and whether to draw the title and the frame.
- **HDR films** are converted to normal colour first, so their posters aren't washed out.
- **Films it can't make a poster for** (streams, disc folders, missing files, or files FFmpeg can't read) are listed with the reason.

Thanks @camarigor for the idea of animated posters as a Jellyfin plugin.
