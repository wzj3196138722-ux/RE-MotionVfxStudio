import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { runLiveTest, StdioClient } from './Test-McpLive.mjs';
const here = path.dirname(fileURLToPath(import.meta.url));
const output = path.join(here, 'test-output', 'mcp-live-offline-' + Date.now());
await mkdir(output, { recursive: true });
let checks = 0;
const check = (value, label) => { assert.ok(value, label); checks++; };
const mutations = report => report.commands.filter(item => item.direction === 'request' &&
  ['motionvfx_set_group_note', 'motionvfx_set_color', 'motionvfx_undo'].includes(item.payload.params?.name));
async function run(scenario, editRoundTrip = false, expectFailure = false) {
  const directory = path.join(output, scenario + (editRoundTrip ? '-edit' : '-read'));
  let error;
  try {
    await runLiveTest({ outputDirectory: directory, editRoundTrip }, record => new StdioClient(
      process.execPath, [path.join(here, 'fixtures', 'mock-mcp-live.mjs'), scenario], record, 5000));
  } catch (failure) { error = failure; }
  check(!!error === expectFailure, scenario + ' outcome');
  const report = JSON.parse(await readFile(path.join(directory, 'mcp-live-evidence.json'), 'utf8'));
  check(report.transport === 'offline_mock_stdio', 'Mock evidence cannot be mistaken for a live test.');
  check(!report.commands.some(item => /motionvfx_(apply|restore|capture_start|capture_stop|scene_time|save_copy)/.test(item.payload.params?.name)), 'No forbidden command was emitted.');
  return report;
}
const read = await run('clean');
check(mutations(read).length === 0 && read.readInspection.rgbCommonFound, 'Default run only reads and leaves the RGB attribute inspected.');
const edit = await run('clean', true);
check(mutations(edit).map(item => item.payload.params.name).join(',') === 'motionvfx_set_group_note,motionvfx_set_group_note,motionvfx_set_color,motionvfx_undo', 'Notes are explicitly restored; only the color edit uses Undo.');
check(edit.checks.length === 2 && edit.canRedoResidual && edit.outstandingTestEdit === null, 'Both round trips verified and Redo residue reported.');
check(JSON.stringify(edit.finalDocument.groups.map(({ index, name, entries }) => ({ index, name, entries }))) === JSON.stringify(edit.originalGroups), 'Native group names and ordered repeated members remain identical.');
check(edit.noteRoundTrip.efxStateUnchanged && edit.noteRoundTrip.undoUsed === false && edit.noteRoundTrip.originalNotesRestored, 'Note changes leave EFX revision/dirty/history untouched.');
check(mutations(edit)[0].payload.params.arguments.note.startsWith('中文备注测试_') && mutations(edit)[1].payload.params.arguments.note === '', 'Chinese note is tested and empty original note is explicitly cleared.');
check(edit.finalDocument.dirty === false && edit.finalDocument.canUndo === false, 'Final document returned to clean baseline.');
check(edit.commands.filter(item => item.direction === 'request').at(-1).payload.params.name === 'motionvfx_inspect', 'Final operation leaves the color panel selected.');
for (const scenario of ['dirty', 'history', 'redo', 'drafts', 'busy', 'applied', 'no-document']) {
  const blocked = await run(scenario, true);
  check(mutations(blocked).length === 0 && blocked.skipped.length > 0, scenario + ' must remain read-only.');
}
const text = await run('text-only');
check(text.passed && text.readInspection.rgbCommonFound, 'MCP text content parses when structured content is absent.');
for (const scenario of ['bad-json', 'wrong-id']) {
  const failed = await run(scenario, false, true);
  check(!failed.passed && mutations(failed).length === 0, scenario + ' fails before any edit.');
}
const existing = await run('existing-note', true);
check(existing.originalNotes[0].note === '原有中文备注' && existing.finalDocument.groups[0].note === '原有中文备注', 'Existing duplicate Chinese note is preserved and restored.');
const changedRevision = await run('bad-note-revision', true, true);
check(mutations(changedRevision).length === 1 && changedRevision.outstandingTestEdit.operation === 'set_group_note', 'Unexpected EFX revision mutation aborts without unrelated Undo.');
console.log(`PASS: ${checks} live-test harness checks; separate mock stdio processes only. Evidence: ${output}`);
