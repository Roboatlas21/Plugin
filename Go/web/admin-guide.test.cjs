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
        ApiClient: { getUrl: path => path, ajax: async () => ({ channels: [{ groups: await responses.shift() }] }) },
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
