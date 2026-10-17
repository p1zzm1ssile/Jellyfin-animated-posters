// The Library Cover Maker page of the Dashboard: the grid of libraries and the editor of one library.
//
// Jellyfin loads this file as a module when the page opens (data-controller="__plugin/librarycovers.js") and only
// once, so the page is wired up on every "viewshow" of its element, which may be a new element each visit.
//
// Every request carries Jellyfin's "Authorization: MediaBrowser ..." header, the only form Jellyfin 12 accepts, and
// pictures are fetched the same way and shown through blob URLs, because an <img> cannot send that header. While a
// request for a library is in flight every action on that library is disabled, so two actions never overlap.
(function () {
    'use strict';

    const PAGE_ID = 'LibraryCoverMakerPage';
    const API = 'LibraryCovers/';
    const TASK_KEY = 'LibraryCoverMakerRefresh';
    const SLOT_COUNT = 5;
    const MAX_UPLOAD_BYTES = 20 * 1024 * 1024;
    const UPLOAD_TYPES = ['image/webp', 'image/png', 'image/jpeg', 'image/gif'];
    const POLL_MS = 3000;
    const WATCH_MS = 5 * 60 * 1000;
    const SEARCH_DELAY_MS = 300;
    const FRAME_DELAY_MS = 250;
    const GLOBAL = 'global';

    // The ranges the server accepts. The page keeps its own controls inside them; the server still has the last word.
    const LIMITS = {
        SlotCount: [1, SLOT_COUNT],
        LengthSeconds: [2, 12],
        TextSize: [40, 200],
        Darken: [0, 70],
        RenewEveryDays: [1, 365]
    };
    const MODES = ['Art', 'Scenes', 'Manual'];
    const MODE_HELP = {
        Art: 'Each slot shows one of the title\'s backdrops, with a slow zoom or pan.',
        Scenes: 'Each slot is a short clip from the title\'s video, picked by score.',
        Manual: 'The cover is rebuilt only when you ask. A locked slot uses the artwork or the scene you pick.'
    };
    const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
    const TICKS_PER_SECOND = 10000000;

    /** An answer of the server that is not a success, with the reason it gave. */
    class ApiFailure extends Error {
        constructor(status, message) {
            super(message);
            this.status = status;
        }
    }

    // ---------- Requests ----------

    async function reasonOf(res) {
        let text = '';
        try { text = await res.text(); } catch { /* no body */ }
        try {
            const body = JSON.parse(text);
            if (typeof body === 'string' && body) return body;
            if (body && typeof body.Detail === 'string') return body.Detail;
            if (body && body.errors) return Object.values(body.errors).flat().join(' ');
            if (body && typeof body.title === 'string') return body.title;
        } catch { /* not JSON */ }
        if (res.status === 401 || res.status === 403) return 'Only an administrator can do this. Sign in again.';
        return 'The server answered ' + res.status + '.';
    }

    /** Calls the server. options: query, json (a body), form (FormData), blob (answer as a Blob), signal. */
    async function request(method, path, options) {
        const opts = options || {};
        const headers = {};
        ApiClient.setRequestHeaders(headers);
        let body;
        if (opts.json !== undefined) {
            headers['Content-Type'] = 'application/json';
            body = JSON.stringify(opts.json);
        } else if (opts.form) {
            body = opts.form;
        }
        if (!opts.blob) headers.Accept = 'application/json';
        const res = await fetch(ApiClient.getUrl(path, opts.query), { method, headers, body, signal: opts.signal });
        if (!res.ok) throw new ApiFailure(res.status, await reasonOf(res));
        if (opts.blob) return res.blob();
        const text = await res.text();
        return text ? JSON.parse(text) : null;
    }

    // ---------- Pictures ----------

    /**
     * Blob URLs of pictures, by group (cards, editor, history, slots). A render asks for the pictures it shows and
     * then drops the rest of its group, which revokes their URLs.
     */
    const pictures = {
        groups: new Map(),

        load(group, key, path, query) {
            let entries = this.groups.get(group);
            if (!entries) {
                entries = new Map();
                this.groups.set(group, entries);
            }
            let entry = entries.get(key);
            if (!entry) {
                entry = { url: null, dropped: false };
                entry.promise = request('GET', path, { query, blob: true }).then(
                    (blob) => {
                        if (entry.dropped) return null;
                        entry.url = URL.createObjectURL(blob);
                        return entry.url;
                    },
                    () => {
                        // Asked again on the next render.
                        if (entries.get(key) === entry) entries.delete(key);
                        return null;
                    });
                entries.set(key, entry);
            }
            return entry.promise;
        },

        keep(group, keys) {
            const entries = this.groups.get(group);
            if (!entries) return;
            for (const [key, entry] of entries) {
                if (!keys.has(key)) {
                    this.drop(entry);
                    entries.delete(key);
                }
            }
        },

        drop(entry) {
            entry.dropped = true;
            if (entry.url) URL.revokeObjectURL(entry.url);
            entry.url = null;
        },

        clear() {
            for (const entries of this.groups.values()) {
                for (const entry of entries.values()) this.drop(entry);
            }
            this.groups.clear();
        }
    };

    /** Shows a picture in a .lcm-cover box, or `empty` when there is none. Gives the key, for pictures.keep. */
    function showPicture(box, group, picture, empty) {
        const img = box.querySelector('img');
        const label = box.querySelector('.lcm-empty');
        img.removeAttribute('src');
        label.textContent = picture ? 'Loading...' : empty;
        if (!picture) return null;
        pictures.load(group, picture.key, picture.path, picture.query).then((url) => {
            if (url) {
                img.src = url;
                label.textContent = '';
            } else {
                label.textContent = 'The picture could not be loaded.';
            }
        });
        return picture.key;
    }

    function coverPicture(libraryId, coverId, still) {
        return {
            key: libraryId + '/' + coverId + (still ? '/still' : ''),
            path: API + 'Libraries/' + libraryId + '/Covers/' + coverId,
            query: still ? { still: true } : undefined
        };
    }

    // ---------- Small helpers ----------

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    /** A Jellyfin button. The "is" attribute must be set before the button is in the page. */
    function button(text, extraClass) {
        const node = document.createElement('button');
        node.setAttribute('is', 'emby-button');
        node.type = 'button';
        node.className = 'raised emby-button' + (extraClass ? ' ' + extraClass : '');
        node.appendChild(el('span', null, text));
        return node;
    }

    function coverBox() {
        const box = el('div', 'lcm-cover');
        const img = el('img');
        img.alt = '';
        box.appendChild(img);
        box.appendChild(el('span', 'lcm-empty'));
        return box;
    }

    function when(value) {
        const date = value ? new Date(value) : null;
        if (!date || isNaN(date.getTime())) return '';
        return date.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
    }

    function clamp(value, range) {
        const number = Math.round(Number(value));
        if (!Number.isFinite(number)) return range[0];
        return Math.min(range[1], Math.max(range[0], number));
    }

    // Jellyfin writes enums as names; an older or differently configured server might send numbers.
    function modeName(value) {
        return typeof value === 'number' ? MODES[value] || 'Art' : value || 'Art';
    }

    function formatName(value) {
        return value === 1 || value === 'Still' ? 'Still' : 'Animated';
    }

    function originName(value) {
        return typeof value === 'number' ? ['Generated', 'Uploaded', 'Original'][value] : value;
    }

    function seconds(value) {
        const total = Math.max(0, Math.round(value));
        const m = Math.floor(total / 60);
        const s = total % 60;
        return Math.floor(m / 60) + ':' + String(m % 60).padStart(2, '0') + ':' + String(s).padStart(2, '0');
    }

    // Ids are compared without dashes and case, whatever form the server wrote them in.
    function sameId(a, b) {
        const plain = (id) => String(id || '').replace(/-/g, '').toLowerCase();
        return !!a && plain(a) === plain(b);
    }

    function same(a, b) {
        return JSON.stringify(a) === JSON.stringify(b);
    }

    // ---------- The page ----------

    function wire(view) {
        const $ = (id) => view.querySelector('#' + id);

        const page = {
            settings: null,
            scheduleText: '',
            scheduleShort: '',
            cards: [],
            primaryItems: {},
            busy: new Set(),
            watching: new Map(),
            timer: null,
            visible: true,
            editor: null,
            // Counts Edit clicks, so that only the answer for the last one opens the editor.
            editorTicket: 0,
            uploadFor: null
        };

        // ---------- Status lines ----------

        function say(where, text, kind) {
            const line = $(where === 'editor' ? 'lcm-editor-status' : 'lcm-status');
            line.textContent = text || '';
            if (kind) line.setAttribute('data-kind', kind);
            else line.removeAttribute('data-kind');
        }

        function nameOf(id) {
            const card = page.cards.find((c) => c.Id === id);
            return card ? card.Name : 'A library';
        }

        /**
         * Says something about a library where the admin is looking: in the editor while it shows that library,
         * otherwise on the page, with the library's name.
         */
        function tell(id, where, text, kind) {
            if (where === 'editor' && !(page.editor && page.editor.id === id)) {
                say('page', id === GLOBAL ? text : nameOf(id) + ': ' + text, kind);
            } else {
                say(where, text, kind);
            }
        }

        // ---------- In-flight actions ----------

        /** Runs `work` for a library (or GLOBAL), with that library's actions disabled until it ends. */
        async function act(lib, where, work) {
            if (page.busy.has(lib)) return;
            page.busy.add(lib);
            refreshDisabled();
            try {
                await work();
            } catch (err) {
                const reason = err instanceof ApiFailure ? err.message : 'Something went wrong: ' + err.message;
                tell(lib, where, reason, 'error');
            } finally {
                page.busy.delete(lib);
                refreshDisabled();
            }
        }

        /** Enables or disables every control tied to a library from what is in flight and being built. */
        function refreshDisabled() {
            view.querySelectorAll('[data-lcm-lib]').forEach((node) => {
                const lib = node.getAttribute('data-lcm-lib');
                node.disabled = node.hasAttribute('data-lcm-off')
                    || page.busy.has(lib)
                    || (node.hasAttribute('data-lcm-generate') && page.watching.has(lib));
            });
            if (page.editor) updateDirty();
        }

        // ---------- Loading ----------

        async function loadSettings() {
            page.settings = await request('GET', API + 'Settings');
            $('lcm-format').value = formatName(page.settings.PrimaryFormat);
            const select = $('lcm-default-mode');
            const mode = modeName(page.settings.DefaultMode);
            if (!select.querySelector('option[value="' + mode + '"]')) {
                const option = el('option', null, mode);
                option.value = mode;
                select.appendChild(option);
            }
            select.value = mode;
        }

        async function loadSchedule() {
            const tasks = await request('GET', 'ScheduledTasks');
            const task = (tasks || []).find((t) => t.Key === TASK_KEY);
            const triggers = task && task.Triggers ? task.Triggers : [];
            const parts = triggers.map(describeTrigger).filter(Boolean);
            page.scheduleText = parts.length
                ? parts.join('; ') + ' (server time)'
                : 'not scheduled: the task "Refresh library covers" has no trigger';
            page.scheduleShort = parts.length ? parts[0] : 'not scheduled';
            $('lcm-schedule').textContent = page.scheduleText;
        }

        function describeTrigger(trigger) {
            const minutes = Math.round((trigger.TimeOfDayTicks || 0) / TICKS_PER_SECOND / 60);
            const time = String(Math.floor(minutes / 60)).padStart(2, '0') + ':'
                + String(minutes % 60).padStart(2, '0');
            const day = typeof trigger.DayOfWeek === 'number' ? WEEKDAYS[trigger.DayOfWeek] : trigger.DayOfWeek;
            switch (trigger.Type) {
                case 'WeeklyTrigger': return 'every ' + day + ' at ' + time;
                case 'DailyTrigger': return 'every day at ' + time;
                case 'IntervalTrigger': {
                    const hours = (trigger.IntervalTicks || 0) / TICKS_PER_SECOND / 3600;
                    return 'every ' + (hours >= 24 ? Math.round(hours / 24) + ' days' : Math.round(hours) + ' hours');
                }
                case 'StartupTrigger': return 'when the server starts';
                default: return '';
            }
        }

        async function loadCards() {
            const [cards, folders] = await Promise.all([
                request('GET', API + 'Libraries'),
                request('GET', 'Library/VirtualFolders')
            ]);
            page.cards = cards || [];
            page.primaryItems = {};
            for (const folder of folders || []) {
                if (folder.ItemId && folder.PrimaryImageItemId) {
                    page.primaryItems[folder.ItemId] = folder.PrimaryImageItemId;
                }
            }
            renderCards();
        }

        // ---------- Grid ----------

        function cardOf(id) {
            return page.cards.find((card) => card.Id === id);
        }

        function badgeOf(card) {
            if (card.Pinned) return ['Manual: pinned', 'manual'];
            if (!card.CanRender) return ['Upload only', 'off'];
            const mode = modeName(card.Mode);
            if (mode === 'Manual') return ['Manual', 'manual'];
            return mode === 'Scenes' ? ['Auto: scenes', 'scenes'] : ['Auto: art', 'art'];
        }

        function cardPicture(card) {
            const still = page.settings && formatName(page.settings.PrimaryFormat) === 'Still';
            if (card.CurrentCoverId) return coverPicture(card.Id, card.CurrentCoverId, still);
            const item = page.primaryItems[card.Id];
            if (!item) return null;
            return {
                key: card.Id + '/jellyfin/' + item,
                path: 'Items/' + item + '/Images/Primary',
                query: { maxWidth: 640 }
            };
        }

        function nextText(card) {
            if (!card.Enabled) return 'This library is switched off.';
            if (card.Schedule === 'own') {
                return 'Next ' + when(card.NextRenewal) + ' (every ' + card.RenewEveryDays + ' days)';
            }
            if (card.Schedule === 'weekly') return 'Next: ' + page.scheduleShort;
            if (card.Pinned) return 'Out of the rotation';
            if (!card.CanRender) return 'Not rendered by the plugin';
            return 'Rebuilt only when you ask';
        }

        function renderCards() {
            const grid = $('lcm-grid');
            grid.textContent = '';
            const keys = new Set();
            if (!page.cards.length) grid.appendChild(el('p', 'lcm-muted', 'There are no libraries yet.'));
            for (const card of page.cards) {
                const tile = el('article', 'lcm-card');
                tile.setAttribute('role', 'listitem');
                tile.setAttribute('data-id', card.Id);
                if (page.editor && page.editor.id === card.Id) tile.classList.add('lcm-selected');

                const box = coverBox();
                box.querySelector('img').alt = 'Cover of ' + card.Name;
                const key = showPicture(box, 'cards', cardPicture(card), 'No cover yet');
                if (key) keys.add(key);
                tile.appendChild(box);

                const title = el('h3');
                title.appendChild(el('span', null, card.Name));
                const [badgeText, badgeKind] = badgeOf(card);
                const badge = el('span', 'lcm-badge', badgeText);
                badge.setAttribute('data-kind', badgeKind);
                title.appendChild(badge);
                tile.appendChild(title);

                const made = card.CreatedAt ? 'Made ' + when(card.CreatedAt) : 'No cover from the plugin yet';
                tile.appendChild(el('div', 'lcm-meta lcm-muted lcm-small', made + ' \u00b7 ' + nextText(card)));
                if (card.LastError) {
                    tile.appendChild(el('div', 'lcm-error lcm-small', 'Last render failed: ' + card.LastError));
                }
                if (card.Notice) {
                    const at = card.NoticeAt ? when(card.NoticeAt) + ': ' : '';
                    tile.appendChild(el('div', 'lcm-notice lcm-small', at + card.Notice));
                }
                if (page.watching.has(card.Id)) {
                    tile.appendChild(el('div', 'lcm-building lcm-small', 'Building a new cover...'));
                }

                const buttons = el('div', 'lcm-btns');
                if (card.Pinned) {
                    const unpin = button('Back to automatic');
                    unpin.setAttribute('data-lcm-lib', card.Id);
                    unpin.addEventListener('click', () => unpinLibrary(card.Id, 'page'));
                    buttons.appendChild(unpin);
                } else if (card.CanRender) {
                    const generate = button('Generate now', 'button-submit');
                    generate.setAttribute('data-lcm-lib', card.Id);
                    generate.setAttribute('data-lcm-generate', '');
                    if (!card.Enabled) generate.setAttribute('data-lcm-off', '');
                    generate.addEventListener('click', () => generateLibrary(card.Id, 'page'));
                    buttons.appendChild(generate);
                }
                const edit = button('Edit');
                edit.setAttribute('data-lcm-lib', card.Id);
                edit.addEventListener('click', () => openEditor(card.Id));
                buttons.appendChild(edit);
                const upload = button('Upload cover');
                upload.setAttribute('data-lcm-lib', card.Id);
                upload.addEventListener('click', () => openUpload(card.Id));
                buttons.appendChild(upload);
                tile.appendChild(buttons);

                grid.appendChild(tile);
            }
            pictures.keep('cards', keys);
            refreshDisabled();
        }

        // ---------- Watching builds ----------

        function snapshotOf(state) {
            return [state.CurrentCoverId, state.LastRenderedAt, state.LastErrorAt].join('|');
        }

        /** The library's state before a request that may build a cover, for watch. Null when it cannot be read. */
        function before(id) {
            return request('GET', API + 'Libraries/' + id).catch(() => null);
        }

        /**
         * Shows a library as building until its state differs from `earlier`, read BEFORE the request that started
         * the build, so that a build that ends before the answer arrives is not missed.
         */
        function watch(id, earlier) {
            if (!earlier) return;
            page.watching.set(id, {
                snapshot: snapshotOf(earlier),
                errorAt: earlier.LastErrorAt,
                until: Date.now() + WATCH_MS
            });
            renderCards();
            if (page.editor && page.editor.id === id) renderEditorButtons();
            schedulePoll();
        }

        function schedulePoll() {
            if (page.timer || !page.watching.size || !page.visible) return;
            page.timer = setTimeout(poll, POLL_MS);
        }

        async function poll() {
            page.timer = null;
            if (!page.visible || !view.isConnected) return;
            let changed = false;
            let editorBuilt = false;
            for (const [id, watched] of Array.from(page.watching)) {
                let state;
                try {
                    state = await request('GET', API + 'Libraries/' + id);
                } catch {
                    continue;
                }
                const card = cardOf(id);
                if (snapshotOf(state) !== watched.snapshot) {
                    page.watching.delete(id);
                    changed = true;
                    const failed = state.LastError && state.LastErrorAt !== watched.errorAt;
                    if (card) {
                        const result = failed ? ': the render failed. ' + state.LastError : ': the new cover is ready.';
                        say('page', card.Name + result, failed ? 'error' : 'done');
                    }
                    if (page.editor && page.editor.id === id) editorBuilt = true;
                } else if (Date.now() > watched.until) {
                    page.watching.delete(id);
                    changed = true;
                    if (card) {
                        say('page', card.Name
                            + ': no new cover yet. The render waits while someone is watching, so check back later.');
                    }
                }
            }
            if (changed) {
                try {
                    await loadCards();
                } catch (err) {
                    say('page', err.message, 'error');
                }
                // After the cards, whose current cover the editor's preview shows.
                if (editorBuilt && page.editor && !page.busy.has(page.editor.id)) {
                    const id = page.editor.id;
                    await reloadEditor(true).catch((err) => tell(id, 'editor', err.message, 'error'));
                } else if (page.editor) {
                    renderEditorButtons();
                }
            }
            schedulePoll();
        }

        // ---------- Actions on a library ----------

        function generateLibrary(id, where) {
            return act(id, where, async () => {
                const earlier = await before(id);
                const answer = await request('POST', API + 'Libraries/' + id + '/Generate');
                tell(id, where, nameOf(id) + ': ' + (answer && answer.Queued
                    ? 'building a new cover.'
                    : 'a build is already due; the cover follows it.'));
                watch(id, earlier);
            });
        }

        function unpinLibrary(id, where) {
            return act(id, where, async () => {
                const state = await request('POST', API + 'Libraries/' + id + '/Unpin');
                await afterChange(id, state, true);
                tell(id, where, 'Back to automatic: the next render follows the rules again.', 'done');
            });
        }

        function pinLibrary(id, where) {
            return act(id, where, async () => {
                const state = await request('POST', API + 'Libraries/' + id + '/Pin');
                await afterChange(id, state, true);
                tell(id, where, 'Pinned: this cover stays until you choose Back to automatic.', 'done');
            });
        }

        function restoreCover(id, coverId) {
            return act(id, 'editor', async () => {
                const state = await request('POST', API + 'Libraries/' + id + '/Restore/' + coverId);
                await afterChange(id, state, true);
                tell(id, 'editor', 'Restored and pinned.', 'done');
            });
        }

        /**
         * After a change: the cards again, and the editor when it still shows that library. With keepEdits, edits the
         * admin has not saved stay in the form (pinning, restoring and uploading do not touch them).
         */
        async function afterChange(id, state, keepEdits) {
            await loadCards();
            if (page.editor && page.editor.id === id) await showEditorState(state, keepEdits);
        }

        // ---------- Upload ----------

        /** Whether the theme is dark: its text is light. */
        function darkTheme() {
            const rgb = (getComputedStyle(view).color.match(/[\d.]+/g) || ['0', '0', '0']).slice(0, 3).map(Number);
            return (0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]) / 255 > 0.5;
        }

        /** The color behind the page: the first ancestor with a background that is not transparent. */
        function pageBackground() {
            for (let node = view; node; node = node.parentElement) {
                const color = getComputedStyle(node).backgroundColor;
                if (color && color !== 'transparent' && !/rgba\(.*,\s*0\)$/.test(color)) return color;
            }
            return darkTheme() ? '#202020' : '#f2f2f2';
        }

        /** Marks a dark dashboard theme, for the CSS to pick colors that read on it. */
        function markTheme() {
            view.setAttribute('data-lcm-theme', darkTheme() ? 'dark' : 'light');
        }

        function dialogColors(dialog) {
            // The dialog is drawn in the top layer, outside the page's background: give it the page's own colors.
            dialog.style.background = pageBackground();
            dialog.style.color = getComputedStyle(view).color;
        }

        function openUpload(id) {
            const dialog = $('lcm-upload-dialog');
            page.uploadFor = id;
            const card = cardOf(id);
            $('lcm-upload-title').textContent = 'Upload a cover for ' + (card ? card.Name : 'this library');
            $('lcm-upload-file').value = '';
            $('lcm-upload-error').textContent = '';
            dialogColors(dialog);
            if (!dialog.open) dialog.showModal();
        }

        function closeUpload() {
            const dialog = $('lcm-upload-dialog');
            if (dialog.open) dialog.close();
            page.uploadFor = null;
        }

        function sendUpload() {
            const id = page.uploadFor;
            const file = $('lcm-upload-file').files[0];
            const error = $('lcm-upload-error');
            if (!id) return;
            if (!file) {
                error.textContent = 'Choose a picture first.';
                return;
            }
            if (file.size > MAX_UPLOAD_BYTES) {
                error.textContent = 'A cover can be at most 20 MB.';
                return;
            }
            if (file.type && UPLOAD_TYPES.indexOf(file.type) < 0) {
                error.textContent = 'Choose a WebP, PNG, JPEG or GIF picture.';
                return;
            }
            if (page.busy.has(id)) {
                error.textContent = 'Something else is running for this library. Try again when it is done.';
                return;
            }
            const send = $('lcm-upload-send');
            send.disabled = true;
            error.textContent = '';
            act(id, 'page', async () => {
                const form = new FormData();
                form.append('file', file, file.name);
                let state;
                try {
                    state = await request('POST', API + 'Libraries/' + id + '/Upload', { form });
                } catch (err) {
                    error.textContent = err.message;
                    return;
                }
                closeUpload();
                // From here on the dialog is closed: a failure shows on the page (through act).
                await afterChange(id, state, true);
                tell(id, 'editor', 'Uploaded and pinned.', 'done');
            }).finally(() => {
                send.disabled = false;
            });
        }

        // ---------- Editor ----------

        function openEditor(id) {
            const ticket = ++page.editorTicket;
            return act(id, 'page', async () => {
                const state = await request('GET', API + 'Libraries/' + id);
                // A later Edit click, or Close, won meanwhile.
                if (ticket !== page.editorTicket) return;
                page.editor = { id, state: null, draft: null, items: new Map(), openSlot: null, slotKeys: new Set() };
                $('lcm-editor').hidden = false;
                say('editor', '');
                await showEditorState(state);
                if (!page.editor || page.editor.id !== id) return;
                renderCards();
                $('lcm-editor').scrollIntoView({ behavior: 'smooth', block: 'start' });
            });
        }

        function closeEditor() {
            page.editorTicket++;
            page.editor = null;
            $('lcm-editor').hidden = true;
            pictures.keep('editor', new Set());
            pictures.keep('history', new Set());
            pictures.keep('slots', new Set());
            renderCards();
        }

        /** Reads the library again. With keepEdits, changes the admin has not saved stay as they are. */
        async function reloadEditor(keepEdits) {
            const editor = page.editor;
            if (!editor) return;
            const state = await request('GET', API + 'Libraries/' + editor.id);
            // Another library's editor may be open by now; showEditorState checks the library.
            if (page.editor === editor) await showEditorState(state, keepEdits);
        }

        function draftOf(state) {
            return {
                Mode: modeName(state.Mode),
                Style: Object.assign({}, state.Style),
                RenewEveryDays: state.RenewEveryDays || 0,
                Slots: (state.Slots || []).map((slot) => ({
                    ItemId: slot.ItemId || null,
                    MomentSeconds: slot.MomentSeconds === undefined ? null : slot.MomentSeconds,
                    BackdropIndex: slot.BackdropIndex === undefined ? null : slot.BackdropIndex
                }))
            };
        }

        function currentRecord(state) {
            const id = state.CurrentCoverId;
            if (!id) return null;
            const original = state.Original && state.Original.Id === id ? state.Original : null;
            return (state.History || []).find((r) => r.Id === id) || original;
        }

        /** Every id the current cover's record lists: its slot titles, then the episodes series slots show. */
        function coverIds(state) {
            const record = currentRecord(state);
            if (!record || originName(record.Origin) !== 'Generated') return [];
            return record.TitleIds || [];
        }

        /**
         * The titles the current cover shows in its slots. The record lists the slot titles and after them the episode
         * each series slot shows, and a cover may have fewer titles than slots, so episodes are told apart by their
         * loaded type, not by position. An id whose details could not be read is left out.
         */
        function coverTitles(editor, state) {
            return coverIds(state).filter((id) => {
                const item = editor.items.get(id);
                return item && !item.partial && item.Type !== 'Episode';
            });
        }

        /**
         * Names, years, run times and image tags of the titles, for the slots. A series gets the run time of its
         * first episode, which is the video its scenes come from.
         */
        async function loadItems(ids, editor) {
            const known = (id) => editor.items.has(id) && !editor.items.get(id).partial;
            const missing = ids.filter((id) => id && !known(id));
            if (!missing.length) return;
            const answer = await request('GET', 'Items', {
                query: { Ids: missing.join(','), Fields: 'ProductionYear', EnableImageTypes: 'Primary,Backdrop' }
            });
            for (const item of (answer && answer.Items) || []) {
                item.partial = false;
                if (item.Type === 'Series') {
                    const episodes = await request('GET', 'Items', {
                        query: {
                            ParentId: item.Id,
                            Recursive: true,
                            IncludeItemTypes: 'Episode',
                            SortBy: 'ParentIndexNumber,IndexNumber',
                            Limit: 1
                        }
                    }).catch(() => null);
                    const first = episodes && episodes.Items && episodes.Items[0];
                    if (first && first.RunTimeTicks) item.RunTimeTicks = first.RunTimeTicks;
                }
                editor.items.set(item.Id, item);
            }
        }

        /**
         * Shows a library's state in the editor, when the editor still shows that library. With keepEdits and edits
         * not saved yet, the form keeps them and only the cover, history, buttons and slots are drawn again.
         */
        async function showEditorState(state, keepEdits) {
            const editor = page.editor;
            if (!editor || !state || !sameId(state.LibraryId, editor.id)) return;
            const keep = keepEdits && isDirty();
            const draft = keep ? editor.draft : draftOf(state);
            const ids = coverIds(state).concat(draft.Slots.map((s) => s.ItemId).filter(Boolean));
            try {
                await loadItems(ids, editor);
            } catch (err) {
                tell(editor.id, 'editor', 'The titles could not be read: ' + err.message, 'error');
            }
            // The admin may have switched to another library while the titles loaded.
            if (page.editor !== editor) return;
            editor.state = state;
            if (keep) {
                renderPreview();
                renderEditorButtons();
                renderHistory(state);
                renderSlots();
                updateDirty();
                return;
            }
            editor.draft = draft;
            editor.openSlot = null;
            renderEditor();
        }

        function renderEditor() {
            const editor = page.editor;
            const card = cardOf(editor.id);
            const state = editor.state;
            $('lcm-editor-title').textContent = 'Edit ' + (card ? card.Name : 'library');
            const canRender = !!(card && card.CanRender);
            $('lcm-upload-only').hidden = canRender;
            $('lcm-render-settings').hidden = !canRender;
            $('lcm-slots-section').hidden = !canRender;
            $('lcm-save').hidden = !canRender;
            $('lcm-undo').hidden = !canRender;

            renderPreview();
            renderEditorButtons();
            renderHistory(state);
            fillSettings();
            renderSlots();
            updateDirty();
        }

        function renderPreview() {
            const card = cardOf(page.editor.id);
            const preview = $('lcm-preview');
            preview.querySelector('img').alt = card ? 'Current cover of ' + card.Name : '';
            const key = showPicture(preview, 'editor', card ? cardPicture(card) : null, 'No cover yet');
            pictures.keep('editor', new Set(key ? [key] : []));
        }

        function renderEditorButtons() {
            const editor = page.editor;
            if (!editor) return;
            const card = cardOf(editor.id);
            const state = editor.state;
            const id = editor.id;
            const generate = $('lcm-ed-generate');
            const pin = $('lcm-ed-pin');
            const upload = $('lcm-ed-upload');
            [generate, pin, upload].forEach((node) => node.setAttribute('data-lcm-lib', id));
            generate.hidden = !(card && card.CanRender) || state.Pinned;
            if (card && !card.Enabled) generate.setAttribute('data-lcm-off', '');
            else generate.removeAttribute('data-lcm-off');
            pin.querySelector('span').textContent = state.Pinned ? 'Back to automatic' : 'Pin this cover';
            if (!state.Pinned && !state.CurrentCoverId) pin.setAttribute('data-lcm-off', '');
            else pin.removeAttribute('data-lcm-off');

            const meta = [];
            if (state.LastRenderedAt) meta.push('Last rendered ' + when(state.LastRenderedAt));
            if (card) meta.push(nextText(card));
            if (page.watching.has(id)) meta.push('Building a new cover...');
            $('lcm-ed-meta').textContent = meta.join(' \u00b7 ');
            refreshDisabled();
        }

        function renderHistory(state) {
            const strip = $('lcm-history');
            strip.textContent = '';
            const records = (state.History || []).slice();
            if (state.Original) records.push(state.Original);
            const keys = new Set();
            if (!records.length) strip.appendChild(el('p', 'lcm-muted lcm-small', 'No covers yet.'));
            for (const record of records) {
                const figure = el('figure');
                const box = coverBox();
                const origin = originName(record.Origin);
                box.querySelector('img').alt = origin + ' cover of ' + when(record.CreatedAt);
                // The still version is a small JPEG; an upload or the original is the one file it has.
                const key = showPicture(box, 'history', coverPicture(state.LibraryId, record.Id, true), '');
                keys.add(key);
                figure.appendChild(box);
                const caption = el('figcaption');
                const label = origin === 'Original'
                    ? 'Original'
                    : (origin === 'Uploaded' ? 'Uploaded ' : 'Made ') + when(record.CreatedAt);
                caption.appendChild(el('span', null, label));
                if (record.Id === state.CurrentCoverId) {
                    caption.appendChild(el('span', 'lcm-chip', 'Current'));
                } else {
                    const restore = button('Restore');
                    restore.setAttribute('data-lcm-lib', state.LibraryId);
                    restore.setAttribute('data-cover-id', record.Id);
                    restore.addEventListener('click', () => restoreCover(state.LibraryId, record.Id));
                    caption.appendChild(restore);
                }
                figure.appendChild(caption);
                strip.appendChild(figure);
            }
            pictures.keep('history', keys);
        }

        // ---------- Editor: settings ----------

        function fillSettings() {
            const draft = page.editor.draft;
            const style = draft.Style;
            view.querySelectorAll('#lcm-mode button').forEach((b) => {
                b.setAttribute('aria-pressed', String(b.getAttribute('data-mode') === draft.Mode));
            });
            $('lcm-mode-help').textContent = MODE_HELP[draft.Mode] || '';
            $('lcm-pool-watched').checked = !!style.UseMostWatched;
            $('lcm-pool-rated').checked = !!style.UseTopRated;
            $('lcm-pool-recent').checked = !!style.UseRecent;
            $('lcm-slot-count').value = style.SlotCount;
            $('lcm-length').value = style.LengthSeconds;
            $('lcm-fade').checked = !!style.Fade;
            $('lcm-title').value = style.Title || '';
            const card = cardOf(page.editor.id);
            $('lcm-title').placeholder = card ? card.Name.toUpperCase() : '';

            const font = $('lcm-font');
            font.textContent = '';
            const families = (page.settings && page.settings.Fonts) || [];
            const names = families.indexOf(style.Font) >= 0 || !style.Font ? families : families.concat([style.Font]);
            for (const name of names) {
                const option = el('option', null, name);
                option.value = name;
                font.appendChild(option);
            }
            font.value = style.Font;
            $('lcm-text-size').value = style.TextSize;
            $('lcm-darken').value = style.Darken;
            $('lcm-frame').checked = !!style.Frame;
            $('lcm-shadow').checked = !!style.Shadow;

            const own = draft.RenewEveryDays > 0;
            $('lcm-renew').value = own ? 'own' : 'weekly';
            $('lcm-renew-days').value = own ? draft.RenewEveryDays : 7;
            $('lcm-renew-days-box').hidden = !own;
        }

        /** Reads the controls into the draft, keeping every number inside the server's range. */
        function readSettings() {
            const draft = page.editor.draft;
            const style = draft.Style;
            style.UseMostWatched = $('lcm-pool-watched').checked;
            style.UseTopRated = $('lcm-pool-rated').checked;
            style.UseRecent = $('lcm-pool-recent').checked;
            style.SlotCount = clamp($('lcm-slot-count').value, LIMITS.SlotCount);
            style.LengthSeconds = clamp($('lcm-length').value, LIMITS.LengthSeconds);
            style.Fade = $('lcm-fade').checked;
            style.Title = $('lcm-title').value.trim().slice(0, 100);
            style.Font = $('lcm-font').value;
            style.TextSize = clamp($('lcm-text-size').value, LIMITS.TextSize);
            style.Darken = clamp($('lcm-darken').value, LIMITS.Darken);
            style.Frame = $('lcm-frame').checked;
            style.Shadow = $('lcm-shadow').checked;
            const own = $('lcm-renew').value === 'own';
            draft.RenewEveryDays = own ? clamp($('lcm-renew-days').value, LIMITS.RenewEveryDays) : 0;
            $('lcm-renew-days-box').hidden = !own;
        }

        /** After a number field is left: show the value the page will send. */
        function settleNumbers() {
            const style = page.editor.draft.Style;
            $('lcm-slot-count').value = style.SlotCount;
            $('lcm-length').value = style.LengthSeconds;
            $('lcm-text-size').value = style.TextSize;
            $('lcm-darken').value = style.Darken;
            if (page.editor.draft.RenewEveryDays > 0) $('lcm-renew-days').value = page.editor.draft.RenewEveryDays;
        }

        function slotsChanged() {
            const editor = page.editor;
            return !same(editor.draft.Slots, draftOf(editor.state).Slots);
        }

        function isDirty() {
            const editor = page.editor;
            return !!editor && !same(editor.draft, draftOf(editor.state));
        }

        function updateDirty() {
            const editor = page.editor;
            if (!editor) return;
            const busy = page.busy.has(editor.id);
            const dirty = isDirty();
            $('lcm-save').disabled = busy || !dirty;
            $('lcm-undo').disabled = busy || !dirty;
            view.querySelectorAll('#lcm-mode button').forEach((b) => { b.disabled = busy; });
        }

        function saveEditor() {
            const editor = page.editor;
            if (!editor) return;
            readSettings();
            settleNumbers();
            const id = editor.id;
            const card = cardOf(id);
            const body = {
                Mode: editor.draft.Mode,
                Style: editor.draft.Style,
                RenewEveryDays: editor.draft.RenewEveryDays
            };
            const withSlots = slotsChanged();
            if (withSlots) body.Slots = editor.draft.Slots;
            return act(id, 'editor', async () => {
                const earlier = withSlots ? await before(id) : null;
                const state = await request('PUT', API + 'Libraries/' + id, { json: body });
                // The server builds a cover by itself when the slots changed, unless nothing may be built now.
                const builds = withSlots && card && card.CanRender && card.Enabled && !state.Pinned;
                if (builds) watch(id, earlier);
                await afterChange(id, state);
                tell(id, 'editor', builds
                    ? 'Saved. A new cover with these slots is being built.'
                    : 'Saved. The next cover uses these settings; choose Generate now to see them.', 'done');
            });
        }

        // ---------- Editor: slots ----------

        function itemName(id) {
            const item = page.editor.items.get(id);
            if (!item) return 'A title';
            return item.ProductionYear ? item.Name + ' (' + item.ProductionYear + ')' : item.Name;
        }

        /** What each slot shows now: a locked slot its title; free slots the cover's other titles, in order. */
        function shownTitles() {
            const editor = page.editor;
            const count = editor.state.Style.SlotCount;
            const locked = new Set(editor.state.Slots.slice(0, count).map((s) => s.ItemId).filter(Boolean));
            const free = coverTitles(editor, editor.state).filter((id) => !locked.has(id));
            const shown = [];
            for (let i = 0; i < SLOT_COUNT; i++) {
                const slot = editor.state.Slots[i];
                shown.push(i < count && !(slot && slot.ItemId) ? free.shift() || null : null);
            }
            return shown;
        }

        function slotPicture(slot, shownId) {
            const id = slot.ItemId || shownId;
            if (!id) return null;
            if (slot.ItemId && slot.MomentSeconds !== null) {
                return {
                    key: id + '/frame/' + slot.MomentSeconds,
                    path: API + 'Items/' + id + '/Frame',
                    query: { at: slot.MomentSeconds }
                };
            }
            const item = page.editor.items.get(id);
            const backdrop = slot.ItemId && slot.BackdropIndex !== null ? slot.BackdropIndex : 0;
            if (item && item.BackdropImageTags && item.BackdropImageTags.length > backdrop) {
                return {
                    key: id + '/backdrop/' + backdrop,
                    path: 'Items/' + id + '/Images/Backdrop/' + backdrop,
                    query: { maxWidth: 320 }
                };
            }
            if (item && item.ImageTags && item.ImageTags.Primary) {
                return { key: id + '/primary', path: 'Items/' + id + '/Images/Primary', query: { maxWidth: 320 } };
            }
            return null;
        }

        function slotDetail(slot) {
            if (slot.MomentSeconds !== null) return 'Scene at ' + seconds(slot.MomentSeconds);
            if (slot.BackdropIndex !== null) return 'Backdrop ' + (slot.BackdropIndex + 1);
            return 'Locked; the plugin picks the ' + (page.editor.draft.Mode === 'Scenes' ? 'moment' : 'picture');
        }

        /** Shows a picture of the slots; it stays until the slots are drawn again. */
        function slotShow(box, picture) {
            const key = showPicture(box, 'slots', picture, '');
            if (key) page.editor.slotKeys.add(key);
            return key;
        }

        function renderSlots() {
            const editor = page.editor;
            const list = $('lcm-slots');
            // Drawing the slots again replaces their controls: keep the keyboard focus on the same control, or on the
            // slot's Choose button when the focused one is gone (a picked title, a backdrop).
            const active = document.activeElement;
            let focus = null;
            if (active && list.contains(active)) {
                const row = active.closest('[data-slot]');
                focus = active.getAttribute('data-focus') || (row ? 'choose-' + row.getAttribute('data-slot') : null);
            }
            list.textContent = '';
            const shown = shownTitles();
            const count = clamp(editor.draft.Style.SlotCount, LIMITS.SlotCount);
            editor.slotKeys = new Set();
            editor.draft.Slots.forEach((slot, index) => {
                const row = el('div', 'lcm-slot');
                row.setAttribute('data-slot', String(index));
                if (index >= count) row.classList.add('lcm-unused');
                const line = el('div', 'lcm-slot-line');
                line.appendChild(el('span', 'lcm-slot-n', String(index + 1)));

                const box = coverBox();
                slotShow(box, slotPicture(slot, shown[index]));
                line.appendChild(box);

                const what = el('div', 'lcm-slot-what');
                if (slot.ItemId) {
                    what.appendChild(el('b', null, itemName(slot.ItemId)));
                    what.appendChild(el('span', 'lcm-muted lcm-small', slotDetail(slot)));
                } else {
                    what.appendChild(el('b', 'lcm-slot-free', index >= count
                        ? 'Not used: the cover has ' + count + (count === 1 ? ' slot' : ' slots')
                        : 'Free: picked again on every renewal'));
                    if (shown[index]) {
                        what.appendChild(el('span', 'lcm-muted lcm-small', 'Now: ' + itemName(shown[index])));
                    }
                }
                line.appendChild(what);

                const controls = el('div', 'lcm-btns');
                const lock = el('button', 'lcm-lock', slot.ItemId ? 'Locked' : 'Free');
                lock.type = 'button';
                lock.setAttribute('aria-pressed', String(!!slot.ItemId));
                lock.setAttribute('aria-label', 'Slot ' + (index + 1)
                    + (slot.ItemId ? ': locked, choose to free it' : ': free, choose to lock it'));
                lock.setAttribute('data-lcm-lib', editor.id);
                lock.setAttribute('data-focus', 'lock-' + index);
                lock.addEventListener('click', () => toggleLock(index, shown[index]));
                controls.appendChild(lock);
                const choose = button(editor.openSlot === index ? 'Done' : 'Choose');
                choose.setAttribute('data-lcm-lib', editor.id);
                choose.setAttribute('data-focus', 'choose-' + index);
                choose.addEventListener('click', () => {
                    editor.openSlot = editor.openSlot === index ? null : index;
                    renderSlots();
                });
                controls.appendChild(choose);
                line.appendChild(controls);
                row.appendChild(line);

                if (editor.openSlot === index) row.appendChild(slotPanel(index, slot));
                list.appendChild(row);
            });
            pictures.keep('slots', editor.slotKeys);
            refreshDisabled();
            const target = focus ? list.querySelector('[data-focus="' + focus + '"]') : null;
            if (target) target.focus();
        }

        function toggleLock(index, shownId) {
            const editor = page.editor;
            const slot = editor.draft.Slots[index];
            if (slot.ItemId) {
                editor.draft.Slots[index] = { ItemId: null, MomentSeconds: null, BackdropIndex: null };
            } else if (shownId) {
                editor.draft.Slots[index] = { ItemId: shownId, MomentSeconds: null, BackdropIndex: null };
            } else {
                // Nothing to keep yet: let the admin pick a title.
                editor.openSlot = index;
            }
            renderSlots();
            updateDirty();
        }

        function setSlot(index, values) {
            const editor = page.editor;
            const others = editor.draft.Slots
                .some((s, i) => i !== index && values.ItemId && s.ItemId === values.ItemId);
            if (others) {
                tell(editor.id, 'editor',
                    'That title is in another slot already; a title is in a cover once.', 'error');
                return;
            }
            editor.draft.Slots[index] = Object.assign(
                { ItemId: null, MomentSeconds: null, BackdropIndex: null },
                values);
            renderSlots();
            updateDirty();
        }

        /** The panel under a slot: search a title, then pick its backdrop or its moment. */
        function slotPanel(index, slot) {
            const editor = page.editor;
            const panel = el('div', 'lcm-slot-panel');

            const field = el('div', 'inputContainer');
            const label = el('label', 'inputLabel inputLabelUnfocused', 'Search this library');
            const input = document.createElement('input');
            input.setAttribute('is', 'emby-input');
            input.type = 'search';
            input.id = 'lcm-slot-search-' + index;
            input.setAttribute('data-focus', 'search-' + index);
            input.maxLength = 100;
            label.htmlFor = input.id;
            field.appendChild(label);
            field.appendChild(input);
            panel.appendChild(field);
            panel.appendChild(el('div', 'lcm-muted lcm-small',
                'Only titles that everyone who can see this library may see are offered.'));
            const results = el('div', 'lcm-results');
            panel.appendChild(results);

            let timer = null;
            let asked = 0;
            const search = () => {
                const term = input.value.trim();
                const ask = ++asked;
                const query = term ? { search: term } : undefined;
                request('GET', API + 'Libraries/' + editor.id + '/Titles', { query })
                    .then((titles) => {
                        if (ask !== asked || !panel.isConnected) return;
                        results.textContent = '';
                        if (!titles || !titles.length) {
                            results.appendChild(el('span', 'lcm-muted lcm-small', 'No titles found.'));
                        }
                        for (const title of titles || []) {
                            const name = title.Year ? title.Name + ' (' + title.Year + ')' : title.Name;
                            const pick = el('button', null, name);
                            pick.type = 'button';
                            pick.setAttribute('data-title-id', title.Id);
                            pick.setAttribute('aria-pressed', String(slot.ItemId === title.Id));
                            pick.addEventListener('click', () => {
                                if (!editor.items.has(title.Id)) {
                                    // Enough for the name until the title's details arrive.
                                    editor.items.set(title.Id, {
                                        Id: title.Id, Name: title.Name, ProductionYear: title.Year, partial: true
                                    });
                                }
                                setSlot(index, { ItemId: title.Id });
                                loadItems([title.Id], editor).then(() => {
                                    if (page.editor === editor) renderSlots();
                                }, () => { /* keeps the name it has */ });
                            });
                            results.appendChild(pick);
                        }
                    })
                    .catch((err) => {
                        if (ask === asked && panel.isConnected) results.textContent = err.message;
                    });
            };
            input.addEventListener('input', () => {
                clearTimeout(timer);
                timer = setTimeout(search, SEARCH_DELAY_MS);
            });
            search();

            if (slot.ItemId) {
                const mode = editor.draft.Mode;
                const useScene = mode === 'Scenes' || (mode === 'Manual' && slot.MomentSeconds !== null);
                if (mode === 'Manual') panel.appendChild(kindChooser(index, slot));
                if (mode === 'Art' || (mode === 'Manual' && slot.BackdropIndex !== null)) {
                    panel.appendChild(backdropPicker(index, slot));
                }
                if (useScene) panel.appendChild(momentPicker(index, slot));
            }
            return panel;
        }

        function kindChooser(index, slot) {
            const box = el('div', 'lcm-seg');
            box.setAttribute('role', 'group');
            box.setAttribute('aria-label', 'What slot ' + (index + 1) + ' shows');
            const choices = [
                [
                    'Let the plugin choose',
                    slot.MomentSeconds === null && slot.BackdropIndex === null,
                    { MomentSeconds: null, BackdropIndex: null }
                ],
                ['Artwork', slot.BackdropIndex !== null, { MomentSeconds: null, BackdropIndex: 0 }],
                ['Scene', slot.MomentSeconds !== null, { MomentSeconds: 0, BackdropIndex: null }]
            ];
            for (const [text, on, values] of choices) {
                const choice = el('button', null, text);
                choice.type = 'button';
                choice.setAttribute('aria-pressed', String(on));
                choice.addEventListener('click', () => setSlot(index, Object.assign({ ItemId: slot.ItemId }, values)));
                box.appendChild(choice);
            }
            return box;
        }

        function backdropPicker(index, slot) {
            const editor = page.editor;
            const wrap = el('div');
            wrap.appendChild(el('div', 'lcm-muted lcm-small', 'Backdrop'));
            const grid = el('div', 'lcm-backdrops');
            wrap.appendChild(grid);
            const any = el('button', null, 'Any backdrop');
            any.type = 'button';
            any.setAttribute('aria-pressed', String(slot.BackdropIndex === null));
            any.addEventListener('click', () => setSlot(index, { ItemId: slot.ItemId }));
            grid.appendChild(any);
            request('GET', API + 'Items/' + slot.ItemId + '/Backdrops').then((backdrops) => {
                // The slots were drawn again meanwhile: this panel is gone.
                if (page.editor !== editor || !wrap.isConnected) return;
                const usable = (backdrops || []).filter((b) => b.Usable);
                if (!usable.length) {
                    grid.appendChild(el('span', 'lcm-muted lcm-small',
                        'This title has no backdrop on this server, so an art cover skips it.'));
                    return;
                }
                for (const backdrop of usable) {
                    const pick = el('button');
                    pick.type = 'button';
                    pick.setAttribute('aria-pressed', String(slot.BackdropIndex === backdrop.Index));
                    pick.setAttribute('aria-label', 'Backdrop ' + (backdrop.Index + 1));
                    const box = coverBox();
                    const [path, query] = backdrop.Url.split('?');
                    const params = Object.fromEntries(new URLSearchParams(query || ''));
                    slotShow(box, { key: slot.ItemId + '/backdrop/' + backdrop.Index, path, query: params });
                    pick.appendChild(box);
                    pick.appendChild(el('span', 'lcm-small', 'Backdrop ' + (backdrop.Index + 1)));
                    pick.addEventListener('click', () => {
                        setSlot(index, { ItemId: slot.ItemId, BackdropIndex: backdrop.Index });
                    });
                    grid.appendChild(pick);
                }
            }, (err) => {
                if (wrap.isConnected) grid.appendChild(el('span', 'lcm-error lcm-small', err.message));
            });
            return wrap;
        }

        function momentPicker(index, slot) {
            const editor = page.editor;
            const item = editor.items.get(slot.ItemId);
            const runtime = item && item.RunTimeTicks ? item.RunTimeTicks / TICKS_PER_SECOND : 0;
            const wrap = el('div');
            const own = slot.MomentSeconds !== null;
            const toggle = el('label', 'checkboxContainer');
            const check = document.createElement('input');
            check.setAttribute('is', 'emby-checkbox');
            check.type = 'checkbox';
            check.checked = !own;
            toggle.appendChild(check);
            toggle.appendChild(el('span', null, 'Let the plugin pick the best moment'));
            wrap.appendChild(toggle);
            check.addEventListener('change', () => {
                const start = check.checked ? null : Math.round(runtime * 0.3);
                setSlot(index, { ItemId: slot.ItemId, MomentSeconds: start });
            });
            if (!own) return wrap;

            const row = el('div', 'lcm-moment');
            const box = coverBox();
            row.appendChild(box);
            const side = el('div');
            const slider = document.createElement('input');
            slider.type = 'range';
            slider.min = '0';
            // A second short of the end: the very last instant of a video has no frame to show.
            slider.max = String(Math.max(1, Math.floor(runtime || 3600) - 1));
            slider.step = '1';
            slider.value = String(Math.min(Number(slider.max), Math.round(slot.MomentSeconds)));
            slider.setAttribute('aria-label', 'Moment of slot ' + (index + 1));
            const time = el('div', 'lcm-small', seconds(slot.MomentSeconds));
            side.appendChild(slider);
            side.appendChild(time);
            if (!runtime) {
                side.appendChild(el('div', 'lcm-muted lcm-small', 'Jellyfin does not know how long this video is.'));
            }
            side.appendChild(el('div', 'lcm-muted lcm-small', 'For a series, the clip comes from its first episode.'));
            row.appendChild(side);
            wrap.appendChild(row);

            // The slider's frame has keys of its own, so moving it never revokes the picture of the slot's row.
            let timer = null;
            let shownKey = null;
            const showFrame = (at) => {
                if (page.editor !== editor) return;
                if (shownKey) editor.slotKeys.delete(shownKey);
                shownKey = slotShow(box, {
                    key: 'slider/' + slot.ItemId + '/' + at,
                    path: API + 'Items/' + slot.ItemId + '/Frame',
                    query: { at }
                });
                pictures.keep('slots', editor.slotKeys);
            };
            showFrame(Number(slider.value));
            slider.addEventListener('input', () => {
                time.textContent = seconds(Number(slider.value));
                clearTimeout(timer);
                timer = setTimeout(() => {
                    if (wrap.isConnected) showFrame(Number(slider.value));
                }, FRAME_DELAY_MS);
            });
            slider.addEventListener('change', () => {
                clearTimeout(timer);
                const at = Number(slider.value);
                editor.draft.Slots[index] = { ItemId: slot.ItemId, MomentSeconds: at, BackdropIndex: null };
                const detail = view.querySelector('[data-slot="' + index + '"] .lcm-slot-what span');
                if (detail) detail.textContent = slotDetail(editor.draft.Slots[index]);
                showFrame(at);
                updateDirty();
            });
            return wrap;
        }

        // ---------- Global actions ----------

        function generateAll() {
            return act(GLOBAL, 'page', async () => {
                const due = page.cards.filter((c) => c.CanRender && c.Enabled && !c.Pinned);
                const earlier = await Promise.all(due.map((c) => before(c.Id)));
                const answer = await request('POST', API + 'GenerateAll');
                const queued = answer ? answer.Queued : 0;
                say('page', queued
                    ? 'Building ' + queued + (queued === 1 ? ' cover.' : ' covers.')
                    : 'Nothing to build: every library is pinned, switched off, upload only, or already due.');
                due.forEach((c, i) => watch(c.Id, earlier[i]));
            });
        }

        function changeSettings(update, done) {
            return act(GLOBAL, 'page', async () => {
                const before = page.settings;
                try {
                    page.settings = await request('PUT', API + 'Settings', { json: update });
                } catch (err) {
                    await loadSettings().catch(() => { page.settings = before; });
                    throw err;
                }
                say('page', done, 'done');
            });
        }

        // ---------- Wiring ----------

        $('lcm-generate-all').addEventListener('click', generateAll);
        $('lcm-default-mode').addEventListener('change', (e) => {
            const label = e.target.selectedOptions[0].textContent.toLowerCase();
            changeSettings({ DefaultMode: e.target.value }, 'New libraries start with ' + label + '.');
        });
        $('lcm-format').addEventListener('change', (e) => {
            const value = e.target.value;
            const done = 'Applying the ' + value.toLowerCase() + ' version of every cover...';
            changeSettings({ PrimaryFormat: value }, done).then(() => {
                // The server answers 202 and applies the other version in the background.
                for (const delay of [2000, 8000]) {
                    setTimeout(() => {
                        if (!page.visible || !view.isConnected) return;
                        // A failed refresh is left to the next one.
                        loadCards().then(() => page.editor && renderEditor(), () => {});
                    }, delay);
                }
            });
        });

        $('lcm-editor-close').addEventListener('click', closeEditor);
        $('lcm-ed-generate').addEventListener('click', () => page.editor && generateLibrary(page.editor.id, 'editor'));
        $('lcm-ed-pin').addEventListener('click', () => {
            if (!page.editor) return;
            if (page.editor.state.Pinned) unpinLibrary(page.editor.id, 'editor');
            else pinLibrary(page.editor.id, 'editor');
        });
        $('lcm-ed-upload').addEventListener('click', () => page.editor && openUpload(page.editor.id));
        $('lcm-save').addEventListener('click', saveEditor);
        $('lcm-undo').addEventListener('click', () => {
            if (!page.editor) return;
            page.editor.draft = draftOf(page.editor.state);
            page.editor.openSlot = null;
            fillSettings();
            renderSlots();
            updateDirty();
        });
        view.querySelectorAll('#lcm-mode button').forEach((b) => {
            b.addEventListener('click', () => {
                if (!page.editor) return;
                page.editor.draft.Mode = b.getAttribute('data-mode');
                readSettings();
                fillSettings();
                renderSlots();
                updateDirty();
            });
        });
        const settings = $('lcm-settings');
        settings.addEventListener('input', () => {
            if (!page.editor) return;
            const slotsBefore = page.editor.draft.Style.SlotCount;
            readSettings();
            if (page.editor.draft.Style.SlotCount !== slotsBefore) renderSlots();
            updateDirty();
        });
        settings.addEventListener('change', () => {
            if (!page.editor) return;
            readSettings();
            settleNumbers();
            updateDirty();
        });

        $('lcm-upload-send').addEventListener('click', sendUpload);
        $('lcm-upload-cancel').addEventListener('click', closeUpload);
        $('lcm-upload-dialog').addEventListener('close', () => { page.uploadFor = null; });

        // ---------- Showing and hiding ----------

        async function show() {
            page.visible = true;
            markTheme();
            say('page', '');
            try {
                await loadSettings();
                const schedule = loadSchedule().catch(() => { $('lcm-schedule').textContent = 'unknown'; });
                await Promise.all([schedule, loadCards()]);
                if (page.editor) await reloadEditor(true);
            } catch (err) {
                say('page', 'The page could not load: ' + err.message, 'error');
            }
            schedulePoll();
        }

        function hide() {
            page.visible = false;
            clearTimeout(page.timer);
            page.timer = null;
            closeUpload();
            pictures.clear();
        }

        return { show, hide };
    }

    // Jellyfin dispatches viewshow and viewhide on the page's element, and they bubble to the document.
    document.addEventListener('viewshow', (e) => {
        const view = e.target;
        if (!view || view.id !== PAGE_ID) return;
        if (!view.lcmPage) view.lcmPage = wire(view);
        view.lcmPage.show();
    });
    document.addEventListener('viewhide', (e) => {
        const view = e.target;
        if (view && view.id === PAGE_ID && view.lcmPage) view.lcmPage.hide();
    });
})();
