'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { test } = require('node:test');
const hostSource = fs.readFileSync(__dirname + '/host.js', 'utf8');
const page = fs.readFileSync(__dirname + '/configPage.html', 'utf8');
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
        await new Promise(resolve => setImmediate(resolve));
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
    let finishOldRequest;
    const oldRequest = new Promise(resolve => { finishOldRequest = resolve; });
    const jellyfin = picker('Jellyfin', [], [oldRequest, ['News', 'Sports']]);
    await jellyfin.reload();
    jellyfin.reopen();
    await jellyfin.reload();
    assert.equal(checked(jellyfin.root.innerHTML, 'group:Sports'), false);
    jellyfin.switchTo('tv');
    jellyfin.switchTo('global');
    assert.equal(checked(jellyfin.root.innerHTML, 'group:Sports'), false);
    finishOldRequest(['Old']);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(checked(jellyfin.root.innerHTML, 'group:Sports'), false);
    assert.doesNotMatch(jellyfin.root.innerHTML, /group:Old/);
});

test('Emby ignores late group lists and errors from an earlier visit', async () => {
    const source = fs.readFileSync(__dirname + '/../../Emby/Emby.Plugins.Moonfin/Pages/moonfin.js', 'utf8');
    const requests = [], renders = [];
    const context = vm.createContext({
        guideGroupsVisit: 0, availableDefaultGuideGroups: [], defaultGuideGroupsMessage: '',
        ApiClient: { getUrl: path => path, ajax: () => new Promise((resolve, reject) => requests.push({ resolve, reject })) },
        defaultsState: { active: 'global', profiles: {} },
        renderDefaultGuideGroups(view) { renders.push(view); },
    });
    vm.runInContext(pageFunction(source, 'discoverDefaultGuideGroups'), context);
    const discover = view => context.discoverDefaultGuideGroups(view, ++context.guideGroupsVisit);
    discover('old success');
    discover('old error');
    discover('current');
    requests[2].resolve({ Items: [{ Tags: ['Sports'] }] });
    await new Promise(setImmediate);
    requests[0].resolve({ Items: [{ Tags: ['News'] }] });
    requests[1].reject(new Error('offline'));
    await new Promise(setImmediate);
    assert.deepEqual(Array.from(context.availableDefaultGuideGroups), ['Sports']);
    assert.doesNotMatch(context.defaultGuideGroupsMessage, /retry/i);
    assert.deepEqual(renders, ['current']);
});

test('Emby shows a discovery failure alongside saved groups and clears it after recovery', async () => {
    const source = fs.readFileSync(__dirname + '/../../Emby/Emby.Plugins.Moonfin/Pages/moonfin.js', 'utf8');
    const host = { innerHTML: '', querySelectorAll: () => [] };
    const context = vm.createContext({
        guideGroupsVisit: 1, availableDefaultGuideGroups: [], defaultGuideGroupsMessage: '', defaultGuideCategories: [],
        defaultsState: { active: 'global', profiles: { global: { liveTvSavedFilters: ['group:News'] } } },
        ApiClient: { getUrl: path => path, ajax: async () => { throw new Error('offline'); } }, esc: s => s,
    });
    vm.runInContext(pageFunction(source, 'renderDefaultGuideGroups') + '\n' + pageFunction(source, 'discoverDefaultGuideGroups'), context);
    const view = { querySelector: () => host };
    context.discoverDefaultGuideGroups(view, 1);
    await new Promise(setImmediate);
    assert.equal(checked(host.innerHTML, 'group:News'), true);
    assert.match(host.innerHTML, /Reload to retry/);
    context.ApiClient.ajax = async () => ({ Items: [] });
    context.discoverDefaultGuideGroups(view, 1);
    await new Promise(setImmediate);
    assert.equal(checked(host.innerHTML, 'group:News'), true);
    assert.doesNotMatch(host.innerHTML, /retry|No channel tags/i);
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

test('Moonbase restores both the edited pins and their original selection after sign-in', async () => {
    const storage = new Map();
    function open() {
        const context = vm.createContext({
            moonbaseHost: { name: 'Plex' }, URLSearchParams, setInterval() {}, clearInterval() {}, scrollTo() {},
            location: { origin: 'https://moonbase.test', pathname: '/admin' },
            document: { addEventListener() {}, querySelector() { return null; }, getElementById() { return null; } },
            sessionStorage: { getItem: k => storage.get(k), setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) },
            fetch: async (_, init) => init.method === 'POST' ? { ok: false, status: 401 } :
                { ok: true, json: async () => ({ DefaultUserSettings: { liveTvSavedFilters: ['group:News'] } }) },
        });
        context.window = context;
        vm.runInContext(hostSource, context);
        context.Dashboard.hideLoadingMsg = () => {};
        return context.ApiClient;
    }
    const api = open();
    const draft = await api.getPluginConfiguration();
    draft.DefaultUserSettings.liveTvSavedFilters = ['group:Kids'];
    await assert.rejects(api.updatePluginConfiguration('', draft), /401/);
    const restored = await open().getPluginConfiguration();
    assert.deepEqual(Array.from(restored.moonbaseDraftGuidePinBase), ['group:News']);
    assert.deepEqual(Array.from(restored.DefaultUserSettings.liveTvSavedFilters), ['group:Kids']);
});

for (const host of ['Jellyfin/Go', 'Emby']) {
    test(host + ' saves only the pin changes made in this tab', async t => {
        const emby = host === 'Emby';
        const source = emby ? fs.readFileSync(__dirname + '/../../Emby/Emby.Plugins.Moonfin/Pages/moonfin.js', 'utf8') : page;
        const draft = { liveTvSavedFilters: ['group:News', 'group:Shared'] };
        const defaultsState = { active: 'global', profiles: { global: draft }, guidePinBase: draft.liveTvSavedFilters, guideGroups: {} };
        const form = { querySelector: () => ({ value: '' }), querySelectorAll: () => [] };
        let latest = ['group:Shared', 'group:Sports'], rejectSave = false, reads = 0;
        let displayed, editDuringSave;
        let visit = 1, delayedAck;
        const save = vm.runInNewContext([
            pageFunction(source, 'withGuidePinEdits'), emby ? pageFunction(source, 'camelKeysDeep') : '',
            pageFunction(source, emby ? 'saveConfig' : 'submit'), emby ? 'saveConfig' : 'submit',
        ].join('\n'), {
            defaultsState, document: form, DEFAULTS_PROFILE_NAMES: ['global'], loading: { show() {} },
            get guideGroupsVisit() { return visit; },
            Dashboard: { processPluginConfigurationUpdateResult() {} }, MoonfinConfig: {}, PluginUniqueId: '',
            getPosterBadgeDefaults() {}, getPosterBadgeRules() {}, collectActiveDefaults() {}, wholeNumberIn() {}, reportConfigError() {},
            renderDefaultGuideGroups(...args) { displayed = args.at(-1).liveTvSavedFilters; },
            ApiClient: {
                getPluginConfiguration: async () => {
                    const pins = !emby && ++reads % 2 === 1 ? ['group:Old'] : latest;
                    return { DefaultUserSettings: { [emby ? 'LiveTvSavedFilters' : 'liveTvSavedFilters']: pins } };
                },
                updatePluginConfiguration: async (_, config) => {
                    if (rejectSave) throw new Error('save failed');
                    latest = config.DefaultUserSettings.liveTvSavedFilters;
                    if (editDuringSave) defaultsState.profiles.global = { liveTvSavedFilters: editDuringSave };
                    if (delayedAck) await delayedAck;
                },
            },
        });
        await save(form);
        assert.deepEqual(Array.from(latest), ['group:Shared', 'group:Sports']);
        assert.deepEqual(Array.from(displayed), ['group:Shared', 'group:Sports']);
        defaultsState.profiles.global = { liveTvSavedFilters: ['group:Kids'] };
        rejectSave = true;
        await assert.rejects(save(form), /save failed/);
        rejectSave = false;
        latest = [...latest, 'group:Music']; // A different tab adds another pin before the retry.
        editDuringSave = ['group:Kids', 'group:Movies'];
        await save(form);
        assert.deepEqual(Array.from(latest), ['group:Music', 'group:Kids']);
        assert.deepEqual(Array.from(displayed), ['group:Music', 'group:Kids', 'group:Movies']);
        editDuringSave = null;
        await save(form);
        assert.deepEqual(Array.from(latest), ['group:Music', 'group:Kids', 'group:Movies']);
        latest = null; // Another admin clears the pins after this tab's successful save.
        await save(form);
        assert.equal(latest, null);

        await t.test('an earlier save reply cannot undo an unpin on a new visit', async () => {
            defaultsState.profiles.global = { liveTvSavedFilters: ['group:News'] };
            let acknowledge;
            delayedAck = new Promise(resolve => { acknowledge = resolve; });
            const saving = save(form);
            await new Promise(setImmediate);
            assert.deepEqual(Array.from(latest), ['group:News']);
            visit++;
            defaultsState.guideGroups = {};
            latest = ['group:Sports']; // The reopened page loaded another admin's change.
            defaultsState.profiles.global = { liveTvSavedFilters: latest };
            defaultsState.guidePinBase = latest;
            acknowledge();
            await saving;
            defaultsState.profiles.global = { liveTvSavedFilters: [] };
            await save(form);
            assert.deepEqual(Array.from(latest), []);
        });
        if (!emby) await t.test('overlapping saves keep the submitted unpin and later unsaved edits', async () => {
            defaultsState.profiles.global = { liveTvSavedFilters: ['group:News'] };
            let acknowledge;
            delayedAck = new Promise(resolve => { acknowledge = resolve; });
            const first = save(form);
            await new Promise(setImmediate);
            defaultsState.profiles.global = { liveTvSavedFilters: [] };
            const second = save(form);
            await new Promise(setImmediate);
            const beforeAcknowledgement = latest;
            latest = [...latest, 'group:Sports']; // Another administrator adds Sports.
            defaultsState.profiles.global = { liveTvSavedFilters: ['group:Kids'] }; // Still unsaved.
            acknowledge();
            await Promise.all([first, second]);
            assert.deepEqual(Array.from(beforeAcknowledgement), ['group:News']);
            assert.deepEqual(Array.from(latest), ['group:Sports']);
            assert.deepEqual(Array.from(displayed), ['group:Sports', 'group:Kids']);
        });
        if (!emby) await t.test('a lost save reply still allows the queued unpin', async () => {
            let fail;
            delayedAck = new Promise((_, reject) => { fail = reject; });
            const first = assert.rejects(save(form), /save failed/);
            await new Promise(setImmediate);
            delayedAck = null;
            defaultsState.profiles.global = { liveTvSavedFilters: ['group:Movies'] };
            const second = save(form);
            fail(new Error('save failed'));
            await Promise.all([first, second]);
            assert.deepEqual(Array.from(latest), ['group:Movies']);
        });
    });
}
