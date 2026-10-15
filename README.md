<img src="web/icon.png" width="96" alt="">

# Library Cover Maker

Make animated cover art for your Jellyfin libraries.

Add up to 5 videos or pictures, type the library name, and it builds an animated WebP that cuts between random moments from each file, with a rounded white frame and the name in a big serif font. Pictures get a slow zoom. Click **Make** again for a new set of random moments.

Everything runs in your browser. Your videos are never uploaded anywhere, not even to the container.

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
      - "8097:80"
    restart: unless-stopped
```

### Docker

```sh
docker run -d --name library-cover-maker -p 8097:80 --restart unless-stopped ghcr.io/p1zzm1ssile/library-cover-maker:latest
```

Then open `http://<your-server-ip>:8097` in Edge, Chrome or Firefox. Safari can't make WebP images.

Jellyfin uses port 8096, so the default here is 8097. Change it to any free port.

The fonts load from Google Fonts, so the computer you open the page on needs internet access. Without it the text falls back to Georgia.

## Make a cover

1. Drop in up to 5 videos (MP4 works best) or pictures.
2. Pick or type the library name, choose a font, and drag the preview to position the picture.
3. Set the animation length (2 to 12 seconds, shared between your files).
4. Click **Make animated cover**, then **Download animated cover**.

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
docker run -d -p 8097:80 library-cover-maker
```

Or open `web/index.html` straight in your browser. It works without a server.
