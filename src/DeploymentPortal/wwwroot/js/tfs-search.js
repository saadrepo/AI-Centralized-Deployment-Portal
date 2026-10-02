(() => {
    const form = document.querySelector('[data-tfs-search]') || document.getElementById('pipeline-search-form');
    if (!form) return;
    const input = document.getElementById(form.dataset.inputId || 'pipeline-search');
    const results = document.getElementById(form.dataset.resultsId || 'pipeline-search-results');
    const status = document.getElementById(form.dataset.statusId || 'pipeline-search-status');
    const clear = document.getElementById(form.dataset.clearId || 'pipeline-search-clear');
    const count = document.querySelector('.page-heading .count-pill');
    if (!input || !results || !status || !clear || !count) return;
    let timer;
    let pending;
    let revision = 0;

    function cancel() {
        clearTimeout(timer);
        pending?.abort();
        revision++;
    }

    async function search() {
        cancel();
        const current = revision;
        pending = new AbortController();
        const url = new URL(form.action, location.href);
        if (input.value.trim()) url.searchParams.set('search', input.value.trim());
        results.setAttribute('aria-busy', 'true');
        status.textContent = 'Searching...';
        clear.hidden = !input.value;
        try {
            const response = await fetch(url, { signal: pending.signal });
            const page = new DOMParser().parseFromString(await response.text(), 'text/html');
            if (current !== revision) return;
            const nextResults = page.getElementById(form.dataset.resultsId || 'pipeline-search-results');
            const nextCount = page.querySelector('.page-heading .count-pill');
            if (!nextResults || !nextCount) throw new Error('Invalid search response');
            results.replaceChildren(...nextResults.childNodes);
            count.textContent = nextCount.textContent;
            status.textContent = response.ok ? nextCount.textContent : 'Search failed. Please try again.';
            history.replaceState(null, '', url);
        } catch (error) {
            if (current !== revision || error.name === 'AbortError') return;
            status.textContent = 'Unable to search. Previous results are still shown. Please try again.';
        } finally {
            if (current === revision) results.removeAttribute('aria-busy');
        }
    }

    input.addEventListener('input', event => {
        cancel();
        clear.hidden = !input.value;
        if (!event.isComposing) timer = setTimeout(search, 350);
    });
    input.addEventListener('compositionend', () => { cancel(); timer = setTimeout(search, 350); });
    form.addEventListener('submit', event => { event.preventDefault(); search(); });
    clear.addEventListener('click', event => {
        event.preventDefault();
        input.value = '';
        input.focus();
        search();
    });
})();
