// Sample plugin UI. Built by `npm run build:plugins -- web/mock/sample-plugin` into wwwroot/ui.js (one ES module).
//
// The host imports /plugins/<pluginId>/ui.js?v=<version> and calls mount(el, ctx) — or module[export](el, ctx)
// when the tab declares `export`. Return { unmount, onShow, onHide } (all optional).
import { mount as svelteMount, unmount as svelteUnmount } from 'svelte';
import SampleTab from './SampleTab.svelte';

/** Svelte tab ("Sample"). */
export function mount(el, ctx) {
  const view = svelteMount(SampleTab, { target: el, props: { ctx } });
  return {
    unmount: () => svelteUnmount(view),
    onShow: () => view.setVisible(true),
    onHide: () => view.setVisible(false),
  };
}

/** Vanilla JS tab ("Events"): a live bus event log, using only the host's np-* classes. */
export function mountEvents(el, ctx) {
  el.innerHTML = `
    <div class="np-toolbar" style="border-bottom:1px solid var(--border)">
      <input class="np-input" placeholder="Filter (e.g. agent.* or stream)" style="flex:1" />
      <button class="np-btn np-btn-sm" data-act="pause">Pause</button>
      <button class="np-btn np-btn-sm np-btn-ghost" data-act="clear">Clear</button>
    </div>
    <div class="np-scroll" style="flex:1;min-height:0;padding:6px 0" data-list></div>
    <div class="np-toolbar np-dim np-small" style="border-top:1px solid var(--border)" data-foot></div>`;
  const list = el.querySelector('[data-list]');
  const foot = el.querySelector('[data-foot]');
  const filter = el.querySelector('input');
  let paused = false;
  let visible = true;
  let count = 0;
  const rows = [];

  const esc = (s) => String(s).replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]);
  const matches = (type) => {
    const f = filter.value.trim();
    if (!f) return true;
    if (f.endsWith('*')) return type.startsWith(f.slice(0, -1));
    return type.includes(f);
  };
  function render() {
    if (!visible) return;
    const shown = rows.filter((r) => matches(r.type)).slice(-200);
    list.innerHTML = shown.length
      ? shown
          .map(
            (r) =>
              `<div class="np-row" style="min-height:22px;padding:1px 10px" title="${esc(r.json)}">
                 <span class="np-mono np-dim" style="flex:none">${r.time}</span>
                 <span class="np-mono" style="flex:none;color:var(--accent)">${esc(r.type)}</span>
                 <span class="np-mono np-dim np-ellipsis">${esc(r.json)}</span>
               </div>`,
          )
          .join('')
      : `<div class="np-empty">No events yet</div>`;
    list.scrollTop = list.scrollHeight;
    foot.textContent = `${count} events · ${paused ? 'paused' : 'live'}`;
  }
  let scheduled = false;
  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(() => {
      scheduled = false;
      render();
    });
  };

  const off = ctx.on('*', (data, evt) => {
    if (paused || evt.type === 'stream.delta' || evt.type === 'tool.output') return;
    count++;
    rows.push({ type: evt.type, time: new Date(evt.ts || Date.now()).toLocaleTimeString(), json: JSON.stringify(data).slice(0, 300) });
    if (rows.length > 500) rows.splice(0, rows.length - 500);
    schedule();
  });
  el.addEventListener('click', (e) => {
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (act === 'pause') {
      paused = !paused;
      e.target.textContent = paused ? 'Resume' : 'Pause';
    } else if (act === 'clear') rows.length = 0;
    render();
  });
  filter.addEventListener('input', render);
  render();

  return {
    unmount() {
      off();
      el.innerHTML = '';
    },
    onShow() {
      visible = true;
      render();
    },
    onHide() {
      visible = false;
    },
  };
}
