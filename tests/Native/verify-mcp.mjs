// Read-only integration probe. No model prompts and no changes to provider config files.
import { readFileSync, writeFileSync, appendFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
const output = path.join(path.dirname(fileURLToPath(import.meta.url)), 'bin');
const config = JSON.parse(readFileSync(path.join(output, 'mcp-fixture.json')));
const logfile = path.join(output, 'mcp-clients.log');
writeFileSync(logfile, '');
const log = text => appendFileSync(logfile, text + '\n');
async function rpc(method, params, id = 1) {
  const res = await fetch(config.url, { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' }, body: JSON.stringify({ jsonrpc: '2.0', id, method, params }), signal: AbortSignal.timeout(12000) });
  const text = await res.text();
  assert(res.ok, res.status + ': ' + text);
  const data = JSON.parse(text.startsWith('event:') || text.startsWith('data:') ? text.split('\n').find(l => l.startsWith('data:')).slice(5).trim() : text);
  assert(!data.error, JSON.stringify(data.error));
  return data.result;
}
function client(command, args) {
  assert(command.length, 'CLI not found');
  const env = { ...process.env }; delete env.CLAUDECODE;
  const child = spawn(command[0], [...command.slice(1), ...args], { windowsHide: true, cwd: output, env, stdio: ['pipe','pipe','pipe'] });
  const messages = [], waiters = []; let errors = '';
  child.stderr.on('data', data => errors = (errors + data).slice(-4000));
  const lines = createInterface({ input: child.stdout });
  lines.on('line', line => { try { messages.push(JSON.parse(line)); for (const wake of [...waiters]) wake(); } catch {} });
  return {
    send: value => child.stdin.write(JSON.stringify(value) + '\n'),
    wait: predicate => new Promise((resolve, reject) => {
      const timeout = setTimeout(() => { remove(); reject(new Error('CLI timeout: ' + errors)); }, 30000);
      const remove = () => { clearTimeout(timeout); const at = waiters.indexOf(wake); if (at >= 0) waiters.splice(at, 1); };
      const wake = () => { const index = messages.findIndex(predicate); if (index >= 0) { const value = messages.splice(index, 1)[0]; remove(); resolve(value); } };
      waiters.push(wake); wake();
    }),
    close: () => { lines.close(); child.stdin.end(); child.kill(); },
  };
}
try {
  const init = await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'CrayonValidation', version: '1' } });
  assert(init.serverInfo);
  const listing = await rpc('tools/list', {});
  assert(listing.tools.some(t => t.name === 'get_context'));
  log('PASS MCP initialize and tools/list on exact document endpoint (' + listing.tools.length + ' tools)');
  const context = await rpc('tools/call', { name: 'get_context', arguments: {} });
  assert(!context.isError, JSON.stringify(context));
  log('PASS read-only get_context executes inside isolated Rhino');
  const codex = client(config.codex, ['app-server', '--listen', 'stdio://', ...config.codexArgs]);
  try {
    codex.send({ id: 1, method: 'initialize', params: { clientInfo: { name: 'crayon_validation', version: '1' }, capabilities: { experimentalApi: false } } });
    assert(!(await codex.wait(m => m.id === 1)).error);
    codex.send({ method: 'initialized', params: {} });
    codex.send({ id: 2, method: 'mcpServerStatus/list', params: {} });
    const status = await codex.wait(m => m.id === 2);
    assert(!status.error, JSON.stringify(status.error));
    const server = status.result.data.find(s => s.name === 'crayon_rhino');
    assert(server && Object.keys(server.tools).length > 0, JSON.stringify(server || status));
    log('PASS real Codex discovers crayon_rhino tools through generated process config');
  } finally { codex.close(); }
  const claude = client(config.claude, ['-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose', '--permission-prompt-tool', 'stdio', ...config.claudeArgs]);
  try {
    claude.send({ type: 'control_request', request_id: 'init', request: { subtype: 'initialize', hooks: null } });
    const init = await claude.wait(m => m.type === 'control_response' && m.response?.request_id === 'init');
    assert(init.response.subtype === 'success', JSON.stringify(init));
    claude.send({ type: 'control_request', request_id: 'mcp', request: { subtype: 'mcp_status' } });
    const status = await claude.wait(m => m.type === 'control_response' && m.response?.request_id === 'mcp');
    const server = status.response.response?.mcpServers?.find(s => s.name === 'crayon_rhino');
    assert(server?.status === 'connected', JSON.stringify(status));
    log('PASS real Claude Code reports crayon_rhino connected');
  } finally { claude.close(); }
} catch (error) { log(error.stack); process.exitCode = 1; }
