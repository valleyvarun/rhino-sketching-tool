// Deterministic transport fixture. Never calls a model, account service or network.
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const childProcess = require('child_process');
const provider = process.argv[2];
fs.writeFileSync(path.join(process.cwd(), provider + '-arguments.json'), JSON.stringify(process.argv.slice(3)));
const worker = childProcess.spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { windowsHide: true, stdio: 'ignore' });
fs.writeFileSync(path.join(process.cwd(), provider + '-child.pid'), String(worker.pid));
const output = message => process.stdout.write(JSON.stringify(message) + '\n');
const notify = (method, params) => output({ method, params });
let turn = 0;
readline.createInterface({ input: process.stdin }).on('line', line => {
  const message = JSON.parse(line);
  if (provider === 'claude') {
    if (message.type === 'control_request') {
      output({ type: 'control_response', response: { subtype: 'success', request_id: message.request_id, response: { account: { email: 'fixture@example.invalid' }, models: [{ value: 'default', displayName: 'Fixture', supportsEffort: true, supportedEffortLevels: ['low', 'medium', 'high', 'max'] }, { value: 'legacy', displayName: 'Legacy' }] } } });
    } else if (message.type === 'user') {
      fs.writeFileSync(path.join(process.cwd(), 'claude-request.json'), JSON.stringify(message));
      output({ type: 'assistant', message: { id: 'claude-text', content: [{ type: 'text', text: 'Claude fixture reply' }] } });
      output({ type: 'result', subtype: 'success', session_id: 'claude-session' });
    }
    return;
  }
  const reply = result => output({ id: message.id, result });
  switch (message.method) {
    case 'initialize': reply({ userAgent: 'Codex fixture' }); break;
    case 'account/read': reply({ account: { type: 'chatgpt', email: 'fixture@example.invalid', planType: 'plus' } }); break;
    case 'model/list': reply({ data: [{ model: 'fixture-model', displayName: 'Fixture', description: 'Test model', isDefault: true, defaultReasoningEffort: 'medium', supportedReasoningEfforts: ['low','medium','high','xhigh','ultra'].map(reasoningEffort => ({reasoningEffort, description: ''})) }] }); break;
    case 'thread/start':
    case 'thread/resume':
      fs.writeFileSync(path.join(process.cwd(), 'thread-request.json'), JSON.stringify(message));
      reply({ thread: { id: 'codex-session' }, model: 'fixture-model' }); break;
    case 'turn/start':
      turn++;
      fs.writeFileSync(path.join(process.cwd(), 'codex-request.json'), JSON.stringify(message));
      if (!Object.hasOwn(message.params.sandboxPolicy, 'networkAccess')) throw new Error('Sandbox network policy is required');
      reply({ turn: { id: 'turn-' + turn } });
      if (message.params.input[0].text.includes('MCP transport failure')) {
        notify('item/completed', {threadId:'codex-session', item:{type:'mcpToolCall',id:'mcp-'+turn,server:'crayon_rhino',tool:'get_context',arguments:{},status:'failed',error:{message:'Transport send error: connection refused'}}});
        notify('turn/completed', {threadId:'codex-session',turn:{id:'turn-'+turn,status:'completed'}});
        break;
      }
      if (message.params.input[0].text.includes('MCP approval fixture')) {
        output({id:'mcp-approval',method:'mcpServer/elicitation/request',params:{threadId:'codex-session',serverName:'crayon_rhino',mode:'form',message:'Allow Rhino to create a box?',requestedSchema:{type:'object',properties:{}},_meta:{codex_approval_kind:'mcp_tool_call',tool_title:'Run Rhino Command',tool_params:{command:'_Box 0,0,0 10,10,0 10'}}}});
        break;
      }
      notify('item/started', { threadId: 'codex-session', item: { type: 'agentMessage', id: 'text-' + turn, text: '' } });
      notify('item/agentMessage/delta', { threadId: 'codex-session', itemId: 'text-' + turn, delta: 'Codex fixture reply' });
      output({ id: 73, method: 'item/commandExecution/requestApproval', params: { threadId: 'codex-session', turnId: 'turn-' + turn, itemId: 'command-' + turn, command: 'fixture-command', reason: 'Test approval' } });
      break;
    case 'turn/interrupt':
      reply({}); notify('turn/completed', { threadId: 'codex-session', turn: { id: 'turn-' + turn, status: 'interrupted' } }); break;
    default:
      if (message.id === 'mcp-approval' && message.result) {
        fs.writeFileSync(path.join(process.cwd(),'mcp-approval-response.json'),JSON.stringify(message));
        const denied = message.result.action === 'decline';
        notify('item/completed',{threadId:'codex-session',item:{type:'mcpToolCall',id:'mcp-'+turn,server:'crayon_rhino',tool:'run_command',arguments:{},status:denied?'failed':'completed',error:denied?{message:'user rejected MCP tool call'}:null,result:denied?null:{content:[{type:'text',text:'Box created'}]}}});
        notify('turn/completed',{threadId:'codex-session',turn:{id:'turn-'+turn,status:'completed'}});
      }
      if (message.id === 73 && message.result) {
        fs.writeFileSync(path.join(process.cwd(), 'approval-response.json'), JSON.stringify(message));
        notify('item/completed', { threadId: 'codex-session', item: { type: 'agentMessage', id: 'text-' + turn, text: 'Codex fixture reply' } });
        notify('turn/completed', { threadId: 'codex-session', turn: { id: 'turn-' + turn, status: 'completed' } });
      }
  }
});
