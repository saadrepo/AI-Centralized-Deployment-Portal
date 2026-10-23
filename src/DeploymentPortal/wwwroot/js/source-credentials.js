(() => {
    'use strict';
    const modal = document.getElementById('credential-provisioning');
    if (!modal) return;
    const provider = document.getElementById('Provider');
    const destination = document.getElementById('Destination');
    const command = document.getElementById('provisioning-command');
    const commandStatus = document.getElementById('provisioning-command-status');
    const copy = document.getElementById('copy-provisioning-command');
    const refresh = document.getElementById('refresh-credentials');
    const references = document.getElementById('CredentialReference');
    const refreshStatus = document.getElementById('credential-refresh-status');
    let revision = 0;

    function clearCommand() {
        revision++;
        command.textContent = '';
        copy.disabled = true;
    }
    provider.addEventListener('change', clearCommand);
    destination.addEventListener('input', clearCommand);
    modal.addEventListener('hidden.bs.modal', clearCommand);
    modal.addEventListener('show.bs.modal', async () => {
        clearCommand();
        const current = revision;
        if (provider.value === 'Tfs2018') {
            commandStatus.textContent = 'TFS uses existing protected Windows credentials. No PAT provisioning command is needed.';
            return;
        }
        commandStatus.textContent = 'Validating provider and destination…';
        const url = new URL(modal.dataset.commandUrl, window.location.href);
        url.search = new URLSearchParams({ provider: provider.value, destination: destination.value });
        try {
            const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
            if (current !== revision) return;
            if (response.status === 401 || response.status === 403) throw new Error('Access denied. Sign in with an approved Source Management account.');
            if (!response.ok) {
                if (response.status === 400) {
                    const problem = await response.json();
                    if (current !== revision) return;
                    throw new Error(problem.error);
                }
                throw new Error('Command unavailable. Close this help and try again.');
            }
            const result = await response.json();
            if (current !== revision) return;
            command.textContent = result.command;
            commandStatus.textContent = 'Validated command for the selected provider and organization:';
            copy.disabled = false;
        } catch (error) {
            if (current === revision) commandStatus.textContent = error.message || 'Command unavailable. Try again.';
        }
    });
    copy.addEventListener('click', async () => {
        if (copy.disabled || !command.textContent) return;
        try {
            await navigator.clipboard.writeText(command.textContent);
            commandStatus.textContent = 'Command copied. Run it manually on SOURCE in elevated PowerShell.';
        } catch {
            commandStatus.textContent = 'Clipboard unavailable. Select and copy the displayed command manually.';
        }
    });
    refresh.addEventListener('click', async () => {
        refresh.disabled = true;
        refreshStatus.textContent = 'Refreshing credential references…';
        try {
            const response = await fetch(refresh.dataset.url, { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
            if (!response.ok) throw new Error();
            const credentials = await response.json();
            const selected = references.value;
            const options = [new Option('TFS: existing protected Windows credentials', '')];
            for (const credential of credentials) {
                // Metadata only; text-based option creation prevents markup interpretation.
                const name = typeof credential.provider === 'number' ? ['AzureDevOps', 'GitHub'][credential.provider] : credential.provider;
                options.push(new Option(`${name} · ${credential.destination} · ${credential.reference}`, credential.reference));
            }
            references.replaceChildren(...options);
            references.value = options.some(option => option.value === selected) ? selected : '';
            refreshStatus.textContent = credentials.length
                ? 'Credentials refreshed. Select the reference returned by the script for this provider and organization.'
                : 'No token credentials found on this portal host. TFS connections remain available.';
        } catch {
            refreshStatus.textContent = 'Credential refresh failed. Your selection is unchanged. Verify Source Management access and the credential store on this portal host, then retry.';
        } finally {
            refresh.disabled = false;
        }
    });
})();
