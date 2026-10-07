(() => {
    const drawer = document.getElementById('ai-assistant');
    if (!drawer || !window.bootstrap) return;
    const search = document.getElementById('assistant-search');
    const input = document.getElementById('assistant-message');
    const send = document.getElementById('assistant-send');
    const messages = document.getElementById('assistant-messages');
    const microphones = document.querySelectorAll('[data-assistant-mic]');
    let busy = false;
    const syncSend = () => { send.disabled = busy || !input.value.trim(); };
    let timers = [];
    let activeInput = null;
    let originalValue = '';
    const announce = text => document.querySelectorAll('[data-assistant-status]').forEach(status => { status.textContent = text; });
    function stopListening() {
        timers.forEach(clearTimeout);
        timers = [];
        if (activeInput) {
            activeInput.readOnly = false;
            activeInput.placeholder = activeInput.dataset.idlePlaceholder;
            activeInput = null;
        }
        document.querySelectorAll('.assistant-field').forEach(field => {
            delete field.dataset.voiceState;
            field.removeAttribute('aria-busy');
            field.querySelectorAll('.assistant-processing, .assistant-cancel').forEach(item => { item.hidden = true; });
        });
        microphones.forEach(button => {
            button.hidden = false;
            button.setAttribute('aria-pressed', 'false');
            button.setAttribute('aria-label', 'Start demo microphone');
        });
        document.querySelectorAll('[data-assistant-status]').forEach(status => {
            status.textContent = 'Text deployment preparation is available. Voice is a demo.';
        });
    }
    microphones.forEach(button => button.addEventListener('click', () => {
        const start = button.getAttribute('aria-pressed') !== 'true';
        if (!start && activeInput) activeInput.value = originalValue;
        stopListening();
        syncSend();
        if (start) {
            const field = button.closest('.assistant-field');
            const target = field.querySelector('input');
            originalValue = target.value;
            activeInput = target;
            target.dataset.idlePlaceholder = target.placeholder;
            target.value = '';
            target.placeholder = 'Listening...';
            target.readOnly = true;
            field.dataset.voiceState = 'listening';
            syncSend();
            button.setAttribute('aria-pressed', 'true');
            button.setAttribute('aria-label', 'Stop demo microphone');
            document.querySelectorAll('[data-assistant-status]').forEach(status => {
                status.textContent = 'Listening... (UI demo only; no audio is recorded)';
            });
            timers.push(setTimeout(() => {
                field.dataset.voiceState = 'detected';
                target.value = 'Deploy Portal on DEV server';
                announce('Demo speech detected: Deploy Portal on DEV server. No audio was recorded.');
                syncSend();
            }, 1800));
            timers.push(setTimeout(() => {
                field.dataset.voiceState = 'processing';
                field.setAttribute('aria-busy', 'true');
                const progress = field.querySelector('.assistant-processing');
                if (progress) {
                    const hadFocus = document.activeElement === button;
                    button.hidden = true;
                    progress.hidden = false;
                    field.querySelector('.assistant-cancel').hidden = false;
                    if (hadFocus) field.querySelector('.assistant-cancel').focus();
                }
                announce('Processing demo request. No AI or server is connected.');
            }, 3800));
            timers.push(setTimeout(() => {
                const wasSearch = target === search;
                stopListening();
                if (wasSearch) bootstrap.Offcanvas.getOrCreateInstance(drawer).show();
                else input.focus();
                announce('Demo complete. Sample text is ready to edit; nothing was executed.');
                syncSend();
            }, 5600));
        }
    }));
    document.querySelectorAll('.assistant-cancel').forEach(button => button.addEventListener('click', () => {
        const target = activeInput;
        if (target) target.value = originalValue;
        stopListening();
        syncSend();
        target?.focus();
    }));
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && activeInput) {
            activeInput.value = originalValue;
            stopListening();
            syncSend();
        }
    });
    drawer.addEventListener('show.bs.offcanvas', () => {
        stopListening();
        if (search.value.trim()) input.value = search.value.trim();
        syncSend();
    });
    drawer.addEventListener('shown.bs.offcanvas', () => input.focus());
    drawer.addEventListener('hide.bs.offcanvas', stopListening);
    function submitOnEnter(event) {
        if (event.key === 'Enter' && !event.isComposing) {
            event.preventDefault();
            const command = event.currentTarget.value.trim();
            if (!command || busy || event.repeat) return;
            bootstrap.Offcanvas.getOrCreateInstance(drawer).show();
            input.value = command;
            syncSend();
            form.requestSubmit(send);
        }
    }
    search.addEventListener('keydown', submitOnEnter);
    input.addEventListener('keydown', submitOnEnter);
    document.querySelectorAll('.assistant-example').forEach(button => button.addEventListener('click', () => {
        input.value = button.textContent.trim();
        syncSend();
        input.focus();
    }));
    input.addEventListener('input', syncSend);
    function addMessage(kind, text) {
        const message = document.createElement('div');
        message.className = `assistant-message assistant-message-${kind}`;
        message.textContent = text;
        messages.appendChild(message);
        message.scrollIntoView({ block: 'nearest' });
    }
    const form = document.getElementById('assistant-form');
    form.addEventListener('submit', async event => {
        event.preventDefault();
        const request = input.value.trim();
        if (!request || busy) return;
        stopListening();
        busy = true;
        syncSend();
        form.setAttribute('aria-busy', 'true');
        addMessage('user', `You: ${request}`);
        announce('Resolving the application and registered destination...');
        try {
            const payload = new FormData(form);
            payload.set('text', request);
            const response = await fetch(form.action, { method: 'POST', body: payload, credentials: 'same-origin', signal: AbortSignal.timeout(30000) });
            const result = await response.json().catch(() => null);
            if (!response.ok) throw new Error(result?.message || 'Could not prepare the request. Refresh the page and try again.');
            if (result?.intent !== 'DeploySite' || !result.fields) throw new Error('Unexpected assistant response. Nothing was submitted for review.');
            const review = document.createElement('form');
            review.method = 'POST';
            review.action = form.dataset.reviewUrl;
            const fields = { ...result.fields, __RequestVerificationToken: payload.get('__RequestVerificationToken') };
            for (const [name, value] of Object.entries(fields)) {
                const field = document.createElement('input');
                field.type = 'hidden';
                field.name = name;
                field.value = value ?? '';
                review.appendChild(field);
            }
            document.body.appendChild(review);
            announce('Opening the existing deployment Review. Confirmation is still required.');
            review.submit();
        } catch (error) {
            const message = error.name === 'TimeoutError'
                ? 'Server resolution timed out. Please try again.'
                : error.message || 'Request failed. Please try again.';
            addMessage('demo', `Assistant: ${message}`);
            announce(`Could not open deployment Review: ${message}`);
            busy = false;
            syncSend();
            form.removeAttribute('aria-busy');
            input.focus();
        }
    });
})();
