const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
class Element {
    constructor(id) { this.id = id; this.listeners = {}; this.attrs = {}; this.hidden = false; this.dataset = {}; this.classes = new Set(); this.classList = { toggle: (name, on) => on ? this.classes.add(name) : this.classes.delete(name) }; }
    addEventListener(name, fn) { this.listeners[name] = fn; }
    setAttribute(name, value) { this.attrs[name] = value; }
    removeAttribute(name) { delete this.attrs[name]; }
    focus() { this.focused = true; }
    dispatchEvent(event) { this.listeners[event.type]?.(event); }
    querySelector(tag) { return this[tag]; }
}
const code = fs.readFileSync(path.join(__dirname, '../../src/DeploymentPortal/wwwroot/js/pipeline-provider.js'), 'utf8');
for (const initial of ['Tfs2018', 'GitHub', 'AzureDevOps']) {
    const elements = {};
    for (const id of ['pipeline-provider', 'pipeline-provider-widget', 'pipeline-provider-trigger', 'pipeline-provider-list', 'pipeline-provider-name', 'pipeline-provider-label']) elements[id] = new Element(id);
    const provider = elements['pipeline-provider']; provider.value = initial; provider.dataset.url = '/Pipeline';
    const options = ['Tfs2018', 'GitHub', 'AzureDevOps'].map((id, i) => {
        const el = new Element('provider-option-' + id); el.dataset.value = id;
        el.span = { textContent: ['TFS', 'GitHub', 'Azure DevOps'][i] }; el.img = { src: '/icons/' + id + '.svg?v=hash' }; return el;
    });
    const trigger = elements['pipeline-provider-trigger']; trigger.img = { src: options.find(o => o.dataset.value === initial).img.src }; trigger.hidden = true;
    const list = elements['pipeline-provider-list']; list.hidden = true; list.querySelectorAll = () => options;
    const widget = elements['pipeline-provider-widget']; widget.contains = el => Object.values(elements).includes(el) || options.includes(el);
    const document = { getElementById: id => elements[id], addEventListener(name, fn) { this[name] = fn; } };
    let navigated, removed;
    vm.runInNewContext(code, { document, URL, URLSearchParams, Event, sessionStorage: { removeItem: key => { removed = key; } }, location: { href: 'https://portal.test/Pipeline?repository=stale', assign: url => { navigated = new URL(url); } } });
    const key = name => { const event = { key: name, preventDefault() { this.prevented = true; } }; trigger.listeners.keydown(event); return event; };
    assert.equal(provider.hidden, true); assert.equal(trigger.hidden, false);
    const mouse = { preventDefault() { this.prevented = true; } }; list.listeners.mousedown(mouse); assert.equal(mouse.prevented, true);
    trigger.listeners.click(); assert.equal(trigger.attrs['aria-expanded'], 'true');
    assert.equal(trigger.attrs['aria-activedescendant'], 'provider-option-' + initial);
    key('Escape'); assert.equal(list.hidden, true); assert.equal(navigated, undefined);
    key('ArrowDown'); key('Home'); assert.equal(trigger.attrs['aria-activedescendant'], options[0].id);
    key('End'); assert.equal(trigger.attrs['aria-activedescendant'], options[2].id);
    key('ArrowDown'); assert.equal(trigger.attrs['aria-activedescendant'], options[0].id);
    key('ArrowUp'); assert.equal(trigger.attrs['aria-activedescendant'], options[2].id);
    key('g'); assert.equal(trigger.attrs['aria-activedescendant'], options[1].id);
    const tab = key('Tab'); assert.equal(list.hidden, true); assert.equal(tab.prevented, undefined);
    trigger.listeners.click(); document.click({ target: {} }); assert.equal(list.hidden, true);
    trigger.listeners.click(); widget.listeners.focusout({ relatedTarget: {} }); assert.equal(list.hidden, true);
    trigger.listeners.click(); key('Escape');
    // Selecting the current provider must not reload or clear state.
    options.find(o => o.dataset.value === initial).listeners.click(); assert.equal(navigated, undefined);
    const target = initial === 'GitHub' ? 2 : 1;
    trigger.listeners.click(); key(target === 1 ? 'g' : 'a'); key('Enter');
    assert.equal(provider.value, options[target].dataset.value);
    assert.equal(trigger.img.src, options[target].img.src);
    assert.equal(elements['pipeline-provider-name'].textContent, options[target].span.textContent);
    assert.equal(options[target].attrs['aria-selected'], 'true');
    assert.equal(options.filter(o => o.attrs['aria-selected'] === 'true').length, 1);
    assert.equal(trigger.attrs['aria-expanded'], 'false'); assert.equal(trigger.focused, true);
    assert.equal(navigated.search, '?provider=' + options[target].dataset.value); assert.equal(removed, 'pipeline-build');
    console.log('PASS:', initial, 'selected icon, keyboard navigation, dismiss/focus, selected state and provider navigation');
}
