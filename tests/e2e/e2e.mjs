// End-to-end checks against a real Jellyfin 12 that run.sh started with the plugin installed and the sample
// media of make-media.sh. Needs node 20 or later; the page checks also need Playwright (see page.mjs).
//
//   node e2e.mjs [names...]    run the checks whose names contain one of the words (all of them with none)
//
// Every check name starts with its group (covers, clips, hdr, trickplay, audience, images, api, page, filter,
// restart, log), so `node e2e.mjs audience images` runs two groups. The restart group is slow: each of its checks
// restarts Jellyfin (the last two filter checks do too, with another network.xml). A word that matches no check
// name is a mistake, so it fails the run, naming the word and listing the checks.
//
// JF is the server's address (default http://127.0.0.1:18096), CONTAINER the name of its Docker container (default
// lcm-e2e) and WORK the folder run.sh mounted into it (default tests/e2e/.work): the checks read the plugin's state
// and cover files there, move a film aside for a while, paint over trickplay tiles and edit network.xml. run.sh
// sets all three.
//
// The checks that set the server up (wizard, admin, libraries, scan) always run, because every other check
// depends on them, and a failed one ends the run. They also work on a server that is already set up, so a
// server kept with KEEP=1 can be checked again without rebuilding anything. Each later check sets up what it
// needs itself (a cover, a lock, a viewer's limits), so any group can run alone.
//
// Requests carry the "MediaBrowser" Authorization header and nothing else: on Jellyfin 12.2 a request that
// sends its token as X-Emby-Token, X-MediaBrowser-Token or an api_key query parameter is answered with 401.
//
// Pictures are measured on decoded frames. Jellyfin's ffmpeg (8.1.3) cannot decode an animated WebP, so covers are
// decoded by ImageMagick in a small image built from alpine:3.22 (TOOLS_DOCKERFILE), which also has libwebp's
// webpinfo. Docker builds it on first use, which needs the internet once.

import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import zlib from 'node:zlib';
import { pageChecks } from './page.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const JF = process.env.JF ?? 'http://127.0.0.1:18096';
const CONTAINER = process.env.CONTAINER ?? 'lcm-e2e';
const WORK = path.resolve(process.env.WORK ?? path.join(HERE, '.work'));

// The checks stop, start and exec into CONTAINER: only a container that run.sh made for these tests (its label) is
// ever touched, also when this script is run by hand against a kept server.
const OWN_LABEL = 'org.library-cover-maker.e2e';
{
    const labels = spawnSync(
        'docker',
        ['container', 'inspect', '-f', `{{index .Config.Labels "${OWN_LABEL}"}}`, CONTAINER],
        {
            encoding: 'utf8'
        }
    );
    if (labels.status !== 0 || labels.stdout.trim() !== '1') {
        console.error(`e2e.mjs: ${CONTAINER} has no ${OWN_LABEL}=1 label, so it is not a server of these tests`);
        process.exit(2);
    }
}

// The server is a throwaway one that listens on 127.0.0.1 only, so the accounts are test values.
const ADMIN = { Name: 'admin', Password: 'admin' };
const KID = { Name: 'kid', Password: 'kid-pass' };
const PLUGIN = { name: 'Library Cover Maker', id: 'ba3b88b5029c4a6f90b3734af8385652' };

// The libraries, over the folders make-media.sh fills. itemTypes are what the library's metadata options list.
// Mixed has no collection type (Jellyfin's "mixed content"); Trick is the one library with trickplay switched on.
const LIBRARIES = {
    Movies: { type: 'movies', path: '/media/movies', itemTypes: ['Movie'] },
    Shows: { type: 'tvshows', path: '/media/shows', itemTypes: ['Series', 'Season', 'Episode'] },
    Home: { type: 'homevideos', path: '/media/home', itemTypes: ['Video'] },
    Mixed: { type: 'mixed', path: '/media/mixed', itemTypes: ['Movie', 'Video', 'Series', 'Season', 'Episode'] },
    Trick: { type: 'movies', path: '/media/trick', itemTypes: ['Movie'], trickplay: true },
    Empty: { type: 'movies', path: '/media/empty', itemTypes: ['Movie'] }
};

// The films of make-media.sh as Jellyfin should read them. Later checks lean on every line: the ratings (a viewer
// limited to PG), the backdrops (artwork or not), the video range (HDR against SDR) and the missing duration.
const SAMPLE_FILMS = new Map([
    ...Array.from({ length: 8 }, (_, i) => [`Film ${i + 1}`, { rating: i % 2 ? 'R' : 'PG', backdrop: true }]),
    ['Dark Start', { rating: 'PG' }],
    ['HDR Film', { rating: 'PG', range: 'HDR10' }],
    ['SDR Twin', { rating: 'PG', range: 'SDR' }],
    ['HDR Untagged', { rating: 'PG', range: 'SDR' }],
    ['Live', { rating: 'PG', noDuration: true }]
]);

// Every cover is 640x360 (the plugin's default output size) at 12 frames a second, and lasts 6 s by default.
const COVER = { width: 640, height: 360, fps: 12, seconds: 6 };

// The HDR check's limit, measured on real covers on 2026-10-17 (make-media.sh has the details at the HDR pair): a
// tone mapped slot was 4.9 % darker than its SDR twin, one made without tone mapping 27.7 %. 16 % is about halfway.
const HDR_LIMIT = 0.16;

// The image that decodes covers and checks them. A fixed alpine, so a run only changes when this text does.
const TOOLS_DOCKERFILE = [
    'FROM alpine:3.22@sha256:5291449c3df73caf6ed85e649dec1b9e818b39a5d8c871e97afc13e9cd5e8fa8',
    'RUN apk add --no-cache libwebp-tools imagemagick imagemagick-webp',
    'LABEL org.library-cover-maker.e2e=1'
].join('\n');
const TOOLS_IMAGE = `lcm-e2e-tools:${createHash('sha256').update(TOOLS_DOCKERFILE).digest('hex').slice(0, 12)}`;

const AUTH = `MediaBrowser Client="e2e", Device="e2e", DeviceId="${CONTAINER}", Version="1.0"`;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const words = process.argv.slice(2);

let token = '';
let passed = 0;
let failed = 0;
let known = 0;
/** The words that matched the name of some check, and the names of all checks reached so far. */
const matchedWords = new Set();
const checkNames = [];

/** Each library's id by name (32 hex digits), filled in by the libraries check for the checks after it. */
const libraryIds = {};

// ---------- Helpers ----------

/** An id as Jellyfin's API writes it: 32 hex digits, no dashes. The plugin's state files have dashes. */
const N = (id) =>
    String(id ?? '')
        .replaceAll('-', '')
        .toLowerCase();
/** The same id with dashes, as some routes and file names have it. */
const dashed = (id) => N(id).replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');

/** Calls Jellyfin. `as` is the token to send: the admin's by default, '' for the header without a token, null for
 *  no Authorization header at all (as a browser's <img> asks). `headers` adds headers, `base` is the server's base
 *  URL. A body that is FormData, bytes or a string goes as it is, anything else as JSON. Gives { status, json,
 *  text, bytes, headers }. */
async function call(method, urlPath, { body, as = token, headers = {}, base = '' } = {}) {
    const sent = { ...headers };
    if (as !== null) sent.Authorization = AUTH + (as ? `, Token="${as}"` : '');
    let payload = body;
    if (
        body !== undefined &&
        !(body instanceof FormData) &&
        !(body instanceof Uint8Array) &&
        typeof body !== 'string'
    ) {
        sent['Content-Type'] = 'application/json';
        payload = JSON.stringify(body);
    }
    const res = await fetch(JF + base + urlPath, {
        method,
        headers: sent,
        body: payload,
        signal: AbortSignal.timeout(120_000)
    });
    const bytes = Buffer.from(await res.arrayBuffer());
    const text = bytes.toString('utf8');
    let json = null;
    try {
        json = text ? JSON.parse(text) : null;
    } catch {
        /* not JSON */
    }
    return { status: res.status, json, text, bytes, headers: res.headers };
}

/** Calls Jellyfin as the admin. Gives { status, json, text, bytes, headers }. */
const api = (method, urlPath, body) => call(method, urlPath, { body });

function expect(condition, message) {
    if (!condition) throw new Error(message);
}

/** GETs a path that must answer 200 and returns its JSON. */
async function ok(urlPath) {
    const res = await api('GET', urlPath);
    expect(res.status === 200, `${urlPath} answered ${res.status} ${res.text.slice(0, 200)}`);
    return res.json;
}

/** Sends a request that must answer with `status`. */
async function expectStatus(status, method, urlPath, body) {
    const res = await api(method, urlPath, body);
    expect(
        res.status === status,
        `${method} ${urlPath} answered ${res.status} (wanted ${status}) ${res.text.slice(0, 200)}`
    );
    return res;
}

/** Calls `fn` until it gives something truthy, and returns that. `fn` may throw to say what is still missing. When
 *  time runs out the error names `what` and ends with the last such message. */
async function waitFor(what, fn, { timeout = 60_000, interval = 1_000 } = {}) {
    const end = Date.now() + timeout;
    let problem = '';
    for (;;) {
        try {
            const value = await fn();
            if (value) return value;
            problem = '';
        } catch (e) {
            problem = `: ${e.message}`;
        }
        if (Date.now() > end) throw new Error(`timed out after ${timeout / 1000} s waiting for ${what}${problem}`);
        await sleep(interval);
    }
}

/** Whether any of these check names runs under the name filter. Records nothing. */
const wanted = (names) =>
    !words.length || names.some((name) => words.some((word) => name.toLowerCase().includes(word.toLowerCase())));

/** Whether a check of this name runs under the name filter. Records the name and the words it matches. */
function selected(name) {
    checkNames.push(name);
    const hits = words.filter((word) => name.toLowerCase().includes(word.toLowerCase()));
    for (const word of hits) matchedWords.add(word);
    return !words.length || hits.length > 0;
}

/** Runs one check and prints its result. A check that sets the server up is never skipped by the name filter.
 *  `defect` marks a check that fails because of a known defect of the plugin (it says which): its failure is
 *  printed as KNOWN and does not fail the run, but its passing does, so that the mark is taken off once fixed. */
async function check(name, fn, { setup = false, defect } = {}) {
    if (!selected(name) && !setup) return true;
    try {
        const detail = await fn();
        if (defect) {
            failed++;
            console.log(`FAIL  ${name}  -> it passes now: take off its mark of a known defect (${defect})`);
            return false;
        }
        passed++;
        console.log(`PASS  ${name}${detail ? `  (${detail})` : ''}`);
        return true;
    } catch (e) {
        if (defect) {
            known++;
            console.log(`KNOWN ${name}  -> ${e.message}\n      known defect of the plugin: ${defect}`);
            return true;
        }
        failed++;
        console.log(`FAIL  ${name}  -> ${e.message}`);
        return false;
    }
}

/** A setup check: when it fails nothing after it can mean anything, so the run ends there. */
async function setupCheck(name, fn) {
    if (!(await check(name, fn, { setup: true }))) finish({ complete: false });
}

/** Ends the run. A word that matched no check name fails it, but only when every check was reached: after a
 *  failed setup check the later ones never were, so a word may simply belong to one of them. */
function finish({ complete = true } = {}) {
    const unmatched = complete ? [...new Set(words.filter((word) => !matchedWords.has(word)))] : [];
    if (unmatched.length) {
        failed++;
        console.log(`FAIL  no check has a name containing ${unmatched.map((word) => `"${word}"`).join(' or ')}`);
        console.log(`      the checks are: ${checkNames.join('; ')}`);
    }
    console.log(
        `${passed}/${passed + failed} passed` +
            (known ? `, ${known} known plugin defect${known > 1 ? 's' : ''} (KNOWN above)` : '')
    );
    process.exit(failed ? 1 : 0);
}

/** Runs a program and gives its standard output. Fails with what it printed when it exits with anything but 0. */
function run(program, args, { input, what = program } = {}) {
    const result = spawnSync(program, args, { input, maxBuffer: 1 << 30 });
    if (result.error) throw new Error(`${what} could not run: ${result.error.message}`);
    expect(
        result.status === 0,
        `${what} exited with ${result.status}: ${result.stderr.toString().trim().split('\n').slice(-3).join(' / ')}`
    );
    return result.stdout;
}

/** The server's console log, all of it or from a moment on (`since`, as docker logs takes it). */
function serverLog(since) {
    const result = spawnSync('docker', ['logs', ...(since ? ['--since', String(since)] : []), CONTAINER], {
        encoding: 'utf8',
        maxBuffer: 1 << 28
    });
    expect(result.status === 0, `docker logs ${CONTAINER} failed: ${result.stderr || result.error}`);
    return `${result.stdout}\n${result.stderr}`;
}

/** The [ERR] and [FTL] entries of the server log that name the plugin. Same rule as run.sh: an entry starts with
 *  "[21:07:57.227] [INF]" (the console log has the time only, the log file the date and zone too), and the lines
 *  after it belong to it. */
function pluginErrorEntries(log) {
    const entries = [];
    for (const line of log.split('\n')) {
        const start = /^\[\d[\d:. +-]*\] \[([A-Z]+)\]/.exec(line);
        if (start) entries.push({ level: start[1], lines: [] });
        entries.at(-1)?.lines.push(line);
    }
    return entries
        .filter(
            (entry) =>
                (entry.level === 'ERR' || entry.level === 'FTL') &&
                entry.lines.some((line) => /LibraryCoverMaker|Library Cover Maker/i.test(line))
        )
        .map((entry) => entry.lines.join('\n'));
}

/** How often Jellyfin's image processor said it cannot decode an animated image: what the cover filter prevents. */
const decodeErrors = (log) =>
    log.split('\n').filter((line) => line.includes('Cannot decode images with multiple frames')).length;

/** How many times Jellyfin's image saver could not write an image where it wanted and saved it elsewhere. */
const savedElsewhere = (log) => log.split('\n').filter((line) => line.includes('Will retry saving to')).length;

/** Where a path inside the container is on this machine, and back: /config and /media are folders of WORK. */
function hostPath(containerPath) {
    for (const mount of ['/config/', '/media/']) {
        if (containerPath.startsWith(mount)) {
            return path.join(WORK, mount.slice(1, -1), containerPath.slice(mount.length));
        }
    }
    throw new Error(`${containerPath} is not in a folder the container shares with this machine`);
}
function containerPath(file) {
    for (const mount of ['config', 'media']) {
        const root = path.join(WORK, mount) + path.sep;
        if (file.startsWith(root)) return `/${mount}/${file.slice(root.length)}`;
    }
    throw new Error(`${file} is not in a folder the container shares with this machine`);
}

/** Runs Jellyfin's own ffmpeg inside the container and gives what it wrote to its standard output. */
const jellyfinFfmpeg = (args) =>
    run(
        'docker',
        [
            'exec',
            CONTAINER,
            '/usr/lib/jellyfin-ffmpeg/ffmpeg',
            '-hide_banner',
            '-loglevel',
            'error',
            '-nostdin',
            ...args
        ],
        { what: 'ffmpeg in the container' }
    );

/** One frame of a video (a path in the container) at `seconds`, `vf` applied, as raw RGB at cover size. */
function sourceFrame(video, seconds, vf = `scale=${COVER.width}:${COVER.height}`) {
    const bytes = jellyfinFfmpeg([
        '-ss',
        String(seconds),
        '-i',
        video,
        '-frames:v',
        '1',
        '-vf',
        vf,
        '-f',
        'rawvideo',
        '-pix_fmt',
        'rgb24',
        '-'
    ]);
    expect(
        bytes.length === COVER.width * COVER.height * 3,
        `ffmpeg gave ${bytes.length} bytes for a frame of ${video}`
    );
    return bytes;
}

// ---------- Cover files and their pictures ----------

let toolsReady = false;
let toolsRuns = 0;

/** Runs a command in the tools image (built on first use), with `mounts` as read-only folders, and gives its output. */
function tools(args, { input, mounts = {} } = {}) {
    if (!toolsReady) {
        if (spawnSync('docker', ['image', 'inspect', TOOLS_IMAGE], { stdio: 'ignore' }).status !== 0) {
            run('docker', ['build', '-q', '-t', TOOLS_IMAGE, '-'], {
                input: TOOLS_DOCKERFILE,
                what: `docker build of ${TOOLS_IMAGE}`
            });
        }
        toolsReady = true;
    }
    const volumes = Object.entries(mounts).flatMap(([from, to]) => ['-v', `${from}:${to}:ro`]);
    const name = `${CONTAINER}-tools-${process.pid}-${++toolsRuns}`;
    return run('docker', ['run', '--rm', '-i', '--name', name, '--network', 'none', ...volumes, TOOLS_IMAGE, ...args], {
        input,
        what: `${args[0]} in ${TOOLS_IMAGE}`
    });
}

/** What the RIFF chunks of a WebP say: its canvas size, whether it is animated, each frame's duration (ms), and
 *  whether it holds a plain picture (a still). */
function webpInfo(bytes) {
    expect(
        bytes.length >= 12 && bytes.toString('latin1', 0, 4) === 'RIFF' && bytes.toString('latin1', 8, 12) === 'WEBP',
        `not a WebP file (it starts with ${bytes.subarray(0, 4).toString('hex')})`
    );
    const end = Math.min(bytes.length, 8 + bytes.readUInt32LE(4));
    const info = { width: 0, height: 0, animated: false, durations: [], still: false };
    for (let at = 12; at + 8 <= end; ) {
        const tag = bytes.toString('latin1', at, at + 4);
        const size = bytes.readUInt32LE(at + 4);
        if (tag === 'VP8X') {
            info.animated = Boolean(bytes[at + 8] & 0x02);
            info.width = 1 + bytes.readUIntLE(at + 12, 3);
            info.height = 1 + bytes.readUIntLE(at + 15, 3);
        } else if (tag === 'ANMF') {
            info.durations.push(bytes.readUIntLE(at + 20, 3));
        } else if (tag === 'VP8 ' || tag === 'VP8L') {
            info.still = true;
        }
        at += 8 + size + (size & 1);
    }
    return info;
}

/** Whether bytes are an animated WebP. */
const isAnimated = (bytes) => bytes.toString('latin1', 0, 4) === 'RIFF' && webpInfo(bytes).animated;

/** Whether the bytes are a whole RIFF file: as long as its header says (the RIFF size plus its 8 bytes). */
const isWhole = (bytes) => bytes.length >= 12 && bytes.length === bytes.readUInt32LE(4) + 8;

/** A cover's pictures, one per tick of its timeline (12 a second). libwebp folds a frame into the one before it
 *  when it sees no change and gives that one twice the duration, so such a picture appears twice. Raw RGB. */
function decodeCover(bytes) {
    const info = webpInfo(bytes);
    const raw = tools(['magick', 'webp:-', '-coalesce', '-depth', '8', 'rgb:-'], { input: bytes });
    const size = COVER.width * COVER.height * 3;
    const frames = Array.from({ length: raw.length / size }, (_, i) => raw.subarray(i * size, (i + 1) * size));
    if (!info.durations.length) return { info, ticks: frames.slice(0, 1) };
    expect(
        frames.length === info.durations.length,
        `ImageMagick gave ${frames.length} frames for ${info.durations.length} ANMF chunks`
    );
    const ticks = frames.flatMap((frame, i) =>
        Array(Math.max(1, Math.round((info.durations[i] * COVER.fps) / 1000))).fill(frame)
    );
    return { info, ticks };
}

/** A rectangle of a cover picture: [x0, y0, x1, y1], the ends left out. */
const WHOLE = [0, 0, COVER.width, COVER.height];

/** Mean Rec. 709 luma (0 to 255) of an RGB picture over a rectangle. */
function meanLuma(frame, [x0, y0, x1, y1] = WHOLE) {
    let sum = 0;
    for (let y = y0; y < y1; y++) {
        for (let x = x0, at = (y * COVER.width + x0) * 3; x < x1; x++, at += 3)
            sum += 0.2126 * frame[at] + 0.7152 * frame[at + 1] + 0.0722 * frame[at + 2];
    }
    return sum / ((x1 - x0) * (y1 - y0));
}

/** How sharp a rectangle of a picture is: the variance of the 3x3 Laplacian of its luma. */
function sharpness(frame, [x0, y0, x1, y1]) {
    const luma = (x, y) => {
        const at = (y * COVER.width + x) * 3;
        return 0.2126 * frame[at] + 0.7152 * frame[at + 1] + 0.0722 * frame[at + 2];
    };
    let sum = 0;
    let squares = 0;
    let count = 0;
    for (let y = y0 + 1; y < y1 - 1; y++) {
        for (let x = x0 + 1; x < x1 - 1; x++) {
            const value = luma(x - 1, y) + luma(x + 1, y) + luma(x, y - 1) + luma(x, y + 1) - 4 * luma(x, y);
            sum += value;
            squares += value * value;
            count++;
        }
    }
    return squares / count - (sum / count) ** 2;
}

/** The ticks of each slot of a cover without fades, shared out as the renderer does (cumulative rounding). */
function slotTicks(count, seconds = COVER.seconds) {
    const ranges = [];
    let done = 0;
    for (let i = 1; i <= count; i++) {
        const boundary = Math.round(((i * seconds) / count) * COVER.fps);
        ranges.push([done, boundary]);
        done = boundary;
    }
    return ranges;
}

/** The plugin's folder of a library in the work folder, and the state file in it as the store wrote it. */
const storeFolder = (name) => path.join(WORK, 'config', 'data', 'library-cover-maker', libraryIds[name]);
const stateFile = (name) => JSON.parse(fs.readFileSync(path.join(storeFolder(name), 'state.json'), 'utf8'));

/** The animated WebP of a cover in the store. */
const coverFile = (name, coverId) => path.join(storeFolder(name), 'covers', `${N(coverId)}.webp`);

// ---------- The plugin's API ----------

const covers = (urlPath) => `/LibraryCovers${urlPath}`;
const coverState = (name) => ok(covers(`/Libraries/${libraryIds[name]}`));
const currentRecord = (state) =>
    state.History.find((cover) => N(cover.Id) === N(state.CurrentCoverId)) ??
    (state.Original && N(state.Original.Id) === N(state.CurrentCoverId) ? state.Original : null);
const slotKey = (list) =>
    JSON.stringify(list.map((slot) => [N(slot.ItemId), slot.MomentSeconds ?? null, slot.BackdropIndex ?? null]));

/** Waits until the library has a cover other than the one of `before` and gives its state. A failed build fails. */
async function newCover(name, before, { timeout = 120_000 } = {}) {
    return waitFor(
        `a new cover on ${name}`,
        async () => {
            const state = await coverState(name);
            if (state.LastErrorAt && state.LastErrorAt !== before.LastErrorAt) {
                throw new Error(`the build failed: ${state.LastError}`);
            }
            if (N(state.CurrentCoverId) === N(before.CurrentCoverId)) {
                throw new Error(`the cover is still ${N(before.CurrentCoverId) || 'none'}`);
            }
            return state;
        },
        { timeout, interval: 500 }
    );
}

/** Changes a library (PUT) and waits for the one new cover that follows: the build that a change of slots starts by
 *  itself, or a Generate when the slots stay as they are. Never both, so that no build is left running behind. */
async function rebuild(name, update = {}) {
    const started = Date.now();
    const before = await coverState(name);
    expect(!before.Pinned, `${name} is pinned, so it would not be built`);
    const slotsChange = update.Slots && slotKey(update.Slots) !== slotKey(before.Slots);
    if (Object.keys(update).length) {
        const res = await api('PUT', covers(`/Libraries/${libraryIds[name]}`), update);
        expect(res.status === 200, `PUT ${name} answered ${res.status} ${res.text.slice(0, 300)}`);
    }
    if (!slotsChange) {
        const res = await api('POST', covers(`/Libraries/${libraryIds[name]}/Generate`));
        expect(res.status === 202, `Generate on ${name} answered ${res.status} ${res.text.slice(0, 300)}`);
    }
    const state = await newCover(name, before);
    slowestBuild = Math.max(slowestBuild, Date.now() - started);
    return state;
}

/** The longest a build has taken in this run, in ms (rebuild measures it). */
let slowestBuild = 0;

/** For a check that something does not happen: looks every half second for as long as twice the slowest build of
 *  the run plus 2 s (at least 3 s), and fails as soon as `changed` names a change. Gives the time it watched. */
async function staysSame(what, changed) {
    const watched = Math.max(3_000, 2 * slowestBuild + 2_000);
    const end = Date.now() + watched;
    for (;;) {
        const change = await changed();
        expect(!change, `${what}: ${change}`);
        if (Date.now() > end) return watched;
        await sleep(500);
    }
}

/** The five slots, the first ones locked as given ({ ItemId, MomentSeconds, BackdropIndex }), the rest free. */
const slots = (...locked) => Array.from({ length: 5 }, (_, i) => locked[i] ?? {});

const DEFAULT_STYLE = { SlotCount: 5, Fade: true, Darken: 15, Frame: true, Shadow: true, LengthSeconds: 6 };

/** Puts a library back as the scheduled task leaves it (art, the default style, nothing locked, unpinned) with a
 *  generated cover, building one only when something differs. */
async function resetLibrary(name) {
    if ((await coverState(name)).Pinned) {
        await expectStatus(200, 'POST', covers(`/Libraries/${libraryIds[name]}/Unpin`));
    }
    const state = await coverState(name);
    const same =
        state.Mode === 'Art' &&
        slotKey(state.Slots) === slotKey(slots()) &&
        currentRecord(state)?.Origin === 'Generated' &&
        Object.entries(DEFAULT_STYLE).every(([key, value]) => state.Style[key] === value);
    return same ? state : rebuild(name, { Mode: 'Art', Style: DEFAULT_STYLE, Slots: slots() });
}

/** The cover a library shows now: its state and record, and its file in the store, decoded. */
async function currentCover(name) {
    const state = await coverState(name);
    const record = currentRecord(state);
    expect(record, `${name} has no current cover`);
    const bytes = fs.readFileSync(coverFile(name, record.Id));
    return { state, record, bytes, ...decodeCover(bytes) };
}

/** The items of a library, of the given types (comma separated), with the extra Fields asked for. */
async function libraryItems(name, types, fields) {
    const query = `ParentId=${libraryIds[name]}&Recursive=true&IncludeItemTypes=${types}&SortBy=SortName`;
    const urlPath = `/Items?${query}${fields ? `&Fields=${fields}` : ''}`;
    return (await ok(urlPath)).Items;
}

/** The films of a library by name. */
const titlesOf = async (name, type = 'Movie') =>
    new Map((await libraryItems(name, type)).map((item) => [item.Name, item]));

/** Starts a scheduled task by key and waits for it to end with a result newer than the start. */
async function runTask(key, timeout = 300_000) {
    const task = (await ok('/ScheduledTasks')).find((t) => t.Key === key);
    expect(task, `Jellyfin has no ${key} task`);
    const started = Date.now();
    await expectStatus(204, 'POST', `/ScheduledTasks/Running/${task.Id}`);
    return waitFor(
        `the ${key} task to finish`,
        async () => {
            const now = await ok(`/ScheduledTasks/${task.Id}`);
            const result = now.LastExecutionResult;
            if (now.State !== 'Idle' || !result || Date.parse(result.EndTimeUtc) < started - 2000) {
                throw new Error(`it is ${now.State}`);
            }
            expect(result.Status === 'Completed', `it ended ${result.Status}: ${result.ErrorMessage ?? ''}`);
            return result;
        },
        { timeout, interval: 500 }
    );
}

let coversMade = false;

/** Runs the plugin's scheduled task once in a run, so that every library has a cover of its own. */
async function ensureCovers() {
    if (coversMade) return;
    await runTask('LibraryCoverMakerRefresh');
    coversMade = true;
}

// ---------- Users and uploads ----------

let pgValue;

/** The value Jellyfin gives the US rating PG. */
async function pg() {
    pgValue ??= (await ok('/Localization/ParentalRatings')).find((rating) => rating.Name === 'PG')?.Value;
    expect(typeof pgValue === 'number', 'Jellyfin lists no PG rating');
    return pgValue;
}

/** The user "kid", made on first use, with its policy changed by `change` (when given) and saved. */
async function kid(change) {
    let user = (await ok('/Users')).find((u) => u.Name === KID.Name);
    if (!user) user = (await expectStatus(200, 'POST', '/Users/New', { Name: KID.Name, Password: KID.Password })).json;
    if (change) {
        const policy = { ...(await ok(`/Users/${user.Id}`)).Policy };
        change(policy);
        await expectStatus(204, 'POST', `/Users/${user.Id}/Policy`, policy);
        user = await ok(`/Users/${user.Id}`);
    }
    return user;
}

/** Kid with no limit and every library, what a check that limits kid starts from. Loosening changes no cover. */
const kidUnlimited = () =>
    kid((policy) => {
        policy.MaxParentalRating = null;
        policy.EnableAllFolders = true;
        policy.EnabledFolders = [];
    });

/** Kid limited to PG, in every library unless `folders` lists some. */
async function kidPg(folders) {
    const limit = await pg();
    return kid((policy) => {
        policy.MaxParentalRating = limit;
        policy.EnableAllFolders = !folders;
        policy.EnabledFolders = folders ?? [];
    });
}

/** Signs a user in and gives the token. */
async function signIn(user) {
    const res = await call('POST', '/Users/AuthenticateByName', {
        body: { Username: user.Name, Pw: user.Password },
        as: ''
    });
    expect(res.status === 200, `${user.Name} could not sign in: ${res.status}`);
    return res.json.AccessToken;
}

/** A PNG of one colour, made here, for uploads. */
function png(width, height, [red, green, blue] = [0x80, 0x80, 0x80]) {
    const table = Array.from({ length: 256 }, (_, n) => {
        let c = n;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
        return c >>> 0;
    });
    const crc = (bytes) => {
        let c = 0xffffffff;
        for (const b of bytes) c = table[(c ^ b) & 255] ^ (c >>> 8);
        return (c ^ 0xffffffff) >>> 0;
    };
    const chunk = (type, data) => {
        const body = Buffer.concat([Buffer.from(type, 'latin1'), data]);
        const length = Buffer.alloc(4);
        length.writeUInt32BE(data.length);
        const sum = Buffer.alloc(4);
        sum.writeUInt32BE(crc(body));
        return Buffer.concat([length, body, sum]);
    };
    const row = Buffer.concat([
        Buffer.from([0]),
        Buffer.from(Array.from({ length: width }, () => [red, green, blue]).flat())
    ]);
    const header = Buffer.alloc(13);
    header.writeUInt32BE(width, 0);
    header.writeUInt32BE(height, 4);
    header[8] = 8;
    header[9] = 2;
    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        chunk('IHDR', header),
        chunk('IDAT', zlib.deflateSync(Buffer.concat(Array(height).fill(row)))),
        chunk('IEND', Buffer.alloc(0))
    ]);
}

/** A multipart form with one file, as the plugin's upload takes it. */
function form(bytes, name, type) {
    const data = new FormData();
    data.append('file', new Blob([bytes], { type }), name);
    return data;
}

// ---------- Server setup ----------

/** The options of a new library: nothing is fetched from the internet, nothing is written next to the media. */
function libraryOptions(library) {
    return {
        PathInfos: [{ Path: library.path }],
        EnableInternetProviders: false,
        MetadataSavers: [],
        SaveLocalMetadata: false,
        TypeOptions: library.itemTypes.map((type) => ({ Type: type, MetadataFetchers: [], ImageFetchers: [] })),
        EnableRealtimeMonitor: false,
        EnableChapterImageExtraction: false,
        EnableTrickplayImageExtraction: Boolean(library.trickplay),
        ExtractTrickplayImagesDuringLibraryScan: false,
        SaveTrickplayWithMedia: false
    };
}

/** Waits for Jellyfin to be the real server (not its start-up page) under `base`, and gives /System/Info/Public. */
async function waitForServer(base = '', timeout = 60_000) {
    // While it starts, Jellyfin answers from a start-up page: /System/Info/Public gives 200 at once, but without
    // StartupWizardCompleted, and everything else gives 503. Only the real server's answer has that key.
    return waitFor(
        'Jellyfin to finish starting',
        async () => {
            const res = await call('GET', '/System/Info/Public', { base }).catch((e) => {
                throw new Error(`no answer (${e.cause?.code ?? e.cause?.message ?? e.message})`);
            });
            if (typeof res.json?.StartupWizardCompleted !== 'boolean') {
                throw new Error(`still on the start-up page (answered ${res.status})`);
            }
            return res.json;
        },
        { timeout }
    );
}

await setupCheck('startup wizard completes and the admin signs in', async () => {
    const info = await waitForServer();
    if (!info.StartupWizardCompleted) {
        await waitFor('the startup wizard to answer', async () => {
            const res = await api('GET', '/Startup/Configuration');
            if (res.status !== 200) throw new Error(`answered ${res.status}`);
            return true;
        });
        await expectStatus(204, 'POST', '/Startup/Configuration', {
            UICulture: 'en-US',
            MetadataCountryCode: 'US',
            PreferredMetadataLanguage: 'en'
        });
        await expectStatus(200, 'GET', '/Startup/User');
        await expectStatus(204, 'POST', '/Startup/User', ADMIN);
        await expectStatus(204, 'POST', '/Startup/RemoteAccess', {
            EnableRemoteAccess: true,
            EnableAutomaticPortMapping: false
        });
        await expectStatus(204, 'POST', '/Startup/Complete');
    }
    token = await signIn(ADMIN);
    expect((await ok('/Users/Me')).Policy.IsAdministrator, 'the account is not an administrator');
    return `Jellyfin ${info.Version}`;
});

await setupCheck('libraries Movies, Shows, Home, Mixed, Trick and Empty exist with internet metadata off', async () => {
    const existing = new Set((await ok('/Library/VirtualFolders')).map((folder) => folder.Name));
    for (const [name, library] of Object.entries(LIBRARIES)) {
        if (existing.has(name)) continue;
        await expectStatus(
            204,
            'POST',
            `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=${library.type}` +
                '&refreshLibrary=false',
            { LibraryOptions: libraryOptions(library) }
        );
    }
    const folders = await ok('/Library/VirtualFolders');
    for (const [name, library] of Object.entries(LIBRARIES)) {
        const folder = folders.find((f) => f.Name === name);
        expect(folder, `${name} was not created`);
        expect(
            folder.CollectionType === library.type,
            `${name} is a ${folder.CollectionType} library, not ${library.type}`
        );
        expect(
            folder.Locations.includes(library.path),
            `${name} reads ${folder.Locations.join(', ')}, not ${library.path}`
        );
        expect(folder.LibraryOptions.EnableInternetProviders === false, `${name} has internet providers on`);
        expect(
            folder.LibraryOptions.EnableTrickplayImageExtraction === Boolean(library.trickplay),
            `${name} has trickplay ${library.trickplay ? 'off' : 'on'}`
        );
        const types = folder.LibraryOptions.TypeOptions.map((o) => o.Type);
        for (const type of library.itemTypes) {
            const options = folder.LibraryOptions.TypeOptions.find((o) => o.Type === type);
            expect(
                options,
                `${name} has no options for ${type} (it has ${types.join(', ') || 'none'}), ` +
                    'so every fetcher would be on'
            );
            expect(
                options.MetadataFetchers.length === 0 && options.ImageFetchers.length === 0,
                `${name} still has fetchers for ${type}`
            );
        }
        libraryIds[name] = N(folder.ItemId);
    }
    return Object.keys(LIBRARIES).join(', ');
});

await setupCheck('library scan finds the sample media', async () => {
    await expectStatus(204, 'POST', '/Library/Refresh');
    const want = [
        ['Movies', 'Movie', SAMPLE_FILMS.size],
        ['Shows', 'Series', 1],
        ['Shows', 'Season', 2],
        ['Shows', 'Episode', 4],
        ['Home', 'Video', 2],
        ['Mixed', 'Movie', 2],
        ['Trick', 'Movie', 1]
    ];
    const found = await waitFor(
        'the sample media to appear',
        async () => {
            const counts = [];
            for (const [name, type] of want) counts.push((await libraryItems(name, type)).length);
            if (counts.some((n, i) => n !== want[i][2])) {
                throw new Error(
                    `last count: ${want.map(([name, type, n], i) => `${counts[i]} of ${n} ${name} ${type}`).join(', ')}`
                );
            }
            return counts;
        },
        { timeout: 180_000, interval: 2_000 }
    );
    // The scan goes on after the items appear (probing, images): wait until it is over, twice in a row.
    let idle = 0;
    await waitFor(
        'the library scan to finish',
        async () => {
            const task = (await ok('/ScheduledTasks')).find((t) => t.Key === 'RefreshLibrary');
            expect(task, 'Jellyfin has no RefreshLibrary task');
            idle = task.State === 'Idle' ? idle + 1 : 0;
            if (idle < 2) {
                const progress =
                    typeof task.CurrentProgressPercentage === 'number'
                        ? ` at ${Math.round(task.CurrentProgressPercentage)} %`
                        : '';
                throw new Error(`RefreshLibrary is ${task.State}${progress}`);
            }
            return true;
        },
        { timeout: 180_000, interval: 1_000 }
    );

    const films = await libraryItems('Movies', 'Movie', 'MediaStreams');
    expect(
        films
            .map((f) => f.Name)
            .sort()
            .join('|') === [...SAMPLE_FILMS.keys()].sort().join('|'),
        `the films are ${films.map((f) => f.Name).join(', ')}`
    );
    for (const film of films) {
        const sample = SAMPLE_FILMS.get(film.Name);
        const range = film.MediaStreams?.find((s) => s.Type === 'Video')?.VideoRangeType;
        expect(
            film.OfficialRating === sample.rating,
            `${film.Name} is rated ${film.OfficialRating}, not ${sample.rating}`
        );
        expect(
            (film.BackdropImageTags?.length ?? 0) === (sample.backdrop ? 1 : 0),
            `${film.Name} has ${film.BackdropImageTags?.length ?? 0} backdrops`
        );
        expect(!sample.range || range === sample.range, `${film.Name} is ${range}, not ${sample.range}`);
        expect(
            Boolean(film.RunTimeTicks) === !sample.noDuration,
            `${film.Name} has ${film.RunTimeTicks ? 'a' : 'no'} duration`
        );
    }
    // The home videos and the films of Mixed and Trick have no artwork of any kind: that is what makes their
    // covers come from scenes.
    for (const [name, type] of [
        ['Home', 'Video'],
        ['Mixed', 'Movie'],
        ['Trick', 'Movie']
    ]) {
        for (const video of await libraryItems(name, type)) {
            expect(
                !video.BackdropImageTags?.length && !Object.keys(video.ImageTags ?? {}).length,
                `${video.Name} has artwork`
            );
        }
    }
    return want.map(([name, type], i) => `${found[i]} ${name} ${type}`).join(', ');
});

// ---------- Checks ----------

await check('plugin Library Cover Maker is Active', async () => {
    const plugins = await ok('/Plugins');
    const plugin = plugins.find((p) => p.Name === PLUGIN.name);
    expect(plugin, `it is not listed (the server lists ${plugins.map((p) => p.Name).join(', ') || 'no plugins'})`);
    expect(plugin.Id.replaceAll('-', '').toLowerCase() === PLUGIN.id, `its id is ${plugin.Id}, not ${PLUGIN.id}`);
    expect(plugin.Status === 'Active', `its status is ${plugin.Status}`);
    return `version ${plugin.Version}`;
});

// ---------- covers: what the scheduled task makes ----------

await check('covers: Refresh_task_gives_every_library_an_animated_cover_under_5_MB', async () => {
    await ensureCovers();
    const report = [];
    for (const name of Object.keys(LIBRARIES)) {
        const state = await coverState(name);
        const record = currentRecord(state);
        expect(
            record?.Origin === 'Generated',
            `${name} has no generated cover: ${JSON.stringify(state).slice(0, 300)}`
        );
        expect(!state.LastError, `${name}: ${state.LastError}`);
        // The library's Primary image is the plugin's cover, byte for byte.
        const cover = fs.readFileSync(coverFile(name, record.Id));
        const primary = fs.readFileSync(hostPath(stateFile(name).AppliedPrimaryPath));
        expect(sha(primary) === sha(cover), `${name}'s Primary image is not its current cover file`);
        expect(cover.length < 5 * 1024 * 1024, `${name}'s cover is ${cover.length} bytes`);
        const info = webpInfo(cover);
        // Trick Film is flat grey and has no trickplay yet, and Empty has no title: those two get the name-only cover,
        // which may be a still WebP (libwebp writes identical frames as one picture, Ruling 29a).
        const nameOnly = name === 'Trick' || name === 'Empty';
        expect((record.TitleIds.length === 0) === nameOnly, `${name}'s cover shows ${record.TitleIds.length} titles`);
        if (nameOnly) {
            expect(info.still || info.durations.length > 0, `${name}'s name-only cover holds no picture`);
            report.push(`${name} name only (${info.animated ? 'animated' : 'still'})`);
            continue;
        }
        expect(
            info.width === COVER.width && info.height === COVER.height,
            `${name}'s cover is ${info.width}x${info.height}`
        );
        expect(
            info.animated && info.durations.length > 1,
            `${name}'s cover has ${info.durations.length} ANMF chunks, not more than one`
        );
        const total = info.durations.reduce((a, b) => a + b, 0);
        expect(Math.abs(total - COVER.seconds * 1000) <= 1000 / COVER.fps, `${name}'s cover lasts ${total} ms`);
        // And Jellyfin's own route gives it as it is (the plugin's filter serves an animated cover itself).
        const served = await call('GET', `/Items/${libraryIds[name]}/Images/Primary?maxWidth=300`, { as: null });
        expect(
            served.status === 200 && sha(served.bytes) === sha(cover),
            `${name}'s Primary route answered ${served.status} with other bytes`
        );
        report.push(`${name} ${info.durations.length} frames ${Math.round(cover.length / 1024)} KB`);
    }
    return report.join(', ');
});

await check('covers: Home_without_artwork_gets_a_cover_from_its_videos', async () => {
    await ensureCovers();
    const { record, ticks } = await currentCover('Home');
    const videos = new Set((await libraryItems('Home', 'Video')).map((video) => N(video.Id)));
    expect(
        record.TitleIds.length > 0 && record.TitleIds.every((id) => videos.has(N(id))),
        `the cover shows ${record.TitleIds.join(', ')}`
    );
    // A picture would zoom smoothly; a clip of the moving pattern changes from tick to tick.
    expect(ticks.length === COVER.seconds * COVER.fps, `${ticks.length} ticks`);
    const changes = ticks.slice(1).map((frame, i) => Math.abs(meanLuma(frame) - meanLuma(ticks[i])));
    expect(Math.max(...changes) > 0.5, 'the cover does not move');
    return `${record.TitleIds.length} home videos, made in a library set to ${record.Mode}`;
});

await check('covers: Library_without_eligible_titles_gets_name_only_cover', async () => {
    await ensureCovers();
    const { state, record, info, ticks } = await currentCover('Empty');
    expect(record.TitleIds.length === 0 && !state.LastError, JSON.stringify(record));
    // The plain 0x1b1d24 background (luma 29), darkened 15 %, inside the frame and away from the name.
    for (const frame of [ticks[0], ticks.at(-1)]) {
        const corner = meanLuma(frame, [40, 40, 120, 90]);
        const flat = Math.abs(meanLuma(frame, [40, 40, 80, 65]) - meanLuma(frame, [80, 65, 120, 90]));
        expect(
            corner > 15 && corner < 35 && flat < 2,
            `the background is ${corner.toFixed(1)} (even to ${flat.toFixed(1)})`
        );
    }
    const kind = info.animated ? `${info.durations.length} frames` : 'still';
    return `${kind}, background luma ${meanLuma(ticks[0], [40, 40, 120, 90]).toFixed(1)}`;
});

/** A library's state, after checking that its current cover is the newest one and is what Jellyfin serves. */
async function applied(name) {
    const state = await coverState(name);
    const record = currentRecord(state);
    expect(
        record && N(state.History[0].Id) === N(record.Id) && !state.LastError,
        `${name}: ${state.LastError ?? 'the newest cover is not current'}`
    );
    const primary = await api('GET', `/Items/${libraryIds[name]}/Images/Primary`);
    expect(
        sha(primary.bytes) === sha(fs.readFileSync(coverFile(name, record.Id))),
        `${name}'s Primary is not its cover`
    );
    return record;
}

await check('covers: Tvshows_and_mixed_libraries_build_and_apply', async () => {
    await ensureCovers();
    const series = (await libraryItems('Shows', 'Series'))[0];
    const first = (await libraryItems('Shows', 'Episode', 'ParentIndexNumber,IndexNumber')).find(
        (episode) => episode.ParentIndexNumber === 1 && episode.IndexNumber === 1
    );
    // A build checks every title of its cover against the library when it commits (GetCollectionFolders on the
    // real server), so a cover that was applied is the proof.
    await resetLibrary('Shows');
    let record = await applied('Shows');
    expect(record.TitleIds.map(N).join() === N(series.Id), `the art cover shows ${record.TitleIds.join(', ')}`);
    // Scenes: a clip of the first episode of season one, listed after its series.
    await rebuild('Shows', { Mode: 'Scenes' });
    record = await applied('Shows');
    expect(
        record.TitleIds.map(N).join() === [series.Id, first.Id].map(N).join(),
        `the scenes cover shows ${record.TitleIds.join(', ')}, not the series and S01E01`
    );
    await resetLibrary('Shows');
    // Mixed has no collection type: its two films, from scenes since they have no artwork.
    const mixed = await titlesOf('Mixed');
    await resetLibrary('Mixed');
    record = await applied('Mixed');
    expect(
        record.TitleIds.map(N).sort().join() ===
            [...mixed.values()]
                .map((film) => N(film.Id))
                .sort()
                .join(),
        `Mixed shows ${record.TitleIds.join(', ')}`
    );
    return 'Shows in art and in scenes, Mixed';
});

/** The darkest edge of a picture: the mean luma of a band 12 px deep along each side, the lowest of the four. */
function darkestEdge(frame) {
    const { width: w, height: h } = COVER;
    return Math.min(
        meanLuma(frame, [0, 0, w, 12]),
        meanLuma(frame, [0, h - 12, w, h]),
        meanLuma(frame, [0, 0, 12, h]),
        meanLuma(frame, [w - 12, 0, w, h])
    );
}

// Black bars measure 0 here, the pattern's own edges over 60. The cover's frame and darkening are off for this check.
const BAR_LUMA = 20;

await check('covers: Bars_burned_into_a_clip_never_reach_the_cover', async () => {
    await ensureCovers();
    await resetLibrary('Mixed');
    await rebuild('Mixed', { Style: { Fade: false, Frame: false, Darken: 0, Shadow: false } });
    const { record, ticks } = await currentCover('Mixed');
    expect(record.TitleIds.length === 2, `the cover shows ${record.TitleIds.length} titles`);
    const darkest = Math.min(...ticks.map(darkestEdge));
    expect(darkest > BAR_LUMA, `an edge of the cover is as dark as ${darkest.toFixed(1)}: a bar reached it`);
    // The control: the same clips scaled to the cover without the crop do show their bars to this measure.
    const size = `${COVER.width}:${COVER.height}`;
    const fill = `scale=${size}:force_original_aspect_ratio=increase,crop=${size}`;
    for (const film of ['Letterbox (2023)', 'Pillarbox (2023)']) {
        const edge = darkestEdge(sourceFrame(`/media/mixed/${film}/${film}.mkv`, 10, fill));
        expect(
            edge <= BAR_LUMA,
            `the control failed: ${film} without the crop has its darkest edge at ${edge.toFixed(1)}`
        );
    }
    await resetLibrary('Mixed');
    return `darkest edge ${darkest.toFixed(1)} over ${ticks.length} ticks; without the crop the clips have 0`;
});

await check('covers: Unreadable_title_is_replaced_by_another', async () => {
    await ensureCovers();
    await resetLibrary('Mixed');
    const films = await titlesOf('Mixed');
    const [lost, other] = [films.get('Letterbox'), films.get('Pillarbox')];
    const file = path.join(WORK, 'media', 'mixed', 'Letterbox (2023)', 'Letterbox (2023).mkv');
    const aside = path.join(WORK, 'letterbox-aside.mkv');
    // The file goes away after the scan: Jellyfin still lists the film (no real time monitor), the plugin cannot
    // read it. One slot, locked to it, so that it is tried first; the one other title of the library must take its
    // place, which in Movies would be any of a dozen. The file comes back at the end.
    fs.renameSync(file, aside);
    try {
        const state = await rebuild('Mixed', {
            Mode: 'Scenes',
            Style: { SlotCount: 1 },
            Slots: slots({ ItemId: lost.Id })
        });
        const record = currentRecord(state);
        expect(
            record.TitleIds.map(N).join() === N(other.Id),
            `the cover shows ${record.TitleIds.join(', ') || 'only the name'}, not Pillarbox`
        );
        expect(!state.LastError, state.LastError);
        const { info } = await currentCover('Mixed');
        const total = info.durations.reduce((a, b) => a + b, 0);
        expect(Math.abs(total - COVER.seconds * 1000) <= 1000 / COVER.fps, `the cover lasts ${total} ms`);
        return 'Letterbox (no file) locked, Pillarbox shown';
    } finally {
        fs.renameSync(aside, file);
        await resetLibrary('Mixed');
    }
});

// ---------- clips: where a clip starts ----------

// Dark Start is black until 9 s and blurred until 18 s. In the band of the cover above the name (inside the frame),
// a sharp tick of the pattern measures about 85, a blurred one about 5 and a black one 0 (2026-10-17).
const SHARP_BAND = [41, 41, 599, 109];
const SHARP_ENOUGH = 40;

/** The ticks of Dark Start's slot (the first) of the Movies cover, without the first and last tick of the slot. A
 *  slot whose two titles both fail is dropped, so the slots are counted from the record. */
async function darkStartTicks(darkStart) {
    const { record, ticks } = await currentCover('Movies');
    expect(N(record.TitleIds[0]) === N(darkStart.Id), 'Dark Start is not in the first slot');
    const [start, end] = slotTicks(record.TitleIds.length)[0];
    return ticks.slice(start + 1, end - 1);
}

await check('clips: Movies_scenes_cover_never_starts_in_the_dark_or_blurred_part_of_Dark_Start', async () => {
    await ensureCovers();
    await resetLibrary('Movies');
    const darkStart = (await titlesOf('Movies')).get('Dark Start');
    const least = (ticks) => Math.min(...ticks.map((frame) => sharpness(frame, SHARP_BAND)));
    // Locked without a moment, the plugin picks it: never in the first 60 %.
    await rebuild('Movies', { Mode: 'Scenes', Style: { Fade: false }, Slots: slots({ ItemId: darkStart.Id }) });
    const chosen = least(await darkStartTicks(darkStart));
    expect(
        chosen >= SHARP_ENOUGH,
        `a tick of Dark Start's slot has a sharpness of ${chosen.toFixed(1)} (at least ${SHARP_ENOUGH} wanted)`
    );
    // The controls: an admin's moment in the black part and in the blurred part fail the same measure.
    const controls = [];
    for (const moment of [3, 12]) {
        await rebuild('Movies', { Slots: slots({ ItemId: darkStart.Id, MomentSeconds: moment }) });
        const value = least(await darkStartTicks(darkStart));
        expect(value < SHARP_ENOUGH, `the control at ${moment} s passed the measure (${value.toFixed(1)})`);
        controls.push(`${moment} s ${value.toFixed(1)}`);
    }
    await resetLibrary('Movies');
    return `chosen clip ${chosen.toFixed(1)}; controls ${controls.join(', ')}`;
});

// ---------- hdr: tone mapping ----------

await check('hdr: HDR_cover_is_as_bright_as_its_SDR_twin', async () => {
    await ensureCovers();
    await resetLibrary('Movies');
    const films = await titlesOf('Movies');
    const names = ['SDR Twin', 'HDR Film', 'HDR Untagged'];
    // The three films at the same moment in three slots without fades: one cover, one overlay, the same picture.
    const state = await rebuild('Movies', {
        Mode: 'Scenes',
        Style: { SlotCount: 3, Fade: false },
        Slots: slots(...names.map((name) => ({ ItemId: films.get(name).Id, MomentSeconds: 10 })))
    });
    expect(
        currentRecord(state).TitleIds.map(N).join() === names.map((name) => N(films.get(name).Id)).join(),
        'the slots are not the three films in order'
    );
    const { ticks } = await currentCover('Movies');
    const [sdr, hdr, untagged] = slotTicks(3).map(([start, end]) => {
        const inner = ticks.slice(start + 1, end - 1);
        return inner.reduce((sum, frame) => sum + meanLuma(frame), 0) / inner.length;
    });
    const off = (value) => value / sdr - 1;
    expect(
        Math.abs(off(hdr)) <= HDR_LIMIT,
        `HDR Film's slot is ${(100 * off(hdr)).toFixed(1)} % from SDR Twin's (at most ${100 * HDR_LIMIT} % wanted)`
    );
    // The control: the same PQ picture, which Jellyfin takes for SDR and so is not tone mapped, must fail.
    expect(
        Math.abs(off(untagged)) > HDR_LIMIT,
        `the control passed: HDR Untagged (no tone mapping) is only ${(100 * off(untagged)).toFixed(1)} % from SDR Twin`
    );
    await resetLibrary('Movies');
    const percent = (value) => `${(100 * off(value)).toFixed(1)} %`;
    return `SDR Twin ${sdr.toFixed(1)}, HDR Film ${percent(hdr)}, without tone mapping ${percent(untagged)}`;
});

// ---------- trickplay: the scene finder reads Jellyfin's real tiles ----------

/** The trickplay tiles of Trick Film under a folder of WORK, found by name rather than by a layout. */
function trickTiles(within, test) {
    if (!fs.existsSync(within)) return [];
    return fs
        .readdirSync(within, { recursive: true })
        .map(String)
        .filter((file) => file.endsWith('.jpg') && !file.endsWith('.painted.jpg') && test(file))
        .map((file) => path.join(within, file));
}

/** Paints over thumbnails of a trickplay tile (320x180 each, ten to a row): `bright` gets a sharp colourful picture,
 *  each of `dark` black. Jellyfin's ffmpeg writes the result beside the tile, and it is then moved over it. */
function paintTile(tile, { bright, dark = [] }) {
    const cells = [...dark.map((cell) => ['2:v', cell]), ...(bright === undefined ? [] : [['1:v', bright]])];
    const steps = cells.map(
        ([input, cell], i) =>
            `[${i ? `p${i - 1}` : '0:v'}][${input}]overlay=${(cell % 10) * 320}:${Math.floor(cell / 10) * 180}[p${i}]`
    );
    const painted = `${tile}.painted.jpg`;
    jellyfinFfmpeg([
        '-i',
        containerPath(tile),
        '-f',
        'lavfi',
        '-i',
        'testsrc2=size=320x180:rate=1:duration=1',
        '-f',
        'lavfi',
        '-i',
        'color=black:size=320x180:duration=1',
        '-filter_complex',
        steps.join(';'),
        '-map',
        `[p${cells.length - 1}]`,
        '-frames:v',
        '1',
        '-q:v',
        '2',
        '-y',
        containerPath(painted)
    ]);
    fs.renameSync(painted, tile);
}

/** The grey level the Trick cover starts with (in its corner, away from the name), and Trick Film's own level at
 *  the moment `expected` and at `other`, decoded the same way. */
async function trickStart(film, expected, other) {
    const { record, ticks } = await currentCover('Trick');
    expect(
        record.TitleIds.map(N).join() === N(film.Id),
        `the cover does not show Trick Film: ${record.TitleIds.join(', ') || 'name only'}`
    );
    const corner = [20, 20, 200, 100];
    const level = meanLuma(ticks[0], corner);
    const [want, not] = [expected, other].map((moment) =>
        meanLuma(sourceFrame('/media/trick/Trick Film (2024)/Trick Film (2024).mkv', moment), corner)
    );
    expect(
        Math.abs(level - want) < 6 && Math.abs(level - not) > 20,
        `the cover starts at grey ${level.toFixed(1)}; ` +
            `the film is ${want.toFixed(1)} at ${expected} s and ${not.toFixed(1)} at ${other} s`
    );
    return level;
}

await check('trickplay: Scene_finder_reads_real_trickplay_tiles_in_the_data_folder_and_beside_the_media', async () => {
    await ensureCovers();
    const film = (await titlesOf('Trick')).get('Trick Film');
    const folder = (await ok('/Library/VirtualFolders')).find((f) => f.Name === 'Trick');
    const saveWithMedia = async (on) => {
        await expectStatus(204, 'POST', '/Library/VirtualFolders/LibraryOptions', {
            Id: folder.ItemId,
            LibraryOptions: { ...folder.LibraryOptions, SaveTrickplayWithMedia: on }
        });
        await runTask('MoveTrickplayImages');
    };
    const dataTiles = () =>
        trickTiles(path.join(WORK, 'config', 'data', 'trickplay'), (file) => file.includes(dashed(film.Id)));
    const mediaTiles = () => trickTiles(path.join(WORK, 'media', 'trick'), (file) => file.includes('.trickplay'));
    // Tiles in Jellyfin's data folder. A kept server that ran this check before has painted tiles: blacked out
    // again, they are as flat as the film.
    await saveWithMedia(false);
    for (const tile of dataTiles()) paintTile(tile, { dark: [0, 1, 2] });
    await resetLibrary('Trick');
    await api('PUT', covers(`/Libraries/${libraryIds.Trick}`), {
        Mode: 'Scenes',
        Style: { Darken: 0, Frame: false, Shadow: false }
    });
    // A flat picture scores under the floor at every moment, so with no usable thumbnail the film gets no scene.
    let state = await rebuild('Trick');
    expect(currentRecord(state).TitleIds.length === 0, 'the flat film got a scene without trickplay');
    await runTask('RefreshTrickplayImages');
    const item = await ok(`/Items/${film.Id}?Fields=Trickplay`);
    const info = Object.values(Object.values(item.Trickplay ?? {})[0] ?? {})[0];
    expect(info?.Interval === 10000 && info.ThumbnailCount === 3, `trickplay info: ${JSON.stringify(item.Trickplay)}`);
    const [dataTile] = dataTiles();
    expect(dataTile, 'Jellyfin wrote no trickplay tile into its data folder');
    // Its real tiles are as flat, and give no scene either.
    state = await rebuild('Trick');
    expect(currentRecord(state).TitleIds.length === 0, 'the real tiles of the flat film gave a scene');
    // Thumbnail 2 (20 s) painted: only the tile can say that this moment is worth a cover.
    paintTile(dataTile, { bright: 2 });
    await rebuild('Trick');
    const first = await trickStart(film, 20, 10);
    // Saved with the media: Jellyfin moves the tiles beside the film. Thumbnail 1 (10 s) painted, 2 blacked out.
    await saveWithMedia(true);
    const [mediaTile] = mediaTiles();
    expect(mediaTile, 'Jellyfin did not move the trickplay tiles beside the film');
    paintTile(mediaTile, { bright: 1, dark: [2] });
    await rebuild('Trick');
    const second = await trickStart(film, 10, 20);
    // Back as the run found it: tiles in the data folder, as flat as the film, and a name-only cover.
    await saveWithMedia(false);
    for (const tile of dataTiles()) paintTile(tile, { dark: [0, 1, 2] });
    await resetLibrary('Trick');
    return `data folder: from 20 s (grey ${first.toFixed(0)}); beside the media: from 10 s (grey ${second.toFixed(0)})`;
});

// ---------- audience: viewers and their limits ----------

/** The titles of a cover record that are not rated PG, by name. */
async function notPg(name, record) {
    const items = await libraryItems(name, 'Movie');
    const rating = new Map(items.map((item) => [N(item.Id), item]));
    return record.TitleIds.filter((id) => rating.get(N(id))?.OfficialRating !== 'PG').map(
        (id) => rating.get(N(id))?.Name ?? id
    );
}

/** Puts an R film on the Movies cover, locked in the first slot so that it is certainly there, and gives the state. */
async function rOnMovies(title) {
    await resetLibrary('Movies');
    const state = await rebuild('Movies', { Slots: slots({ ItemId: (await titlesOf('Movies')).get(title).Id }) });
    expect((await notPg('Movies', currentRecord(state))).includes(title), `${title} is not on the cover`);
    return state;
}

await check('audience: Kid_limited_to_PG_leaves_only_PG_titles_on_the_Movies_cover', async () => {
    await ensureCovers();
    await kidUnlimited();
    const before = await rOnMovies('Film 4');
    const started = Date.now();
    await kidPg();
    // Read where the brief says: the plugin's state file in the work folder.
    const after = await waitFor(
        'the Movies state file to name a new cover',
        async () => {
            const state = stateFile('Movies');
            if (N(state.CurrentCoverId) === N(before.CurrentCoverId)) {
                throw new Error('it still names the cover with Film 4');
            }
            return state;
        },
        { timeout: 60_000, interval: 500 }
    );
    const seconds = (Date.now() - started) / 1000;
    const wrong = await notPg('Movies', currentRecord(after));
    expect(!wrong.length, `the new cover still shows ${wrong.join(', ')}`);
    expect(
        after.Slots.every((slot) => !slot.ItemId),
        'Film 4 is still locked'
    );
    return `replaced after ${seconds.toFixed(1)} s, ${currentRecord(after).TitleIds.length} PG titles`;
});

await check('audience: Tightening_a_policy_rebuilds_without_waiting', async () => {
    await ensureCovers();
    await kidUnlimited();
    const before = await rOnMovies('Film 2');
    await expectStatus(200, 'POST', covers(`/Libraries/${libraryIds.Movies}/Pin`));
    const started = Date.now();
    await kidPg();
    const after = await newCover('Movies', before, { timeout: 60_000 });
    const seconds = (Date.now() - started) / 1000;
    expect(after.Pinned, 'the library is no longer pinned');
    expect(
        /kid/.test(after.Notice ?? '') && /Film 2/.test(after.Notice ?? '') && after.NoticeAt,
        `the notice is ${JSON.stringify(after.Notice)}`
    );
    const wrong = await notPg('Movies', currentRecord(after));
    expect(!wrong.length, `the new cover still shows ${wrong.join(', ')}`);
    await resetLibrary('Movies');
    return `${seconds.toFixed(1)} s, still pinned, notice "${after.Notice}"`;
});

await check('audience: Granting_access_rebuilds_that_library_only', async () => {
    await ensureCovers();
    await kidUnlimited();
    await kidPg([libraryIds.Home]);
    // Kid does not see Movies, so an R title may be on its cover.
    const movies = await rOnMovies('Film 6');
    const before = {};
    for (const name of Object.keys(LIBRARIES)) before[name] = N((await coverState(name)).CurrentCoverId);
    const granted = Date.now();
    await kidPg([libraryIds.Home, libraryIds.Movies]);
    const after = await newCover('Movies', movies, { timeout: 60_000 });
    slowestBuild = Math.max(slowestBuild, Date.now() - granted);
    const wrong = await notPg('Movies', currentRecord(after));
    expect(!wrong.length, `the new cover still shows ${wrong.join(', ')}`);
    // Long enough for a wrong rebuild of another library to show, measured by the builds of this run.
    await staysSame('only Movies is rebuilt', async () => {
        for (const name of Object.keys(LIBRARIES).filter((n) => n !== 'Movies')) {
            if (N((await coverState(name)).CurrentCoverId) !== before[name]) return `${name} was rebuilt too`;
        }
        return null;
    });
    await kidPg();
    await resetLibrary('Movies');
    return 'Movies replaced, the five others untouched';
});

// ---------- images: Jellyfin's own "Edit images" ----------

const editImagesPng = png(300, 200, [0x30, 0x90, 0xd0]);

await check('images: Edit_images_upload_pins_the_library', async () => {
    await ensureCovers();
    const before = await resetLibrary('Shows');
    // What the dashboard's Edit images dialog sends: the picture, in base64, as the body.
    const res = await call('POST', `/Items/${libraryIds.Shows}/Images/Primary`, {
        body: editImagesPng.toString('base64'),
        headers: { 'Content-Type': 'image/png' }
    });
    expect(res.status === 204, `the upload answered ${res.status} ${res.text.slice(0, 200)}`);
    const state = await newCover('Shows', before, { timeout: 20_000 });
    const record = currentRecord(state);
    expect(
        state.Pinned && record.Origin === 'Uploaded' && N(state.History[0].Id) === N(record.Id),
        JSON.stringify({ pinned: state.Pinned, record })
    );
    expect(state.Notice && state.NoticeAt, 'no notice');
    const kept = await api('GET', covers(`/Libraries/${libraryIds.Shows}/Covers/${record.Id}`));
    expect(kept.status === 200 && sha(kept.bytes) === sha(editImagesPng), 'the kept cover is not the uploaded picture');
    return `pinned, notice "${state.Notice}"`;
});

await check('images: Uploaded_cover_is_left_alone', async () => {
    const state = await coverState('Shows');
    const record = currentRecord(state);
    expect(
        state.Pinned && record?.Origin === 'Uploaded',
        'Shows has no upload: run images: Edit_images_upload_pins_the_library first'
    );
    // The schedule, a Generate and a change of a viewer's limits all leave it alone.
    await runTask('LibraryCoverMakerRefresh');
    expect(
        (await api('POST', covers(`/Libraries/${libraryIds.Shows}/Generate`))).status === 409,
        'Generate on the pinned library was not refused'
    );
    await kidUnlimited();
    await kidPg();
    await staysSame('the upload stays', async () => {
        const now = await coverState('Shows');
        return now.Pinned && N(now.CurrentCoverId) === N(record.Id) ? null : `replaced by ${now.CurrentCoverId}`;
    });
    const after = await coverState('Shows');
    expect(
        after.Pinned && N(after.CurrentCoverId) === N(record.Id),
        `the upload was replaced by ${after.CurrentCoverId}`
    );
    const primary = await api('GET', `/Items/${libraryIds.Shows}/Images/Primary`);
    expect(
        primary.bytes.subarray(1, 4).toString('latin1') === 'PNG',
        `the Primary starts ${primary.bytes.subarray(0, 4).toString('hex')}`
    );
    await resetLibrary('Shows');
    return 'kept through the schedule, a Generate (409) and a change of limits';
});

// ---------- api: the plugin's admin API ----------

await check('api: Non_admin_gets_403_on_every_endpoint', async () => {
    await ensureCovers();
    await kid();
    const kidToken = await signIn(KID);
    const film = (await titlesOf('Movies')).get('Film 1').Id;
    const movies = libraryIds.Movies;
    const endpoints = [
        ['GET', 'Libraries'],
        ['GET', `Libraries/${movies}`],
        ['PUT', `Libraries/${movies}`, {}],
        ['POST', `Libraries/${movies}/Generate`],
        ['POST', 'GenerateAll'],
        ['POST', `Libraries/${movies}/Upload`, 'upload'],
        ['POST', `Libraries/${movies}/Pin`],
        ['POST', `Libraries/${movies}/Unpin`],
        ['POST', `Libraries/${movies}/Restore/${N(randomUUID())}`],
        ['GET', `Libraries/${movies}/Covers/${N(randomUUID())}`],
        ['GET', `Libraries/${movies}/Titles`],
        ['GET', `Items/${film}/Frame?at=1`],
        ['GET', `Items/${film}/Backdrops`],
        ['GET', 'Settings'],
        ['PUT', 'Settings', {}]
    ];
    for (const [method, route, body] of endpoints) {
        const make = () => (body === 'upload' ? form(png(8, 8), 'a.png', 'image/png') : body);
        const asKid = await call(method, covers(`/${route}`), { body: make(), as: kidToken });
        expect(asKid.status === 403, `${method} ${route} gave ${asKid.status} to a user who is not an admin`);
        const anonymous = await call(method, covers(`/${route}`), { body: make(), as: '' });
        expect(anonymous.status === 401, `${method} ${route} gave ${anonymous.status} without a token`);
    }
    return `${endpoints.length} endpoints: 403 for kid, 401 without a token`;
});

await check('api: Generate_returns_202_and_the_card_updates', async () => {
    await ensureCovers();
    await resetLibrary('Movies');
    const card = async () => (await ok(covers('/Libraries'))).find((c) => c.Name === 'Movies');
    const before = await card();
    const res = await api('POST', covers(`/Libraries/${libraryIds.Movies}/Generate`));
    expect(res.status === 202 && res.json?.Queued === 1, `${res.status} ${res.text}`);
    const after = await waitFor(
        'the card to show the new cover',
        async () => {
            const now = await card();
            return N(now.CurrentCoverId) !== N(before.CurrentCoverId) ? now : null;
        },
        { timeout: 120_000, interval: 500 }
    );
    expect(after.CreatedAt && !after.LastError, JSON.stringify(after));
    const primary = await api('GET', `/Items/${libraryIds.Movies}/Images/Primary`);
    expect(isAnimated(primary.bytes), 'the Primary is not the animated cover');
    return `cover ${N(after.CurrentCoverId).slice(0, 8)}`;
});

await check('api: Titles_search_hides_titles_kid_may_not_see', async () => {
    await ensureCovers();
    const search = async () =>
        (await ok(covers(`/Libraries/${libraryIds.Movies}/Titles?search=Film`)))
            .map((title) => title.Name)
            .filter((name) => /^Film \d$/.test(name))
            .sort();
    await kidUnlimited();
    const all = await search();
    expect(all.length === 8, `with kid unlimited the search gives ${all.join(', ')}`);
    await kidPg();
    const limited = await search();
    expect(
        limited.join() === 'Film 1,Film 3,Film 5,Film 7',
        `with kid limited to PG the search gives ${limited.join(', ')}`
    );
    const unfiltered = await ok(covers(`/Libraries/${libraryIds.Movies}/Titles`));
    expect(
        unfiltered.length <= 50 && !unfiltered.some((title) => /^Film [2468]$/.test(title.Name)),
        'the list without a search shows an R title'
    );
    return `${all.length} titles, then ${limited.length}`;
});

await check('api: Upload_pins_and_appears_in_history', async () => {
    await ensureCovers();
    await resetLibrary('Movies');
    const picture = png(64, 36, [0xd0, 0x40, 0x40]);
    const res = await api(
        'POST',
        covers(`/Libraries/${libraryIds.Movies}/Upload`),
        form(picture, 'mine.png', 'image/png')
    );
    expect(res.status === 200 && res.json.Pinned === true, `${res.status} ${res.text.slice(0, 300)}`);
    const upload = res.json.History.find((cover) => cover.Origin === 'Uploaded');
    expect(upload && N(res.json.CurrentCoverId) === N(upload.Id), 'the upload is not in the history, or not current');
    const primary = await api('GET', `/Items/${libraryIds.Movies}/Images/Primary`);
    expect(
        primary.bytes.subarray(1, 4).toString('latin1') === 'PNG',
        `the Primary starts ${primary.bytes.subarray(0, 4).toString('hex')}`
    );
    const kept = await api('GET', covers(`/Libraries/${libraryIds.Movies}/Covers/${upload.Id}`));
    expect(
        kept.status === 200 && kept.headers.get('cache-control') === 'private, max-age=31536000, immutable',
        `${kept.status} ${kept.headers.get('cache-control')}`
    );
    expect(
        kept.headers.get('content-type') === 'image/png' && sha(kept.bytes) === sha(picture),
        'the kept file is not the upload'
    );
    return `${res.json.History.length} covers in the history`;
});

await check('api: Restore_brings_back_an_older_cover_and_pins_it', async () => {
    await ensureCovers();
    if ((await coverState('Movies')).Pinned) {
        await expectStatus(200, 'POST', covers(`/Libraries/${libraryIds.Movies}/Unpin`));
    }
    const state = await coverState('Movies');
    const older = state.History.find(
        (cover) => cover.Origin === 'Generated' && N(cover.Id) !== N(state.CurrentCoverId)
    );
    expect(older, 'Movies has no older generated cover');
    const res = await api('POST', covers(`/Libraries/${libraryIds.Movies}/Restore/${older.Id}`));
    expect(
        res.status === 200 && res.json.Pinned && N(res.json.CurrentCoverId) === N(older.Id),
        `${res.status} ${res.text.slice(0, 300)}`
    );
    const primary = await api('GET', `/Items/${libraryIds.Movies}/Images/Primary`);
    const file = await api('GET', covers(`/Libraries/${libraryIds.Movies}/Covers/${older.Id}`));
    expect(file.status === 200 && sha(file.bytes) === sha(primary.bytes), 'the Primary is not the restored cover');
    const still = await api('GET', covers(`/Libraries/${libraryIds.Movies}/Covers/${older.Id}?still=true`));
    expect(
        still.status === 200 && still.headers.get('content-type') === 'image/jpeg',
        `the still answered ${still.status} ${still.headers.get('content-type')}`
    );
    expect(
        (await api('POST', covers(`/Libraries/${libraryIds.Movies}/Restore/${N(randomUUID())}`))).status === 404,
        'an unknown cover was not 404'
    );
    await resetLibrary('Movies');
    return `restored ${N(older.Id).slice(0, 8)}`;
});

/** Whether Jellyfin's route for a library's Primary gives an animated WebP now, or something else. */
const primaryIsAnimated = async (id, query = '') =>
    isAnimated((await call('GET', `/Items/${id}/Images/Primary${query}`, { as: null })).bytes);

/** Switches the Primary format to Still, waits for the still, and always switches back and waits for the animation. */
async function throughStill(id, query) {
    try {
        const res = await api('PUT', covers('/Settings'), { PrimaryFormat: 'Still' });
        expect(res.status === 202 && res.json.PrimaryFormat === 'Still', `${res.status} ${res.text}`);
        await waitFor('the still Primary', async () => !(await primaryIsAnimated(id, query)), {
            timeout: 60_000,
            interval: 500
        });
    } finally {
        await api('PUT', covers('/Settings'), { PrimaryFormat: 'Animated' });
    }
    await waitFor('the animated Primary again', () => primaryIsAnimated(id, query), { timeout: 60_000, interval: 500 });
}

await check('api: Switching_to_still_reapplies_a_jpeg_primary', async () => {
    await ensureCovers();
    await resetLibrary('Movies');
    let jpeg = false;
    const id = libraryIds.Movies;
    try {
        await expectStatus(202, 'PUT', covers('/Settings'), { PrimaryFormat: 'Still' });
        jpeg = await waitFor(
            'a JPEG Primary',
            async () =>
                (await api('GET', `/Items/${id}/Images/Primary`)).bytes.subarray(0, 2).toString('hex') === 'ffd8',
            { timeout: 60_000, interval: 500 }
        );
    } finally {
        await api('PUT', covers('/Settings'), { PrimaryFormat: 'Animated' });
    }
    await waitFor('the animated Primary again', () => primaryIsAnimated(id), { timeout: 60_000, interval: 500 });
    return jpeg && 'a JPEG, then the animated WebP again';
});

await check('api: Out_of_range_input_is_refused_with_400_naming_the_field', async () => {
    await ensureCovers();
    const put = (body) => api('PUT', covers(`/Libraries/${libraryIds.Movies}`), body);
    const before = JSON.stringify((await coverState('Movies')).Style);
    const cases = [
        [{ Style: { Darken: 71 } }, 'Darken'],
        [{ Style: { Darken: -1 } }, 'Darken'],
        [{ Style: { LengthSeconds: 13 } }, 'LengthSeconds'],
        [{ Style: { LengthSeconds: 1 } }, 'LengthSeconds'],
        [{ Style: { SlotCount: 6 } }, 'SlotCount'],
        [{ Style: { SlotCount: 0 } }, 'SlotCount'],
        [{ Style: { TextSize: 39 } }, 'TextSize'],
        [{ Style: { TextSize: 201 } }, 'TextSize'],
        [{ Style: { Font: 'Comic Sans' } }, 'Font'],
        [{ RenewEveryDays: 366 }, 'RenewEveryDays'],
        [{ Style: { Darken: 40, LengthSeconds: 99 } }, 'LengthSeconds'],
        [{ Mode: 'Nonsense' }, null]
    ];
    for (const [body, field] of cases) {
        const res = await put(body);
        expect(
            res.status === 400 && (!field || res.text.includes(field)),
            `${JSON.stringify(body)} gave ${res.status} ${res.text.slice(0, 200)}`
        );
    }
    expect(JSON.stringify((await coverState('Movies')).Style) === before, 'a refused change was saved');
    for (const body of [
        { PrimaryFormat: 'Nope' },
        { OutputWidth: 641 },
        { OutputWidth: 640, OutputHeight: 480 },
        { DefaultMode: 'Nope' }
    ]) {
        const res = await api('PUT', covers('/Settings'), body);
        expect(res.status === 400, `Settings ${JSON.stringify(body)} gave ${res.status}`);
    }
    return `${cases.length} library changes and 4 settings refused, nothing saved`;
});

// ---------- page: the dashboard page in a real browser ----------

await pageChecks({
    check,
    wanted,
    JF,
    ADMIN,
    ok,
    covers,
    coverState,
    currentRecord,
    resetLibrary,
    newCover,
    waitFor,
    expect,
    png,
    libraryIds,
    N,
    kidUnlimited,
    primaryIsAnimated
});

// ---------- covers: every file the plugin wrote ----------

await check('covers: Every_cover_file_passes_webpinfo', async () => {
    await ensureCovers();
    const store = path.join(WORK, 'config', 'data', 'library-cover-maker');
    const appliedFolder = path.join(WORK, 'config', 'root', 'default');
    const script =
        'for f in $(find /store /applied -name "*.webp"); do ' +
        'if webpinfo -diag -summary "$f" | grep -q "No error detected."; ' +
        'then echo "ok $f"; else echo "bad $f"; fi; done';
    const lines = tools(['sh', '-c', script], { mounts: { [store]: '/store', [appliedFolder]: '/applied' } })
        .toString()
        .trim()
        .split('\n')
        .filter(Boolean);
    const bad = lines.filter((line) => !line.startsWith('ok '));
    expect(lines.length > 0, 'no WebP file found');
    expect(!bad.length, `webpinfo found errors in ${bad.join(', ')}`);
    return `${lines.length} files, "No error detected." for each`;
});

// ---------- filter: the plugin serves its animated covers itself ----------

let served;

/** Movies with a generated animated cover, and what a client needs to ask for it. */
async function servedCover() {
    if (served) return served;
    await ensureCovers();
    await resetLibrary('Movies');
    const state = stateFile('Movies');
    const file = fs.readFileSync(hostPath(state.AppliedPrimaryPath));
    expect(webpInfo(file).animated, 'Movies does not show an animated cover');
    const me = await ok('/Users/Me');
    const tag = (await ok(`/UserViews?userId=${me.Id}`)).Items.find((view) => N(view.Id) === libraryIds.Movies)
        ?.ImageTags?.Primary;
    expect(tag, 'Movies has no Primary image tag');
    served = { file, tag, id: libraryIds.Movies };
    return served;
}

/** Asks for the cover through every route of Jellyfin's item image action without a token, three times for the two
 *  a library card uses, and checks each answer is the stored file. */
async function requestEveryRoute({ file, tag, id }, base = '') {
    const routes = [
        [`/Items/${id}/Images/Primary?fillWidth=326&fillHeight=184&quality=96&tag=${tag}`, 3],
        [`/Items/${id}/Images/Primary?maxWidth=300`, 3],
        [`/Items/${id}/Images/Primary?quality=50`, 1],
        [`/Items/${id}/Images/Primary/0?maxWidth=300`, 1],
        [`/Items/${id}/Images/Primary/?maxWidth=300`, 1],
        [`/Items/${id}/Images/Primary?imageIndex=0&maxWidth=300`, 1],
        [`/Items/${id}/Images/Primary/0/${tag}/Webp/300/300/0/0`, 1],
        [`/items/${id}/images/primary?fillWidth=200`, 1],
        [`/Items/${dashed(id)}/Images/Primary?maxWidth=120`, 1]
    ];
    let count = 0;
    for (const [route, times] of routes) {
        for (let i = 0; i < times; i++) {
            // The second and third ask without image/webp in Accept: the file is still the answer.
            const res = await call('GET', route, {
                as: null,
                base,
                headers: { Accept: i ? 'image/jpeg,image/png' : 'image/webp,*/*' }
            });
            expect(
                res.status === 200 && res.headers.get('content-type') === 'image/webp',
                `${route} answered ${res.status} ${res.headers.get('content-type')}`
            );
            expect(sha(res.bytes) === sha(file), `${route} is not the stored cover (${res.bytes.length} bytes)`);
            count++;
        }
    }
    return count;
}

/** A filter check: runs with Movies showing an animated cover. */
const filterCheck = (name, fn, options) => check(`filter: ${name}`, async () => fn(await servedCover()), options);

await filterCheck('Animated_cover_never_reaches_the_image_processor', async (cover) => {
    const before = decodeErrors(serverLog());
    const count = await requestEveryRoute(cover);
    await sleep(1_000);
    const gained = decodeErrors(serverLog()) - before;
    expect(gained === 0, `the log gained ${gained} "Cannot decode images with multiple frames" lines`);
    return `${count} requests without a token, each the stored bytes, no decode error`;
});

await filterCheck('Tagged_and_untagged_answers_carry_their_cache_headers', async ({ tag, id }) => {
    const tagged = await call('GET', `/Items/${id}/Images/Primary?tag=${tag}&maxWidth=300`, { as: null });
    expect(
        tagged.headers.get('cache-control') === 'public, max-age=31536000, immutable' &&
            tagged.headers.get('etag') === `"${tag}"`,
        `tagged: ${tagged.headers.get('cache-control')} ${tagged.headers.get('etag')}`
    );
    const legacy = await call('GET', `/Items/${id}/Images/Primary/0/${tag}/Webp/300/300/0/0`, { as: null });
    expect(
        legacy.headers.get('cache-control') === 'public, max-age=31536000, immutable' &&
            legacy.headers.get('etag') === `"${tag}"`,
        `legacy route: ${legacy.headers.get('cache-control')} ${legacy.headers.get('etag')}`
    );
    const plain = await call('GET', `/Items/${id}/Images/Primary?maxWidth=300`, { as: null });
    expect(
        plain.headers.get('cache-control') === 'public' && /^"[0-9a-f]{32}"$/.test(plain.headers.get('etag') ?? ''),
        `untagged: ${plain.headers.get('cache-control')} ${plain.headers.get('etag')}`
    );
    return 'tagged: immutable, the tag as ETag; untagged: public, the file ETag';
});

await filterCheck('Head_requests_answer_with_the_length_and_no_body', async ({ file, tag, id }) => {
    for (const route of [
        `/Items/${id}/Images/Primary?maxWidth=300`,
        `/Items/${id}/Images/Primary/0/${tag}/Webp/300/300/0/0`
    ]) {
        const res = await call('HEAD', route, { as: null });
        expect(
            res.status === 200 && Number(res.headers.get('content-length')) === file.length && res.bytes.length === 0,
            `HEAD ${route}: ${res.status}, length ${res.headers.get('content-length')}, body ${res.bytes.length}`
        );
    }
    return `${file.length} bytes announced, none sent`;
});

await filterCheck('Conditional_and_range_requests', async ({ file, tag, id }) => {
    const route = `/Items/${id}/Images/Primary?tag=${tag}`;
    const match = await call('GET', route, { as: null, headers: { 'If-None-Match': `"${tag}"` } });
    expect(match.status === 304, `If-None-Match gave ${match.status}`);
    // A date in the future is no date (RFC 9110), so the cover's own Last-Modified is sent back.
    const modified = (await call('HEAD', route, { as: null })).headers.get('last-modified');
    const since = await call('GET', route, { as: null, headers: { 'If-Modified-Since': modified } });
    expect(modified && since.status === 304, `If-Modified-Since ${modified} gave ${since.status}`);
    const range = await call('GET', route, { as: null, headers: { Range: 'bytes=0-99' } });
    expect(
        range.status === 206 && range.bytes.length === 100 && sha(range.bytes) === sha(file.subarray(0, 100)),
        `Range gave ${range.status} with ${range.bytes.length} bytes`
    );
    return '304, 304 and 206 with 100 bytes';
});

await filterCheck('Odd_tags_are_served', async ({ file, id }) => {
    for (const odd of ['%C3%A9', '%01']) {
        const res = await call('GET', `/Items/${id}/Images/Primary?tag=${odd}`, { as: null });
        expect(
            res.status === 200 &&
                sha(res.bytes) === sha(file) &&
                res.headers.get('cache-control') === 'public, max-age=31536000, immutable' &&
                /^"[0-9a-f]{32}"$/.test(res.headers.get('etag') ?? ''),
            `tag ${odd}: ${res.status} ${res.headers.get('cache-control')} ${res.headers.get('etag')}`
        );
    }
    return 'a non-ASCII and a control character tag: 200 with the file ETag';
});

/** Fails as soon as the log has more decode errors than `before`, looking for 1.5 s: Jellyfin's console log is
 *  written a moment after its answer. Jellyfin sends the original file even when its processor fails, so the same
 *  bytes alone do not show that the plugin answered; the missing decode error does. */
async function expectNoDecodeErrorSince(before, what) {
    const end = Date.now() + 1_500;
    for (;;) {
        const gained = decodeErrors(serverLog()) - before;
        expect(gained === 0, `${what}: the log gained ${gained} "Cannot decode images with multiple frames" lines`);
        if (Date.now() > end) return;
        await sleep(250);
    }
}

await filterCheck('Requests_with_a_token_are_served_the_same', async ({ file, id }) => {
    const before = decodeErrors(serverLog());
    const res = await api('GET', `/Items/${id}/Images/Primary?maxWidth=301`);
    expect(res.status === 200 && sha(res.bytes) === sha(file), `${res.status}, ${res.bytes.length} bytes`);
    await expectNoDecodeErrorSince(before, 'the admin token');
    return 'the stored bytes, no decode error';
});

// An API key authenticates with an empty user id. Jellyfin's own action answers it for every library (200, its
// processor's error logged, the original sent), so the plugin answers it as it answers an anonymous request.
await filterCheck('Requests_with_an_api_key_are_served_the_same', async ({ file, id }) => {
    const app = `lcm-e2e-${randomUUID().slice(0, 8)}`;
    await expectStatus(204, 'POST', `/Auth/Keys?app=${app}`);
    const key = (await ok('/Auth/Keys')).Items.find((item) => item.AppName === app)?.AccessToken;
    expect(key, `the API key of ${app} is not listed`);
    try {
        const before = decodeErrors(serverLog());
        const res = await call('GET', `/Items/${id}/Images/Primary?maxWidth=302`, { as: key });
        expect(res.status === 200 && sha(res.bytes) === sha(file), `${res.status}, ${res.bytes.length} bytes`);
        await expectNoDecodeErrorSince(before, 'the API key');
        // Jellyfin itself answers the key for an image it resizes: a film's backdrop.
        const film = (await titlesOf('Movies')).get('Film 1');
        const backdrop = await call('GET', `/Items/${film.Id}/Images/Backdrop?maxWidth=100`, { as: key });
        expect(backdrop.status === 200, `a backdrop with the key answered ${backdrop.status}`);
        return 'the stored bytes, no decode error; Jellyfin answers the key for a backdrop too';
    } finally {
        if (key) await expectStatus(204, 'DELETE', `/Auth/Keys/${key}`);
    }
});

await filterCheck('Cross_origin_requests_get_the_cors_header', async ({ id }) => {
    const res = await call('GET', `/Items/${id}/Images/Primary?maxWidth=300`, {
        as: null,
        headers: { Origin: 'http://example.test' }
    });
    expect(
        res.headers.get('access-control-allow-origin') === '*',
        `Access-Control-Allow-Origin is ${res.headers.get('access-control-allow-origin')}`
    );
    return 'Access-Control-Allow-Origin: *';
});

await filterCheck('Other_pictures_pass_through_to_jellyfin', async () => {
    // A still upload on Home and a film's backdrop, both resized: Jellyfin's own processor answers.
    const picture = png(400, 240, [0x20, 0xa0, 0x60]);
    await resetLibrary('Home');
    await expectStatus(
        200,
        'POST',
        covers(`/Libraries/${libraryIds.Home}/Upload`),
        form(picture, 'home.png', 'image/png')
    );
    try {
        const home = await call('GET', `/Items/${libraryIds.Home}/Images/Primary?maxWidth=100`, { as: null });
        expect(
            home.status === 200 && home.bytes.length > 0 && sha(home.bytes) !== sha(picture) && !isAnimated(home.bytes),
            `Home: ${home.status} ${home.headers.get('content-type')} ${home.bytes.length} bytes`
        );
        const film = (await titlesOf('Movies')).get('Film 1');
        const backdrop = await call('GET', `/Items/${film.Id}/Images/Backdrop?maxWidth=100`, { as: null });
        expect(
            backdrop.status === 200 && /^image\//.test(backdrop.headers.get('content-type') ?? ''),
            `backdrop: ${backdrop.status}`
        );
        const type = home.headers.get('content-type');
        return `Home resized to ${home.bytes.length} bytes (${type}), backdrop ${backdrop.bytes.length} bytes`;
    } finally {
        await resetLibrary('Home');
    }
});

await filterCheck('Still_format_is_left_to_jellyfin_and_the_animation_comes_back', async ({ id }) => {
    const before = decodeErrors(serverLog());
    await throughStill(id, '?maxWidth=300');
    served = undefined;
    const cover = await servedCover();
    const res = await call('GET', `/Items/${id}/Images/Primary?maxWidth=300`, { as: null });
    expect(sha(res.bytes) === sha(cover.file), 'after the switch back the stored bytes are not served');
    expect(decodeErrors(serverLog()) === before, 'decode errors appeared');
    return 'still from Jellyfin, then the stored animated bytes again';
});

// Seen first as one decode error in the log while the page's Generate all ran, then made to happen at will: a
// client that keeps asking for a library's card while the plugin applies new covers to it. The requests landed
// between Jellyfin writing the new Primary and the plugin saving the state that names it, and while Jellyfin held
// the file to write it; the filter now serves any animated Primary of a library and waits out the write. Jellyfin's
// image saver also cannot write a file that a request is reading, and then saved the cover to the library's
// metadata folder instead ("Will retry saving to"): the plugin now moves its own cover aside before each save, so
// every apply writes a new file (a new inode) that no one reads, and nothing is left beside it.
const APPLIES = 6;

await filterCheck('Covers_being_applied_never_reach_the_image_processor', async ({ id }) => {
    const log = serverLog();
    const before = decodeErrors(log);
    const movedBefore = savedElsewhere(log);
    const answers = {};
    let stop = false;
    let asked = 0;
    // One client, one request after the other, as fast as the server answers.
    const client = (async () => {
        while (!stop) {
            let outcome;
            try {
                const res = await call('GET', `/Items/${id}/Images/Primary?maxWidth=300&n=${asked}`, { as: null });
                outcome =
                    res.status === 200 && isWhole(res.bytes) && isAnimated(res.bytes)
                        ? 'animated'
                        : `${res.status} ${res.headers.get('content-type')} ${res.bytes.length} bytes`;
            } catch (e) {
                outcome = `no answer (${e.cause?.code ?? e.message})`;
            }
            answers[outcome] = (answers[outcome] ?? 0) + 1;
            asked++;
        }
    })();
    const files = [];
    try {
        for (let i = 0; i < APPLIES; i++) {
            await rebuild('Movies');
            const primary = hostPath(stateFile('Movies').AppliedPrimaryPath);
            files.push({ primary, inode: fs.statSync(primary).ino });
        }
    } finally {
        stop = true;
        await client;
    }
    served = undefined;
    const after = serverLog();
    const gained = decodeErrors(after) - before;
    const moved = savedElsewhere(after) - movedBefore;
    const summary = Object.entries(answers)
        .map(([outcome, n]) => `${n} ${outcome}`)
        .join(', ');
    expect(
        gained === 0 && Object.keys(answers).join() === 'animated',
        `during ${APPLIES} applies: ${gained} decode errors in the log; answers: ${summary}`
    );
    expect(moved === 0, `Jellyfin saved ${moved} of the ${APPLIES} covers elsewhere ("Will retry saving to")`);
    const paths = new Set(files.map((file) => file.primary));
    expect(paths.size === 1, `the Primary moved between ${[...paths].join(', ')}`);
    const reused = files.filter((file, i) => i > 0 && file.inode === files[i - 1].inode).length;
    expect(reused === 0, `${reused} applies wrote over the previous file in place`);
    const primary = files[0].primary;
    const left = fs.readdirSync(path.dirname(primary)).filter((name) => name.startsWith(`.${path.basename(primary)}.`));
    expect(left.length === 0, `left beside the cover: ${left.join(', ')}`);
    return `${asked} requests during ${APPLIES} applies, all the animated cover, no decode error, each a new file`;
});

// Jellyfin's action looks the library up for the request's user and answers 404 when that user may not see it; an
// anonymous request (an <img> on the login page) gets every library's image, as before.
await filterCheck('Users_who_may_not_see_a_library_do_not_get_its_cover', async () => {
    await kid((policy) => {
        policy.MaxParentalRating = null;
        policy.EnableAllFolders = false;
        policy.EnabledFolders = [libraryIds.Home];
    });
    const kidToken = await signIn(KID);
    try {
        served = undefined;
        const { file, id } = await servedCover();
        const route = `/Items/${id}/Images/Primary?maxWidth=300`;
        const denied = await call('GET', route, { as: kidToken });
        expect(denied.status === 404, `kid without Movies got ${denied.status} (${denied.bytes.length} bytes)`);
        const anonymous = await call('GET', route, { as: null });
        expect(anonymous.status === 200 && sha(anonymous.bytes) === sha(file), `anonymous got ${anonymous.status}`);
        await kidUnlimited();
        const before = decodeErrors(serverLog());
        const allowed = await call('GET', route, { as: kidToken });
        expect(allowed.status === 200 && isAnimated(allowed.bytes), `kid with Movies got ${allowed.status}`);
        await expectNoDecodeErrorSince(before, 'kid with Movies');
        return 'kid without Movies: 404, as in Jellyfin; anonymous and kid with Movies: the cover, no decode error';
    } finally {
        await kidUnlimited();
        served = undefined;
    }
});

// The last checks restart Jellyfin, two of them with another network.xml, and put it back afterwards.

const networkXml = path.join(WORK, 'config', 'config', 'network.xml');
const NETWORK_DEFAULTS = {
    BaseUrl: '',
    LocalNetworkSubnets: '',
    RemoteIPFilter: '',
    IsRemoteIPFilterBlacklist: 'false'
};

/** Stops Jellyfin, sets elements of its network.xml, runs `whileStopped`, starts it, and waits until its log says
 *  it started. */
async function restartWith(elements, whileStopped = () => {}) {
    run('docker', ['stop', '-t', '30', CONTAINER]);
    whileStopped();
    let text = fs.readFileSync(networkXml, 'utf8');
    for (const [name, inner] of Object.entries(elements)) {
        const pattern = new RegExp(`<${name}\\s*/>|<${name}>[\\s\\S]*?</${name}>`);
        expect(pattern.test(text), `network.xml has no ${name}`);
        text = text.replace(pattern, inner ? `<${name}>${inner}</${name}>` : `<${name} />`);
    }
    fs.writeFileSync(networkXml, text);
    run('docker', ['start', CONTAINER]);
    // Only what this start logged: the time Docker gives for it, to the nanosecond.
    const started = run('docker', ['inspect', '-f', '{{.State.StartedAt}}', CONTAINER]).toString().trim();
    await waitFor('Jellyfin to start again', async () => /Startup complete/.test(serverLog(started)), {
        timeout: 120_000
    });
    return started;
}

await filterCheck('Base_url_prefix_is_honored', async (cover) => {
    const before = decodeErrors(serverLog());
    await restartWith({ ...NETWORK_DEFAULTS, BaseUrl: '/jf' });
    try {
        await waitForServer('/jf');
        const count = await requestEveryRoute(cover, '/jf');
        expect(decodeErrors(serverLog()) === before, 'decode errors appeared under /jf');
        return `${count} requests under /jf, each the stored bytes`;
    } finally {
        await restartWith(NETWORK_DEFAULTS);
        await waitForServer();
    }
});

await filterCheck('Remote_ip_blocklist_still_applies', async ({ id, tag }) => {
    // This machine's address as Docker shows it to Jellyfin, made remote and then blocked.
    const gateway = run('docker', [
        'inspect',
        '-f',
        '{{range .NetworkSettings.Networks}}{{.Gateway}} {{end}}',
        CONTAINER
    ])
        .toString()
        .trim()
        .split(' ')[0];
    expect(/^\d+\.\d+\.\d+\.\d+$/.test(gateway), `no gateway address for ${CONTAINER}: "${gateway}"`);
    const started = await restartWith({
        ...NETWORK_DEFAULTS,
        LocalNetworkSubnets: '<string>10.99.0.0/16</string>',
        RemoteIPFilter: `<string>${gateway}/32</string>`,
        IsRemoteIPFilterBlacklist: 'true'
    });
    try {
        // Jellyfin logs that it started a moment before the real server listens, and its start-up page answers
        // /System/Info/Public with 200: the real server, blocking this machine, answers it with 503.
        await waitFor(
            'the real server to answer',
            async () => (await call('GET', '/System/Info/Public', { as: null }).catch(() => ({}))).status === 503,
            { timeout: 60_000, interval: 500 }
        );
        for (const route of [
            `/Items/${id}/Images/Primary?maxWidth=300`,
            `/Items/${id}/Images/Primary`,
            `/Items/${id}/Images/Primary/0/${tag}/Webp/300/300/0/0`,
            '/System/Info/Public'
        ]) {
            const res = await call('GET', route, { as: null });
            expect(res.status === 503, `${route} answered ${res.status} to a blocked address`);
        }
        expect(/RejectDueToIPBlocklist/.test(serverLog(started)), 'Jellyfin did not log the block');
        return `${gateway} blocked: 503 on every cover route, as on the rest`;
    } finally {
        await restartWith(NETWORK_DEFAULTS);
        await waitForServer();
        token = await signIn(ADMIN);
    }
});

// ---------- restart: slow, each restarts Jellyfin ----------

// The render queue deletes the plugin's temporary folder at every start, and the scene finder writes its result
// files there: the first Scenes build after a start once found every clip unreadable and made the name-only cover.
await check('restart: First_scenes_build_after_a_restart_has_clips', async () => {
    await ensureCovers();
    await resetLibrary('Mixed');
    const temp = path.join(WORK, 'config', 'data', 'library-cover-maker', 'tmp');
    await restartWith(NETWORK_DEFAULTS);
    await waitForServer();
    token = await signIn(ADMIN);
    try {
        expect(!fs.existsSync(temp), 'the temporary folder is there after the start, so this would prove nothing');
        const state = await rebuild('Mixed', { Mode: 'Scenes' });
        const record = currentRecord(state);
        expect(record.TitleIds.length > 0 && !state.LastError, `the cover shows only the name: ${state.LastError}`);
        return `the first build after the start shows ${record.TitleIds.length} titles`;
    } finally {
        await resetLibrary('Mixed');
    }
});

// The applier renames its cover to .<name>.<32 hex> right before Jellyfin's SaveImage writes the new one. Jellyfin
// stopped in between left the library's Primary missing (404 and an error per request, a refresh did not help):
// the plugin's start puts the cover back.
await check('restart: Cover_set_aside_by_a_stop_during_an_apply_is_put_back', async () => {
    const { file, id } = await servedCover();
    const primary = hostPath(stateFile('Movies').AppliedPrimaryPath);
    const aside = path.join(path.dirname(primary), `.${path.basename(primary)}.${randomUUID().replaceAll('-', '')}`);
    try {
        await restartWith(NETWORK_DEFAULTS, () => fs.renameSync(primary, aside)); // as the applier renames it
        await waitForServer();
        token = await signIn(ADMIN);
        const before = decodeErrors(serverLog());
        const res = await call('GET', `/Items/${id}/Images/Primary?maxWidth=300`, { as: null });
        expect(res.status === 200 && sha(res.bytes) === sha(file), `the cover answered ${res.status}`);
        await expectNoDecodeErrorSince(before, 'the cover put back');
        const left = fs
            .readdirSync(path.dirname(primary))
            .filter((name) => name.startsWith(`.${path.basename(primary)}.`));
        expect(left.length === 0, `left in the folder: ${left.join(', ')}`);
        expect(/was put back from/.test(serverLog()), 'the plugin did not log that it put the cover back');
        return 'the cover served again after the start, nothing left beside it';
    } finally {
        if (fs.existsSync(aside) && !fs.existsSync(primary)) fs.renameSync(aside, primary);
        served = undefined;
    }
});

// ---------- log ----------

// Keep this check last: it reads the whole log up to this point. Jellyfin's own decode errors are counted for the
// record only: the filter checks assert none in their windows.
await check('log: server log has no plugin [ERR] or [FTL] entries', async () => {
    const log = serverLog();
    const errors = pluginErrorEntries(log);
    expect(errors.length === 0, `${errors.length} entries:\n${errors.slice(0, 5).join('\n')}`);
    const decode = decodeErrors(log);
    const lines = log.split('\n').length;
    return `${lines} log lines read; Jellyfin logged ${decode} "Cannot decode images..." in the whole run`;
});

finish();
