const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

class Element {
    constructor(tag = 'div') { this.tag = tag; this.children = []; this.listeners = {}; this.disabled = false; this.value = ''; this.textContent = ''; this.classList = { add() {}, remove() {} }; }
    replaceChildren(...children) { this.children = children; if (this.tag === 'select') this.value = children[0]?.value || ''; }
    append(...children) { this.children.push(...children); }
    add(child) { this.children.push(child); }
    addEventListener(event, fn) { this.listeners[event] = fn; }
    querySelector(tag) { return this.children.find(child => child.tag === tag); }
    get options() { return this.children; }
}
async function until(condition) {
    for (let i = 0; i < 1000; i++) { if (condition()) return; await new Promise(resolve => setImmediate(resolve)); }
    throw new Error('UI did not finish the expected state transition');
}
function environment(provider, search, artifactSize = 123) {
    const elements = {};
    const ids = ['application', 'source', ...(provider === 'AzureDevOps' ? ['project'] : []), 'repository', 'branch', 'definition', 'build'];
    for (const id of ids) elements['cp-' + id] = new Element('select');
    for (const id of ['cloud-pipeline-message', 'cp-status', 'cp-artifacts', 'cp-stages', 'cp-refresh']) elements[id] = new Element();
    elements['cloud-pipeline'] = { dataset: { provider, base: '/Pipeline/Cloud/' } };
    const requests = [];
    let fail = false, heldStatus;
    const context = {
        document: { getElementById: id => elements[id], createElement: tag => new Element(tag) },
        Option: function(name, id) { return { textContent: name, value: String(id) }; },
        location: { href: 'https://portal.test/Pipeline' + search, search },
        URL, URLSearchParams, AbortController, setTimeout, clearTimeout,
        window: { addEventListener() {} },
        fetch: async (url, options) => {
            assert.equal(options.credentials, 'same-origin'); assert.equal(options.cache, 'no-store');
            assert.ok(!options.method || options.method === 'GET');
            requests.push(new URL(url));
            if (options.signal.aborted) throw Object.assign(new Error('Aborted'), { name: 'AbortError' });
            const q = url.searchParams, op = q.get('operation'); let body;
            if (url.pathname.endsWith('/Applications')) body = [{ id: 'Portal', name: 'Portal' }];
            else if (op === 'sources') body = { items: [{ id: '1', name: 'Registered source' }], next: q.get('cursor') ? null : '2' };
            else if (op === 'projects') body = { items: [{ id: 'project', name: 'Project' }] };
            else if (op === 'repositories') body = { items: [{ id: 'app', name: '<untrusted repository>' }] };
            else if (op === 'branches') body = { items: [{ id: 'main', name: 'main' }, { id: 'Dev', name: 'Dev' }] };
            else if (op === 'definitions') body = { items: [{ id: '7', name: 'Build only' }] };
            else if (op === 'builds') body = { builds: [{ id: 42, number: '42', status: 'completed', result: 'success' }] };
            else if (op === 'status') {
                if (heldStatus) await heldStatus;
                body = { builds: [{ id: 42, number: '42', status: 'completed', result: 'success', branch: 'main', commit: 'abc' }] };
            } else if (op === 'artifacts') body = { artifacts: [{ name: 'deployment-portal-build', type: 'Workflow artifact', size: artifactSize }] };
            else throw new Error('Unexpected read: ' + url);
            return { ok: !fail, status: fail ? 403 : 200, headers: { get: () => 'application/json' }, json: async () => body };
        }
    };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/DeploymentPortal/wwwroot/js/pipeline-cloud.js'), 'utf8'), context);
    return { elements, requests, fail: () => { fail = true; }, hold: promise => { heldStatus = promise; } };
}
(async () => {
    for (const provider of ['GitHub', 'AzureDevOps']) {
        const ui = environment(provider, '?applicationKey=Portal&sourceId=1&project=project&repository=app&branch=main');
        const el = id => ui.elements['cp-' + id];
        await until(() => !el('refresh').disabled);
        assert.equal(el('definition').options[1].value, '7');
        assert.equal(el('repository').options[1].textContent, '<untrusted repository>');
        assert.ok(ui.requests.some(q => q.searchParams.get('cursor') === '2'));
        el('definition').value = '7'; el('definition').listeners.change();
        await until(() => !el('refresh').disabled);
        assert.equal(el('build').options[1].textContent, provider === 'GitHub' ? '#42 | success' : '#42 ? success');
        el('build').value = '42'; el('build').listeners.change();
        await until(() => !el('refresh').disabled);
        assert.match(el('status').textContent, /success/);
        assert.match(el('artifacts').children[0].textContent, /deployment-portal-build/);
        const separator = provider === 'GitHub' ? ' | ' : ' ? ';
        assert.equal(el('status').textContent, ['#42', 'success', 'main', 'Commit abc'].join(separator));
        assert.equal(el('artifacts').children[0].textContent, ['deployment-portal-build', 'Workflow artifact', '123 bytes'].join(separator));
        assert.equal(el('stages').children[3].querySelector('small').textContent, 'Not queried');
        el('refresh').listeners.click();
        await until(() => !el('refresh').disabled);
        assert.equal(el('build').value, '42');
        assert.equal(el('build').options[1].textContent, provider === 'GitHub' ? '#42 | success' : '#42 ? success');
        assert.equal(el('status').textContent, ['#42', 'success', 'main', 'Commit abc'].join(separator));
        // A late response for an old run cannot overwrite a new branch selection.
        let release; ui.hold(new Promise(resolve => { release = resolve; }));
        el('build').listeners.change();
        await until(() => ui.requests.filter(q => q.searchParams.get('operation') === 'status').length === 3);
        el('branch').value = 'Dev'; el('branch').listeners.change(); release();
        await until(() => !el('refresh').disabled);
        assert.equal(el('build').value, ''); assert.equal(el('definition').value, '');
        assert.match(el('status').textContent, /Select an existing run/);
        ui.fail(); el('refresh').listeners.click();
        await until(() => !el('refresh').disabled);
        assert.match(ui.elements['cloud-pipeline-message'].textContent, /Access denied/);
        assert.match(el('status').textContent, /Select an existing run/);
        assert.ok(ui.requests.every(q => !/Queue|Approve|Deploy|dispatch/i.test(q.pathname)));
        console.log('PASS:', provider, 'handoff, pagination, status, stale-response isolation, authorization errors and GET-only reads');
    }
    const withoutSize = environment('GitHub', '?applicationKey=Portal&sourceId=1&repository=app&branch=main', null);
    const getWithoutSize = id => withoutSize.elements['cp-' + id];
    await until(() => !getWithoutSize('refresh').disabled);
    getWithoutSize('definition').value = '7'; getWithoutSize('definition').listeners.change();
    await until(() => !getWithoutSize('refresh').disabled);
    getWithoutSize('build').value = '42'; getWithoutSize('build').listeners.change();
    await until(() => !getWithoutSize('refresh').disabled);
    assert.equal(getWithoutSize('artifacts').children[0].textContent, 'deployment-portal-build | Workflow artifact');
    console.log('PASS: GitHub status separators with known and unknown artifact sizes; Azure formatting preserved');
    let navigated, removedPreference;
    const select = new Element('select'); select.value = 'AzureDevOps'; select.dataset = { url: '/Pipeline' };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/DeploymentPortal/wwwroot/js/pipeline-provider.js'), 'utf8'), {
        document: { getElementById: id => id === 'pipeline-provider' ? select : null }, URL, URLSearchParams,
        sessionStorage: { removeItem: key => { removedPreference = key; } },
        location: { href: 'https://portal.test/Pipeline?provider=GitHub&sourceId=1&repository=old&branch=main', assign: url => { navigated = new URL(url); } }
    });
    select.listeners.change(); assert.equal(navigated.search, '?provider=AzureDevOps');
    assert.equal(removedPreference, 'pipeline-build');
    console.log('PASS: provider switching clears all previous selectors');
})().catch(error => { console.error(error); process.exitCode = 1; });
