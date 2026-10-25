(() => {
    'use strict';
    const provider = document.getElementById('pipeline-provider');
    provider?.addEventListener('change', () => {
        // A fresh document stops prior polling and discards all provider selectors.
        try { sessionStorage.removeItem('pipeline-build'); } catch { /* Optional storage may be disabled. */ }
        const url = new URL(provider.dataset.url, location.href);
        url.search = new URLSearchParams({ provider: provider.value });
        location.assign(url.href);
    });
    const widget = document.getElementById('pipeline-provider-widget');
    if (!provider || !widget) return;
    const trigger = document.getElementById('pipeline-provider-trigger');
    const list = document.getElementById('pipeline-provider-list');
    const options = [...list.querySelectorAll('[role="option"]')];
    let active = options.findIndex(option => option.dataset.value === provider.value);
    function highlight(index) {
        active = (index + options.length) % options.length;
        options.forEach((option, i) => option.classList.toggle('active', i === active));
        trigger.setAttribute('aria-activedescendant', options[active].id);
    }
    function close() {
        list.hidden = true; trigger.setAttribute('aria-expanded', 'false'); trigger.removeAttribute('aria-activedescendant');
    }
    function open() {
        list.hidden = false; trigger.setAttribute('aria-expanded', 'true');
        highlight(options.findIndex(option => option.dataset.value === provider.value));
    }
    function choose(index) {
        const option = options[index];
        const changed = provider.value !== option.dataset.value;
        provider.value = option.dataset.value;
        document.getElementById('pipeline-provider-name').textContent = option.querySelector('span').textContent;
        trigger.querySelector('img').src = option.querySelector('img').src;
        options.forEach(item => item.setAttribute('aria-selected', String(item === option)));
        close(); trigger.focus();
        if (changed) provider.dispatchEvent(new Event('change', { bubbles: true }));
    }
    trigger.addEventListener('click', () => list.hidden ? open() : close());
    trigger.addEventListener('keydown', event => {
        if (event.key === 'Tab') { close(); return; }
        if (event.key === 'Escape') { close(); event.preventDefault(); return; }
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
            event.preventDefault();
            if (list.hidden) { open(); if (event.key.startsWith('Arrow')) return; }
            highlight(event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1 : active + (event.key === 'ArrowDown' ? 1 : -1));
        } else if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault(); if (list.hidden) open(); else choose(active);
        } else if (/^[a-z]$/i.test(event.key)) {
            const match = options.findIndex(option => option.querySelector('span').textContent.toLowerCase().startsWith(event.key.toLowerCase()));
            if (match >= 0) { event.preventDefault(); if (list.hidden) open(); highlight(match); }
        }
    });
    options.forEach((option, index) => option.addEventListener('click', () => choose(index)));
    // Keep focus on the combobox until a mouse click has selected its option.
    list.addEventListener('mousedown', event => event.preventDefault());
    document.addEventListener('click', event => { if (!widget.contains(event.target)) close(); });
    widget.addEventListener('focusout', event => { if (!widget.contains(event.relatedTarget)) close(); });
    // Keep the original native select as the no-JavaScript fallback and value source.
    provider.hidden = true;
    document.getElementById('pipeline-provider-label').htmlFor = trigger.id;
    trigger.hidden = false;
})();
