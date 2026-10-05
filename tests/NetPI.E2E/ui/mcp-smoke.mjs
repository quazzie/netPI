import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { hostUi } from './host.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const PANEL = Number(process.env.MCP_PANEL || 300);
const bundle = fs.readFileSync(path.join(root, 'plugins/NetPI.Mcp/wwwroot/ui.js'));
const host = hostUi(root);
// The tab is mounted the way the app mounts it: inside a short, scrolling side panel. A menu positioned inside that
// panel is clipped by it, which is how the action menu lost four of its five items (idea-pii7hv) — a bare page cannot
// see that, so the panel is part of the fixture, not an accident of the test.
const html = `<!doctype html><html><head>${host.head}<style>
  body {margin:0;background:#191919;color:#ddd;font-family:system-ui}
  .fixture-panel { position:fixed; inset:40px 0 0 auto; width:${PANEL}px; height:190px; overflow:auto; border-left:1px solid #333; z-index:9999; background:#191919}
</style></head><body><div id="app" hidden></div><div class="fixture-panel"><div id="fixture"></div></div><script type="module">
import {mount} from '/ui.js';
const server = {id:'fixture',status:'connected',toolCount:1,config:{enabled:true,transport:'stdio',command:'node',args:[],cwd:'C:/tools',pinned:[],readOnly:[]}};
const tool = {id:'mcp_fixture_weather_123456789',name:'weather',description:'City weather',exposed:true,deferred:true,readOnly:false,schema:{type:'object',properties:{city:{type:'string'}}}};
mount(document.getElementById('fixture'),{on:()=>()=>{},rpc:async(method,args)=>{
  if(method==='mcp.list')return {servers:[server]};
  if(method==='mcp.tools')return {tools:[tool]};
  if(method==='mcp.save')throw Error('Connection failed; review configuration');
  return {servers:[server]};
}});
</script></body></html>`;
const server = http.createServer((req,res)=>{if(host.serve(req,res))return;res.setHeader('Content-Type',req.url==='/ui.js'?'text/javascript':'text/html');res.end(req.url==='/ui.js'?bundle:html);});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
let browser;
const errors=[];
try {
  try { browser=await chromium.launch({args:["--disable-features=msWindowTabManagerPublic"]}); }
  catch { browser=await chromium.launch({channel:'msedge',args:['--disable-features=msWindowTabManagerPublic']}); }
  const page=await browser.newPage();
  page.on('pageerror',error=>errors.push(String(error)));
  for(const width of [230,380,1000]) {
    await page.setViewportSize({width:Math.max(width,PANEL+40),height:420});
    await page.goto('http://127.0.0.1:'+server.address().port);
    await page.locator('button.pick').click();
    await page.getByText('weather',{exact:true}).click();
    await page.getByLabel('Expose tool').waitFor();
    const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth);
    if(overflow)throw Error('MCP tab overflows at '+width+'px');
    // The per-server actions are one menu now (idea-pii7hv): it opens, holds every action, and closes on Escape.
    const menu=page.getByRole('button',{name:'Actions for fixture'});
    if(!(await menu.isVisible()))throw Error('the server action menu button is missing');
    if(await page.getByRole('menuitem').count())throw Error('the action menu is open before it was clicked');
    await menu.click();
    const items=await page.getByRole('menuitem').allInnerTexts();
    if(items.length!==5)throw Error('expected 5 actions in the menu, got '+items.length);
    if(!items.join(' ').includes('Remove'))throw Error('Remove is missing from the menu: '+items.join(', '));
    // …and every one of them is actually on screen. The panel scrolls, and a menu positioned *inside* it used to be
    // clipped after the first item — the menu is fixed and floats over the panel now, so what matters is that each
    // item is on screen and is the thing you would hit with the mouse.
    const hidden=await page.evaluate(()=>{
      const bad=[];
      for(const el of document.querySelectorAll('[role=menuitem]')){
        const r=el.getBoundingClientRect();
        const off=r.top<0||r.bottom>innerHeight||r.left<0||r.right>innerWidth;
        const hit=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);
        const painted=!!hit&&(hit===el||el.contains(hit));
        if(off||!painted)bad.push(el.textContent.trim()+(off?' (off-screen)':' (covered)'));
      }
      return bad;
    });
    if(hidden.length)throw Error('the action menu is not fully usable: '+hidden.join(', '));
    await page.keyboard.press('Escape');
    if(await page.getByRole('menuitem').count())throw Error('Escape did not close the action menu');
    await menu.click();
    await page.getByRole('menuitem',{name:/Edit/}).click();
    const draft=await page.getByLabel('Configuration',{exact:true}).inputValue();
    await page.getByRole('button',{name:'Save',exact:true}).click();
    await page.getByRole('alert').waitFor();
    if(await page.getByLabel('Configuration',{exact:true}).inputValue()!==draft)throw Error('Failed save lost edits');
    console.log('PASS MCP UI '+width+'px: catalog controls and failed-save draft retained');
  }
  if(errors.length)throw Error(errors.join('\n'));
} finally { await browser?.close(); await new Promise(resolve=>server.close(resolve)); }
