document.getElementById('menu-toggle')?.addEventListener('click', function () {
    const open = document.getElementById('sidebar').classList.toggle('open');
    this.setAttribute('aria-expanded', String(open));
});
document.querySelectorAll('[data-table-filter]').forEach(input => input.addEventListener('input', () => {
    document.querySelectorAll(input.dataset.tableFilter + ' tbody tr').forEach(row => { row.hidden = !row.textContent.toLowerCase().includes(input.value.toLowerCase()); });
}));
document.querySelectorAll('form[data-busy]').forEach(form => form.addEventListener('submit', event => {
    if (!form.checkValidity() || (window.jQuery && jQuery.validator && !jQuery(form).valid())) return;
    const button = event.submitter;
    if (button) { button.textContent = 'Working…'; button.setAttribute('aria-busy', 'true'); }
    // Preserve formaction and submitter semantics until the native submission is dispatched.
    setTimeout(() => form.querySelectorAll('button[type="submit"]').forEach(b => b.disabled = true), 0);
}));
document.getElementById('deployment-server')?.addEventListener('change', function () {
    const environment = this.selectedOptions[0]?.dataset.environment;
    if (environment) document.getElementById('deployment-environment').value = environment;
});
document.querySelectorAll('[data-auto-open]').forEach(modal => bootstrap.Modal.getOrCreateInstance(modal).show());
if (document.querySelector('[data-refresh="true"]')) setTimeout(() => location.reload(), 5000);
