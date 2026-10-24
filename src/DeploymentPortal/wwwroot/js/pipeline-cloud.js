(() => {
    'use strict';
    const root = document.getElementById('cloud-pipeline');
    if (!root) return;
    const statusSeparator = root.dataset.provider === 'GitHub' ? ' | ' : ' ? ';
    const ids = ['application', 'source', ...(root.dataset.provider === 'AzureDevOps' ? ['project'] : []), 'repository', 'branch', 'definition', 'build'];
    const selects = Object.fromEntries(ids.map(id => [id, document.getElementById(`cp-${id}`)]));
    const message = document.getElementById('cloud-pipeline-message');
    const status = document.getElementById('cp-status'), artifacts = document.getElementById('cp-artifacts');
    const stages = document.getElementById('cp-stages'), refresh = document.getElementById('cp-refresh');
    const operations = { application: 'Applications', source: 'sources', project: 'projects', repository: 'repositories', branch: 'branches', definition: 'definitions', build: 'builds' };
    let version = 0, pending;
    const controlled = root.dataset.execution === 'controlled' && root.dataset.provider === 'GitHub';
    const runButton = document.getElementById('cp-run'), approveButton = document.getElementById('cp-approve');
    let readiness = null, operationId = null, mutating = false;
    function executionSelection() {
        return { applicationKey: selects.application.value, provider: root.dataset.provider, sourceId: selects.source.value,
            project: selects.project?.value || '', repository: selects.repository.value, branch: selects.branch.value,
            definitionId: selects.definition.value, runId: selects.build.value || '0' };
    }
    function executionButtons() {
        if (!controlled) return;
        runButton.disabled = mutating || !readiness?.canQueue;
        approveButton.disabled = mutating || !readiness?.canApprove;
        refresh.disabled = mutating;
    }
    async function executionApi(action, args, post = false, signal) {
        const url = new URL(root.dataset.base + action, location.href), body = new URLSearchParams(args);
        if (post) body.set('__RequestVerificationToken', root.querySelector('[name="__RequestVerificationToken"]').value);
        else url.search = body;
        const response = await fetch(url, { method: post ? 'POST' : 'GET', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal,
            headers: post ? { 'Content-Type': 'application/x-www-form-urlencoded' } : {}, body: post ? body : undefined });
        if (!response.headers.get('content-type')?.includes('application/json')) throw new Error('Cloud operation unconfirmed. Refresh Status before retrying.');
        const value = await response.json();
        if (!response.ok) throw new Error(value.error || 'Cloud operation unavailable. Refresh Status.');
        return value;
    }
    async function executionStatus(signal, turn) {
        if (!controlled || !selects.definition.value) return;
        const current = await executionApi('Readiness', executionSelection(), false, signal);
        if (signal.aborted || turn !== version) return;
        readiness = current;
        operationId ||= current.operationId || null;
        const panel = document.getElementById('cp-deployment-state');
        panel.textContent = current.reason || 'Cloud execution is disabled.';
        if (operationId) {
            const operation = await executionApi('Operation', { id: operationId }, false, signal);
            if (signal.aborted || turn !== version) return;
            panel.textContent = `Operation ${operation.operationId} | ${operation.state} | Run ${operation.runId || 'unconfirmed'} | Deployment ${operation.deploymentStatus || 'not queued'}${operation.deploymentMessage ? ' | ' + operation.deploymentMessage : ''}`;
            if (['Dispatching', 'Unconfirmed', 'RunAccepted'].includes(operation.state)) readiness.canQueue = false;
            if (operation.deploymentId) readiness.canApprove = false;
            if (operation.deploymentStatus) {
                stages.children[3].querySelector('small').textContent = 'Approved';
                stages.children[4].querySelector('small').textContent = operation.deploymentStatus;
            }
        }
        executionButtons();
    }
    async function executeCloud(approve) {
        if (mutating || !(approve ? readiness?.canApprove : readiness?.canQueue)) return;
        const selected = JSON.stringify(executionSelection());
        const ticket = approve ? readiness.prepareTicket : readiness.queueTicket;
        mutating = true; executionButtons();
        ids.forEach(id => { selects[id].disabled = true; });
        pending?.abort(); version++;
        try {
            if (approve) {
                const prepared = await executionApi('Prepare', { ticket }, true); operationId = prepared.operationId;
                if (!prepared.approvalToken) throw new Error(`Preparation state: ${prepared.state}. Refresh Status; no deployment has been approved.`);
                if (!window.confirm('Approve this exact cloud artifact and IIS target?\n' + JSON.stringify(prepared.evidence, null, 2))) {
                    message.textContent = 'Approval declined. No deployment queued.'; return;
                }
                if (selected !== JSON.stringify(executionSelection())) throw new Error('Selection changed. Review again.');
                const result = await executionApi('Approve', { token: prepared.approvalToken }, true); operationId = result.operationId;
                message.textContent = 'Approved deployment queued. Use Refresh Status to track the existing worker.';
            } else {
                const result = await executionApi('Queue', { ticket }, true); operationId = result.operationId;
                if (result.runId) { selects.build.add(new Option('Run ' + result.runId, String(result.runId))); selects.build.value = String(result.runId); }
                message.textContent = result.state === 'RunAccepted' ? 'Provider accepted the exact run. Refresh Status to track it.' : 'Build outcome unconfirmed. Refresh Status; do not queue again.';
            }
        } catch (error) { message.textContent = error.message; message.classList.add('error'); }
        finally {
            mutating = false; readiness = null;
            ids.forEach(id => { selects[id].disabled = selects[id].options.length <= 1; });
            executionButtons();
        }
    }
    if (controlled) {
        runButton.addEventListener('click', () => executeCloud(false));
        approveButton.addEventListener('click', () => executeCloud(true));
    }
    const node = (tag, value) => { const el = document.createElement(tag); el.textContent = value; return el; };
    function resetResults() {
        readiness = null; executionButtons();
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
            items.push(...(id === 'application' ? data : id === 'build' ? data.builds.map(b => ({ id: String(b.id), name: [`#${b.number}`, b.result || b.status].join(statusSeparator) })) : data.items));
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
        status.textContent = [`#${run.number}`, run.result || run.status, run.branch, `Commit ${run.commit}`].join(statusSeparator);
        artifacts.replaceChildren(...(values.length ? values.map(a => node('li', [a.name, a.type, ...(a.size == null ? [] : [`${a.size} bytes`])].join(statusSeparator))) : [node('li', 'No artifacts returned for this run.')]));
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
            await executionStatus(controller.signal, turn);
            if (turn === version) message.textContent = controlled ? 'Provider status loaded. Execution requires separate authorization and explicit confirmation.' : 'Read-only provider data loaded. Select resources or refresh status.';
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
        if (mutating) return;
        operationId = null;
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
