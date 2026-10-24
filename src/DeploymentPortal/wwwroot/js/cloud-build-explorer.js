(() => {
    'use strict';
    const root = document.getElementById('cloud-build-explorer');
    if (!root) return;
    const source = document.getElementById('cloud-source');
    const project = document.getElementById('cloud-project');
    const repository = document.getElementById('cloud-repository');
    const branch = document.getElementById('cloud-branch');
    const load = document.getElementById('cloud-load');
    const refresh = document.getElementById('cloud-refresh');
    const retry = document.getElementById('cloud-retry');
    const status = document.getElementById('cloud-status');
    const error = document.getElementById('cloud-error');
    const rows = document.getElementById('cloud-builds');
    const artifactPanel = document.getElementById('cloud-artifacts');
    const artifacts = document.getElementById('cloud-artifact-list');
    const cursors = {};
    let busy = false;
    let version = 0;
    let pending;
    let selectedBuild = null;
    const more = operation => document.getElementById(`more-${operation}`);

    function controls() {
        [source, project, repository, branch].filter(Boolean).forEach(select => { select.disabled = busy || select.options.length < 2; });
        load.disabled = refresh.disabled = busy || !source.value || (project && !project.value) || !repository.value || !branch.value;
        retry.disabled = busy;
        root.querySelectorAll('button[id^="more-"]').forEach(button => { button.disabled = busy; });
        root.querySelectorAll('[data-artifact-run]').forEach(link => { link.setAttribute('aria-disabled', String(busy)); });
        root.setAttribute('aria-busy', String(busy));
    }
    function reset(select, text, operation) {
        if (select) select.replaceChildren(new Option(text, ''));
        if (operation) { cursors[operation] = null; const button = more(operation); if (button) button.hidden = true; }
    }
    function pipelineLink() {
        const link = document.getElementById('cloud-pipeline-link');
        link.hidden = !source.value || !repository.value || !branch.value || (project && !project.value);
        if (link.hidden) { link.removeAttribute('href'); return; }
        const url = new URL(root.dataset.pipeline, location.href);
        url.search = new URLSearchParams({ provider: root.dataset.provider, applicationKey: root.dataset.application, sourceId: source.value, project: project?.value || '', repository: repository.value, branch: branch.value });
        link.href = url.href;
    }
    function clearResults() {
        pipelineLink();
        rows.replaceChildren(); artifacts.replaceChildren(); artifactPanel.hidden = true; selectedBuild = null;
        ['builds', 'artifacts'].forEach(op => { cursors[op] = null; more(op).hidden = true; });
    }
    function pagination(operation, next) {
        cursors[operation] = next || null;
        const button = more(operation); if (button) button.hidden = !next;
    }
    async function api(operation, cursor, buildId) {
        const url = new URL(root.dataset.url, window.location.href);
        // Send only selectors needed for this stage. Source discovery needs no prior selection.
        const params = { provider: root.dataset.provider, operation };
        if (operation !== 'sources') {
            params.sourceId = source.value;
            if (project?.value) params.project = project.value;
            if (['branches', 'builds', 'artifacts'].includes(operation)) params.repository = repository.value;
            if (['builds', 'artifacts'].includes(operation)) params.branch = branch.value;
        }
        if (cursor) params.cursor = cursor;
        if (buildId) params.buildId = String(buildId);
        url.search = new URLSearchParams(params);
        const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: pending.signal });
        if (response.status === 401 || response.status === 403) throw new Error('Access denied. Sign in with an approved Source Management account.');
        if (!response.headers.get('content-type')?.includes('application/json')) throw new Error('Explorer unavailable. Retry or contact the portal administrator.');
        const result = await response.json();
        if (!response.ok) throw new Error(result.error || 'Source API unavailable. Verify permissions and retry.');
        return result;
    }
    async function run(message, work) {
        pending?.abort();
        const request = pending = new AbortController();
        const current = ++version;
        const timeout = setTimeout(() => request.abort(), 50000);
        busy = true; error.hidden = true; status.textContent = message; controls();
        try { await work(() => current === version); }
        catch (failure) {
            if (current === version) {
                error.textContent = failure.name === 'AbortError' ? 'Explorer request timed out. Retry when the provider is available.' : failure.message || 'Explorer unavailable. Retry.';
                error.hidden = false; status.textContent = '';
            }
        } finally {
            clearTimeout(timeout);
            if (current === version) { busy = false; controls(); }
        }
    }
    function list(operation, select, prompt, append = false) {
        return run(`Loading ${operation}…`, async active => {
            const result = await api(operation, append ? cursors[operation] : null);
            if (!active()) return;
            if (!append) select.replaceChildren(new Option(prompt, ''));
            for (const item of result.items) {
                if (![...select.options].some(option => option.value === item.id))
                    select.add(new Option(item.name + (item.private ? ' (private)' : ''), item.id));
            }
            pagination(operation, result.next);
            if (operation === 'sources') {
                const providerName = root.dataset.provider === 'GitHub' ? 'GitHub' : 'Azure DevOps';
                if (result.items.length === 0) {
                    select.replaceChildren(new Option(`No ${providerName} source configured`, ''));
                    status.textContent = `No ${providerName} source configured. Add or enable a source in Sources Management on this portal host.`;
                } else {
                    status.textContent = `Select a ${providerName} source to load ${project ? 'projects' : 'repositories'}.`;
                }
                return;
            }
            status.textContent = result.items.length ? `Select from the loaded ${operation}.` :
                (result.next ? `No matching ${operation} on this page. Load more to continue.` : `No accessible ${operation} found for this selection.`);
        });
    }
    source.addEventListener('change', () => {
        reset(project, 'Select a project', 'projects'); reset(repository, 'Select a repository', 'repositories'); reset(branch, 'Select a repository first', 'branches'); clearResults();
        if (!source.value) { controls(); return; }
        if (project) list('projects', project, 'Select a project');
        else list('repositories', repository, 'Select a repository');
    });
    project?.addEventListener('change', () => {
        reset(repository, 'Select a repository', 'repositories'); reset(branch, 'Select a repository first', 'branches'); clearResults();
        if (project.value) list('repositories', repository, 'Select a repository'); else controls();
    });
    repository.addEventListener('change', () => {
        reset(branch, 'Select a branch', 'branches'); clearResults();
        if (repository.value) list('branches', branch, 'Select a branch'); else controls();
    });
    branch.addEventListener('change', () => { clearResults(); status.textContent = branch.value ? 'Select Load Builds to browse recent builds.' : 'Select a branch.'; controls(); });
    function builds(cursor = null) {
        clearResults();
        return run('Loading recent builds…', async active => {
            const result = await api('builds', cursor);
            if (!active()) return;
            for (const build of result.builds) {
                const row = document.createElement('tr');
                [build.number, build.branch, build.commit, build.status, build.result || 'Not available yet'].forEach(value => {
                    const cell = document.createElement('td'); cell.textContent = value; cell.className = 'text-break'; row.append(cell);
                });
                const cell = document.createElement('td');
                if (build.hasArtifacts) {
                    const link = document.createElement('a'); link.href = '#cloud-artifacts'; link.className = 'btn btn-outline-primary btn-sm'; link.textContent = 'View Artifacts';
                    link.dataset.artifactRun = String(build.id);
                    link.addEventListener('click', event => { event.preventDefault(); if (!busy) showArtifacts(build.id); }); cell.append(link);
                } else cell.textContent = 'None';
                row.append(cell); rows.append(row);
            }
            pagination('builds', result.next);
            status.textContent = result.builds.length ? `${result.builds.length} recent builds shown.` : 'No workflow runs or builds found for this branch.';
        });
    }
    function showArtifacts(id, append = false) {
        if (!append) { selectedBuild = id; artifacts.replaceChildren(); artifactPanel.hidden = true; }
        return run('Loading artifact metadata…', async active => {
            const result = await api('artifacts', append ? cursors.artifacts : null, id);
            if (!active()) return;
            for (const artifact of result.artifacts) {
                const item = document.createElement('li'); item.textContent = `${artifact.name} · ${artifact.type}` + (artifact.size == null ? '' : ` · ${artifact.size} bytes`); artifacts.append(item);
            }
            document.getElementById('artifact-build').textContent = `for run/build ${id}`;
            pagination('artifacts', result.next); artifactPanel.hidden = false; artifactPanel.focus();
            status.textContent = result.artifacts.length ? 'Artifact metadata loaded.' : 'No artifacts are available for this build.';
        });
    }
    load.addEventListener('click', () => builds()); refresh.addEventListener('click', () => builds());
    more('builds').addEventListener('click', () => builds(cursors.builds));
    more('artifacts').addEventListener('click', () => showArtifacts(selectedBuild, true));
    [['projects', project, 'Select a project'], ['repositories', repository, 'Select a repository'], ['branches', branch, 'Select a branch']].forEach(([op, select, prompt]) => {
        if (select) more(op).addEventListener('click', () => list(op, select, prompt, true));
    });
    function sources() {
        reset(source, 'Select a source'); reset(project, 'Select a source first', 'projects'); reset(repository, 'Select a source first', 'repositories'); reset(branch, 'Select a repository first', 'branches'); clearResults();
        return list('sources', source, 'Select a registered source');
    }
    retry.addEventListener('click', sources);
    sources();
})();
