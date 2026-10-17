<img src="web/icon.png" width="96" alt="">

# Library Cover Maker

Make animated cover art for your Jellyfin libraries, and animated posters for your films and series.

Add up to 5 videos or pictures, or pick a whole folder and let it choose 5 at random. Type the library name and it builds an animated WebP or GIF that cuts between moments from each file, with a rounded white frame and the name in a big serif font. Switch to **Poster** for a portrait poster in the same style. Choose the moment for each video yourself, or leave it random. Pictures get a slow zoom.

Use files from the computer you're on, or from your media folders on the server. The covers are made in your browser. Nothing is sent anywhere unless you use **Send to Jellyfin**, which sends it to your own Jellyfin server.

## Install

### Unraid

Search for **Library Cover Maker** in Community Applications, or add the template from [p1zzm1ssile/unraid-templates](https://github.com/p1zzm1ssile/unraid-templates).

To use **Send to Jellyfin**, edit the container, click **Add another Path, Port, Variable, Label or Device**, choose **Variable**, and add one with the key `JELLYFIN_URL` and your Jellyfin address as the value, such as `http://192.168.1.10:8096`.

### Docker Compose

```yaml
services:
  library-cover-maker:
    image: ghcr.io/p1zzm1ssile/library-cover-maker:latest
    container_name: library-cover-maker
    ports:
      - "8097:8097"
    volumes:
      - /path/to/media:/media:ro
    environment:
      # Optional: your Jellyfin address, for Send to Jellyfin
      - JELLYFIN_URL=http://192.168.1.10:8096
    restart: unless-stopped
```

### Docker

```sh
docker run -d --name library-cover-maker -p 8097:8097 -v /path/to/media:/media:ro -e JELLYFIN_URL=http://192.168.1.10:8096 --restart unless-stopped ghcr.io/p1zzm1ssile/library-cover-maker:latest
```

Then open `http://<your-server-ip>:8097` in Edge, Chrome or Firefox. Safari can't make WebP images, but it can make GIFs.

Jellyfin uses port 8096, so the default here is 8097. To use another port, change the first number (`8097:8097` → `9000:8097`).

`JELLYFIN_URL` is optional. Set it to the address you use to open Jellyfin, and the page can send covers and posters straight to it (see [Send to Jellyfin](#send-to-jellyfin)). If Jellyfin runs in Docker on the same network as this container, its container name works too, such as `http://jellyfin:8096`. Leave it out and everything else works the same.

Map your media folder to `/media`, read-only. You can map several folders under it, such as `/media/movies` and `/media/tv`. They appear under the **Server media** button. Leave it out if you only want to use files from your computer.

The fonts load from Google Fonts, so the computer you open the page on needs internet access. Without it the text falls back to Georgia.

## Make a cover

1. Drop in up to 5 videos (MP4 works best) or pictures, or click **Pick a folder** to have 5 chosen at random from a folder on your computer. **Server media** browses the folders mapped to `/media`: click a file to add it, or **Use this folder** for 5 random files from that folder and its subfolders. **New random files** picks a different 5, and with **New random files each time I click Make** ticked, every cover uses a fresh set.
   **Videos only when picking from a folder** is ticked by default, so posters and other pictures in your media folders are skipped.
2. Each video in the list has its own slider and a small preview of the frame. Drag the slider to choose the moment that video's clip starts at. Click **Random** to go back to a random moment.
3. Pick or type the library name, choose a font, and drag the preview to position the picture.
4. Set the animation length (2 to 12 seconds, shared between your files), and choose WebP or GIF.
5. Click **Make animated cover**, then **Download animated cover**.

Keep the file under about 5 MB so your home screen loads quickly. Shorter animations make smaller files. The page shows the size of each file it makes.

## Make a poster

Click **Poster** at the top of the right-hand panel. It works the same way, but makes a portrait 2:3 poster with the title near the bottom, split over up to 3 lines if it's long. Your library cover's name is kept for when you switch back.

## WebP or GIF?

- **WebP** is smaller and looks better. It's animated in the Jellyfin web page, but the Jellyfin apps (Android TV, phones, and so on) show it as a still picture, because Jellyfin resizes every picture it sends to them.
- **GIF** is the one picture type Jellyfin sends untouched, so a GIF **stays animated in the apps too**. GIFs are made a bit smaller (480×270 for covers, 320×480 for posters, 10 frames a second) to keep the file size sensible.

## Send to Jellyfin

With `JELLYFIN_URL` set, the page has a **Send to Jellyfin** section under the preview.

1. In Jellyfin, go to **Dashboard → API Keys**, click **+**, and give it a name like `Cover Maker`.
2. Paste the key into the page and click **Connect**. The key is saved in your browser only.
3. For a library cover: choose the library. Its name is filled in for you. Make the cover, then click **Send cover to this library**. It becomes the library's main image, so there's no CSS to add.
4. For a poster: switch to **Poster**, search for the film or series, and click it. Its title is filled in. Make the poster, then click **Send poster to this title**.

Make it as a GIF if you want it to move in the Jellyfin apps. The page talks to Jellyfin through the container, so you don't need to change any Jellyfin settings.

If the page says it can't reach Jellyfin, check that `JELLYFIN_URL` is the address you'd type in a browser, including `http://` and the port, then restart the container.

## Use it in Jellyfin by hand

If you'd rather not use Send to Jellyfin, or want the cover to only animate when you hover over it:

1. In Jellyfin, right-click the library on the home screen → **Edit images** → upload the file with type **Logo**.
2. Find the library's ID: open the library and copy the `topParentId` value from the address bar.
3. Add this to **Dashboard → General → Custom CSS code**, with your ID in both places:

```css
.card[data-id="YOUR_LIBRARY_ID"] .cardImageContainer {
  background-image: url("/Items/YOUR_LIBRARY_ID/Images/Logo") !important;
}
```

For an animation that only plays when you hover over the card, use `.card[data-id="YOUR_LIBRARY_ID"]:hover .cardImageContainer` as the first line instead.

Add one block per library, then press Ctrl+F5.

Leave `maxWidth` and `quality` off the image address: if Jellyfin resizes the image, it can lose its animation.

## Build it yourself

```sh
docker build -t library-cover-maker .
docker run -d -p 8097:8097 -v /path/to/media:/media:ro library-cover-maker
```

Or open `web/index.html` straight in your browser. It works without a server.
