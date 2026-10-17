// The checks of the plugin's dashboard page, in headless Chromium driven by Playwright, through Jellyfin's real
// login form. e2e.mjs runs them and passes its helpers in.
//
// Playwright is not a dependency of the repository: it is loaded with require(), so NODE_PATH may point at a global
// install (run.sh sets it from the `playwright` command on PATH), and its Chromium must be installed
// (`playwright install chromium`). When it cannot be loaded, every page check fails and says why.
//
// Notes on Jellyfin 12.2's web client that the checks lean on:
//  - the page is opened from the Dashboard's side menu: setting #/configurationpage?name=... straight from the
//    home screen does not load it;
//  - an emby-checkbox hides its input, so its label (.checkboxContainer) is what takes the click;
//  - "Choose" needs an exact match, since the lock button's label also contains the word;
//  - a new cover is waited for through the API (CurrentCoverId), not through the status line, which keeps the
//    previous message until the next one;
//  - Jellyfin itself logs a console error on every page here (its /socket answers 403) and aborts
//    Sessions/Capabilities/Full: Page_has_no_errors leaves those two out. Jellyfin's React views also cancel their
//    queries when the route changes (a CancelledError of @tanstack/query in the console and an aborted request,
//    seen once for /UserViews on the way to the Dashboard); an aborted request is counted only when it is the
//    plugin's own, and that console error not at all.

import { createRequire } from 'node:module';

const PARENTAL_SENTENCE = "The plugin can't check what an uploaded cover shows against your users' parental controls.";
const NAMES = [
    'Page_lists_one_card_per_library',
    'Generate_now_shows_a_new_cover',
    'Editor_opens_clean_and_clamps_numbers',
    'Editor_lists_the_Jellyfin_font_first',
    'Editor_locks_a_title_and_the_next_build_keeps_it',
    'Slot_search_and_backdrop_pick',
    'Scenes_moment_slider_shows_frames',
    'Upload_from_the_page_pins_the_library',
    'History_restore_works_from_the_page',
    'Primary_format_switch_answers_and_reloads',
    'Default_mode_switch',
    'Generate_all_now',
    'Page_has_no_errors'
].map((name) => `page: ${name}`);

/** Loads Playwright, or gives the reason it cannot be. */
function loadPlaywright() {
    try {
        return { playwright: createRequire(import.meta.url)('playwright') };
    } catch (e) {
        return { problem: `Playwright cannot be loaded (${e.code ?? e.message}); put it on NODE_PATH, see page.mjs` };
    }
}

/** Runs the page checks. `ctx` holds e2e.mjs's helpers. */
export async function pageChecks(ctx) {
    const {
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
    } = ctx;
    if (!wanted(NAMES)) {
        for (const name of NAMES) await check(name, async () => '');
        return;
    }

    const { playwright, problem } = loadPlaywright();
    let browser;
    let page;
    let why = problem;
    const problems = [];
    try {
        if (playwright) {
            browser = await playwright.chromium.launch({ headless: true });
            // Every library back to what the schedule makes, and kid back to no limit (a lock below picks Film 2).
            await kidUnlimited();
            for (const name of Object.keys(libraryIds)) await resetLibrary(name);
            page = await openPage(browser, { JF, ADMIN, problems });
        }
    } catch (e) {
        why = `the page could not be opened: ${e.message.split('\n')[0]}`;
    }
    const step = (name, fn) =>
        check(`page: ${name}`, async () => {
            expect(page, why ?? 'the page did not open');
            return fn();
        });

    const state = (name) => coverState(name);
    const card = (name) =>
        page.locator('.lcm-card', { has: page.locator('h3 span:first-child', { hasText: new RegExp(`^${name}$`) }) });
    const clearStatus = () =>
        page.evaluate(() => {
            for (const id of ['lcm-status', 'lcm-editor-status']) document.getElementById(id).textContent = '';
        });
    const waitStatus = (pattern, id = 'lcm-status', timeout = 240_000) =>
        page.waitForFunction(
            ([source, where]) => new RegExp(source).test(document.getElementById(where).textContent),
            [pattern.source, id],
            { timeout }
        );
    const openEditor = async (name) => {
        await card(name).getByRole('button', { name: 'Edit', exact: true }).click();
        await page.waitForFunction(
            (n) => new RegExp(n).test(document.querySelector('#lcm-editor-title').textContent),
            name
        );
        await page.locator('#lcm-editor:not([hidden]) .lcm-slot').first().waitFor();
    };

    await step('Page_lists_one_card_per_library', async () => {
        const folders = await ok('/Library/VirtualFolders');
        // Every library has a cover by now (Empty's is its name), so every card shows a picture.
        await page.waitForFunction(
            (n) => document.querySelectorAll('.lcm-card img[src^="blob:"]').length === n,
            folders.length,
            { timeout: 30_000 }
        );
        const names = await page.locator('.lcm-card h3 span:first-child').allTextContents();
        expect(
            names.sort().join() ===
                folders
                    .map((f) => f.Name)
                    .sort()
                    .join(),
            `the cards are ${names.join(', ')}`
        );
        return names.join(', ');
    });

    await step('Generate_now_shows_a_new_cover', async () => {
        const before = await state('Movies');
        const src = await card('Movies').locator('img').getAttribute('src');
        await clearStatus();
        await card('Movies').getByRole('button', { name: 'Generate now' }).click();
        expect(
            await card('Movies').getByRole('button', { name: 'Generate now' }).isDisabled(),
            'Generate now stays enabled while it builds'
        );
        const after = await newCover('Movies', before);
        await waitStatus(/Movies: the new cover is ready/);
        await page.waitForFunction((old) => {
            const tile = [...document.querySelectorAll('.lcm-card')].find(
                (c) => c.querySelector('h3 span').textContent === 'Movies'
            );
            return tile.querySelector('img').src && tile.querySelector('img').src !== old;
        }, src);
        return `cover ${N(after.CurrentCoverId).slice(0, 8)}`;
    });

    await step('Editor_opens_clean_and_clamps_numbers', async () => {
        await openEditor('Movies');
        expect(await page.locator('#lcm-save').isDisabled(), 'Save is enabled with nothing changed');
        await page.locator('#lcm-text-size').fill('999');
        await page.locator('#lcm-text-size').blur();
        const clamped = await page.locator('#lcm-text-size').inputValue();
        expect(clamped === '200', `999 became ${clamped}, not 200`);
        expect(!(await page.locator('#lcm-save').isDisabled()), 'Save stays disabled after a change');
        await page.locator('#lcm-undo').click();
        const undone = await page.locator('#lcm-text-size').inputValue();
        expect(undone === '112', `Undo left ${undone}`);
        return '999 shown as 200, Undo back to 112';
    });

    await step('Editor_lists_the_Jellyfin_font_first', async () => {
        // The check before leaves the editor open on Movies; Edit is only clicked when it does not.
        const open = await page.evaluate(
            () =>
                !document.getElementById('lcm-editor').hidden &&
                /Movies/.test(document.getElementById('lcm-editor-title').textContent)
        );
        if (!open) await openEditor('Movies');
        const fonts = await page.locator('#lcm-font option').allTextContents();
        expect(fonts[0] === 'Jellyfin', `the fonts are ${fonts.join(', ')}`);
        expect((await page.locator('#lcm-font').inputValue()) === 'Jellyfin', 'Jellyfin is not the font chosen');
        expect(!(await page.locator('#lcm-frame').isChecked()), 'the frame is on');
        // Jellyfin's font writes the name in its own case, the other fonts in uppercase, and the hint follows.
        const hint = () => page.locator('#lcm-title').getAttribute('placeholder');
        const own = await hint();
        // The font is put back by hand, not with Undo, which stays disabled while a build of Movies is running.
        await page.locator('#lcm-font').selectOption('Cinzel');
        const upper = await hint();
        await page.locator('#lcm-font').selectOption('Jellyfin');
        const back = await hint();
        expect(
            own === 'Movies' && upper === 'MOVIES' && back === 'Movies',
            `the hint is ${own}, then ${upper} in Cinzel and ${back} back in Jellyfin`
        );
        expect(await page.locator('#lcm-save').isDisabled(), 'Save is enabled with the font put back');
        return fonts.join(', ');
    });

    let lockedId;
    await step('Editor_locks_a_title_and_the_next_build_keeps_it', async () => {
        const before = await state('Movies');
        lockedId = currentRecord(before).TitleIds[0];
        const slot = page.locator('.lcm-slot[data-slot="0"]');
        await slot.locator('.lcm-lock').click();
        expect((await slot.locator('.lcm-lock').getAttribute('aria-pressed')) === 'true', 'the lock is not pressed');
        await clearStatus();
        await page.locator('#lcm-save').click();
        await waitStatus(/being built/, 'lcm-editor-status', 30_000);
        const after = await newCover('Movies', before);
        expect(N(after.Slots[0].ItemId) === N(lockedId), 'the lock was not saved');
        expect(currentRecord(after).TitleIds.map(N).includes(N(lockedId)), 'the new cover lacks the locked title');
        return `locked ${N(lockedId).slice(0, 8)}`;
    });

    await step('Slot_search_and_backdrop_pick', async () => {
        await page.locator('#lcm-mode button[data-mode="Art"]').click(); // backdrops are picked in art mode
        const slot = page.locator('.lcm-slot[data-slot="1"]');
        await slot.getByRole('button', { name: 'Choose', exact: true }).click();
        await slot.locator('input[type=search]').fill('Film 2');
        await slot.locator('.lcm-results button', { hasText: 'Film 2' }).first().click();
        const backdrop = page.locator('.lcm-slot[data-slot="1"] .lcm-backdrops button[aria-label="Backdrop 1"]');
        await backdrop.locator('img[src^="blob:"]').waitFor({ timeout: 20_000 });
        await backdrop.click();
        const before = await state('Movies');
        await clearStatus();
        await page.locator('#lcm-save').click();
        await waitStatus(/Saved/, 'lcm-editor-status', 30_000);
        const saved = await state('Movies');
        expect(
            saved.Slots[1].ItemId && saved.Slots[1].BackdropIndex === 0,
            `slot 2 is ${JSON.stringify(saved.Slots[1])}`
        );
        const after = await newCover('Movies', before);
        expect(!after.LastError, after.LastError);
        return 'Film 2, backdrop 1, rebuilt';
    });

    await step('Scenes_moment_slider_shows_frames', async () => {
        await openEditor('Shows');
        await page.locator('#lcm-mode button[data-mode="Scenes"]').click();
        const slot = page.locator('.lcm-slot[data-slot="0"]');
        await slot.getByRole('button', { name: 'Choose', exact: true }).click();
        await slot.locator('.lcm-results button').first().click();
        await page.locator('.lcm-slot[data-slot="0"] .lcm-slot-panel .checkboxContainer').click();
        const img = page.locator('.lcm-slot[data-slot="0"] .lcm-moment img[src^="blob:"]');
        await img.waitFor({ timeout: 30_000 });
        const first = await img.getAttribute('src');
        await page.locator('.lcm-slot[data-slot="0"] input[type=range]').fill('12');
        await page.waitForFunction(
            (old) => {
                const frame = document.querySelector('.lcm-slot[data-slot="0"] .lcm-moment img');
                return frame.src && frame.src !== old;
            },
            first,
            { timeout: 30_000 }
        );
        const before = await state('Shows');
        await clearStatus();
        await page.locator('#lcm-save').click();
        await waitStatus(/Saved/, 'lcm-editor-status', 30_000);
        const saved = await state('Shows');
        expect(
            saved.Mode === 'Scenes' && saved.Slots[0].MomentSeconds === 12,
            `Shows is ${saved.Mode} with ${JSON.stringify(saved.Slots[0])}`
        );
        const after = await newCover('Shows', before);
        return `new cover ${N(after.CurrentCoverId).slice(0, 8)}`;
    });

    await step('Upload_from_the_page_pins_the_library', async () => {
        await card('Home').getByRole('button', { name: 'Upload cover' }).click();
        const dialog = page.locator('#lcm-upload-dialog');
        await dialog.waitFor();
        expect(
            (await dialog.textContent()).includes(PARENTAL_SENTENCE),
            'the dialog lacks the parental controls sentence'
        );
        await page
            .locator('#lcm-upload-file')
            .setInputFiles({ name: 'home.png', mimeType: 'image/png', buffer: png(320, 180, [0xe0, 0xa0, 0x20]) });
        await clearStatus();
        await page.locator('#lcm-upload-send').click();
        await waitStatus(/Uploaded and pinned/, 'lcm-status', 30_000);
        expect(!(await dialog.isVisible()), 'the dialog is still open');
        const after = await state('Home');
        expect(
            after.Pinned &&
                after.History[0].Origin === 'Uploaded' &&
                N(after.CurrentCoverId) === N(after.History[0].Id),
            JSON.stringify(after.History[0])
        );
        const badge = await card('Home').locator('.lcm-badge').textContent();
        expect(badge === 'Manual: pinned', `the badge reads ${badge}`);
        await card('Home').getByRole('button', { name: 'Back to automatic' }).waitFor();
        return 'pinned, badge Manual: pinned';
    });

    await step('History_restore_works_from_the_page', async () => {
        await openEditor('Movies');
        const before = await state('Movies');
        const older = before.History.find((cover) => N(cover.Id) !== N(before.CurrentCoverId));
        await clearStatus();
        await page.locator(`#lcm-history button[data-cover-id="${older.Id}"]`).click();
        await waitStatus(/Restored and pinned/, 'lcm-editor-status', 30_000);
        const restored = await state('Movies');
        expect(restored.Pinned && N(restored.CurrentCoverId) === N(older.Id), 'the cover was not restored and pinned');
        const label = await page.locator('#lcm-ed-pin').innerText();
        expect(label.includes('Back to automatic'), `the pin button reads ${label}`);
        for (const [pattern, pinned] of [
            [/Back to automatic/, false],
            [/Pinned:/, true],
            [/Back to automatic/, false]
        ]) {
            await clearStatus();
            await page.locator('#lcm-ed-pin').click();
            await waitStatus(pattern, 'lcm-editor-status', 30_000);
            expect((await state('Movies')).Pinned === pinned, `Pinned is not ${pinned}`);
        }
        return `restored ${N(older.Id).slice(0, 8)}, then unpinned, pinned and unpinned`;
    });

    await step('Primary_format_switch_answers_and_reloads', async () => {
        try {
            await clearStatus();
            await page.locator('#lcm-format').selectOption('Still');
            await waitStatus(/Applying the still version/, 'lcm-status', 20_000);
            expect((await ok(covers('/Settings'))).PrimaryFormat === 'Still', 'the format was not saved');
            await waitFor('the still Primary on Movies', async () => !(await primaryIsAnimated(libraryIds.Movies)), {
                timeout: 60_000,
                interval: 500
            });
        } finally {
            await clearStatus();
            await page.locator('#lcm-format').selectOption('Animated');
        }
        await waitStatus(/Applying the animated version/, 'lcm-status', 20_000);
        await waitFor('the animated Primary on Movies', () => primaryIsAnimated(libraryIds.Movies), {
            timeout: 60_000,
            interval: 500
        });
        return 'Still, then Animated, each applied';
    });

    await step('Default_mode_switch', async () => {
        await clearStatus();
        expect((await ok(covers('/Settings'))).DefaultMode === 'Scenes', 'a fresh install does not default to Scenes');
        await page.locator('#lcm-default-mode').selectOption('Art');
        await waitStatus(/official artwork/, 'lcm-status', 20_000);
        expect((await ok(covers('/Settings'))).DefaultMode === 'Art', 'Art was not saved');
        await clearStatus();
        await page.locator('#lcm-default-mode').selectOption('Scenes');
        await waitStatus(/New libraries start with scenes/, 'lcm-status', 20_000);
        expect((await ok(covers('/Settings'))).DefaultMode === 'Scenes', 'Scenes was not saved');
        return 'Scenes by default, then Art, then Scenes';
    });

    await step('Generate_all_now', async () => {
        await clearStatus();
        const names = Object.keys(libraryIds);
        const before = {};
        for (const name of names) before[name] = await state(name);
        // What the button asked for, and then the libraries themselves through the API: the status line may pass
        // before a check sees it.
        const answer = page.waitForResponse((res) => /\/GenerateAll$/.test(res.url()), { timeout: 20_000 });
        await page.locator('#lcm-generate-all').click();
        const res = await answer;
        expect(res.status() === 202, `Generate all answered ${res.status()}`);
        const { Queued: queued } = await res.json();
        expect(queued > 0, 'Generate all queued no library');
        await waitFor(
            `${queued} new covers`,
            async () => {
                let built = 0;
                for (const name of names) {
                    const now = await state(name);
                    if (N(now.CurrentCoverId) !== N(before[name].CurrentCoverId)) built++;
                }
                expect(built >= queued, `${built} of ${queued} built`);
                return true;
            },
            { timeout: 300_000, interval: 1_000 }
        );
        await page.waitForFunction(() => !document.querySelector('.lcm-building'), null, { timeout: 60_000 });
        const status = (await page.locator('#lcm-status').textContent()).trim();
        return `${queued} libraries rebuilt; the page says "${status}"`;
    });

    await step('Page_has_no_errors', async () => {
        expect(!problems.length, problems.join('\n'));
        return 'no page error, console error, failed request or answer of 400 or more';
    });

    await browser?.close();
    // What the page left (a pinned upload, locks, Shows in scenes) goes, so that the checks after start as usual.
    if (page) {
        for (const name of Object.keys(libraryIds)) await resetLibrary(name);
    }
}

/** Signs in through Jellyfin's login form and opens the plugin's page from the Dashboard's side menu. Everything
 *  that goes wrong on the page from then on is collected in `problems`. */
async function openPage(browser, { JF, ADMIN, problems }) {
    const context = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
    const page = await context.newPage();
    const jellyfinOwn = (text) => /\/socket|Sessions\/Capabilities/.test(text);
    const cancelledQuery = (text) => /^\S+: CancelledError\b/.test(text) && /tanstack\.query-core/.test(text);
    const abortedElsewhere = (request) =>
        request.failure()?.errorText === 'net::ERR_ABORTED' && !request.url().includes('/LibraryCovers/');
    page.on('pageerror', (e) => problems.push(`page error: ${e.message}`));
    page.on('console', (message) => {
        if (message.type() === 'error' && !jellyfinOwn(message.text()) && !cancelledQuery(message.text())) {
            problems.push(`console: ${message.text()}`);
        }
    });
    page.on('requestfailed', (request) => {
        if (!jellyfinOwn(request.url()) && !abortedElsewhere(request)) {
            problems.push(`failed request: ${request.url()} ${request.failure()?.errorText}`);
        }
    });
    page.on('response', (response) => {
        if (response.status() >= 400 && !jellyfinOwn(response.url())) {
            problems.push(`answer ${response.status()}: ${response.request().method()} ${response.url()}`);
        }
    });
    await page.goto(`${JF}/web/`);
    await page.locator('#txtManualName').fill(ADMIN.Name, { timeout: 30_000 });
    await page.locator('#txtManualPassword').fill(ADMIN.Password);
    await page.locator('.manualLoginForm button[type=submit]').click();
    // The login page's own address carries "url=%2Fhome", so only the home route itself means signed in.
    await page.waitForURL(/\/web\/#\/home/, { timeout: 30_000 });
    // Let the home screen finish its own requests first, so that leaving it cancels as few as possible. Only a
    // pause: a home screen that never goes quiet is not what these checks are about.
    await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});
    await page.evaluate(() => {
        location.hash = '#/dashboard';
    });
    await page.locator('a', { hasText: 'Library Cover Maker' }).first().click({ timeout: 20_000 });
    await page.locator('.lcm-card').first().waitFor({ timeout: 20_000 });
    return page;
}
