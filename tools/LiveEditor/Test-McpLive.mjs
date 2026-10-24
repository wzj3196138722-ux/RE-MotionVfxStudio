import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

// This test has no mailbox/file/game API. Every editor operation passes through MCP stdio.
const allowed = new Set(['status', 'list_effects', 'inspect', 'set_group_note', 'set_color', 'undo']);
const clone = value => JSON.parse(JSON.stringify(value));
const equal = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const assert = (condition, message) => { if (!condition) throw new Error(message); };

export class StdioClient {
  constructor(command, args, record, timeoutMs = 110_000) {
    this.record = record; this.timeoutMs = timeoutMs; this.sequence = 0; this.pending = null;
    this.stderr = ''; this.buffer = ''; this.failed = null;
    this.child = spawn(command, args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    this.child.stdout.setEncoding('utf8'); this.child.stderr.setEncoding('utf8');
    this.child.stdout.on('data', data => this.receive(data));
    this.child.stderr.on('data', data => { this.stderr = (this.stderr + data).slice(-262144); });
    this.child.on('error', error => this.fail(error));
    this.child.on('exit', (code, signal) => {
      if (this.pending) this.fail(new Error(`MCP exited while waiting: code=${code}, signal=${signal}`));
    });
  }
  fail(error) {
    this.failed ??= error;
    if (this.pending) { clearTimeout(this.pending.timer); this.pending.reject(error); this.pending = null; }
  }
  receive(data) {
    this.buffer += data;
    if (Buffer.byteLength(this.buffer, 'utf8') > 64 * 1024 * 1024) return this.fail(new Error('MCP output exceeded the 64 MiB bounded line limit.'));
    let newline;
    while ((newline = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, newline).replace(/\r$/, ''); this.buffer = this.buffer.slice(newline + 1);
      let response;
      try { response = JSON.parse(line); } catch { this.fail(new Error('MCP stdout contained non-JSON output.')); return; }
      this.record('response', response);
      if (!this.pending || response.jsonrpc !== '2.0' || response.id !== this.pending.id) {
        this.fail(new Error('Unexpected MCP response identity; no further operations will be sent.')); return;
      }
      const pending = this.pending; this.pending = null; clearTimeout(pending.timer);
      if (response.error) pending.reject(new Error(`MCP ${response.error.code}: ${response.error.message}`));
      else if (!Object.hasOwn(response, 'result')) pending.reject(new Error('MCP response has no result.'));
      else pending.resolve(response.result);
    }
  }
  async request(method, params = {}) {
    if (this.failed) throw this.failed;
    assert(!this.pending, 'The test only issues one MCP request at a time.');
    const id = ++this.sequence;
    const request = { jsonrpc: '2.0', id, method, params }; this.record('request', request);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => this.fail(new Error(`MCP timeout for ${method}; the outcome may be uncertain. No automatic retry/undo was sent.`)), this.timeoutMs);
      this.pending = { id, resolve, reject, timer };
      this.child.stdin.write(JSON.stringify(request) + '\n', error => { if (error) this.fail(error); });
    });
  }
  notify(method) {
    assert(!this.failed, 'Transport is unavailable.');
    const notification = { jsonrpc: '2.0', method }; this.record('notification', notification);
    this.child.stdin.write(JSON.stringify(notification) + '\n');
  }
  async close() {
    if (!this.child || this.child.exitCode !== null) return;
    this.child.stdin.end();
    await Promise.race([new Promise(resolve => this.child.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 2000))]);
    if (this.child.exitCode === null) this.child.kill();
  }
}

function unsafeReasons(status, requireClean) {
  const reasons = [];
  if (status.backendReady !== true) reasons.push('backend_not_ready');
  if (status.hasDrafts !== false) reasons.push('ui_drafts_or_unknown');
  if (status.pending || status.applyRequested || status.bindingsRestorePending || status.bindingsRestoreFailed || status.bindings?.busy) reasons.push('editor_busy_or_restore_failed');
  if (requireClean) {
    if (!status.document) reasons.push('no_document');
    else for (const key of ['dirty', 'canUndo', 'canRedo']) if (status.document[key] !== false) reasons.push(`document_${key}_or_unknown`);
    if (status.bindings?.appliedCount !== 0 || status.bindings?.requiresRestoreBeforeReset === true) reasons.push('applied_resources_or_unknown');
    if (status.capture?.recording === true) reasons.push('recording_in_progress');
  }
  return reasons;
}

function revision(document) {
  assert(Number.isSafeInteger(document?.revision) && document.revision >= 0, 'No valid current document revision was returned.');
  return document.revision;
}
function groups(document) {
  return (document.groups ?? []).map(group => ({ index: group.index, name: group.name, entries: [...(group.entries ?? [])] }));
}
function notes(document) {
  return (document.groups ?? []).map(group => ({ index: group.index, note: group.note ?? '', displayName: group.displayName ?? group.name }));
}
function efxState(document) {
  return { source: document.source, revision: document.revision, dirty: document.dirty, canUndo: document.canUndo, canRedo: document.canRedo, groups: groups(document) };
}
function colorValues(group) {
  assert(group.storage === 'rgba8' && group.scale === 255 && group.channels?.length === 4, 'The color is not an explicit four-channel rgba8 group.');
  return ['R', 'G', 'B', 'A'].map(component => {
    const matches = group.channels.filter(channel => channel.component === component);
    assert(matches.length === 1 && Number.isInteger(matches[0].value) && matches[0].value >= 0 && matches[0].value <= 255, 'Invalid explicit color channel.');
    return matches[0].value;
  });
}

export async function runLiveTest(options, transportFactory) {
  const outputDirectory = path.resolve(options.outputDirectory);
  await mkdir(outputDirectory, { recursive: true });
  const evidence = { test: 'MCP stdio read/optional edit-and-undo', startedAt: new Date().toISOString(),
    editRoundTripRequested: options.editRoundTrip === true, transport: transportFactory ? 'offline_mock_stdio' : 'real_mcp_stdio',
    commands: [], checks: [], forbiddenOperationsSent: false, appliedToGame: false, outstandingTestEdit: null, passed: false };
  const record = (direction, payload) => evidence.commands.push({ at: new Date().toISOString(), direction, payload });
  const client = transportFactory ? transportFactory(record) : new StdioClient(
    options.mcpPath.toLowerCase().endsWith('.dll') ? 'dotnet' : path.resolve(options.mcpPath),
    [...(options.mcpPath.toLowerCase().endsWith('.dll') ? [path.resolve(options.mcpPath)] : []), '--game-root', path.resolve(options.gameRoot)], record);
  let document, currentStatus, colorTarget, colorInspection, originalGroups;
  const tool = async (name, args = {}) => {
    assert(allowed.has(name), `Test refuses non-test operation: ${name}`);
    if (name !== 'status' && name !== 'list_effects') assert(args.expectedRevision === revision(document), 'Every document operation must use the latest revision.');
    const result = await client.request('tools/call', { name: 'motionvfx_' + name, arguments: args });
    let outcome = result.structuredContent;
    if (!outcome) {
      const texts = (result.content ?? []).filter(item => item.type === 'text');
      assert(texts.length === 1, 'Missing unambiguous MCP tool payload.'); outcome = JSON.parse(texts[0].text);
    }
    assert(outcome?.protocol === 1 && typeof outcome.id === 'string' && typeof outcome.ok === 'boolean', 'Invalid Lua bridge outcome.');
    if (result.isError === true || !outcome.ok) {
      const error = new Error(`${name}: ${outcome.error?.code ?? 'tool_failed'}: ${outcome.error?.message ?? 'unknown failure'}`);
      error.uncertain = outcome.error?.uncertain === true; error.requestId = outcome.error?.requestId ?? outcome.id; throw error;
    }
    if (outcome.result?.document) { document = outcome.result.document; revision(document); }
    return outcome.result;
  };
  const inspect = target => tool('inspect', { target, expectedRevision: revision(document) });
  try {
    const initialization = await client.request('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'MotionVfx-live-validation', version: '1' } });
    assert(initialization.protocolVersion === '2025-11-25', 'MCP negotiated an unexpected protocol.');
    evidence.server = initialization.serverInfo; client.notify('notifications/initialized');
    currentStatus = await tool('status'); document = currentStatus.document;
    evidence.initialStatus = clone(currentStatus); evidence.effects = await tool('list_effects');
    if (!document) { evidence.skipped = ['no_current_document']; evidence.passed = true; return evidence; }
    revision(document); originalGroups = groups(document); evidence.originalGroups = clone(originalGroups);
    const readGuards = unsafeReasons(currentStatus, false);
    if (readGuards.length) { evidence.skipped = readGuards; evidence.passed = true; return evidence; }
    const groupZero = originalGroups.find(group => group.index === 0);
    if (groupZero) await inspect({ kind: 'group', index: 0 });
    const rgbTargets = (document.entries ?? []).flatMap(entry => (entry.attributes ?? [])
      .filter(attribute => /^rgbcommon$/i.test(attribute.type))
      .map(attribute => ({ kind: 'entry', index: entry.index, attributeIndex: attribute.index })));
    assert(rgbTargets.length <= 512, 'RGB attribute inspection exceeded the bounded candidate count.');
    for (const target of rgbTargets) {
      const inspected = await inspect(target);
      if ((inspected.colorGroups ?? []).some(group => group.editable === true && group.storage === 'rgba8')) {
        colorTarget = target; colorInspection = inspected; break;
      }
    }
    if (!groupZero && !colorInspection) await inspect(currentStatus.selectedTarget ?? { kind: 'document', index: 0 });
    evidence.readInspection = { groupZeroFound: !!groupZero, rgbCommonFound: !!colorInspection, target: colorTarget };
    if (!options.editRoundTrip) { evidence.passed = true; return evidence; }
    // Recheck just before the first mutation. Initial history must also be clean.
    const initialGuards = unsafeReasons(currentStatus, true);
    const originalRevision = revision(document), originalSource = document.source;
    currentStatus = await tool('status'); document = currentStatus.document;
    const guards = [...new Set([...initialGuards, ...unsafeReasons(currentStatus, true)])];
    if (guards.length) { evidence.skipped = guards; evidence.passed = true; return evidence; }
    if (!groupZero || !colorInspection) { evidence.skipped = ['both_group_zero_and_explicit_rgbcommon_color_required']; evidence.passed = true; return evidence; }
    assert(revision(document) === originalRevision && document.source === originalSource, 'The document changed during preflight; no mutation was sent.');
    assert(equal(groups(document), originalGroups), 'Groups changed before testing; no mutation was sent.');
    const startRevision = revision(document);
    colorInspection = await inspect(colorTarget);
    assert(revision(document) === startRevision, 'Document changed during the preflight inspection.');
    const colorGroup = colorInspection.colorGroups.find(group => group.editable === true && group.storage === 'rgba8');
    assert(colorGroup, 'The previously inspected explicit color is no longer available.');
    const oldValues = colorValues(colorGroup), oldFields = clone(colorInspection.fields), oldColors = clone(colorInspection.colorGroups);
    evidence.originalColor = { target: colorTarget, path: colorGroup.path, values: oldValues };

    const initialNotes = notes(document), initialEfxState = efxState(document);
    const priorNote = initialNotes.find(group => group.index === 0).note;
    const temporaryNote = '中文备注测试_' + randomUUID().replaceAll('-', '');
    evidence.originalNotes = clone(initialNotes);
    evidence.outstandingTestEdit = { operation: 'set_group_note', groupIndex: 0, originalNote: priorNote, requestedNote: temporaryNote };
    await tool('set_group_note', { index: 0, note: temporaryNote, expectedRevision: revision(document) });
    const noted = await inspect({ kind: 'group', index: 0 });
    assert(equal(efxState(document), initialEfxState), 'Setting a note changed native groups, EFX revision, dirty state, or Undo/Redo; no further writes were sent.');
    const expectedNotes = clone(initialNotes); const expectedGroup = expectedNotes.find(group => group.index === 0);
    expectedGroup.note = temporaryNote; expectedGroup.displayName = temporaryNote;
    const noteVerified = equal(notes(noted.document), expectedNotes);
    assert(notes(document).find(group => group.index === 0)?.note === temporaryNote, 'The target note changed or did not persist; refusing to overwrite another note during cleanup.');
    // Notes are outside EFX undo history. Restore the original note explicitly.
    await tool('set_group_note', { index: 0, note: priorNote, expectedRevision: revision(document) });
    const restoredNote = await inspect({ kind: 'group', index: 0 });
    assert(equal(efxState(document), initialEfxState) && equal(notes(restoredNote.document), initialNotes), 'Explicit note restoration did not preserve the exact original notes and native document state.');
    evidence.outstandingTestEdit = null;
    assert(noteVerified, 'Chinese note did not read back correctly. The original note was explicitly restored.');
    evidence.noteRoundTrip = { efxStateUnchanged: true, originalNotesRestored: true, undoUsed: false };
    evidence.checks.push('Chinese group note saved and explicitly restored; native names, members, EFX revision/dirty/history unchanged.');

    colorInspection = await inspect(colorTarget);
    assert(equal(colorInspection.fields, oldFields) && equal(colorInspection.colorGroups, oldColors), 'Color/other RGB fields changed during the group test; no color edit was sent.');
    const newValues = [...oldValues]; newValues[0] += newValues[0] === 255 ? -1 : 1;
    evidence.outstandingTestEdit = { operation: 'set_color', target: colorTarget, path: colorGroup.path, originalValues: oldValues, requestedValues: newValues };
    await tool('set_color', { target: colorTarget, path: colorGroup.path, values: newValues, expectedRevision: revision(document) });
    const colorRevision = revision(document);
    const changed = await inspect(colorTarget);
    assert(revision(document) === colorRevision, 'Document changed after color edit; refusing to undo an unrelated transaction.');
    const changedGroup = changed.colorGroups.find(group => group.path === colorGroup.path);
    const colorVerified = !!changedGroup && equal(colorValues(changedGroup), newValues);
    await tool('undo', { expectedRevision: revision(document) });
    const restoredColor = await inspect(colorTarget);
    assert(equal(restoredColor.fields, oldFields) && equal(restoredColor.colorGroups, oldColors), 'Undo did not restore all original RGBCommon fields and colors.');
    assert(equal(groups(restoredColor.document), originalGroups), 'Color transaction changed group names or ordered membership.');
    assert(document.dirty === false && document.canUndo === false, 'Round trip did not return to the clean document baseline.');
    evidence.outstandingTestEdit = null;
    assert(colorVerified, 'The RGBA write did not read back as requested. The known edit was undone.');
    evidence.checks.push('Explicit RGBA red channel changed by one raw byte and undone; every RGBCommon field/color restored.');
    evidence.finalDocument = clone(document);
    evidence.canRedoResidual = document.canRedo === true;
    evidence.redoNote = 'The final undone color test may remain in Redo. It was not saved or applied. Existing history was required to be empty.';
    evidence.finalSelectedTarget = colorTarget;
    evidence.passed = true;
    return evidence;
  } catch (error) {
    evidence.error = { message: error.message, uncertain: error.uncertain === true, requestId: error.requestId };
    evidence.recovery = 'No automatic replay, save, apply, restore, scene, or capture command was sent. If outstandingTestEdit is present, inspect the in-game editor first. A group note must be restored by setting its originalNote, never Undo; a known EFX parameter edit may be undone after checking current history. A pending AI request must finish before another AI request can be sent.';
    throw error;
  } finally {
    evidence.finishedAt = new Date().toISOString();
    await client.close(); evidence.stderr = client.stderr;
    await writeFile(path.join(outputDirectory, 'mcp-live-evidence.json'), JSON.stringify(evidence, null, 2), 'utf8');
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2), options = {};
  for (let i = 0; i < args.length; i++) {
    const name = args[i];
    if (name === '--edit-round-trip') options.editRoundTrip = true;
    else if (['--mcp-path', '--game-root', '--output-directory'].includes(name) && args[i + 1]) {
      options[({ '--mcp-path': 'mcpPath', '--game-root': 'gameRoot', '--output-directory': 'outputDirectory' })[name]] = args[++i];
    } else throw new Error('Usage: node Test-McpLive.mjs --mcp-path <exe|dll> --game-root <directory> --output-directory <evidence directory> [--edit-round-trip]');
  }
  if (!options.mcpPath || !options.gameRoot || !options.outputDirectory) throw new Error('mcp-path, game-root and output-directory are required.');
  try {
    const result = await runLiveTest(options);
    console.log(`${result.skipped ? 'READ-ONLY / SKIPPED EDIT' : 'PASS'}: ${path.join(path.resolve(options.outputDirectory), 'mcp-live-evidence.json')}`);
    if (result.skipped) console.log(result.skipped.join(', '));
    if (result.canRedoResidual) console.log(result.redoNote);
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
