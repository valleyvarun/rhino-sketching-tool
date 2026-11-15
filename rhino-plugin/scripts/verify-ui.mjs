// Dependency-free smoke tests against the actual embedded pages in headless Chrome.
// node rhino-plugin/scripts/verify-ui.mjs
import { spawn } from 'node:child_process';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'bin', 'UiValidation');
await mkdir(output, { recursive: true });
const chrome = process.env.CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const profile = path.join(output, 'chrome-' + process.pid);
const child = spawn(chrome, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: ['ignore','ignore','pipe'] });
let browserErrors = '';
child.stderr.on('data', data => browserErrors += data.toString());
let socket;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function pageFile(name) {
  try { return await readFile(path.join(root, 'UI', 'Web', name), 'utf8'); }
  catch { return readFile(path.join(root, name), 'utf8'); }
}
try {
  let port;
  for (let i = 0; i < 100; i++) {
    try { port = Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); break; } catch { await sleep(100); }
  }
  assert(port, 'Headless Chrome did not start');
  const targets = await (await fetch('http://localhost:' + port + '/json/list')).json();
  socket = new WebSocket(targets.find(x => x.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  let id = 0;
  const pending = new Map(), errors = [];
  socket.onclose = event => { for (const request of pending.values()) request.reject(new Error('Chrome disconnected: ' + event.code + ' ' + event.reason + '\n' + browserErrors)); pending.clear(); };
  socket.onmessage = e => {
    const message = JSON.parse(e.data);
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.text);
    if (!message.id) return;
    const callback = pending.get(message.id); pending.delete(message.id);
    if (message.error) callback.reject(new Error(message.error.message)); else callback.resolve(message.result);
  };
  const call = (method, params = {}) => new Promise((resolve, reject) => {
    const requestId = ++id;
    const timeout = setTimeout(() => { pending.delete(requestId); reject(new Error('CDP timed out: ' + method)); }, 10000);
    pending.set(requestId, { resolve: value => { clearTimeout(timeout); resolve(value); }, reject: error => { clearTimeout(timeout); reject(error); } });
    socket.send(JSON.stringify({ id: requestId, method, params }));
  });
  const evaluate = async expression => {
    const result = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };
  await call('Runtime.enable'); await call('Page.enable');
  const load = async (html, api, width = 348, height = 900) => {
    await call('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
    await call('Page.navigate', { url: 'data:text/html;charset=utf-8,' + encodeURIComponent(html) });
    for (let i = 0; i < 100; i++) {
      if (await evaluate('typeof window.' + api + ' === "object"')) return;
      await sleep(30);
    }
    throw new Error(api + ' did not initialize');
  };
  const screenshot = async name => {
    const result = await call('Page.captureScreenshot', { format: 'png' });
    await writeFile(path.join(output, name + '.png'), Buffer.from(result.data, 'base64'));
  };
  const agent = await pageFile('agent.html');
  await load(agent, 'crayon');
  assert.equal(await evaluate('document.querySelector("#chooser").hidden'), false);
  assert.equal(await evaluate('document.querySelector("#composer").hidden'), true);
  await screenshot('provider-choice');
  await evaluate('crayon.state({mcpMessage:"Install RhinoMCP from Package Manager: Rhino-MCP-Platform",mcpMissing:true});');
  assert.equal(await evaluate('document.querySelector("#mcp-notice").hidden'), false);
  await screenshot('missing-rhinomcp');
  await evaluate('crayon.take(); document.querySelector("#mcp-install").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayon.take()))'))[0].type, 'mcpInstall');
  await evaluate('document.querySelector("#mcp-retry").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayon.take()))'))[0].type, 'mcpRetry');
  await evaluate('crayon.state({mcpMessage:"",mcpMissing:false});');
  assert.equal(await evaluate('document.querySelector("#mcp-notice").hidden'), true);
  await evaluate('crayon.take(); document.querySelector("[data-provider=codex]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayon.take()))'))[0].value, 'codex');
  const state = { provider: 'codex', providerChosen: true, providerLabel: 'Codex', status: 'idle', models: [], modes: [], ids: [], entries: [], attachments: [], folder: 'model', folderPath: 'C:/model', signedIn: true, email: 'Signed in with ChatGPT' };
  await evaluate('crayon.state(' + JSON.stringify(state) + ')');
  assert(await evaluate('document.querySelector("#input").getBoundingClientRect().height >= 30'), 'composer input collapsed after provider choice');
  for (const provider of ['claude', 'codex']) {
    await evaluate('crayon.state(' + JSON.stringify({provider, providerLabel: provider === 'codex' ? 'Codex' : 'Claude Code', efforts: [['default','Default'],['low','Low'],['high','High']], effort: 'default', status: 'idle'}) + ')');
    await evaluate('crayon.take(); document.querySelector("#effort").click(); Array.from(document.querySelectorAll("#menu button")).find(b => b.textContent.trim() === "High").click()');
    const effortAction = (await evaluate('JSON.parse(decodeURIComponent(crayon.take()))'))[0];
    assert.equal(effortAction.type, 'effort');
    assert.equal(effortAction.value, 'high');
    await evaluate('crayon.state({effort:"high"})');
    assert.match(await evaluate('document.querySelector("#effort").textContent'), /Effort: High/);
    await screenshot(provider + '-effort');
    await evaluate('crayon.state({status:"working"})');
    assert.equal(await evaluate('document.querySelector("#effort").disabled'), true);
    await evaluate('crayon.state({status:"idle"})');
  }
  await evaluate('document.querySelector("#plus").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayon.take()))'))[0].type, 'attach');
  state.attachments = [{ id: 'test-file', name: 'An attached drawing reference.png' }];
  await evaluate('crayon.state(' + JSON.stringify(state) + ')');
  assert.equal(await evaluate('document.querySelector("#go").disabled'), false, 'attachment-only messages should send');
  await screenshot('codex-attachments');
  await evaluate('document.querySelector("[data-remove]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayon.take()))'))[0].type, 'removeAttachment');
  await call('Emulation.setDeviceMetricsOverride', { width: 260, height: 680, deviceScaleFactor: 1, mobile: false });
  assert(await evaluate('document.documentElement.scrollWidth <= innerWidth'), 'chat overflows narrow sidebar');
  await evaluate('crayon.state({ids:[91],entries:[{id:91,kind:"ask",name:"run_command",text:"Create ten boxes in Rhino",status:"pending",request:"mcp-request"}]});');
  await screenshot('mcp-approval');
  await evaluate('crayon.take(); document.querySelector("[data-answer=allow]").click()');
  const approvalAction = (await evaluate('JSON.parse(decodeURIComponent(crayon.take()))'))[0];
  assert.equal(approvalAction.type, 'answer');
  assert.equal(approvalAction.request, 'mcp-request');
  assert.equal(approvalAction.decision, 'allow');
  await evaluate('crayon.state({ids:[92],entries:[{id:92,kind:"tool",name:"get_context",status:"error",result:"Transport send error: connection refused"}]});');
  assert.match(await evaluate('document.querySelector(".tool.error").title'), /connection refused/);

  const logo = await readFile(path.join(root, 'Assets', 'Brand', 'crayon-mark.svg'), 'utf8');
  const wordmarkFont = await readFile(path.join(root, 'Assets', 'Fonts', 'InstrumentSans.ttf'));
  const sidebar = (await pageFile('sidebar.html')).replace('{{LOGO}}', logo).replace('{{WORDMARK_FONT}}', wordmarkFont.toString('base64'));
  const sketch = { active: true, tab: 'sketch', dark: false, tool: 0, method: 3, erase: 0, color: '#23262C', size: 6, opacity: 80, hardness: 80, pressure: 70, snap: true, sizePressure: true, opacityPressure: true, cursor: [0,0,0], views: ['Perspective sketch', 'Front elevation'], view: 'Perspective sketch' };
  await load(sidebar.replace('{{MODE}}', 'header'), 'crayonSidebar', 348, 92);
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  await screenshot('sidebar-header');
  assert.equal(await evaluate('document.querySelectorAll(".lockup svg").length'), 1);
  await evaluate('crayonSidebar.take(); document.querySelector("#theme").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'theme');
  await evaluate('crayonSidebar.state(' + JSON.stringify({...sketch, dark: true}) + ')');
  assert.equal(await evaluate('document.documentElement.classList.contains("dark")'), true);
  await sleep(350);
  await screenshot('sidebar-header-dark');
  await evaluate('crayonSidebar.state(' + JSON.stringify({...sketch, dark: false}) + ')');
  assert.equal(await evaluate('document.documentElement.classList.contains("dark")'), false);
  await load(sidebar.replace('{{MODE}}', 'body'), 'crayonSidebar');
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  await screenshot('sidebar-sketch');
  assert.equal(await evaluate('document.querySelectorAll("#ranges input[type=range]").length'), 3);
  assert.equal(await evaluate('document.querySelector("label[for=size]").textContent'), 'Size');
  assert.equal(await evaluate('document.querySelectorAll(".sensitivity").length'), 3);
  assert.equal(await evaluate('document.querySelectorAll(".preset-row").length'), 4);
  await evaluate('crayonSidebar.take(); document.querySelector("[data-preset=marker]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].value, 'marker');
  await evaluate('document.querySelector("#presetNew").click(); document.querySelector("#presetNewName").value="My brush"; document.querySelector("#presetEditor").dispatchEvent(new Event("submit",{cancelable:true,bubbles:true}))');
  const newPreset = (await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0];
  assert.equal(newPreset.type, 'presetNew'); assert.equal(newPreset.name, 'My brush');
  await evaluate('crayonSidebar.state(' + JSON.stringify({...sketch, preset:'crayon', presetEdited:true}) + '); crayonSidebar.take(); document.querySelector("#presetSave").click()');
  const savePreset = (await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0];
  assert.equal(savePreset.type, 'presetSave'); assert.equal(savePreset.value, 'crayon');
  await evaluate('document.querySelector("[data-preset=crayon]").dispatchEvent(new MouseEvent("dblclick",{bubbles:true})); document.querySelector("#presetList input").value="My crayon"; document.querySelector("#presetList input").dispatchEvent(new KeyboardEvent("keydown",{key:"Enter",bubbles:true}))');
  const renamedPreset = (await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0];
  assert.equal(renamedPreset.type, 'presetRename'); assert.equal(renamedPreset.name, 'My crayon');
  await evaluate('document.querySelector("[data-act=presetDelete][data-value=redline]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'presetDelete');
  await evaluate('document.querySelector("#colorWheel").click(); document.querySelector("#hex").value="#9731b0"; document.querySelector("#hex").dispatchEvent(new Event("change"))');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'color');
  await screenshot('sidebar-colour-picker');
  await evaluate('document.querySelector("#colorWheel").click(); document.querySelector("[data-act=cursorPlace]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'cursorPlace');
  await evaluate('document.querySelector("#cursorZ").value="12.5"; document.querySelector("#cursorZ").dispatchEvent(new Event("change"))');
  assert.deepEqual((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].xyz, [0,0,12.5]);
  await evaluate('document.querySelector("#size").value="9"; document.querySelector("#size").dispatchEvent(new Event("input"))');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].number, 9);
  await evaluate('document.querySelector("[data-act=hardnessPressure]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'hardnessPressure');
  await evaluate('crayonSidebar.state(' + JSON.stringify({...sketch, dark: true}) + ')');
  await sleep(350);
  await screenshot('sidebar-sketch-dark');
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  for (const width of [260, 300, 348, 640]) {
    await call('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: false });
    assert(await evaluate('document.documentElement.scrollWidth <= innerWidth && document.querySelector("main").scrollWidth <= document.querySelector("main").clientWidth'), 'sketch overflows at ' + width);
  }
  sketch.tab = 'layers'; sketch.layers = [{ id:'a', name: 'Default', visible: true, color: '#999999' }];
  sketch.sketches = [{ id:'b', name: 'Sketch 1', layer: 'Sketch', visible: true, color: '#23262C' }];
  sketch.comments = [{ id:'c', number: 1, text: 'Refine this edge', visible: true }];
  await call('Emulation.setDeviceMetricsOverride', { width: 348, height: 900, deviceScaleFactor: 1, mobile: false });
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  await screenshot('sidebar-layers');
  assert.equal(await evaluate('document.querySelectorAll("#sketchList .item").length'), 1);
  assert.equal(await evaluate('document.querySelectorAll(".item-group[open]").length'), 0, 'groups start collapsed');
  assert.equal(await evaluate('document.querySelector("[data-act=activeOnly]").getAttribute("aria-checked")'), 'true');
  assert.equal(await evaluate('document.querySelector("[data-act=newGroup]").getAttribute("aria-checked")'), 'true');
  assert.equal(await evaluate('document.querySelector("[data-act=hideUsed]").getAttribute("aria-checked")'), 'true');
  await evaluate('crayonSidebar.take(); document.querySelector("[data-act=hideUsed]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'hideUsed');
  await evaluate('crayonSidebar.take(); document.querySelector("[data-act=groupActive]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].value, 'sketch:1');
  assert.equal(await evaluate('document.querySelectorAll(".item-group[open]").length'), 0, 'active checkbox does not expand group');
  await evaluate('document.querySelector("[data-act=groupVisible]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'groupVisible');
  await evaluate('document.querySelector("#sketchList summary").click()');
  await sleep(30);
  await evaluate('document.querySelector("#sketchList [data-act=visible]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].value, 'b');
  sketch.sketchGroups = [{id:1,name:'Group 1',active:false,visible:true},{id:2,name:'Group 2',active:true,visible:true}];
  sketch.commentGroups = [{id:1,name:'Group 1',active:true,visible:true},{id:2,name:'Group 2',active:false,visible:true}];
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  assert.equal(await evaluate('document.querySelector("#sketchList .item-group").open'), true, 'expanded state survives updates');
  assert.equal(await evaluate('document.querySelectorAll("#sketchList [role=checkbox][aria-checked=true]").length'), 1);
  assert.equal(await evaluate('document.querySelectorAll("#commentList [role=checkbox][aria-checked=true]").length'), 1);
  await screenshot('sidebar-groups');
  for (const width of [260, 348]) {
    await call('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: false });
    assert(await evaluate('document.querySelector("main").scrollWidth <= document.querySelector("main").clientWidth'), 'groups overflow at ' + width);
  }
  assert.deepEqual(errors, [], 'JavaScript runtime errors');
  console.log('PASS: provider choice, attachments, sidebar states, responsive widths; screenshots in ' + output);
  await call('Browser.close');
} finally {
  socket?.close();
  child.kill();
}
