(() => {
    'use strict';
    const root = document.getElementById('cloud-pipeline');
    if (!root) return;
    const ids = ['application', 'source', ...(root.dataset.provider === 'AzureDevOps' ? ['project'] : []), 'repository', 'branch', 'definition', 'build'];
    const selects = Object.fromEntries(ids.map(id => [id, document.getElementById(`cp-${id}`)]));
    const message = document.getElementById('cloud-pipeline-message');
    const status = document.getElementById('cp-status'), artifacts = document.getElementById('cp-artifacts');
    const stages = document.getElementById('cp-stages'), refresh = document.getElementById('cp-refresh');
    const operations = { application: 'Applications', source: 'sources', project: 'projects', repository: 'repositories', branch: 'branches', definition: 'definitions', build: 'builds' };
    let version = 0, pending;
    const node = (tag, value) => { const el = document.createElement(tag); el.textContent = value; return el; };
    function resetResults() {
        status.textContent = 'Select an existing run to read provider status.';
        artifacts.replaceChildren(node('li', 'No run selected.'));
        stages.replaceChildren();
        const names = [root.dataset.provider === 'GitHub' ? 'GitHub Source' : 'Azure DevOps Source', root.dataset.provider === 'GitHub' ? 'Actions Workflow' : 'Pipeline Build', 'Publish Artifact', 'Approval', 'Deployment'];
        names.forEach((name, index) => {
            const stage = node('div', ''); stage.className = 'pipeline-stage pending';
            stage.append(node('strong', name), node('small', index >= 3 ? 'Not queried' : 'Not selected')); stages.append(stage);
        });
    }
    function clearAfter(index) {
        ids.slice(index + 1).forEach(id => { selects[id].replaceChildren(new Option('Select ' + id, '')); selects[id].disabled = true; });
        resetResults();
    }
    async function api(operation, signal, cursor, buildId) {
        const url = new URL(root.dataset.base + (operation === 'Applications' ? 'Applications' : 'Browse'), location.href);
        if (operation !== 'Applications') {
            const query = { provider: root.dataset.provider, applicationKey: selects.application.value, operation };
            for (const [key, value] of Object.entries({ sourceId: selects.source.value, project: selects.project?.value, repository: selects.repository.value, branch: selects.branch.value, definitionId: selects.definition.value, buildId })) {
                if (value) query[key] = value;
            }
            if (cursor) query.cursor = cursor;
            url.search = new URLSearchParams(query);
        }
        const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal });
        if ([401, 403].includes(response.status)) throw new Error('Access denied. An approved Sources Management Windows account is required.');
        if (!response.headers.get('content-type')?.includes('application/json')) throw new Error('Provider response unavailable. Refresh status to retry.');
        const data = await response.json();
        if (!response.ok) throw new Error(data.error || 'Provider selection unavailable.');
        return data;
    }
    async function list(id, signal) {
        const items = []; let cursor = null; const seen = new Set();
        for (let page = 0; page < 100; page++) {
            const data = await api(operations[id], signal, cursor);
            items.push(...(id === 'application' ? data : id === 'build' ? data.builds.map(b => ({ id: String(b.id), name: `#${b.number} ? ${b.result || b.status}` })) : data.items));
            cursor = data.next;
            if (!cursor) return items;
            if (seen.has(cursor)) throw new Error('Provider returned repeated pagination. Refresh status to retry.');
            seen.add(cursor);
        }
        throw new Error('Provider selection exceeds the browsing limit. Narrow the registered source.');
    }
    async function readStatus(signal) {
        const id = selects.build.value;
        const run = (await api('status', signal, null, id)).builds[0];
        if (!run) throw new Error('The selected run is no longer available.');
        const values = []; let cursor = null; const seen = new Set();
        for (let page = 0; ; page++) {
            const data = await api('artifacts', signal, cursor, id); values.push(...data.artifacts);
            cursor = data.next;
            if (!cursor) break;
            if (page >= 99 || seen.has(cursor)) throw new Error('Artifact metadata exceeds the browsing limit.');
            seen.add(cursor);
        }
        if (signal.aborted) return;
        status.textContent = `#${run.number} ? ${run.result || run.status} ? ${run.branch} ? Commit ${run.commit}`;
        artifacts.replaceChildren(...(values.length ? values.map(a => node('li', `${a.name} ? ${a.type}${a.size == null ? '' : ` ? ${a.size} bytes`}`)) : [node('li', 'No artifacts returned for this run.')]));
        const state = ['success', 'succeeded'].includes(run.result) ? 'completed' : ['failure', 'failed', 'cancelled', 'canceled', 'timed_out', 'partiallySucceeded'].includes(run.result) ? 'failed' : run.status === 'completed' ? 'pending' : 'running';
        stages.children[0].className = 'pipeline-stage completed'; stages.children[0].querySelector('small').textContent = selects.branch.value;
        stages.children[1].className = `pipeline-stage ${state}`; stages.children[1].querySelector('small').textContent = run.result || run.status;
        const available = values.some(a => a.type !== 'Expired');
        stages.children[2].className = `pipeline-stage ${available ? 'completed' : 'pending'}`;
        stages.children[2].querySelector('small').textContent = available ? 'Available' : values.length ? 'Expired' : 'Not available';
    }
    async function loadFrom(index, saved = {}) {
        pending?.abort(); const controller = pending = new AbortController(), turn = ++version;
        const timeout = setTimeout(() => controller.abort(), 50000);
        clearAfter(index); refresh.disabled = true; message.classList.remove('error'); message.textContent = 'Loading provider selection...';
        try {
            for (let i = index + 1; i < ids.length; i++) {
                const id = ids[i], select = selects[id], items = await list(id, controller.signal);
                if (turn !== version) return;
                select.replaceChildren(new Option(items.length ? 'Select ' + id : 'No accessible ' + id + ' found', ''), ...items.map(item => new Option(item.name, item.id)));
                select.disabled = !items.length;
                if (!saved[id]) break;
                if (!items.some(item => String(item.id) === String(saved[id]))) {
                    // A run may have fallen outside the recent list: exact status validates its scope.
                    if (id !== 'build') throw new Error('Transferred or previous selection is no longer available. Select it again.');
                    select.add(new Option('Run ' + saved[id], saved[id])); select.disabled = false;
                }
                select.value = saved[id];
            }
            if (selects.build.value) await readStatus(controller.signal);
            if (turn === version) message.textContent = 'Read-only provider data loaded. Select resources or refresh status.';
        } catch (error) {
            if (turn === version) {
                resetResults(); message.textContent = error.name === 'AbortError' ? 'Provider request timed out. Refresh Status to retry.' : error.message;
                message.classList.add('error');
            }
        } finally {
            clearTimeout(timeout); if (turn === version) refresh.disabled = false;
        }
    }
    ids.forEach((id, index) => selects[id].addEventListener('change', () => {
        // Every upstream change discards all downstream selections and status.
        if (!selects[id].value) { pending?.abort(); version++; clearAfter(index); refresh.disabled = false; return; }
        loadFrom(index);
    }));
    refresh.addEventListener('click', () => loadFrom(-1, Object.fromEntries(ids.map(id => [id, selects[id].value]))));
    window.addEventListener('pagehide', () => pending?.abort());
    const params = new URLSearchParams(location.search);
    const transferred = { application: params.get('applicationKey'), source: params.get('sourceId'), project: params.get('project'), repository: params.get('repository'), branch: params.get('branch') };
    loadFrom(-1, transferred);
})();
