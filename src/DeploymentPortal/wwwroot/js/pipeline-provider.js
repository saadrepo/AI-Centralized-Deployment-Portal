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
})();
