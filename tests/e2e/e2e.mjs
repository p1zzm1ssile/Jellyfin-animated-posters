// End-to-end checks against a real Jellyfin 12 that run.sh started with the plugin installed and the sample
// media of make-media.sh. Needs only node 20 or later.
//
//   node e2e.mjs [names...]    run the checks whose names contain one of the words (all of them with none)
//
// A word that matches no check name is a mistake, so it fails the run, naming the word and listing the checks.
//
// JF is the server's address (default http://127.0.0.1:18096) and CONTAINER the name of its Docker container
// (default lcm-e2e), whose log the last check reads. run.sh sets both.
//
// The checks that set the server up (wizard, admin, libraries, scan) always run, because every other check
// depends on them, and a failed one ends the run. They also work on a server that is already set up, so a
// server kept with KEEP=1 can be checked again without rebuilding anything.
//
// Requests carry the "MediaBrowser" Authorization header and nothing else: on Jellyfin 12.2 a request that
// sends its token as X-Emby-Token, X-MediaBrowser-Token or an api_key query parameter is answered with 401.
//
// Still to add, each with the feature it tests:
//  - the LibraryCoverMakerRefresh task runs, and every library's Primary image is an animated WebP (more than
//    one ANMF chunk) under 5 MB
//  - the Home library, which has no artwork, got a cover made of scenes
//  - webpinfo -diag -summary (libwebp-tools in alpine:3.22) prints "No error detected." for every cover file
//  - in scenes mode the Movies cover never starts inside the first 60 % of Dark Start
//  - the cover of HDR Film is within 18 % of the mean brightness of the cover of SDR Twin (why 18 % and what it
//    was measured on: make-media.sh, at the HDR pair)
//  - a user "kid", limited to PG after the covers exist, leaves the Movies cover (TitleIds in the plugin's
//    state file under the work folder) with only PG titles within 60 s
//  - Library_without_eligible_titles_gets_name_only_cover and Unreadable_title_is_replaced_by_another

import { spawnSync } from 'node:child_process';

const JF = process.env.JF ?? 'http://127.0.0.1:18096';
const CONTAINER = process.env.CONTAINER ?? 'lcm-e2e';

// The server is a throwaway one that listens on 127.0.0.1 only, so the account is a test value.
const ADMIN = { Name: 'admin', Password: 'admin' };
const PLUGIN = { name: 'Library Cover Maker', id: 'ba3b88b5029c4a6f90b3734af8385652' };

// The libraries, over the folders make-media.sh fills. itemTypes are what the library's metadata options list.
const LIBRARIES = {
    Movies: { type: 'movies', path: '/media/movies', itemTypes: ['Movie'] },
    Shows: { type: 'tvshows', path: '/media/shows', itemTypes: ['Series', 'Season', 'Episode'] },
    Home: { type: 'homevideos', path: '/media/home', itemTypes: ['Video'] }
};

// The films of make-media.sh as Jellyfin should read them. Later checks lean on every line: the ratings (a viewer
// limited to PG), the backdrops (artwork or not), the video range (HDR against SDR) and the missing duration.
const SAMPLE_FILMS = new Map([
    ...Array.from({ length: 8 }, (_, i) => [`Film ${i + 1}`, { rating: i % 2 ? 'R' : 'PG', backdrop: true }]),
    ['Dark Start', { rating: 'PG' }],
    ['HDR Film', { rating: 'PG', range: 'HDR10' }],
    ['SDR Twin', { rating: 'PG', range: 'SDR' }],
    ['Live', { rating: 'PG', noDuration: true }]
]);

const AUTH = `MediaBrowser Client="e2e", Device="e2e", DeviceId="${CONTAINER}", Version="1.0"`;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const words = process.argv.slice(2);

let token = '';
let passed = 0;
let failed = 0;
/** The words that matched the name of some check, and the names of all checks reached so far. */
const matchedWords = new Set();
const checkNames = [];

/** Each library's id by name, filled in by the libraries check for the checks after it. */
const libraryIds = {};

// ---------- Helpers ----------

/** Calls Jellyfin. Gives { status, json, text }. */
async function api(method, path, body) {
    const headers = { Authorization: AUTH + (token ? `, Token="${token}"` : '') };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const res = await fetch(JF + path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(120_000)
    });
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
    return { status: res.status, json, text };
}

function expect(condition, message) {
    if (!condition) throw new Error(message);
}

/** GETs a path that must answer 200 and returns its JSON. */
async function ok(path) {
    const res = await api('GET', path);
    expect(res.status === 200, `${path} answered ${res.status} ${res.text.slice(0, 200)}`);
    return res.json;
}

/** Sends a request that must answer with `status`. */
async function expectStatus(status, method, path, body) {
    const res = await api(method, path, body);
    expect(res.status === status, `${method} ${path} answered ${res.status} (wanted ${status}) ${res.text.slice(0, 200)}`);
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

/** Runs one check and prints its result. A check that sets the server up is never skipped by the name filter. */
async function check(name, fn, { setup = false } = {}) {
    checkNames.push(name);
    const hits = words.filter((word) => name.toLowerCase().includes(word.toLowerCase()));
    for (const word of hits) matchedWords.add(word);
    if (!setup && words.length && !hits.length) return true;
    try {
        const detail = await fn();
        passed++;
        console.log(`PASS  ${name}${detail ? `  (${detail})` : ''}`);
        return true;
    } catch (e) {
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
    console.log(`${passed}/${passed + failed} passed`);
    process.exit(failed ? 1 : 0);
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
        .filter((entry) => (entry.level === 'ERR' || entry.level === 'FTL') && entry.lines.some((line) => /LibraryCoverMaker|Library Cover Maker/i.test(line)))
        .map((entry) => entry.lines.join('\n'));
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
        EnableTrickplayImageExtraction: false
    };
}

await setupCheck('startup wizard completes and the admin signs in', async () => {
    // While it starts, Jellyfin answers from a start-up page: /System/Info/Public gives 200 at once, but without
    // StartupWizardCompleted, and everything else gives 503. Only the real server's answer has that key.
    const info = await waitFor('Jellyfin to finish starting', async () => {
        const res = await api('GET', '/System/Info/Public').catch((e) => {
            throw new Error(`no answer (${e.cause?.code ?? e.cause?.message ?? e.message})`);
        });
        if (typeof res.json?.StartupWizardCompleted !== 'boolean') throw new Error(`still on the start-up page (answered ${res.status})`);
        return res.json;
    });
    if (!info.StartupWizardCompleted) {
        await waitFor('the startup wizard to answer', async () => {
            const res = await api('GET', '/Startup/Configuration');
            if (res.status !== 200) throw new Error(`answered ${res.status}`);
            return true;
        });
        await expectStatus(204, 'POST', '/Startup/Configuration', { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' });
        await expectStatus(200, 'GET', '/Startup/User');
        await expectStatus(204, 'POST', '/Startup/User', ADMIN);
        await expectStatus(204, 'POST', '/Startup/RemoteAccess', { EnableRemoteAccess: true, EnableAutomaticPortMapping: false });
        await expectStatus(204, 'POST', '/Startup/Complete');
    }
    const login = await api('POST', '/Users/AuthenticateByName', { Username: ADMIN.Name, Pw: ADMIN.Password });
    expect(login.status === 200, `the admin could not sign in: ${login.status} ${login.text.slice(0, 200)}`);
    token = login.json.AccessToken;
    expect(login.json.User.Policy.IsAdministrator, 'the account is not an administrator');
    return `Jellyfin ${info.Version}`;
});

await setupCheck('libraries Movies, Shows and Home exist with internet metadata off', async () => {
    const existing = new Set((await ok('/Library/VirtualFolders')).map((folder) => folder.Name));
    for (const [name, library] of Object.entries(LIBRARIES)) {
        if (existing.has(name)) continue;
        await expectStatus(204, 'POST', `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=${library.type}&refreshLibrary=false`,
            { LibraryOptions: libraryOptions(library) });
    }
    const folders = await ok('/Library/VirtualFolders');
    for (const [name, library] of Object.entries(LIBRARIES)) {
        const folder = folders.find((f) => f.Name === name);
        expect(folder, `${name} was not created`);
        expect(folder.CollectionType === library.type, `${name} is a ${folder.CollectionType} library, not ${library.type}`);
        expect(folder.Locations.includes(library.path), `${name} reads ${folder.Locations.join(', ')}, not ${library.path}`);
        expect(folder.LibraryOptions.EnableInternetProviders === false, `${name} has internet providers on`);
        const types = folder.LibraryOptions.TypeOptions.map((o) => o.Type);
        for (const type of library.itemTypes) {
            const options = folder.LibraryOptions.TypeOptions.find((o) => o.Type === type);
            expect(options, `${name} has no options for ${type} (it has ${types.join(', ') || 'none'}), so every fetcher would be on`);
            expect(options.MetadataFetchers.length === 0 && options.ImageFetchers.length === 0, `${name} still has fetchers for ${type}`);
        }
        libraryIds[name] = folder.ItemId;
    }
    return Object.keys(LIBRARIES).join(', ');
});

/** The items of a library, of the given types (comma separated), with the extra Fields asked for. */
async function libraryItems(name, types, fields) {
    const path = `/Items?ParentId=${libraryIds[name]}&Recursive=true&IncludeItemTypes=${types}&SortBy=SortName${fields ? `&Fields=${fields}` : ''}`;
    return (await ok(path)).Items;
}

await setupCheck('library scan finds the sample media', async () => {
    await expectStatus(204, 'POST', '/Library/Refresh');
    const want = [['Movies', 'Movie', 12], ['Shows', 'Series', 1], ['Shows', 'Season', 2], ['Shows', 'Episode', 4], ['Home', 'Video', 2]];
    const found = await waitFor('the sample media to appear', async () => {
        const counts = [];
        for (const [name, type] of want) counts.push((await libraryItems(name, type)).length);
        if (counts.some((n, i) => n !== want[i][2])) {
            throw new Error(`last count: ${want.map(([name, type, n], i) => `${counts[i]} of ${n} ${name} ${type}`).join(', ')}`);
        }
        return counts;
    }, { timeout: 180_000, interval: 2_000 });
    // The scan goes on after the items appear (probing, images): wait until it is over, twice in a row.
    let idle = 0;
    await waitFor('the library scan to finish', async () => {
        const task = (await ok('/ScheduledTasks')).find((t) => t.Key === 'RefreshLibrary');
        expect(task, 'Jellyfin has no RefreshLibrary task');
        idle = task.State === 'Idle' ? idle + 1 : 0;
        if (idle < 2) {
            const progress = typeof task.CurrentProgressPercentage === 'number' ? ` at ${Math.round(task.CurrentProgressPercentage)} %` : '';
            throw new Error(`RefreshLibrary is ${task.State}${progress}`);
        }
        return true;
    }, { timeout: 180_000, interval: 1_000 });

    const films = await libraryItems('Movies', 'Movie', 'MediaStreams');
    expect(films.map((f) => f.Name).sort().join('|') === [...SAMPLE_FILMS.keys()].sort().join('|'),
        `the films are ${films.map((f) => f.Name).join(', ')}`);
    for (const film of films) {
        const sample = SAMPLE_FILMS.get(film.Name);
        const range = film.MediaStreams?.find((s) => s.Type === 'Video')?.VideoRangeType;
        expect(film.OfficialRating === sample.rating, `${film.Name} is rated ${film.OfficialRating}, not ${sample.rating}`);
        expect((film.BackdropImageTags?.length ?? 0) === (sample.backdrop ? 1 : 0), `${film.Name} has ${film.BackdropImageTags?.length ?? 0} backdrops`);
        expect(!sample.range || range === sample.range, `${film.Name} is ${range}, not ${sample.range}`);
        expect(Boolean(film.RunTimeTicks) === !sample.noDuration, `${film.Name} has ${film.RunTimeTicks ? 'a' : 'no'} duration`);
    }
    // The home videos have no artwork of any kind: that is what makes their cover come from scenes.
    for (const video of await libraryItems('Home', 'Video')) {
        expect(!video.BackdropImageTags?.length && !Object.keys(video.ImageTags ?? {}).length, `${video.Name} has artwork`);
    }
    return want.map(([, type], i) => `${found[i]} ${type}`).join(', ');
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

// Keep this check last: it reads the whole log up to this point.
await check('server log has no plugin [ERR] or [FTL] entries', async () => {
    const run = spawnSync('docker', ['logs', CONTAINER], { encoding: 'utf8', maxBuffer: 1 << 28 });
    expect(run.status === 0, `docker logs ${CONTAINER} failed: ${run.stderr || run.error}`);
    const log = `${run.stdout}\n${run.stderr}`;
    const errors = pluginErrorEntries(log);
    expect(errors.length === 0, `${errors.length} entries:\n${errors.slice(0, 5).join('\n')}`);
    return `${log.split('\n').length} log lines read`;
});

finish();
