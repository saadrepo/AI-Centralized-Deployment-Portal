const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');

// Exercise the actual browser script with a small DOM/fetch fixture; no TFS or network requests.
class Element {
    constructor(tag = 'div') {
        this.tag = tag; this.children = []; this.listeners = {}; this.attributes = {};
        this._text = ''; this._value = ''; this.disabled = false; this.hidden = false; this.checked = true;
        this.scrollTop = 0; this.scrollHeight = 100; this.className = '';
        this.classList = { toggle: () => {} };
    }
    get textContent() { return this._text + this.children.map(n => n.textContent || '').join(''); }
    set textContent(value) { this._text = String(value ?? ''); this.children = []; }
    get options() { return this.children; }
    get value() { return this.tag === 'select' ? (this.children.some(n => n.value === this._value) ? this._value : this.children[0]?.value || '') : this._value; }
    set value(value) { this._value = String(value); }
    get selectedOptions() { return this.children.filter(n => n.value === this.value); }
    append(...nodes) { this.children.push(...nodes); }
    add(node, index = this.children.length) { this.children.splice(index, 0, node); }
    replaceChildren(...nodes) { this._text = ''; this.children = [...nodes]; this._value = ''; }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    addEventListener(name, callback) { (this.listeners[name] ||= []).push(callback); }
    fire(name) { return (this.listeners[name] || []).map(callback => callback()); }
    querySelector(selector) {
        if (selector.includes('RequestVerificationToken')) return { value: 'test-antiforgery' };
        return this.children.find(n => selector.startsWith('.') ? n.className.includes(selector.slice(1)) : n.tag === selector);
    }
    querySelectorAll(selector) { return this.children.flatMap(n => [(selector.startsWith('.') ? n.className.includes(selector.slice(1)) : n.tag === selector) ? n : null, ...n.querySelectorAll(selector)]).filter(Boolean); }
}
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };
const tick = async () => { for (let n = 0; n < 5; n++) await new Promise(setImmediate); };
const event = { name: 'Checkout sources', state: 'completed', started: '2026-10-16T08:00:00Z', finished: '2026-10-16T08:00:01Z' };
const state = (id = 14, overrides = {}) => ({
    buildId: id, buildNumber: String(id), branch: 'refs/heads/Dev', buildActive: false, canCancel: false, canApprove: false,
    status: 'Approval Pending', phase: 'CD', releaseId: 5, approvalId: 21, approvalReason: 'Waiting for approver',
    timelineMessage: 'Approval pending. Deployment has not started.',
    stages: ['Source', 'Build', 'Publish Artifact', 'Approval', 'Deploy to IIS', 'Verify Health'].map((name, n) => ({ name, state: n < 3 ? 'completed' : 'pending', detail: n < 3 ? 'Completed' : 'Pending' })),
    events: [], logs: [], ciEvents: [event], ciLogs: [`Observed CI log for build ${id}`], notices: [], refreshedAt: '2026-10-16T08:01:00Z', ...overrides
});
function fixture(overrides = {}) {
    const elements = new Map(), requests = [], timers = new Map(); let timerId = 0; const confirmation = { accepted: true, prompts: [] };
    for (const name of ['pending-releases', 'pipeline', 'application', 'definition', 'build', 'environment', 'branch', 'run', 'run-label', 'queue-spinner', 'cancel', 'approve', 'refresh', 'message', 'action-reason', 'activity', 'stages', 'context', 'status', 'timeline', 'logs', 'autoscroll', 'updated', 'branches']) {
        const select = ['application', 'definition', 'build', 'environment', 'branch'].includes(name);
        elements.set(name === 'pipeline' ? name : `pipeline-${name}`, new Element(select ? 'select' : 'div'));
    }
    elements.get('pipeline').dataset = { base: '/Pipeline' };
    const handlers = {
        Applications: () => [{ id: 'Portal', name: 'Portal' }],
        Options: () => ({ definition: { id: '1', name: 'Portal CI' }, branches: [{ id: 'refs/heads/Dev', name: 'Dev' }], defaultBranch: 'refs/heads/Dev',
            environments: [{ definitionId: 7, id: 9, name: 'Portal-CD / SOURCE' }],
            builds: [14, 15].map(id => ({ id, name: String(id), branch: 'refs/heads/Dev', status: 'completed', result: 'succeeded', time: '2026-10-16T08:00:00Z' })), canQueue: true }),
        QueueReadiness: () => ({ ready: true, message: 'Verified' }),
        History: args => state(Number(args.get('buildId')), { phase: 'CI', events: [event], logs: ['CI log'] }),
        PendingReleases: () => ({ releases: [], explanation: 'Actual TFS statuses' }),
        Status: args => state(Number(args.get('buildId'))),
        ...overrides
    };
    const storage = new Map();
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/DeploymentPortal/wwwroot/js/pipeline.js'), 'utf8'), {
        document: { hidden: false, getElementById: id => elements.get(id), createElement: tag => new Element(tag), addEventListener: () => {} },
        window: { confirm: prompt => { confirmation.prompts.push(prompt); return confirmation.accepted; }, addEventListener: () => {} },
        sessionStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value) },
        Option: function (label, value) { const option = new Element('option'); option.textContent = label; option.value = value; return option; },
        fetch: async (url, options) => {
            const parsed = new URL(url, 'http://localhost'); const action = parsed.pathname.split('/').pop();
            const args = options.method === 'POST' ? options.body : parsed.searchParams;
            requests.push({ action, args, method: options.method });
            const body = await handlers[action](args);
            return { ok: true, json: async () => structuredClone(body) };
        },
        setTimeout: callback => { timers.set(++timerId, callback); return timerId; }, clearTimeout: id => timers.delete(id),
        performance, console: { info: () => {} }, URL, URLSearchParams, AbortController
    });
    return { get: name => elements.get(`pipeline-${name}`), handlers, requests, confirmation,
        poll: async () => { const callback = [...timers.values()].at(-1); timers.clear(); await callback(); await tick(); } };
}

test('initial history loads while readiness and full CD status are delayed', async () => {
    const readiness = deferred(), full = deferred();
    const f = fixture({ QueueReadiness: () => readiness.promise, Status: () => full.promise });
    await tick();
    assert.match(f.get('timeline').textContent, /Checkout sources/);
    assert.match(f.get('logs').textContent, /Observed CI log for build 14/);
    assert.ok(f.requests.some(r => r.action === 'Status'));
    readiness.resolve({ ready: true, message: 'Verified' }); full.resolve(state()); await tick();
    assert.match(f.get('timeline').textContent, /CI build history/);
    assert.match(f.get('timeline').textContent, /CD deployment progress/);
    assert.match(f.get('logs').textContent, /Observed CI log for build 14/);
});

test('queue feedback is immediate and double clicks produce one POST', async () => {
    const queued = deferred();
    const f = fixture({ Queue: () => queued.promise, Status: args => state(Number(args.get('buildId')), args.get('buildId') === '15' ? { phase: 'CI', status: 'CI running', buildActive: true } : {}) });
    await tick();
    assert.equal(f.get('run').disabled, false);
    f.get('run').fire('click'); f.get('run').fire('click');
    assert.equal(f.get('run-label').textContent, 'Queuing Build...');
    assert.equal(f.get('queue-spinner').hidden, false);
    assert.equal(f.get('run').disabled, true);
    assert.equal(f.requests.filter(r => r.action === 'Queue').length, 1);
    queued.resolve({ buildId: 15 }); await tick();
    assert.equal(f.get('run-label').textContent, 'Run CI Build');
    assert.equal(f.get('queue-spinner').hidden, true);
    assert.match(f.get('context').textContent, /15/);
    assert.equal(f.get('run').disabled, true);
});

test('late partial history cannot replace the newer full CD response', async () => {
    const oldHistory = deferred();
    const f = fixture({ History: () => oldHistory.promise, Status: () => state(14, { ciLogs: ['Newest CI log'] }) });
    await tick();
    oldHistory.resolve(state(14, { phase: 'CI', ciLogs: ['Older delayed history'] })); await tick();
    assert.match(f.get('logs').textContent, /Newest CI log/);
    assert.doesNotMatch(f.get('logs').textContent, /Older delayed history/);
    assert.match(f.get('timeline').textContent, /CD deployment progress/);
});

test('delayed responses for a previous selection cannot overwrite the current build', async () => {
    const oldStatus = deferred();
    const f = fixture({ Status: args => args.get('buildId') === '14' ? oldStatus.promise : state(15) });
    await tick(); f.get('build').value = '15'; f.get('build').fire('change'); await tick();
    oldStatus.resolve(state(14, { ciLogs: ['Stale build 14'] })); await tick();
    assert.match(f.get('context').textContent, /15/);
    assert.match(f.get('logs').textContent, /Observed CI log for build 15/);
    assert.doesNotMatch(f.get('logs').textContent, /Stale build 14/);
});

test('empty later history does not erase real CI history during approval wait', async () => {
    const f = fixture(); await tick();
    f.handlers.Status = () => state(14, { ciEvents: [], ciLogs: [] });
    await f.poll();
    assert.match(f.get('timeline').textContent, /Checkout sources/);
    assert.match(f.get('logs').textContent, /Observed CI log for build 14/);
    assert.match(f.get('message').textContent, /retained/);
});

test('manual metadata refresh retains the displayed history while waiting', async () => {
    const f = fixture(); await tick(); const metadata = deferred();
    f.handlers.Options = () => metadata.promise;
    f.get('refresh').fire('click');
    assert.match(f.get('timeline').textContent, /Checkout sources/);
    assert.match(f.get('logs').textContent, /Observed CI log for build 14/);
});

const pendingRow = { releaseId: 7, name: 'Release-7', buildId: 14, approvalId: 31, approvalStatus: 'pending', environmentStatus: 'notStarted', canReject: true };
test('reject confirms the exact release and submits only its approval once', async () => {
    const rejected = deferred();
    const f = fixture({ PendingReleases: () => ({ releases: [pendingRow] }), Reject: () => rejected.promise });
    await tick();
    const button = f.get('pending-releases').querySelectorAll('button')[0];
    button.fire('click'); button.fire('click');
    assert.match(f.confirmation.prompts[0], /Release ID 7, Build ID 14/);
    const posts = f.requests.filter(r => r.action === 'Reject');
    assert.equal(posts.length, 1);
    assert.equal(posts[0].args.get('releaseId'), '7');
    assert.equal(posts[0].args.get('approvalId'), '31');
    rejected.resolve({ message: 'Confirmed rejection' }); await tick();
});
test('declining rejection confirmation never sends a mutation', async () => {
    const f = fixture({ PendingReleases: () => ({ releases: [pendingRow] }) });
    await tick(); f.confirmation.accepted = false;
    f.get('pending-releases').querySelectorAll('button')[0].fire('click'); await tick();
    assert.equal(f.requests.filter(r => r.method === 'POST').length, 0);
});

test('confirmed rejection refreshes pending panel even while selected status is stalled', async () => {
    const stalled = deferred(); let statusCalls = 0, rejected = false;
    const f = fixture({
        PendingReleases: () => ({ releases: [{ ...pendingRow, approvalStatus: rejected ? 'rejected' : 'pending', canReject: !rejected }] }),
        Status: () => ++statusCalls === 1 ? state() : stalled.promise,
        Reject: () => { rejected = true; return { message: 'TFS confirmed rejection' }; }
    });
    await tick(); f.get('pending-releases').querySelectorAll('button')[0].fire('click'); await tick();
    assert.match(f.get('pending-releases').textContent, /Approval: rejected/);
    assert.equal(f.get('pending-releases').querySelectorAll('button').length, 0);
    stalled.resolve(state()); await tick();
});
test('decision failure reason survives automatic status refresh', async () => {
    const f = fixture({ PendingReleases: () => ({ releases: [pendingRow] }), Reject: () => { throw new Error('HTTP 409 revision changed'); } });
    await tick(); f.get('pending-releases').querySelectorAll('button')[0].fire('click'); await tick();
    assert.match(f.get('message').textContent, /HTTP 409 revision changed/);
});
