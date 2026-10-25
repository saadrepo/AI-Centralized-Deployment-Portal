const assert = require('node:assert/strict');
const { environment, until } = require('./pipeline-cloud.test.cjs');
(async () => {
    for (const provider of ['GitHub']) {
        const ui = environment(provider, '?applicationKey=Portal&sourceId=1&project=project&repository=app&branch=main', 123, true);
        const el = id => ui.elements['cp-' + id];
        await until(() => !el('refresh').disabled);
        el('definition').value = '7'; el('definition').listeners.change();
        await until(() => !el('refresh').disabled);
        assert.equal(el('run').disabled, false); assert.equal(el('approve').disabled, true);
        el('build').value = '42'; el('build').listeners.change();
        await until(() => !el('refresh').disabled);
        assert.equal(el('approve').disabled, false); assert.deepEqual(ui.writes, []);
        el('run').listeners.click(); el('run').listeners.click();
        await until(() => !el('refresh').disabled);
        assert.deepEqual(ui.writes, ['Queue']); assert.equal(el('approve').disabled, true);
        el('refresh').listeners.click(); await until(() => !el('refresh').disabled);
        assert.equal(el('approve').disabled, false);
        // The same action requires an exact evidence review; declining never approves.
        el('approve').listeners.click(); await until(() => !el('refresh').disabled);
        assert.deepEqual(ui.writes, ['Queue', 'Prepare']);
        el('refresh').listeners.click(); await until(() => !el('refresh').disabled);
        ui.confirm(true); el('approve').listeners.click(); el('approve').listeners.click();
        await until(() => !el('refresh').disabled);
        assert.deepEqual(ui.writes, ['Queue', 'Prepare', 'Prepare', 'Approve']);
        const before = [...ui.writes]; el('refresh').listeners.click(); await until(() => !el('refresh').disabled);
        assert.deepEqual(ui.writes, before); assert.equal(el('approve').disabled, true);
        assert.match(el('deployment-state').textContent, /Deployment Queued/);
        console.log('PASS:', provider, 'explicit build, CSRF, duplicate click guard, human approval, same three controls and read-only refresh');
    }
    const azure = environment('AzureDevOps', '?applicationKey=Portal&sourceId=1&project=project&repository=app&branch=main', 123, true);
    const a = id => azure.elements['cp-' + id];
    await until(() => !a('refresh').disabled);
    a('definition').value = '7'; a('definition').listeners.change(); await until(() => !a('refresh').disabled);
    a('build').value = '42'; a('build').listeners.change(); await until(() => !a('refresh').disabled);
    assert.equal(a('run').disabled, true); assert.equal(a('approve').disabled, true); assert.deepEqual(azure.writes, []);
    assert.ok(azure.requests.every(url => !/Readiness|Operation|Queue|Prepare|Approve/.test(url.pathname)));
    console.log('PASS: Azure DevOps remains read-only even if the controlled UI flag is set');
})().catch(error => { console.error(error); process.exitCode = 1; });
