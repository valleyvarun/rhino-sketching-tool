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
  const take = async () => JSON.parse(decodeURIComponent(await evaluate('crayon.take()') || '[]'));
  await load(agent, 'crayon');
  assert.equal(await evaluate('document.querySelector("#chooser").hidden'), false);
  assert.equal(await evaluate('document.querySelector("#chatForm").hidden'), true);
  await screenshot('provider-choice');
  await evaluate('crayon.state({mcpMessage:"Install RhinoMCP from Package Manager: Rhino-MCP-Platform",mcpMissing:true});');
  assert.equal(await evaluate('document.querySelector("#mcpNote").hidden'), false);
  await screenshot('missing-rhinomcp');
  await evaluate('crayon.take(); document.querySelector("[data-act=mcpInstall]").click()');
  assert.equal((await take())[0].type, 'mcpInstall');
  await evaluate('document.querySelector("[data-act=mcpRetry]").click()');
  assert.equal((await take())[0].type, 'mcpRetry');
  await evaluate('crayon.state({mcpMessage:"",mcpMissing:false});');
  assert.equal(await evaluate('document.querySelector("#mcpNote").hidden'), true);
  await evaluate('crayon.take(); document.querySelector("#chooser [data-provider=codex]").click()');
  assert.equal((await take())[0].value, 'codex');
  // Onboarding: the pick shows at once and is asked for only once, until Rhino has switched.
  assert.equal(await evaluate('document.querySelector("#chooser [data-provider=codex]").getAttribute("aria-pressed")'), 'true');
  assert.equal(await evaluate('document.querySelector("#chooser [data-provider=claude]").disabled'), true);
  await evaluate('document.querySelector("#chooser [data-provider=claude]").click()');
  assert.equal(await evaluate('crayon.take()'), '', 'a second pick while switching is ignored');
  // The assistant picked is not installed, or not signed in: the other one is offered.
  await evaluate('crayon.state({provider:"claude", providerLabel:"Claude Code", providerChosen:true, missing:true, signedIn:false})');
  assert.equal(await evaluate('document.querySelector("#chooser").hidden'), true, 'the chooser goes once an assistant is chosen');
  assert.match(await evaluate('document.querySelector("#account").textContent'), /Claude Code was not found/);
  await evaluate('document.querySelector("#account [data-provider=codex]").click()');
  assert.deepEqual((await take()).map((a) => a.type + ':' + a.value), ['provider:codex']);
  await evaluate('crayon.state({missing:false, signedIn:false})');
  assert.match(await evaluate('document.querySelector("#account").textContent'), /Sign in to Claude/);
  await evaluate('document.querySelector("#account [data-act=signin]").click()');
  assert.equal((await take())[0].type, 'signin');
  await evaluate('crayon.state({providerChosen:false, missing:false, signedIn:undefined})');
  assert.equal(await evaluate('document.querySelector("#chooser").hidden'), false);
  assert.equal(await evaluate('document.querySelector("#chooser [data-provider=claude]").disabled'), false, 'the chooser is usable again when shown again');
  const state = { provider: 'codex', providerChosen: true, providerLabel: 'Codex', status: 'idle', model: 'gpt-5.5', chosen: 'default',
    models: [{ value: 'default', label: 'Default', description: '', resolved: 'gpt-5.5' }, { value: 'gpt-5.5', label: 'GPT-5.5', description: 'Most capable' }],
    modes: [['default', 'Ask before edits'], ['auto', 'Edit automatically']], mode: 'default', ids: [], entries: [], attachments: [], folder: 'model', folderPath: 'C:/model', signedIn: true, email: 'Signed in with ChatGPT' };
  await evaluate('crayon.state(' + JSON.stringify(state) + ')');
  assert(await evaluate('document.querySelector("#chatText").getBoundingClientRect().height >= 30'), 'composer input collapsed after provider choice');
  assert.equal(await evaluate('document.querySelector("#hello").hidden'), false, 'empty chat shows the welcome');
  assert.equal(await evaluate('document.querySelector("#modelLabel").textContent'), 'GPT-5.5', 'the model button names the model, never the assistant');
  for (const provider of ['claude', 'codex']) {
    await evaluate('crayon.state(' + JSON.stringify({provider, providerLabel: provider === 'codex' ? 'Codex' : 'Claude Code', efforts: [['default','Default'],['low','Low'],['high','High']], effort: 'default', status: 'idle'}) + ')');
    await evaluate('crayon.take(); document.querySelector("#modelBtn").click(); document.querySelector("#modelMenu [data-effort=high]").click()');
    const effortAction = (await take())[0];
    assert.equal(effortAction.type, 'effort');
    assert.equal(effortAction.value, 'high');
    await evaluate('crayon.state({effort:"high"}); document.querySelector("#modelBtn").click()');
    assert.equal(await evaluate('document.querySelector("#modelMenu [data-effort=high]").getAttribute("aria-checked")'), 'true');
    await screenshot(provider + '-effort');
    await evaluate('crayon.state({status:"working"})');
    assert.equal(await evaluate('document.querySelector("#modelMenu [data-effort=high]").disabled'), true);
    assert.equal(await evaluate('document.querySelector("#sendBtn").classList.contains("stop")'), true, 'while working the send button stops');
    await evaluate('crayon.take(); document.querySelector("#sendBtn").click()');
    assert.equal((await take())[0].type, 'stop');
    await evaluate('crayon.state({status:"idle"}); document.dispatchEvent(new PointerEvent("pointerdown", {bubbles:true}))');
  }
  await evaluate('crayon.take(); document.querySelector("#modeBtn").click(); document.querySelector("#modeMenu [data-mode=auto]").click()');
  assert.deepEqual((await take()).map((a) => a.type + ':' + a.value), ['mode:auto']);
  await evaluate('document.querySelector("#chatUpload").click()');
  assert.equal((await take())[0].type, 'attach');
  state.attachments = [{ id: 'test-file', name: 'An attached drawing reference.png' }];
  await evaluate('crayon.state(' + JSON.stringify(state) + ')');
  assert.equal(await evaluate('document.querySelector("#sendBtn").disabled'), false, 'attachment-only messages should send');
  await screenshot('codex-attachments');
  await evaluate('document.querySelector("[data-remove]").click()');
  assert.equal((await take())[0].type, 'removeAttachment');
  await evaluate('document.querySelector("#chatText").value = "Make the roof curve"; document.querySelector("#chatText").dispatchEvent(new KeyboardEvent("keydown", {key:"Enter", bubbles:true}))');
  assert.deepEqual((await take()).map((a) => a.type + ':' + a.text), ['send:Make the roof curve']);
  await evaluate('crayon.sent(true)');
  assert.equal(await evaluate('document.querySelector("#chatText").value'), '', 'a sent message leaves the box');
  await call('Emulation.setDeviceMetricsOverride', { width: 260, height: 680, deviceScaleFactor: 1, mobile: false });
  assert(await evaluate('document.documentElement.scrollWidth <= innerWidth'), 'chat overflows narrow sidebar');
  for (const width of [150, 200, 260]) {
    await call('Emulation.setDeviceMetricsOverride', { width, height: 680, deviceScaleFactor: 1, mobile: false });
    assert(await evaluate('document.querySelector("#modelLabel").getBoundingClientRect().width >= 24'), 'the model name is readable at ' + width + ' px');
  }
  await call('Emulation.setDeviceMetricsOverride', { width: 260, height: 680, deviceScaleFactor: 1, mobile: false });
  await evaluate('crayon.state({reset:true, status:"working", thinking:true, ids:[90,91],entries:[{id:90,kind:"user",text:"Make ten boxes",chips:[{kind:"sketch",label:"Group 2"}]},{id:91,kind:"ask",name:"run_command",text:"Create ten boxes in Rhino",status:"pending",request:"mcp-request"}]});');
  assert.equal(await evaluate('document.querySelector(".ag-user .ag-file.sk span").textContent'), 'Group 2', 'a message shows the groups it took along');
  assert.equal(await evaluate('document.querySelector(".ag-ai").classList.contains("thinking")'), true, 'the loop runs while the agent works');
  assert.equal(await evaluate('document.querySelector(".ag-think").textContent'), 'Thinking');
  await screenshot('mcp-approval');
  await evaluate('crayon.take(); document.querySelector("[data-answer=allow]").click()');
  const approvalAction = (await take())[0];
  assert.equal(approvalAction.type, 'answer');
  assert.equal(approvalAction.request, 'mcp-request');
  assert.equal(approvalAction.decision, 'allow');
  await evaluate('crayon.state({status:"idle", thinking:false, ids:[90,91,92,93],entries:[{id:91,kind:"ask",name:"run_command",text:"Create ten boxes in Rhino",status:"allowed",request:"mcp-request"},{id:92,kind:"tool",name:"get_context",status:"error",result:"Transport send error: connection refused"},{id:93,kind:"notice",text:"The connection to Rhino dropped.",error:true}]});');
  assert.equal(await evaluate('document.querySelector(".ag-ai").classList.contains("settle")'), true, 'the loop draws itself once when the reply is done');
  assert.match(await evaluate('document.querySelector(".ag-step.err").title'), /connection refused/);
  assert.equal(await evaluate('document.querySelectorAll(".ag-note.err").length'), 1);
  await evaluate('crayon.state({historyOpen:true, current:"b", history:[{id:"b",title:"Make ten boxes",updated:Date.now()},{id:"a",title:"Earlier chat",updated:Date.now()-86400000}]})');
  assert.equal(await evaluate('document.querySelectorAll("#chatHist [data-hist]").length'), 2);
  assert.equal(await evaluate('document.querySelector("#chatThread").hidden'), true, 'History replaces the conversation while open');
  await evaluate('crayon.take(); document.querySelector("#chatHist [data-hist=a]").click()');
  assert.deepEqual((await take()).map((a) => a.type + ':' + a.value), ['open:a']);

  const logo = await readFile(path.join(root, 'Assets', 'Brand', 'crayon-mark.svg'), 'utf8');
  const sidebar = (await pageFile('sidebar.html')).replace('{{LOGO}}', logo);
  const sketch = { active: true, tab: 'sketch', dark: false, tool: 0, method: 3, erase: 0, color: '#23262C', size: 6, opacity: 80, hardness: 80, pressure: 70, snap: true, sizePressure: true, opacityPressure: true, cursor: [0,0,0], views: ['Perspective sketch', 'Front elevation'], view: 'Perspective sketch' };
  await load(sidebar.replace('{{MODE}}', 'header'), 'crayonSidebar', 348, 92);
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  await screenshot('sidebar-header');
  assert.equal(await evaluate('document.querySelectorAll(".lockup svg").length'), 1);
  assert.equal(await evaluate('document.querySelectorAll("[role=tab]").length'), 3);
  assert.equal(await evaluate('document.querySelector("#power").getAttribute("aria-checked")'), 'true');
  await evaluate('crayonSidebar.state(' + JSON.stringify({...sketch, dark: true}) + ')');
  assert.equal(await evaluate('document.documentElement.classList.contains("dark")'), true);
  await evaluate('crayonSidebar.state(' + JSON.stringify({...sketch, dark: false}) + ')');
  assert.equal(await evaluate('document.documentElement.classList.contains("dark")'), false);
  // Narrow panels: nothing in the header overlaps, the tabs fall back to icons.
  for (const width of [150, 180, 240, 300, 348, 560]) {
    await call('Emulation.setDeviceMetricsOverride', { width, height: 120, deviceScaleFactor: 1, mobile: false });
    await evaluate('crayonSidebar.state(' + JSON.stringify({...sketch, tab: 'agent', agentReady: true}) + ')');
    assert(await evaluate('(() => { const w = document.querySelector(".wordmark"), s = document.querySelector("#power"); return getComputedStyle(w).display === "none" || w.getBoundingClientRect().right <= s.getBoundingClientRect().left; })()'), 'wordmark runs under the switch at ' + width);
    assert(await evaluate('[...document.querySelectorAll("[role=tab]")].every((b) => b.scrollWidth <= b.clientWidth + 1)'), 'tab labels overflow at ' + width);
    assert(await evaluate('document.documentElement.scrollWidth <= innerWidth'), 'header overflows at ' + width);
  }
  await call('Emulation.setDeviceMetricsOverride', { width: 348, height: 92, deviceScaleFactor: 1, mobile: false });
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  // The header asks Rhino for the height it needs, so the tab switcher is never cropped.
  const told = JSON.parse(decodeURIComponent(await evaluate('crayonSidebar.take()') || '[]'));
  const needed = told.filter((a) => a.type === 'height').map((a) => Number(a.value)).pop();
  assert(needed >= await evaluate('Math.ceil(document.querySelector(".mswitch").getBoundingClientRect().bottom)'), 'the header asks for room for the whole tab switcher');
  await evaluate('document.querySelector("#theme").click(); document.querySelector("[data-tab=layers]").click(); document.querySelector("#power").click()');
  assert.deepEqual((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))')).filter((a) => a.type !== 'height').map((a) => a.type + ':' + a.value), ['theme:dark', 'tab:layers', 'power:']);
  await load(sidebar.replace('{{MODE}}', 'body'), 'crayonSidebar');
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  await screenshot('sidebar-sketch');
  assert.equal(await evaluate('document.querySelectorAll("#bars .cy-bar").length'), 3);
  assert.equal(await evaluate('document.querySelectorAll("#sketch .cy-check").length'), 4, 'every option is a hand-drawn tick');
  assert.equal(await evaluate('document.querySelectorAll("#main [role=switch]").length'), 0, 'no switches in the panel body');
  await evaluate('document.querySelector(".pr[data-preset=marker]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].value, 'marker');
  await evaluate('document.querySelector("[data-check=hardnessPressure]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'hardnessPressure');
  for (const width of [260, 300, 348, 640]) {
    await call('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: false });
    assert(await evaluate('document.documentElement.scrollWidth <= innerWidth && document.querySelector("#sketch").scrollWidth <= document.querySelector("#sketch").clientWidth'), 'sketch overflows at ' + width);
  }
  sketch.tab = 'layers';
  sketch.sketches = [{ id:'b', name: 'Sketch 1', group: 1, ownVisible: true, visible: true, color: '#23262C' }];
  sketch.comments = [{ id:'c', number: 1, text: 'Refine this edge', group: 1, ownVisible: true, visible: true }];
  sketch.sketchGroups = [{id:1,name:'Group 1',active:true,visible:true}];
  sketch.commentGroups = [{id:1,name:'Group 1',active:true,visible:true}];
  sketch.newGroup = true; sketch.hideUsed = true;
  await call('Emulation.setDeviceMetricsOverride', { width: 348, height: 900, deviceScaleFactor: 1, mobile: false });
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  await screenshot('sidebar-layers');
  assert.equal(await evaluate('document.querySelectorAll("#sketchGroups .lrow.item").length'), 1, 'groups start open');
  assert.equal(await evaluate('document.body.textContent.includes("Stroke ")'), false, 'sketches are never called strokes');
  assert.equal(await evaluate('document.querySelector("[data-check=newGroup]").getAttribute("aria-checked")'), 'true');
  assert.equal(await evaluate('document.querySelector("[data-check=hideUsed]").getAttribute("aria-checked")'), 'true');
  await evaluate('crayonSidebar.take(); document.querySelector("[data-check=hideUsed]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'hideUsed');
  await evaluate('document.querySelector("[data-act=groupNew][data-value=sketch]").click()');
  assert.equal((await evaluate('JSON.stringify(JSON.parse(decodeURIComponent(crayonSidebar.take()))[0])')), JSON.stringify({ type: 'groupNew', value: 'sketch' }));
  await evaluate('document.querySelector("#sketchGroups .lrow.grp").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].value, 'sketch:1');
  await evaluate('crayonSidebar.take(); document.querySelector("#sketchGroups [data-act=groupContext]").click()');
  assert.deepEqual((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))')).map((a) => a.type + ':' + a.value), ['groupContext:sketch:1'], 'each group has a tick for sending it to the agent');
  assert.equal(await evaluate('document.querySelector("#sketchGroups [data-act=groupContext]").getAttribute("aria-checked")'), 'false', 'the tick rubs out at once');
  await evaluate('document.querySelector("#sketchGroups [data-act=groupActive]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'groupActive', 'the pen picks the group new sketches go into');
  await evaluate('document.querySelector("#sketchGroups [data-act=groupVisible]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].type, 'groupVisible');
  await evaluate('document.querySelector("#sketchGroups .lrow.grp [data-act=groupDelete]").click()');
  assert.deepEqual((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))')).map((a) => a.type + ':' + a.value), ['groupDelete:sketch:1'], 'one click on the trash of a group deletes it');
  await evaluate('document.querySelector("#sketchGroups .lrow.item [data-act=delete]").click()');
  assert.equal(await evaluate('crayonSidebar.take()'), '', 'a second click straight after is ignored: the rows are about to move up');
  await evaluate('document.querySelector("#sketchGroups .lrow.item [data-act=visible]").click()');
  assert.equal((await evaluate('JSON.parse(decodeURIComponent(crayonSidebar.take()))'))[0].value, 'b');
  sketch.sketchGroups = [{id:1,name:'Group 1',active:false,visible:true},{id:2,name:'Group 2',active:true,visible:true}];
  sketch.commentGroups = [{id:1,name:'Group 1',active:true,visible:true},{id:2,name:'Group 2',active:false,visible:true}];
  await evaluate('crayonSidebar.state(' + JSON.stringify(sketch) + ')');
  assert.equal(await evaluate('document.querySelectorAll("#sketchGroups .lrow.grp.picked").length'), 1);
  assert.equal(await evaluate('document.querySelectorAll("#commentGroups .lrow.grp.picked").length'), 1);
  await screenshot('sidebar-groups');
  for (const [width, scale] of [[150, 1], [150, 2], [200, 1], [260, 1.5], [348, 1], [348, 1.25]]) {
    await call('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: scale, mobile: false });
    assert(await evaluate('document.querySelector("#layers").scrollWidth <= document.querySelector("#layers").clientWidth && document.documentElement.scrollWidth <= innerWidth'), 'groups overflow at ' + width + ' px, scale ' + scale);
  }
  assert.deepEqual(errors, [], 'JavaScript runtime errors');
  console.log('PASS: provider choice, attachments, sidebar states, responsive widths; screenshots in ' + output);
  await call('Browser.close');
} finally {
  socket?.close();
  child.kill();
}
