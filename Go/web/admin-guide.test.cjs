'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { test } = require('node:test');
const hostSource = fs.readFileSync(__dirname + '/host.js', 'utf8');
const page = fs.readFileSync(__dirname + '/configPage.html', 'utf8');
const embyPage = fs.readFileSync(__dirname + '/../../Emby/Emby.Plugins.Moonfin/Pages/moonfin.js', 'utf8');
const { setImmediate: nextTurn } = require('node:timers/promises');

function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}
const start = page.indexOf('var guideGroups =');
const end = page.indexOf('// Fills the User defaults form', start);
assert.ok(start >= 0 && end > start, 'shared guide picker missing');
const switchStart = page.indexOf('function switchDefaultsProfile(name)');
const switchEnd = page.indexOf('function userName(id)', switchStart);
assert.ok(switchStart >= 0 && switchEnd > switchStart, 'profile switch missing');

function picker(host, pins, responses) {
    const calls = [];
    const root = { innerHTML: '', inputs: [], querySelectorAll() { return this.inputs; } };
    const switcher = { dataset: {}, addEventListener(_, listener) { this.click = listener; } };
    const window = { moonbaseHost: { name: host }, fetch: async url => {
        calls.push(url);
        const groups = responses.shift();
        return groups instanceof Error ? { ok: false, status: 502 } :
            { ok: true, status: 200, json: async () => ({ availableGuideGroups: groups }) };
    }};
    const context = vm.createContext({
        window, URLSearchParams, TextEncoder, setInterval: () => 1,
        location: { origin: 'https://moonbase.test', pathname: '/admin' },
        document: { addEventListener() {}, querySelector(selector) {
            if (selector === '#DefaultsProfileSwitch') return switcher;
            if (selector === '#DefaultPinnedGuideGroups') return root;
            return { addEventListener() {} };
        } },
        defaultsState: { active: 'global', profiles: { global: { liveTvSavedFilters: pins }, tv: {} } },
        collectActiveDefaults() {}, snapshotGlobalLabels() {},
        ApiClient: { getUrl: path => path, ajax: async () => {
            const response = await responses.shift();
            return Array.isArray(response) ? { channels: [{ groups: response }] } : response;
        } },
        esc: value => String(value).replace(/&/g, '&amp;').replace(/"/g, '&quot;'),
    });
    if (host === 'Jellyfin') delete window.moonbaseHost;
    else vm.runInContext(hostSource, context);
    function showPage() {
        return vm.runInContext('(function() {' + page.slice(start, end) +
            '\nfunction populateDefaults(d) { renderDefaultGuideGroups(d); }\n' +
            page.slice(switchStart, switchEnd) + '\nreturn discoverDefaultGuideGroups; })()', context);
    }
    let discover = showPage();
    return { calls, root, reopen() { discover = showPage(); }, switchTo(profile) {
        switcher.click({ target: { closest: () => ({ dataset: { profile } }) } });
    }, async reload() {
        discover();
        await nextTurn();
    }};
}

test('Jellyfin shows partial discovery with or without groups', async () => {
    const dashboard = picker('Jellyfin', [], [
        { partial: true, channels: [] }, { partial: true, channels: [{ groups: ['News'] }] },
        { partial: false, channels: [] },
    ]);
    await dashboard.reload();
    assert.match(dashboard.root.innerHTML, /Reload to retry/);
    assert.doesNotMatch(dashboard.root.innerHTML, /No channel groups available/);
    await dashboard.reload();
    assert.match(dashboard.root.innerHTML, /group:News/);
    assert.match(dashboard.root.innerHTML, /Reload to retry/);
    await dashboard.reload();
    assert.match(dashboard.root.innerHTML, /No channel groups available/);
    assert.doesNotMatch(dashboard.root.innerHTML, /Reload to retry/);
});

test('a retained Jellyfin profile switch uses groups from the latest page visit', async () => {
    const oldRequest = deferred();
    const jellyfin = picker('Jellyfin', [], [oldRequest.promise, ['News', 'Sports']]);
    await jellyfin.reload();
    jellyfin.reopen();
    await jellyfin.reload();
    assert.equal(checked(jellyfin.root.innerHTML, 'group:Sports'), false);
    jellyfin.switchTo('tv');
    jellyfin.switchTo('global');
    assert.equal(checked(jellyfin.root.innerHTML, 'group:Sports'), false);
    oldRequest.resolve(['Old']);
    await nextTurn();
    assert.equal(checked(jellyfin.root.innerHTML, 'group:Sports'), false);
    assert.doesNotMatch(jellyfin.root.innerHTML, /group:Old/);
});

test('Emby preserves saved groups during discovery and ignores replies from earlier visits', async () => {
    const requests = [];
    const context = vm.createContext({
        guideGroupsVisit: 0, availableDefaultGuideGroups: [], defaultGuideGroupsMessage: '', defaultGuideCategories: [],
        defaultsState: { active: 'global', profiles: { global: { liveTvSavedFilters: ['group:News'] } } },
        ApiClient: { getUrl: path => path, ajax: () => {
            const request = deferred();
            requests.push(request);
            return request.promise;
        } }, esc: s => s,
    });
    vm.runInContext(['renderDefaultGuideGroups', 'discoverDefaultGuideGroups']
        .map(name => pageFunction(embyPage, name)).join('\n'), context);
    function visit() {
        const root = { innerHTML: '', querySelectorAll: () => [] };
        context.discoverDefaultGuideGroups({ querySelector: () => root }, ++context.guideGroupsVisit);
        return root;
    }
    const abandoned = [visit(), visit()];
    const failed = visit();
    requests[2].reject(new Error('offline'));
    await nextTurn();
    assert.equal(checked(failed.innerHTML, 'group:News'), true);
    assert.match(failed.innerHTML, /Reload to retry/);
    const root = visit();
    requests[3].resolve({ Items: [{ Tags: ['Sports'] }] });
    await nextTurn();
    const latest = root.innerHTML;
    requests[0].resolve({ Items: [{ Tags: ['Old'] }] });
    requests[1].reject(new Error('late'));
    await nextTurn();
    assert.deepEqual(abandoned.map(view => view.innerHTML), ['', '']);
    assert.equal(root.innerHTML, latest);
    assert.equal(checked(latest, 'group:Sports'), false);
    assert.equal(checked(latest, 'group:News'), true);
    assert.doesNotMatch(latest, /retry|group:Old/i);
});

function checked(html, key) {
    const tag = (html.match(/<input[^>]*>/g) || []).find(s => s.includes('data-guide-filter="' + key + '"'));
    assert.ok(tag, 'missing ' + key);
    return tag.includes(' checked');
}

test('Plex guide picker uses provider groups and keeps edits; failure and Silo remain safe', async () => {
    const plex = picker('Plex', ['category:premiere', 'group:Legacy'],
        [['Sports', 'Sports', 'News'], ['News']]);
    await plex.reload();
    assert.deepEqual(plex.calls, ['https://moonbase.test/Moonfin/Defaults?includeLiveTvGroups=true']);
    assert.equal(checked(plex.root.innerHTML, 'category:premiere'), true);
    assert.equal(checked(plex.root.innerHTML, 'group:Legacy'), true);
    assert.equal((plex.root.innerHTML.match(/data-guide-filter="group:Sports"/g) || []).length, 1);
    plex.root.inputs = [
        { checked: false, getAttribute: () => 'category:premiere' },
        { checked: true, getAttribute: () => 'group:News' },
    ];
    await plex.reload();
    assert.equal(checked(plex.root.innerHTML, 'category:premiere'), false);
    assert.equal(checked(plex.root.innerHTML, 'group:News'), true);
    const failed = picker('Plex', [], [new Error('unavailable')]);
    await failed.reload();
    assert.match(failed.root.innerHTML, /Could not load groups\. Reload to retry\./);
    const silo = picker('Silo', ['category:movies'], []);
    await silo.reload();
    assert.equal(silo.calls.length, 0);
    assert.equal(checked(silo.root.innerHTML, 'category:movies'), true);
});

test('Plex uses the same byte limit in the picker as in the settings API', async () => {
    const atLimit = 'group:' + 'é'.repeat(509);
    const tooLong = atLimit + 'x';
    const plex = picker('Plex', [atLimit, tooLong], [[]]);
    await plex.reload();
    assert.equal(checked(plex.root.innerHTML, atLimit), true);
    assert.ok(plex.root.innerHTML.includes('data-guide-filter="' + tooLong + '" disabled'));
});

function pageFunction(source, name) {
    const match = source.match(new RegExp('^( *)function ' + name + '\\([^\\n]*\\) \\{[\\s\\S]*?^\\1\\}', 'm'));
    assert.ok(match, 'missing page function ' + name);
    return match[0];
}

function moonbaseSession(fetch, controls = {}) {
    const storage = new Map();
    return function open() {
        const context = vm.createContext({
            moonbaseHost: { name: 'Plex', arr: true }, URLSearchParams, setInterval() {}, clearInterval() {}, scrollTo() {},
            location: { origin: 'https://moonbase.test', pathname: '/admin' },
            document: { addEventListener() {}, querySelector() { return null; }, getElementById(id) { return controls[id] || null; } },
            sessionStorage: { getItem: k => storage.get(k), setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) },
            fetch,
        });
        context.window = context;
        vm.runInContext(hostSource, context);
        context.Dashboard.hideLoadingMsg = () => {};
        return context.ApiClient;
    };
}

test('a direct configuration save restores the original pins after sign-in', async () => {
    const open = moonbaseSession(async (_, init) => init.method === 'POST' ? { ok: false, status: 401 } :
        { ok: true, json: async () => ({ DefaultUserSettings: { liveTvSavedFilters: ['group:News'] } }) });
    const api = open();
    const draft = await api.getPluginConfiguration();
    draft.DefaultUserSettings.liveTvSavedFilters = ['group:Kids'];
    await assert.rejects(api.updatePluginConfiguration('', draft), /401/);
    const restored = await open().getPluginConfiguration();
    assert.deepEqual(Array.from(restored.moonbaseDraftGuidePinBase), ['group:News']);
    assert.deepEqual(Array.from(restored.DefaultUserSettings.liveTvSavedFilters), ['group:Kids']);
});

async function openMoonbaseSettings(api) {
    const config = await api.getPluginConfiguration();
    const guideGroups = {};
    const defaultsState = { active: 'global', guideGroups, profiles: { global: config.DefaultUserSettings } };
    const load = page.indexOf('defaultsState.guidePinBase = config.');
    assert.ok(load >= 0);
    const save = vm.runInNewContext([
        page.slice(load, page.indexOf("defaultsState.active = 'global';", load)),
        pageFunction(page, 'withGuidePinEdits'), pageFunction(page, 'submit'), 'submit',
    ].join('\n'), {
        config, guideGroups, defaultsState, window: { moonbaseHost: { name: 'Plex' } }, ApiClient: api,
        document: { querySelector: () => ({ value: '' }), querySelectorAll: () => [] },
        MoonfinConfig: {}, DEFAULTS_PROFILE_NAMES: ['global'], Dashboard: { processPluginConfigurationUpdateResult() {} },
        collectActiveDefaults() {}, getPosterBadgeDefaults() {}, getPosterBadgeRules() {}, wholeNumberIn() {}, renderDefaultGuideGroups() {},
    });
    return { save, profile: defaultsState.profiles.global };
}

test('a queued Go save keeps the host settings read for that submission', async () => {
    const sonarr = { value: '' };
    const firstWrite = deferred();
    const posted = [];
    const open = moonbaseSession(async (_, init) => {
        if (init.method !== 'POST') return { ok: true, json: async () => ({ DefaultUserSettings: {} }) };
        posted.push(JSON.parse(init.body).SonarrUrl);
        if (posted.length === 1) await firstWrite.promise;
        return { ok: true };
    }, { SonarrUrl: sonarr });
    const editor = await openMoonbaseSettings(open());
    const first = editor.save();
    await nextTurn();
    sonarr.value = 'https://submitted.example';
    const second = editor.save();
    await nextTurn();
    sonarr.value = 'https://unsaved.example';
    firstWrite.resolve();
    await Promise.all([first, second]);
    assert.deepEqual(posted, [null, 'https://submitted.example']);
});

test('a restored Go admin save keeps explicit pin edits and preserves other changes', async () => {
    let pins = ['group:News'], expired = false;
    const open = moonbaseSession(async (_, init) => {
        if (init.method !== 'POST') return { ok: true, json: async () => ({ DefaultUserSettings: { liveTvSavedFilters: [...pins] } }) };
        if (expired) return { ok: false, status: 401 };
        pins = JSON.parse(init.body).DefaultUserSettings.liveTvSavedFilters;
        return { ok: true };
    });

    const editor = await openMoonbaseSettings(open());
    editor.profile.liveTvSavedFilters = ['category:movies']; // Add Movies and remove News.
    pins = ['category:movies', 'group:Sports']; // Another admin made both edits before our save.
    expired = true;
    await assert.rejects(editor.save(), /401/);
    pins = ['group:News', 'group:Music']; // Both edits were reversed and Sports was removed.
    expired = false;
    const restored = await openMoonbaseSettings(open());
    assert.deepEqual(Array.from(restored.profile.liveTvSavedFilters), ['category:movies', 'group:Sports']);
    await restored.save();
    assert.deepEqual(pins, ['group:Music', 'category:movies']);
});

for (const host of ['Jellyfin/Go', 'Emby']) {
    test(host + ' saves only the pin changes made in this tab', async t => {
        const emby = host === 'Emby';
        const source = emby ? embyPage : page;
        const draft = { liveTvSavedFilters: ['group:News', 'group:Shared'] };
        const defaultsState = { active: 'global', profiles: { global: draft }, guidePinBase: draft.liveTvSavedFilters, guideGroups: {} };
        let librariesReady = false, selectedLibraries = [], gameLibraries = ['arcade'];
        let rommSelection = '', savedRomm, address = '', savedAddress;
        const form = {
            querySelector(selector) {
                if (selector === '.gameLibraryCb' && !librariesReady) return null;
                if (selector === '#GamesRommLibraryId') return { value: rommSelection };
                if (selector === '#PublicServerUrl') return { value: address };
                return { value: '' };
            },
            querySelectorAll: selector => selector === '.gameLibraryCb:checked'
                ? selectedLibraries.map(id => ({ getAttribute: () => id })) : [],
        };
        let latest = ['group:Shared', 'group:Sports'], rejectSave = false, rejectRead = false, reads = 0;
        let displayed, editDuringSave;
        let visit = 1, delayedAck, loseReply = false;
        const save = vm.runInNewContext([
            pageFunction(source, 'withGuidePinEdits'), emby ? pageFunction(source, 'camelKeysDeep') : '',
            pageFunction(source, emby ? 'saveConfig' : 'submit'), emby ? 'saveConfig' : 'submit',
        ].join('\n'), {
            defaultsState, document: form, window: {}, DOMException, DEFAULTS_PROFILE_NAMES: ['global'], loading: { show() {} },
            get guideGroupsVisit() { return visit; },
            Dashboard: { processPluginConfigurationUpdateResult() {} }, MoonfinConfig: {}, PluginUniqueId: '',
            getPosterBadgeDefaults() {}, getPosterBadgeRules() {}, collectActiveDefaults() {}, wholeNumberIn() {}, reportConfigError() {},
            renderDefaultGuideGroups(...args) { displayed = args.at(-1).liveTvSavedFilters; },
            ApiClient: {
                getPluginConfiguration: async () => {
                    if (rejectRead) throw new Error('read failed');
                    const pins = !emby && ++reads === 1 ? ['group:Old'] : latest;
                    return { GameLibraryIds: gameLibraries, DefaultUserSettings: { [emby ? 'LiveTvSavedFilters' : 'liveTvSavedFilters']: pins } };
                },
                updatePluginConfiguration: async (_, config) => {
                    if (rejectSave) throw new Error('save failed');
                    gameLibraries = config.GameLibraryIds;
                    savedRomm = config.GamesRommLibraryId;
                    savedAddress = config.PublicServerUrl;
                    latest = config.DefaultUserSettings.liveTvSavedFilters;
                    if (editDuringSave) defaultsState.profiles.global = { liveTvSavedFilters: editDuringSave };
                    if (delayedAck) await delayedAck;
                    if (loseReply) throw new Error('reply lost');
                },
            },
        });
        function edit(...pins) {
            defaultsState.profiles.global = { liveTvSavedFilters: pins };
        }
        function leavePage() {
            visit++;
            defaultsState.guideGroups = {};
        }
        const submit = () => save(form);
        async function holdSave() {
            const gate = deferred();
            delayedAck = gate.promise;
            const saving = submit();
            await nextTurn();
            delayedAck = null;
            return { gate, saving };
        }

        await submit();
        assert.deepEqual(Array.from(latest), ['group:Shared', 'group:Sports']);
        assert.deepEqual(Array.from(displayed), ['group:Shared', 'group:Sports']);
        edit('group:Kids');
        rejectSave = true;
        await assert.rejects(submit(), /save failed/);
        rejectSave = false;
        latest = [...latest, 'group:Music']; // A different tab adds another pin before the retry.
        editDuringSave = ['group:Kids', 'group:Movies'];
        await submit();
        assert.deepEqual(Array.from(latest), ['group:Music', 'group:Kids']);
        assert.deepEqual(Array.from(displayed), ['group:Music', 'group:Kids', 'group:Movies']);
        editDuringSave = null;
        await submit();
        assert.deepEqual(Array.from(latest), ['group:Music', 'group:Kids', 'group:Movies']);
        latest = null; // Another admin clears the pins after this tab's successful save.
        await submit();
        assert.equal(latest, null);

        await t.test('library picks are read after configuration arrives and can be cleared', async () => {
            gameLibraries = ['arcade'];
            await submit(); // Game library checkboxes are still loading.
            assert.deepEqual(Array.from(gameLibraries), ['arcade']);
            const saving = submit();
            librariesReady = true;
            selectedLibraries = ['arcade'];
            rommSelection = 'retro';
            await saving;
            assert.deepEqual(Array.from(gameLibraries), ['arcade']);
            assert.equal(savedRomm, 'retro');
            selectedLibraries = [];
            rommSelection = '';
            await submit();
            assert.deepEqual(Array.from(gameLibraries), []);
            assert.equal(savedRomm, null);
        });

        await t.test('a save awaiting the form read cannot use a reopened page', async () => {
            const before = savedAddress;
            const saving = submit();
            leavePage();
            address = 'https://reopened.example';
            await assert.rejects(saving, /page changed/);
            assert.equal(savedAddress, before);
        });

        for (const failRead of [false, true]) {
            await t.test('queued pin edits survive a concurrent change, failed read=' + failRead, async () => {
                defaultsState.guidePinBase = ['group:News'];
                edit('group:News');
                latest = ['group:Movies'];
                const { gate, saving: first } = await holdSave();
                edit('group:Movies');
                const second = submit();
                latest = ['group:News', 'group:Music']; // The other admin reverses both pins and adds Music.
                rejectRead = failRead;
                gate.resolve();
                if (failRead) {
                    await assert.rejects(second, /read failed/);
                    rejectRead = false;
                    await submit();
                } else {
                    await second;
                }
                await first;
                assert.deepEqual(Array.from(latest), ['group:Music', 'group:Movies']);
            });
        }

        await t.test('an earlier save reply cannot undo an unpin on a new visit', async () => {
            edit('group:News');
            const { gate, saving } = await holdSave();
            assert.deepEqual(Array.from(latest), ['group:News']);
            leavePage();
            latest = ['group:Sports']; // The reopened page loaded another admin's change.
            defaultsState.profiles.global = { liveTvSavedFilters: latest };
            defaultsState.guidePinBase = latest;
            gate.resolve();
            await saving;
            edit();
            await submit();
            assert.deepEqual(Array.from(latest), []);
        });
        await t.test('overlapping saves keep the submitted unpin and later unsaved edits', async () => {
            edit('group:News');
            const { gate, saving: first } = await holdSave();
            edit();
            const second = submit();
            address = 'https://submitted.example';
            await nextTurn();
            address = 'https://unsaved.example';
            const beforeAcknowledgement = latest;
            latest = [...latest, 'group:Sports']; // Another administrator adds Sports.
            edit('group:Kids'); // Still unsaved.
            gate.resolve();
            await Promise.all([first, second]);
            assert.equal(savedAddress, 'https://submitted.example');
            assert.deepEqual(Array.from(beforeAcknowledgement), ['group:News']);
            assert.deepEqual(Array.from(latest), ['group:Sports']);
            assert.deepEqual(Array.from(displayed), ['group:Sports', 'group:Kids']);
        });
        await t.test('a lost save reply still allows the queued unpin', async () => {
            const { gate, saving } = await holdSave();
            const first = assert.rejects(saving, /save failed/);
            edit('group:Movies');
            const second = submit();
            gate.reject(new Error('save failed'));
            await Promise.all([first, second]);
            assert.deepEqual(Array.from(latest), ['group:Movies']);
        });
        await t.test('an edit after a lost reply can undo the earlier pin changes', async () => {
            edit('group:News');
            loseReply = true;
            await assert.rejects(submit(), /reply lost/);
            assert.deepEqual(Array.from(latest), ['group:News']);

            latest = [...latest, 'group:Music']; // Added independently after the failed reply.
            edit('group:Movies');
            loseReply = false;
            await submit();
            assert.deepEqual(Array.from(latest), ['group:Music', 'group:Movies']);
            assert.deepEqual(Array.from(displayed), Array.from(latest));
        });
    });
}
