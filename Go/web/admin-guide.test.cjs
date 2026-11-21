'use strict';

const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');

const hostSource = readFileSync(join(__dirname, 'host.js'), 'utf8');
const pageSource = readFileSync(join(__dirname, 'configPage.html'), 'utf8');
const start = pageSource.indexOf('var availableDefaultGuideGroups = []');
const end = pageSource.indexOf('// Fills the User defaults form', start);
assert.ok(start >= 0 && end > start, 'admin guide picker must be present in the shared page');
const pickerSource = pageSource.slice(start, end);

function adminPicker(host, savedPins, replies) {
    const requests = [];
    let inputs = [];
    const element = {
        innerHTML: '',
        querySelectorAll: () => inputs,
    };
    const window = {
        moonbaseHost: { name: host },
        fetch: async (url, options) => {
            requests.push({ url, method: options.method });
            const result = replies.shift();
            return result instanceof Error ? { ok: false, status: 502 } :
                { ok: true, status: 200, json: async () => ({ availableGuideGroups: result }) };
        },
    };
    const context = vm.createContext({
        window,
        document: {
            addEventListener() {},
            querySelector: selector => selector === '#DefaultPinnedGuideGroups' ? element : null,
        },
        location: { origin: 'https://moonbase.test', pathname: '/admin' },
        URLSearchParams,
        setInterval: () => 1,
        defaultsState: { active: 'global', profiles: { global: { liveTvSavedFilters: savedPins } } },
        esc: value => String(value).replace(/&/g, '&amp;').replace(/"/g, '&quot;'),
    });
    vm.runInContext(hostSource, context, { filename: 'host.js' });
    vm.runInContext(pickerSource, context, { filename: 'configPage.html:guide-picker' });
    return {
        requests, element,
        select(values) {
            inputs = Object.entries(values).map(([key, checked]) => ({
                checked,
                getAttribute: () => key,
            }));
        },
        async refresh() {
            vm.runInContext('discoverDefaultGuideGroups()', context);
            await new Promise(resolve => setImmediate(resolve));
        },
    };
}

function isChecked(html, key) {
    const input = (html.match(/<input[^>]*>/g) || [])
        .find(tag => tag.includes('data-guide-filter="' + key + '"'));
    assert.ok(input, 'missing picker choice: ' + key);
    return input.includes(' checked');
}

test('Plex admin guide picker loads provider groups and preserves unsaved selections', async () => {
    const picker = adminPicker('Plex', ['category:premiere', 'group:Legacy'],
        [['Sports', 'News', 'Sports'], ['Sports', 'News']]);
    await picker.refresh();
    assert.deepEqual(picker.requests, [{
        url: 'https://moonbase.test/Moonfin/Defaults?includeLiveTvGroups=true', method: 'GET',
    }]);
    assert.equal(isChecked(picker.element.innerHTML, 'category:premiere'), true);
    assert.equal(isChecked(picker.element.innerHTML, 'group:Legacy'), true);
    assert.equal(isChecked(picker.element.innerHTML, 'group:Sports'), false);
    assert.doesNotMatch(picker.element.innerHTML, /data-guide-filter="category:sports"/);
    assert.equal((picker.element.innerHTML.match(/data-guide-filter="group:Sports"/g) || []).length, 1);

    picker.select({ 'category:premiere': false, 'group:Legacy': false, 'group:News': true });
    await picker.refresh();
    assert.equal(isChecked(picker.element.innerHTML, 'category:premiere'), false);
    assert.equal(isChecked(picker.element.innerHTML, 'group:Legacy'), false);
    assert.equal(isChecked(picker.element.innerHTML, 'group:News'), true);
});

test('provider failures show retry and Silo never requests Plex metadata', async () => {
    const plex = adminPicker('Plex', [], [new Error('provider unavailable')]);
    await plex.refresh();
    assert.match(plex.element.innerHTML, /Could not load groups\. Reload to retry\./);
    const silo = adminPicker('Silo', ['category:movies'], []);
    await silo.refresh();
    assert.equal(silo.requests.length, 0);
    assert.equal(isChecked(silo.element.innerHTML, 'category:movies'), true);
});
