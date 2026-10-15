<img src="web/icon.png" width="96" alt="">

# Library Cover Maker

Make animated cover art for your Jellyfin libraries.

Add up to 5 videos or pictures, or pick a whole folder and let it choose 5 at random. Type the library name and it builds an animated WebP that cuts between moments from each file, with a rounded white frame and the name in a big serif font. Choose the moment for each video yourself, or leave it random. Pictures get a slow zoom.

Use files from the computer you're on, or from your media folders on the server. The covers are made in your browser, and nothing is sent anywhere else.

## Install

### Unraid

Search for **Library Cover Maker** in Community Applications, or add the template from [p1zzm1ssile/unraid-templates](https://github.com/p1zzm1ssile/unraid-templates).

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
    restart: unless-stopped
```

### Docker

```sh
docker run -d --name library-cover-maker -p 8097:8097 -v /path/to/media:/media:ro --restart unless-stopped ghcr.io/p1zzm1ssile/library-cover-maker:latest
```

Then open `http://<your-server-ip>:8097` in Edge, Chrome or Firefox. Safari can't make WebP images.

Jellyfin uses port 8096, so the default here is 8097. To use another port, change the first number (`8097:8097` → `9000:8097`).

Map your media folder to `/media`, read-only. You can map several folders under it, such as `/media/movies` and `/media/tv`. They appear under the **Server media** button. Leave it out if you only want to use files from your computer.

The fonts load from Google Fonts, so the computer you open the page on needs internet access. Without it the text falls back to Georgia.

## Make a cover

1. Drop in up to 5 videos (MP4 works best) or pictures, or click **Pick a folder** to have 5 chosen at random from a folder on your computer. **Server media** browses the folders mapped to `/media`: click a file to add it, or **Use this folder** for 5 random files from that folder and its subfolders. **New random files** picks a different 5, and with **New random files each time I click Make** ticked, every cover uses a fresh set.
   **Videos only when picking from a folder** is ticked by default, so posters and other pictures in your media folders are skipped.
2. Each video in the list has its own slider and a small preview of the frame. Drag the slider to choose the moment that video's clip starts at. Click **Random** to go back to a random moment.
3. Pick or type the library name, choose a font, and drag the preview to position the picture.
4. Set the animation length (2 to 12 seconds, shared between your files).
5. Click **Make animated cover**, then **Download animated cover**.

Keep the file under about 5 MB so your home screen loads quickly. Shorter animations make smaller files.

## Use it in Jellyfin

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
