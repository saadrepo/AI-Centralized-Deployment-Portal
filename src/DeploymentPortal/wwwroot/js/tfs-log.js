(() => {
    const input = document.getElementById('log-filter');
    const content = document.getElementById('log-content');
    if (!input || !content) return;
    const original = content.textContent;
    const lines = original.split(/\r?\n/);
    let timer;
    input.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(() => {
            const query = input.value.toLowerCase();
            const matches = lines.filter(line => line.toLowerCase().includes(query));
            content.textContent = query ? matches.join('\n') : original;
            document.getElementById('log-filter-status').textContent = query ? `${matches.length} matching loaded lines` : '';
        }, 150);
    });
})();
