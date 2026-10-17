#!/usr/bin/env bash
# Makes the sample media the end-to-end tests run on. Everything is made by ffmpeg, so nothing binary
# is checked in.
#
#   tests/e2e/make-media.sh [MEDIA_DIR]    default: tests/e2e/.work/media (files already there are overwritten)
#
# Needs ffmpeg with libx264, libx265, libvpx-vp9, libopus, aac and the zscale filter.
#
# What it makes (e2e.mjs points one library at each folder, and checks that Jellyfin read it all):
#
#   movies/   13 films, each in its own "Name (Year)" folder with an NFO that sets <mpaa>, <rating> and
#             <lockdata>. All are 640x360 test pattern with a sine tone, 30 s long except Live.
#               Film 1 to Film 8   odd numbers are rated PG, even numbers R, and each has a fanart.jpg
#                                  backdrop. Community ratings fall from 9.0 to 6.9 as the number grows.
#               Dark Start         black for the first 30 %, the same picture blurred (gblur sigma 20)
#                                  until 60 %, then sharp and colourful. For "a scene never starts in the
#                                  dark or blurred part".
#               HDR Film           10-bit HEVC, PQ, BT.2020: the same light picture as SDR Twin.
#               SDR Twin           8-bit H.264, BT.709. For "an HDR cover is as bright as its SDR twin"; the
#                                  tolerance to use and what it was measured on are at the pair below.
#               HDR Untagged       the PQ BT.2020 picture of HDR Film with no colour tags at all, so Jellyfin
#                                  takes it for SDR and the renderer does not tone map it: the cover that
#                                  the brightness check must refuse.
#               Live               VP9 in WebM written to a pipe, so the file has no duration, like a
#                                  recording of a live stream.
#             The five extra films are rated PG, so a viewer limited to PG can see them, and have no artwork.
#             Their community ratings (9.5, 9.2, 9.1, 6.0 and 5.0) put the first three above every other film, so
#             the top rated pool always holds them, with Film 2 (R) fifth. No other pool promises that, and no
#             pool promises that a slot gets a particular title, so a check that needs one must lock it.
#   shows/    Test Show (2019): two seasons of two episodes, with a tvshow.nfo and a fanart.jpg.
#   home/     two clips with no metadata at all and no artwork, for a home videos library.
#   mixed/    for a library of mixed content: Letterbox (2023), the pattern at 2.39:1 inside black bars above
#             and below, and Pillarbox (2023), the pattern at 3:4 inside black bars left and right. Both rated
#             PG, no artwork. For "bars burned into a clip never reach the cover".
#   trick/    Trick Film (2024): a flat grey picture that gets lighter by 5 levels a second (40 at 0 s, 190 at
#             30 s). A flat picture scores under the scene finder's floor at every moment, so the film gets on a
#             cover only through a trickplay thumbnail that a check paints over, and the grey level of the cover
#             tells the moment it starts at. Rated PG, no artwork.
#   empty/    nothing, for a library without a single title.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MEDIA="${1:-$HERE/.work/media}"

SIZE=640x360
RATE=25
FILM_SECONDS=30     # Dark Start relies on this: 9 s black, 9 s blurred, 12 s sharp
EPISODE_SECONDS=20
CLIP_SECONDS=15

ff() { ffmpeg -hide_banner -loglevel error -nostdin -y "$@"; }

# --- What ffmpeg must be able to do ---------------------------------------------------------------------

encoders="$(ffmpeg -hide_banner -encoders 2>/dev/null)"
filters="$(ffmpeg -hide_banner -filters 2>/dev/null)"
for encoder in libx264 libx265 libvpx-vp9 libopus aac; do
  grep -q " $encoder " <<<"$encoders" || { echo "make-media: this ffmpeg has no $encoder encoder" >&2; exit 1; }
done
for filter in zscale gblur testsrc2 nullsrc geq color sine concat setparams; do
  grep -q " $filter " <<<"$filters" || { echo "make-media: this ffmpeg has no $filter filter" >&2; exit 1; }
done

# --- Building blocks ------------------------------------------------------------------------------------

# pattern SECONDS [HUE]: the picture every video is made of. testsrc2 moves, and a HUE turn (in degrees)
# tells the films apart. Without a HUE the colours stay as they are.
pattern() {
  local source="testsrc2=size=$SIZE:rate=$RATE:duration=$1"
  if [ -n "${2:-}" ]; then source+=",hue=h=$2"; fi
  echo "$source"
}

# light_pattern SECONDS: the picture of the HDR pair (see there): the pattern with little colour and all its
# levels squeezed into the light range 0.55 to 0.85.
light_pattern() { echo "$(pattern "$1"),eq=saturation=0.3,curves=all='0/0.55 1/0.85'"; }

# h264 OUT HUE SECONDS HZ: H.264 with an AAC sine tone. Extra ffmpeg options may follow.
h264() {
  local out=$1 hue=$2 seconds=$3 hz=$4
  shift 4
  ff -f lavfi -i "$(pattern "$seconds" "$hue")" -f lavfi -i "sine=frequency=$hz:duration=$seconds" \
    -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -c:a aac -b:a 48k -metadata:s:a:0 language=eng "$@" "$out"
}

# movie_nfo FILE TITLE YEAR MPAA RATING
movie_nfo() {
  cat > "$1" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>$2</title>
  <year>$3</year>
  <mpaa>$4</mpaa>
  <rating>$5</rating>
  <lockdata>true</lockdata>
</movie>
XML
}

# fanart FILE NUMBER: a 1920x1080 backdrop of smooth colour waves, different for every NUMBER. Its look is
# nothing like the test pattern, so a cover made from artwork can be told from one made from video.
fanart() {
  local waves="r='150+95*sin(X/260+Y/410+$2*1.3)':g='150+95*sin(X/330-Y/270+$2*2.1)'"
  waves+=":b='150+95*sin(X/450+Y/190+$2*0.7)'"
  ff -f lavfi -i "nullsrc=size=1920x1080:rate=1:duration=1,format=gbrp,geq=$waves" \
    -frames:v 1 -q:v 4 "$1"
}

# A film's own folder, "Name (Year)", under movies/ or under another folder of MEDIA.
film_dir_in() { local dir="$MEDIA/$1/$2 ($3)"; mkdir -p "$dir"; echo "$dir"; }
film_dir() { film_dir_in movies "$1" "$2"; }

mkdir -p "$MEDIA/movies" "$MEDIA/shows" "$MEDIA/home" "$MEDIA/mixed" "$MEDIA/trick" "$MEDIA/empty"

# --- movies/ --------------------------------------------------------------------------------------------

echo "make-media: films"
# number|year|rating. The MPAA rating follows from the number: odd is PG, even is R.
for row in "1|2011|9.0" "2|2012|8.7" "3|2013|8.4" "4|2014|8.1" "5|2015|7.8" "6|2016|7.5" "7|2017|7.2" "8|2018|6.9"; do
  IFS='|' read -r n year rating <<<"$row"
  title="Film $n"
  mpaa=PG
  if [ $((n % 2)) -eq 0 ]; then mpaa=R; fi
  dir="$(film_dir "$title" "$year")"
  h264 "$dir/$title ($year).mkv" $((n * 43)) "$FILM_SECONDS" $((200 + n * 40))
  movie_nfo "$dir/$title ($year).nfo" "$title" "$year" "$mpaa" "$rating"
  fanart "$dir/fanart.jpg" "$n"
done

# Dark Start: 9 s black, 9 s blurred, 12 s sharp. The three parts are made apart and joined, each with
# the same pixel format, so the cut points fall on exact frames (225, 225 and 300 of 750).
dir="$(film_dir "Dark Start" 2020)"
parts="[0:v]format=yuv420p,setsar=1[black];[1:v]gblur=sigma=20,format=yuv420p,setsar=1[blur]"
parts+=";[2:v]format=yuv420p,setsar=1[sharp];[black][blur][sharp]concat=n=3:v=1:a=0[v]"
ff -f lavfi -i "color=c=black:size=$SIZE:rate=$RATE:duration=9" \
  -f lavfi -i "$(pattern 9 120)" \
  -f lavfi -i "$(pattern 12 120)" \
  -f lavfi -i "sine=frequency=520:duration=$FILM_SECONDS" \
  -filter_complex "$parts" \
  -map "[v]" -map 3:a -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p \
  -c:a aac -b:a 48k -metadata:s:a:0 language=eng \
  "$dir/Dark Start (2020).mkv"
movie_nfo "$dir/Dark Start (2020).nfo" "Dark Start" 2020 PG 9.5

# HDR Film and SDR Twin: one picture, two ways. Both start from the picture as BT.709 limited range YUV (the
# SDR video of the twin). The HDR file converts that to PQ and BT.2020 (203 nits for reference white) and
# stores it as 10-bit HEVC.
#
# The picture is light and has little colour on purpose, so that tone mapping makes a clear difference. The
# renderer's recipe, tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p, leaves
# values up to about 140 (of 255) alone and squeezes the ones above, while a clip shown with no tone mapping
# at all is darker the lighter it is, so light pixels are where the two part. Colour costs the tone mapped
# result (the same levels at full colour lose 8.7 %). A mid tone picture, the plain pattern with a mean of
# 126, puts the two only 4 points apart (-13.1 % and -9.1 %), too close for any tolerance to tell them apart.
#
# Measured on the files this script makes: all 750 frames, with the jellyfin-ffmpeg 8.1.3 of the pinned image.
# Metric: the mean Rec.709 luma of the 8-bit RGB frames (0.2126 R + 0.7152 G + 0.0722 B over every pixel,
# 0 to 255), which is what a check on decoded cover images computes. To repeat it, decode with that ffmpeg to raw
# rgb24 (the recipe first for the mapped case, then scale=640:360,format=rgb24) and average. Ratios are against
# the SDR twin:
#   SDR Twin                                      176.5
#   HDR Film, no tone mapping (rgb24 only)        123.6   -30.0 %
#   HDR Film, through the tonemapx recipe above   166.9    -5.5 %
# On the clips alone a tolerance of 18 % sat between the two. The text and frame of a cover are the same on both
# covers and pull both ratios toward zero, so the check measures real covers: the cover check in e2e.mjs (HDR_LIMIT)
# locks SDR Twin, HDR Film and HDR Untagged at the same moment into three slots of one cover without fades and
# compares the mean luma of each slot's frames. Measured on 2026-10-17: SDR Twin 153.9, HDR Film -4.9 %, HDR
# Untagged (no tone mapping) -27.7 %, so the check allows 16 %, about halfway. Measure again if the picture, the
# recipe or the cover style changes.
SDR_YUV="scale=out_color_matrix=bt709:out_range=tv,format=yuv420p"
TO_PQ="setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv,zscale=t=linear:npl=203"
TO_PQ+=",format=gbrpf32le,zscale=p=bt2020:t=smpte2084:m=bt2020nc:r=tv:npl=203,format=yuv420p10le"
dir="$(film_dir "HDR Film" 2021)"
ff -f lavfi -i "$(light_pattern "$FILM_SECONDS")" -f lavfi -i "sine=frequency=330:duration=$FILM_SECONDS" \
  -vf "$SDR_YUV,$TO_PQ" \
  -c:v libx265 -preset ultrafast -crf 28 -pix_fmt yuv420p10le -x265-params log-level=error \
  -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range tv \
  -c:a aac -b:a 48k -metadata:s:a:0 language=eng "$dir/HDR Film (2021).mkv"
movie_nfo "$dir/HDR Film (2021).nfo" "HDR Film" 2021 PG 9.2

dir="$(film_dir "SDR Twin" 2021)"
ff -f lavfi -i "$(light_pattern "$FILM_SECONDS")" -f lavfi -i "sine=frequency=330:duration=$FILM_SECONDS" \
  -vf "$SDR_YUV" \
  -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p \
  -color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv \
  -c:a aac -b:a 48k -metadata:s:a:0 language=eng "$dir/SDR Twin (2021).mkv"
movie_nfo "$dir/SDR Twin (2021).nfo" "SDR Twin" 2021 PG 9.1

# HDR Untagged: the picture of HDR Film made the same way, with the tags taken off the frames before the encoder
# writes them, so neither the stream nor the container says PQ or BT.2020. Jellyfin's probe then calls it SDR, and
# the renderer shows its PQ levels as they are: a cover made without tone mapping (about -28 % on the cover, see
# HDR_LIMIT in e2e.mjs), which the brightness check must refuse.
dir="$(film_dir "HDR Untagged" 2021)"
ff -f lavfi -i "$(light_pattern "$FILM_SECONDS")" -f lavfi -i "sine=frequency=330:duration=$FILM_SECONDS" \
  -vf "$SDR_YUV,$TO_PQ,setparams=color_primaries=unknown:color_trc=unknown:colorspace=unknown" \
  -c:v libx265 -preset ultrafast -crf 28 -pix_fmt yuv420p10le -x265-params log-level=error \
  -c:a aac -b:a 48k -metadata:s:a:0 language=eng "$dir/HDR Untagged (2021).mkv"
movie_nfo "$dir/HDR Untagged (2021).nfo" "HDR Untagged" 2021 PG 5.0

# Live: written to a pipe. ffmpeg cannot seek back into a pipe to write the length into the header, so
# the file has no duration, as with a recording of a live stream.
dir="$(film_dir "Live" 2022)"
ff -f lavfi -i "$(pattern 20 200)" -f lavfi -i "sine=frequency=440:duration=20" \
  -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -row-mt 1 -b:v 600k -pix_fmt yuv420p -c:a libopus -b:a 48k \
  -f webm - > "$dir/Live (2022).webm"
movie_nfo "$dir/Live (2022).nfo" "Live" 2022 PG 6.0

# --- shows/ ---------------------------------------------------------------------------------------------

echo "make-media: show"
show="$MEDIA/shows/Test Show (2019)"
mkdir -p "$show"
cat > "$show/tvshow.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow>
  <title>Test Show</title>
  <year>2019</year>
  <mpaa>TV-PG</mpaa>
  <rating>8.0</rating>
  <lockdata>true</lockdata>
</tvshow>
XML
fanart "$show/fanart.jpg" 9
for season in 1 2; do
  mkdir -p "$show/Season $season"
  for episode in 1 2; do
    h264 "$show/Season $season/Test Show S0${season}E0${episode}.mkv" \
      $((season * 90 + episode * 20)) "$EPISODE_SECONDS" $((250 + season * 50 + episode * 25))
  done
done

# --- home/ ----------------------------------------------------------------------------------------------

echo "make-media: home videos"
h264 "$MEDIA/home/Beach Day.mp4" 60 "$CLIP_SECONDS" 300 -movflags +faststart
h264 "$MEDIA/home/Garden Party.mp4" 240 "$CLIP_SECONDS" 360 -movflags +faststart

# --- mixed/, trick/ and empty/ ----------------------------------------------------------------------------

echo "make-media: mixed, trick and empty"
# The picture inside the bars is the moving pattern; the bars are true black, as in a film letterboxed for TV. Both
# offsets are even, so the bars stay exact in yuv420p.
dir="$(film_dir_in mixed "Letterbox" 2023)"
ff -f lavfi -i "testsrc2=size=640x268:rate=$RATE:duration=$FILM_SECONDS,hue=h=300,pad=640:360:0:46:black,setsar=1" \
  -f lavfi -i "sine=frequency=610:duration=$FILM_SECONDS" \
  -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -c:a aac -b:a 48k -metadata:s:a:0 language=eng \
  "$dir/Letterbox (2023).mkv"
movie_nfo "$dir/Letterbox (2023).nfo" "Letterbox" 2023 PG 7.0
dir="$(film_dir_in mixed "Pillarbox" 2023)"
ff -f lavfi -i "testsrc2=size=272x360:rate=$RATE:duration=$FILM_SECONDS,hue=h=30,pad=640:360:184:0:black,setsar=1" \
  -f lavfi -i "sine=frequency=650:duration=$FILM_SECONDS" \
  -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -c:a aac -b:a 48k -metadata:s:a:0 language=eng \
  "$dir/Pillarbox (2023).mkv"
movie_nfo "$dir/Pillarbox (2023).nfo" "Pillarbox" 2023 PG 7.0

# Grey, with no colour, so its level is its luma: 40 + 5 levels a second (TRICK_LUMA in e2e.mjs follows this).
dir="$(film_dir_in trick "Trick Film" 2024)"
ff -f lavfi -i "nullsrc=size=$SIZE:rate=$RATE:duration=$FILM_SECONDS,format=yuv420p,geq=lum='40+5*T':cb=128:cr=128" \
  -f lavfi -i "sine=frequency=700:duration=$FILM_SECONDS" \
  -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -b:a 48k -metadata:s:a:0 language=eng \
  "$dir/Trick Film (2024).mkv"
movie_nfo "$dir/Trick Film (2024).nfo" "Trick Film" 2024 PG 7.0

# --- The two files whose odd property is the whole point ---------------------------------------------------

probe() { ffprobe -v error -select_streams v:0 -show_entries "$1" -of default=noprint_wrappers=1:nokey=1 "$2"; }

live_duration="$(ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \
  "$MEDIA/movies/Live (2022)/Live (2022).webm")"
if [ "$live_duration" != "N/A" ]; then
  echo "make-media: Live (2022) has a duration ($live_duration), but it must have none" >&2
  exit 1
fi
colour_tags() { probe stream=pix_fmt,color_space,color_transfer,color_primaries "$1" | paste -sd' '; }
hdr_tags="$(colour_tags "$MEDIA/movies/HDR Film (2021)/HDR Film (2021).mkv")"
if [ "$hdr_tags" != "yuv420p10le bt2020nc smpte2084 bt2020" ]; then
  echo "make-media: HDR Film (2021) is not 10-bit PQ BT.2020 (ffprobe says: $hdr_tags)" >&2
  exit 1
fi
untagged_tags="$(colour_tags "$MEDIA/movies/HDR Untagged (2021)/HDR Untagged (2021).mkv")"
if [ "$untagged_tags" != "yuv420p10le unknown unknown unknown" ]; then
  echo "make-media: HDR Untagged (2021) must be 10-bit with no colour tags (ffprobe says: $untagged_tags)" >&2
  exit 1
fi

# The pair must stay light enough for tone mapping to matter (see the pair above). This is a coarse check with
# the host's ffmpeg: the mean of the Y plane (16 to 235) of every frame as plain 8-bit yuv420p, so HDR Film is
# shown with no tone mapping. Here the SDR twin is 169 and HDR Film 27 % lower; the plain pattern was 125 and 10 %.
mean_luma() {
  ffmpeg -hide_banner -loglevel error -nostdin -i "$1" \
    -vf "format=yuv420p,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-" -f null - \
    | awk -F= '/YAVG/ { sum += $2; n++ } END { printf "%.1f", sum / n }'
}
sdr_luma="$(mean_luma "$MEDIA/movies/SDR Twin (2021)/SDR Twin (2021).mkv")"
hdr_luma="$(mean_luma "$MEDIA/movies/HDR Film (2021)/HDR Film (2021).mkv")"
if ! awk -v sdr="$sdr_luma" -v hdr="$hdr_luma" 'BEGIN { exit !(sdr >= 150 && hdr <= sdr * 0.8) }'; then
  echo "make-media: the HDR pair is too dark to tell tone mapping from none:" \
    "SDR Twin has a mean luma of $sdr_luma (at least 150 wanted)," >&2
  echo "make-media: and HDR Film without tone mapping has $hdr_luma (at most 80 % of the twin's wanted)" >&2
  exit 1
fi

echo "make-media: done, $(du -sh "$MEDIA" | cut -f1) in $MEDIA"
