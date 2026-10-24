import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { StdioClient } from './Test-McpLive.mjs';

// Read-only inspection through the actual stdio MCP -> Lua mailbox route.
const args = process.argv.slice(2), options = {};
for (let i = 0; i < args.length; i += 2) {
  if (!['--mcp-path', '--game-root', '--output-directory'].includes(args[i]) || !args[i + 1]) {
    throw new Error('Required: --mcp-path <exe> --game-root <directory> --output-directory <directory>');
  }
  options[args[i]] = path.resolve(args[i + 1]);
}
for (const key of ['--mcp-path', '--game-root', '--output-directory']) {
  if (!options[key]) throw new Error(`Missing ${key}`);
}
await mkdir(options['--output-directory'], { recursive: true });
const messages = [];
const client = new StdioClient(options['--mcp-path'], ['--game-root', options['--game-root']],
  (kind, message) => messages.push({ kind, message }));
const evidence = { startedAt: new Date().toISOString(), readOnly: true };
const array = value => Array.isArray(value) ? value : [];
async function read(action, arguments_ = {}) {
  if (!['status', 'list_actions'].includes(action)) throw new Error('Only observation tools are permitted.');
  const result = await client.request('tools/call', { name: `motionvfx_${action}`, arguments: arguments_ });
  const reply = result.structuredContent;
  if (result.isError || !reply?.ok) throw new Error(JSON.stringify(reply ?? result));
  return reply.result;
}
try {
  evidence.initialize = await client.request('initialize', { protocolVersion: '2025-11-25',
    capabilities: {}, clientInfo: { name: 'action-observation-check', version: '1' } });
  client.notify('notifications/initialized');
  evidence.status = await read('status');
  evidence.history = await read('list_actions');
  evidence.details = [];
  const main = array(evidence.history.actions).filter(a => a.componentKind === 'character' && a.motion?.layer === 0);
  const attack = main.filter(a => a.motion?.motionId >= 300);
  for (const action of (attack.length ? attack : main).slice(-8)) {
    evidence.details.push(await read('list_actions', { actionId: action.id }));
  }
  console.log(JSON.stringify({ version: evidence.status.version, actions: evidence.history.actions.length,
    mainActions: main.length, counters: evidence.history.counters,
    details: evidence.details.map(d => ({ id: d.action.id, bank: d.action.motion.bankId,
      motion: d.action.motion.motionId, events: array(d.events).length,
      direct: array(d.events).filter(e => e.association === 'direct_motion_request').length,
      paths: [...new Set(array(d.events).flatMap(e => [...array(e.requested), ...array(e.created)]).map(r => r.path))] })) }, null, 2));
} catch (error) {
  evidence.error = error.message; process.exitCode = 1; console.error(error.message);
} finally {
  await client.close();
  evidence.finishedAt = new Date().toISOString(); evidence.stderr = client.stderr;
  await writeFile(path.join(options['--output-directory'], 'action-mcp-evidence.json'),
    JSON.stringify(evidence, null, 2), 'utf8');
  await writeFile(path.join(options['--output-directory'], 'action-mcp-transcript.json'),
    JSON.stringify(messages, null, 2), 'utf8');
}
