(() => {
    'use strict';
    const root = document.getElementById('pipeline');
    if (!root) return;
    const get = name => document.getElementById(`pipeline-${name}`);
    const app = get('application'), definition = get('definition'), build = get('build'), environment = get('environment'), branch = get('branch');
    const base = root.dataset.base.replace(/\/?Index\/?$/i, '').replace(/\/$/, '');
    let generation = 0, pendingVersion = 0, refreshVersion = 0, abort = new AbortController(), timer, busy = false, mutation = false, failures = 0;
    let options, snapshot, actionUncertain = false, cancelRequestedId = null, cancelPendingReason = '', activeAction = null;
    let identityVersion = 0, decisionNotice = null;
    const ciHistory = new Map();
    const historyKey = id => `${app.value}:${id}`;
    const message = (text, error = false) => { get('message').textContent = text; get('message').classList.toggle('error', error); };
    const text = (tag, value, cls) => { const el = document.createElement(tag); el.textContent = value ?? ''; if (cls) el.className = cls; return el; };
    const date = value => value && !Number.isNaN(Date.parse(value)) ? new Date(value).toISOString().replace('T', ' ').replace('.000Z', ' UTC') : 'Time unavailable';
    const save = id => { try { sessionStorage.setItem('pipeline-build', JSON.stringify({ applicationKey: app.value, buildId: id })); } catch { /* storage can be disabled */ } };
    const pick = (select, items, placeholder) => {
        select.replaceChildren();
        if (placeholder) select.append(new Option(placeholder, ''));
        items.forEach(item => select.append(new Option(item.name, item.id)));
        select.disabled = !items.length;
    };
    function selection() {
        const [releaseDefinitionId, environmentId] = environment.value.split(':');
        return { applicationKey: app.value, buildId: build.value, releaseDefinitionId: releaseDefinitionId || '0', environmentId: environmentId || '0' };
    }
    async function request(action, args = {}, post = false, signal = abort.signal) {
        const data = new URLSearchParams(args);
        if (post) data.set('__RequestVerificationToken', root.querySelector('[name="__RequestVerificationToken"]').value);
        const started = performance.now();
        const response = await fetch(`${base}/${action}${post ? '' : `?${data}`}`, {
            method: post ? 'POST' : 'GET', credentials: 'same-origin', cache: 'no-store', signal,
            headers: post ? { 'Content-Type': 'application/x-www-form-urlencoded' } : {}, body: post ? data : undefined
        });
        const headersMs = performance.now() - started;
        let body;
        try { body = await response.json(); } catch { throw new Error('Server response unavailable. Check authentication and refresh status.'); }
        if (action === 'Queue' || action === 'History' || action === 'Status') {
            console.info('Pipeline timing', { action, headersMs: Math.round(headersMs), totalMs: Math.round(performance.now() - started),
                serverTiming: response.headers?.get?.('Server-Timing') || 'unavailable', httpStatus: response.status });
        }
        if (!response.ok) {
            const error = new Error(body.error || `Request failed (${response.status}).`);
            error.mayHaveQueued = body.mayHaveQueued;
            error.mayHaveReachedTfs = body.mayHaveReachedTfs;
            throw error;
        }
        return body;
    }
    function buttons() {
        get('run-label').textContent = activeAction === 'Queue' ? 'Queuing Build...' : 'Run CI Build';
        get('queue-spinner').hidden = activeAction !== 'Queue';
        get('run').setAttribute('aria-busy', activeAction === 'Queue' ? 'true' : 'false');
        const selectedActive = snapshot?.buildId === Number(build.value) ? snapshot.buildActive
            : options?.builds?.some(b => b.id === Number(build.value) && ['notStarted', 'queued', 'postponed', 'inProgress', 'cancelling'].includes(b.status));
        get('run').disabled = mutation || busy || actionUncertain || selectedActive || (!!build.value && !snapshot) || !options?.canQueue || !branch.value;
        get('cancel').hidden = !selectedActive;
        get('cancel').disabled = mutation || busy || !snapshot?.canCancel || cancelRequestedId === Number(build.value);
        get('cancel').title = snapshot?.cancelReason || 'Waiting for current build state and Stop builds permission.';
        get('approve').disabled = mutation || busy || actionUncertain || !snapshot?.canApprove;
        get('refresh').disabled = mutation || busy;
        [app, build, branch, environment].forEach(el => { if (mutation) el.disabled = true; });
        get('action-reason').textContent = activeAction === 'Queue' ? 'Queuing Build... Waiting for TFS to return the real build ID.' : actionUncertain ? 'Action outcome is unconfirmed. Use Refresh Status to reconcile before retrying.'
            : [selectedActive ? `Selected CI build is active. Run CI Build is disabled. ${cancelRequestedId === Number(build.value) ? cancelPendingReason : snapshot?.cancelReason || 'Waiting for Stop builds permission check.'}` : options?.executionReason, snapshot?.phase === 'CD' || snapshot?.approvalId ? `Approval: ${snapshot.approvalReason}` : ''].filter(Boolean).join('\n');
    }
    function activity(builds) {
        get('activity').replaceChildren();
        builds.forEach(b => {
            const state = b.result === 'succeeded' ? 'completed' : ['failed', 'canceled', 'partiallySucceeded'].includes(b.result) ? 'failed' : b.status === 'inProgress' ? 'running' : 'pending';
            const row = text('div', '', `pipeline-activity-item ${state}`);
            row.append(text('strong', `${b.name} · ${b.result || b.status || 'Unknown'}`), text('small', b.branch), text('small', date(b.time)));
            get('activity').append(row);
        });
        if (!builds.length) get('activity').textContent = 'No recent builds returned by TFS.';
    }
    function render(state) {
        const key = historyKey(state.buildId), previous = ciHistory.get(key);
        const events = state.ciEvents?.length ? state.ciEvents : state.phase === 'CI' && state.events?.length ? state.events : previous?.events || [];
        const logs = state.ciLogs?.length ? state.ciLogs : state.phase === 'CI' && state.logs?.length ? state.logs : previous?.logs || [];
        const retained = !!previous && ((!state.ciEvents?.length && previous.events.length) || (!state.ciLogs?.length && previous.logs.length));
        ciHistory.set(key, { events, logs });
        if (ciHistory.size > 4) ciHistory.delete(ciHistory.keys().next().value);
        state = { ...state, ciEvents: events, ciLogs: logs };
        snapshot = state;
        if (!state.buildActive && cancelRequestedId === state.buildId) cancelRequestedId = null;
        const selected = [...build.options].find(option => option.value === String(state.buildId));
        if (selected) { selected.textContent = `#${state.buildNumber}`; selected.title = state.status; }
        if (options?.builds) {
            const entry = options.builds.find(b => b.id === state.buildId);
            if (entry) { entry.name = state.buildNumber; entry.status = state.status; activity(options.builds); }
        }
        get('stages').replaceChildren();
        state.stages.forEach((stage, i) => {
            const row = text('div', '', `pipeline-stage ${stage.state}`);
            row.append(text('span', ({ completed: '✓', running: '◌', failed: '×', pending: '○' })[stage.state] || '○', 'stage-icon'),
                text('strong', `${i + 1}. ${stage.name}`), text('small', stage.detail === 'Cancelled' || stage.detail === 'Cancellation requested' || stage.detail.startsWith('Waiting for') ? stage.detail : stage.state === 'completed' ? 'Completed' : stage.state === 'running' ? 'In progress' : stage.state === 'failed' ? 'Failed' : stage.name === 'Approval' && state.approvalId ? 'Approval Pending' : 'Pending'));
            row.title = stage.detail;
            if (i === 5 && state.healthUrl) {
                try {
                    const url = new URL(state.healthUrl);
                    if (url.protocol === 'https:' && !url.username && !url.password) {
                        const link = text('a', 'Open health endpoint'); link.href = url.href; link.target = '_blank'; link.rel = 'noopener noreferrer'; row.append(link);
                    }
                } catch { /* only valid server-configured URLs are displayed */ }
            }
            get('stages').append(row);
        });
        const environmentName = environment.selectedOptions[0]?.textContent?.split(' / ').pop();
        get('context').textContent = [state.phase || 'CI', `Build #${state.buildNumber}`, state.phase === 'CD' && environment.value ? environmentName : null, state.branch?.replace(/^refs\/heads\//, '')].filter(Boolean).join('  |  ');
        get('context').title = `${state.status}${state.releaseId ? ` · Release ID ${state.releaseId}` : ''}`;
        get('status').textContent = state.status;
        get('status').className = `pipeline-overall ${state.buildActive || state.stages[4]?.state === 'running' ? 'running' : state.status === 'Cancelled' || state.stages.some(s => s.state === 'failed') ? 'failed' : state.status === 'Completed' ? 'completed' : 'pending'}`;
        get('timeline').replaceChildren();
        const sections = [{ name: 'CI build history', events: state.ciEvents, waiting: 'Waiting for CI task status.' }];
        if (state.phase === 'CD') sections.push({ name: 'CD deployment progress', events: state.events, waiting: state.timelineMessage || 'Waiting for CD task status.' });
        sections.forEach(section => {
            get('timeline').append(text('h3', section.name, 'pipeline-history-title'));
            if (!section.events.length) get('timeline').append(text('p', section.waiting, 'pipeline-history-wait'));
            // Backend orders real parent/job/task sequence; missing timestamps must not move tasks to the front.
            section.events.forEach(event => {
            const row = text('div', '', `pipeline-event ${event.state}`);
            const detail = text('div', '');
            const duration = event.started && event.finished ? Math.round((Date.parse(event.finished) - Date.parse(event.started)) / 1000) : null;
            detail.append(text('strong', event.name), text('small', `${event.state}${duration !== null && Number.isFinite(duration) && duration >= 0 ? ` · ${duration}s` : ''}`));
            const time = text('time', event.started ? date(event.started).slice(11, 19) : '—');
            time.title = event.started ? date(event.started) : 'Timestamp unavailable';
            row.append(time, text('span', '', 'event-dot'), detail);
            get('timeline').append(row);
            });
        });
        const logPanel = get('logs'), scroll = logPanel.scrollTop;
        logPanel.replaceChildren();
        const logText = ['--- CI build history ---', state.ciLogs.length ? state.ciLogs.join('\n\n') : 'Waiting for CI logs.'];
        if (state.phase === 'CD') logText.push('--- CD deployment logs ---', state.logs.length ? state.logs.join('\n\n') : state.timelineMessage || 'Waiting for CD logs.');
        logText.join('\n\n').split('\n').forEach(line => {
            const cls = /\b(failed|error|exception)\b/i.test(line) ? 'log-error' : /\b(PASS|succeeded|successfully)\b/i.test(line) ? 'log-success' : line.startsWith('---') ? 'log-heading' : '';
            logPanel.append(text('span', line + '\n', cls));
        });
        logPanel.scrollTop = get('autoscroll').checked ? logPanel.scrollHeight : scroll;
        get('updated').textContent = `Updated ${date(state.refreshedAt)}`;
        message([...state.notices, decisionNotice?.text, retained ? 'Previously received CI history is retained while waiting for the latest TFS history.' : ''].filter(Boolean).join('\n'), decisionNotice?.error === true);
        buttons();
    }
    function schedule() {
        clearTimeout(timer);
        if (!document.hidden && app.value) {
            const terminal = snapshot && ['Completed', 'Cancelled', 'Health Failed', 'Deployment succeeded; health unverified', 'failed', 'canceled', 'rejected'].includes(snapshot.status);
            timer = setTimeout(refresh, Math.min(60000, failures ? 5000 * 2 ** Math.min(failures, 4) : terminal ? 30000 : 5000));
        }
    }
    function clearSelection(preserve = false) {
        clearTimeout(timer); abort.abort(); abort = new AbortController(); generation++; busy = false; failures = 0; actionUncertain = false;
        if (preserve) { buttons(); return; }
        snapshot = null; options = null; decisionNotice = null;
        [definition, build, environment, branch].forEach(el => pick(el, [], 'Loading…'));
        get('logs').textContent = 'Loading selected application…';
        get('timeline').textContent = 'Loading selected application…';
        get('context').textContent = 'Loading…'; get('updated').textContent = '';
        get('stages').querySelectorAll('.pipeline-stage').forEach(el => { el.className = 'pipeline-stage pending'; el.querySelector('small').textContent = 'Pending'; el.querySelector('.stage-icon').textContent = '○'; });
        get('branches').textContent = 'Loading…'; get('activity').textContent = 'Loading…';
        buttons();
    }
    async function load(keepBuild, reset = false) {
        const currentBuild = reset ? null : keepBuild || build.value;
        const currentEnvironment = reset ? null : environment.value;
        const currentBranch = reset ? null : branch.value;
        clearSelection(!reset && snapshot?.buildId === Number(currentBuild));
        const turn = generation; busy = true; buttons();
        try {
            const data = await request('Options', { applicationKey: app.value });
            if (turn !== generation) return;
            options = data;
            const checkQueue = data.canQueue;
            data.canQueue = false;
            pick(definition, [data.definition]); definition.disabled = true;
            pick(branch, data.branches, data.branches.length ? null : 'No branches available');
            if (data.branches.some(b => b.id === data.defaultBranch)) branch.value = data.defaultBranch;
            if (data.branches.some(b => b.id === currentBranch)) branch.value = currentBranch;
            pick(environment, data.environments.map(e => ({ id: `${e.definitionId}:${e.id}`, name: e.name })), data.environments.length ? null : 'CD not configured / unavailable');
            if ([...environment.options].some(o => o.value === currentEnvironment)) environment.value = currentEnvironment;
            pick(build, data.builds.map(b => ({ id: b.id, name: `${b.name} · ${b.result || b.status}` })), data.builds.length ? null : 'No builds available');
            if (Number(currentBuild) > 0 && ![...build.options].some(o => o.value === String(currentBuild))) { build.add(new Option(`Build ID ${currentBuild}`, currentBuild), 0); build.disabled = false; }
            if ([...build.options].some(o => o.value === String(currentBuild))) build.value = currentBuild;
            get('branches').replaceChildren();
            data.branches.forEach(b => { const row = text('div', `⑂ ${b.name}`, 'pipeline-branch'); if (b.id === data.defaultBranch) row.append(text('span', 'Default')); get('branches').append(row); });
            if (!data.branches.length) get('branches').textContent = 'No branches returned by TFS.';
            activity(data.builds);
            message(data.releaseWarning || '');
            if (checkQueue) {
                // Permission readiness must not hold existing build history behind a slow authentication call.
                request('QueueReadiness', { applicationKey: app.value }).then(readiness => {
                    if (turn !== generation || options !== data) return;
                    data.canQueue = readiness.ready === true; data.executionReason = readiness.message; buttons();
                }).catch(error => {
                    if (error.name === 'AbortError' || turn !== generation || options !== data) return;
                    data.canQueue = false; data.executionReason = error.message; buttons();
                });
            }
        } catch (error) { if (error.name !== 'AbortError' && turn === generation) { failures++; if (options) options.canQueue = false; message(error.message, true); } }
        finally { if (turn === generation) { busy = false; buttons(); } }
        if (turn === generation) { if (options) await refresh(); else schedule(); }
    }
    async function refresh(skipPending = false) {
        if (busy || mutation || !app.value) return;
        personalIdentity();
        if (!options) { await load(); return; }
        if (!build.value) { schedule(); return; }
        const turn = generation, version = ++refreshVersion, args = selection(), scope = JSON.stringify(args);
        let settled = false, fullSucceeded = false;
        busy = true; buttons();
        if (!snapshot || !snapshot.ciEvents?.length || !snapshot.ciLogs?.length) {
            request('History', { applicationKey: args.applicationKey, buildId: args.buildId }).then(history => {
                if ((!settled || (!fullSucceeded && !snapshot)) && version === refreshVersion && turn === generation && !mutation && scope === JSON.stringify(selection())) render(history);
            }).catch(() => { /* Full status owns error handling; it also returns CI history. */ });
        }
        try {
            const state = await request('Status', args);
            if (turn !== generation || version !== refreshVersion || mutation || scope !== JSON.stringify(selection())) return;
            settled = true; fullSucceeded = true;
            failures = 0; render(state); if (!skipPending) pendingReleases();
        } catch (error) { if (error.name !== 'AbortError' && turn === generation) { failures++; snapshot = null; message(error.message, true); } }
        finally { settled = true; if (turn === generation) { busy = false; buttons(); schedule(); } }
    }
    async function pendingReleases() {
        const panel = get('pending-releases');
        if (!panel) return;
        const args = selection(), turn = generation, version = ++pendingVersion, scope = JSON.stringify(args);
        if (args.releaseDefinitionId === '0') { panel.textContent = 'CD environment not configured.'; return; }
        try {
            const data = await request('PendingReleases', args);
            if (turn !== generation || version !== pendingVersion || scope !== JSON.stringify(selection()) || mutation) return;
            panel.replaceChildren();
            panel.append(text('p', [data.explanation, data.executionReason, data.unresolvedReleaseIds?.length ? `Unresolved release IDs: ${data.unresolvedReleaseIds.join(', ')}` : '',
                data.concurrencyCount ? `TFS concurrency: ${data.concurrencyCount}; queue depth: ${data.queueDepthCount || 'unavailable'}` : 'TFS queue policy unavailable.'].filter(Boolean).join(' ')));
            data.releases.forEach(row => {
                const item = text('div', '', 'pipeline-branch');
                item.append(text('span', `${row.name} (ID ${row.releaseId}) | Build ID ${row.buildId} | Approval: ${row.approvalStatus} | Environment: ${row.environmentStatus}${row.operationStatus ? ` | Operation: ${row.operationStatus}` : ''}${row.deploymentQueueId ? ` | Queue ID: ${row.deploymentQueueId}` : ''}`));
                if (row.approvalStatus === 'pending') {
                    for (const action of ['Reject', 'Approve']) {
                        const approving = action === 'Approve';
                        const decisionButton = text('button', approving ? 'Approve & Deploy' : 'Reject', approving ? 'btn btn-primary' : 'btn btn-outline-danger');
                        decisionButton.disabled = approving ? !row.canApprove : !row.canReject;
                        decisionButton.title = (approving ? row.approveReason : row.rejectReason) || data.executionReason || 'Waiting for TFS eligibility.';
                        decisionButton.addEventListener('click', async () => {
                            if (decisionButton.disabled || mutation || busy || turn !== generation || scope !== JSON.stringify(selection())) return;
                            if (!window.confirm(`${approving ? 'Approve & Deploy' : 'Reject'} approval ${row.approvalId} for ${row.name} (Release ID ${row.releaseId}, Build ID ${row.buildId}, Environment ID ${args.environmentId})? ${approving ? 'Deployment follows the configured TFS process.' : 'This prevents this approval from deploying.'}`)) return;
                            decisionNotice = null; message(approving ? 'Submitting approval to TFS...' : 'Submitting rejection to TFS...');
                            mutation = true; clearTimeout(timer); generation++; abort.abort(); abort = new AbortController();
                            panel.querySelectorAll('button').forEach(button => { button.disabled = true; }); buttons();
                            try {
                                const result = await request(action, { ...args, buildId: row.buildId, releaseId: row.releaseId, approvalId: row.approvalId }, true);
                                decisionNotice = { text: result.message, error: false }; message(result.message);
                            } catch (error) { decisionNotice = { text: error.message, error: true }; message(error.message, true); }
                            finally {
                                mutation = false;
                                app.disabled = false; build.disabled = !build.value; branch.disabled = !options?.branches.length; environment.disabled = !environment.value;
                                buttons(); await Promise.allSettled([pendingReleases(), refresh(true)]);
                            }
                        });
                        item.append(decisionButton);
                    }
                }
                panel.append(item);
            });
            if (!data.releases.length) panel.append(text('p', 'No matching release approval records returned by TFS.'));
        } catch (error) {
            if (turn === generation && error.name !== 'AbortError') panel.textContent = `Pending releases unavailable: ${error.message}`;
        }
    }
    async function mutate(action) {
        if (mutation || busy || actionUncertain) return;
        if (action === 'Cancel') {
            if (!snapshot?.canCancel || snapshot.buildId !== Number(build.value)) return;
            if (!window.confirm(`Cancel CI build #${snapshot.buildNumber} (ID ${snapshot.buildId})? This stops only this CI build.`)) return;
        }
        if (action === 'Approve') {
            if (!snapshot?.canApprove || snapshot.buildId !== Number(build.value)) return;
            if (!window.confirm(`Approve & Deploy approval ${snapshot.approvalId} for Release ID ${snapshot.releaseId}, Build ID ${snapshot.buildId}, Environment ID ${environment.value}? Deployment follows the configured TFS process.`)) return;
        }
        decisionNotice = null;
        mutation = true; activeAction = action;
        clearTimeout(timer); abort.abort(); abort = new AbortController(); generation++;
        buttons();
        if (action === 'Queue') message('Queuing Build... Waiting for TFS to return the real build ID.');
        const args = action === 'Queue' ? { applicationKey: app.value, branch: branch.value, selectedBuildId: build.value || '0' }
            : action === 'Cancel' ? { applicationKey: app.value, buildId: snapshot.buildId }
            : { ...selection(), releaseId: snapshot.releaseId, approvalId: snapshot.approvalId };
        let id, succeeded = false;
        try {
            const data = await request(action, args, true);
            succeeded = true;
            id = action === 'Queue' ? data.buildId : null;
            if (action === 'Cancel') { cancelRequestedId = Number(build.value); cancelPendingReason = data.message; }
            if (id) {
                save(id);
                if (![...build.options].some(o => o.value === String(id))) build.add(new Option(`Build ID ${id}`, id), 0);
                build.value = String(id);
                snapshot = { buildId: id, buildActive: true, canCancel: false };
                get('timeline').textContent = `Loading CI history for build ID ${id}...`;
                get('logs').textContent = `Loading TFS logs for build ID ${id}...`;
                get('context').textContent = `Build ID ${id}`;
                get('status').textContent = 'Waiting for TFS status';
                get('status').className = 'pipeline-overall pending';
                get('stages').querySelectorAll('.pipeline-stage').forEach(el => { el.className = 'pipeline-stage pending'; el.querySelector('small').textContent = 'Waiting for status'; el.querySelector('.stage-icon').textContent = '○'; });
            }
            message(data.message || `TFS queued build ID ${id}.`);
        } catch (error) {
            if (action === 'Cancel') { cancelRequestedId = error.mayHaveReachedTfs === false ? null : Number(build.value); cancelPendingReason = error.message; }
            else actionUncertain = error.mayHaveQueued !== false;
            if (action === 'Queue' && !actionUncertain && options) { options.canQueue = false; options.executionReason = error.message; }
            if (action === 'Approve') decisionNotice = { text: error.message, error: true };
            message(error.message, true);
        }
        finally {
            mutation = false; activeAction = null;
            app.disabled = false; build.disabled = !build.options.length || !build.value;
            branch.disabled = !options?.branches.length; environment.disabled = !environment.value;
            buttons();
        }
        if (succeeded || action === 'Cancel') await refresh();
        else schedule();
    }
    app.addEventListener('change', () => load(undefined, true));
    for (const select of [build, environment]) select.addEventListener('change', () => {
        clearTimeout(timer); abort.abort(); abort = new AbortController(); generation++; busy = false; snapshot = null; decisionNotice = null; buttons(); refresh();
        get('timeline').textContent = 'Loading selected build history...'; get('logs').textContent = 'Loading selected build logs...';
        if (build.value) save(build.value);
    });
    branch.addEventListener('change', buttons);
    get('run').addEventListener('click', () => mutate('Queue'));
    get('cancel').addEventListener('click', () => mutate('Cancel'));
    get('approve').addEventListener('click', () => mutate('Approve'));
    get('refresh').addEventListener('click', async () => {
        actionUncertain = false;
        if (app.value) await load(build.value); else await initialize();
    });
    document.addEventListener('visibilitychange', () => { if (document.hidden) clearTimeout(timer); else refresh(); });
    window.addEventListener('pagehide', () => { clearTimeout(timer); abort.abort(); });
    async function personalIdentity() {
        const panel = get('personal-auth');
        if (!panel) return;
        const turn = ++identityVersion;
        try {
            const state = await request('Identity');
            if (turn !== identityVersion) return;
            panel.hidden = !state.enabled;
            get('personal-status').textContent = state.connected ? `Personal TFS identity connected; expires ${date(state.expires)}.` : state.message;
            get('personal-connect').disabled = !state.secure || state.connected;
            get('personal-disconnect').disabled = !state.connected;
        } catch { if (turn === identityVersion) panel.hidden = true; }
    }
    if (get('personal-form')) {
        get('personal-form').addEventListener('submit', async event => {
            event.preventDefault();
            if (mutation || busy || get('personal-connect').disabled) return;
            get('personal-connect').disabled = true;
            try {
                const personalToken = get('personal-token').value;
                get('personal-token').value = '';
                const result = await request('Identity/Connect', { personalAccessToken: personalToken }, true);
                get('personal-status').textContent = result.message;
                await personalIdentity(); await refresh();
            } catch (error) { get('personal-status').textContent = error.message; get('personal-connect').disabled = false; }
            finally { get('personal-token').value = ''; }
        });
        get('personal-disconnect').addEventListener('click', async () => {
            if (mutation || busy) return;
            get('personal-disconnect').disabled = true;
            try { await request('Identity/Disconnect', {}, true); await personalIdentity(); await refresh(); }
            catch (error) { get('personal-status').textContent = error.message; }
        });
    }
    async function initialize() {
        try {
            const applications = await request('Applications');
            pick(app, applications, applications.length ? null : 'No configured applications');
            if (!applications.length) { message('No applications have a configured TFS CI definition.'); return; }
            let saved;
            try { saved = JSON.parse(sessionStorage.getItem('pipeline-build')); } catch { /* optional preference */ }
            if (saved && applications.some(a => a.id === saved.applicationKey)) app.value = saved.applicationKey;
            await load(saved?.applicationKey === app.value ? saved.buildId : undefined);
        } catch (error) { message(error.message, true); }
    }
    personalIdentity();
    initialize();
})();
