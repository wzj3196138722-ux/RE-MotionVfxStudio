# Re4Native render-host protocol v2 (P0/P1)

Start `python -I -X utf8 -u re4host.py --exe <re4.exe>`. stdin/stdout are UTF-8 JSON lines; stderr is reserved for logs. Every command can carry `id`, echoed in the reply. Success: `{id, ok:true, ...}`. Error: `{id, ok:false, fatal, error:{kind,message,entry,rva?,backtrace?}}`. After a fatal error the worker exits and its image must never be reused. `load` errors are fatal. Unknown / invalid commands are nonfatal. A non-native error while `frame` advances the emitters rewinds the worker to the load snapshot (the next frame replays from tick 0) and is reported as nonfatal `RecoveredError`; if the rewind itself fails the error is fatal. The host assigns the worker to a kill-on-close job object, so the worker ends with the host even while it is busy.

One command is in flight at a time. The client owns clock and slot selection.

| Command | Request | Response fields |
|---|---|---|
| load | `fixture:"bb"/"bb2"` or `entries:[{path,offset,drop_types?}]` (offset is a byte offset), optional `uvs_resources:{path:[frameCount,...]}`, `global_seed:[u32,u32,u32,u32]`; fixture mode also accepts `drop_types` | `entries`, `slot_bytes`, `mapping_bytes`, `input_offset`, `track_capacity`, `snapshot_bytes`, `load_ms` |
| bind | `name` of mapping already created by client, size from load | `bound:true` |
| track | `length`, `matrices:false/true` after writing shared input | `length`; resets simulation to loaded state |
| frame | `tick` in 0..track length, `slot` in 0..2, optional `diagnostics:true` | `tick,slot,sim_ms,restore_ms,total_ms,hashes,alive`; last two arrays populated only with diagnostics |
| shutdown | none | exits after successful response |
| test_fault | `exit:false/true`, only with `--allow-test-faults` | native fault response then exit, or immediate exit code 73 |

`load` is once per worker. To load new bytes, restart worker. Fixed default track: 3600 float32 values of 1/60. Maximum track length: 36000. Ticks are the number of completed native updates; tick 0 is the loaded, play-requested state. Backward seeks restore and replay the exact float32 track. World matrix tracks are service-level plumbing; P0 UI uses identity. New documents currently rebuild all entries.

All binary fields are little endian. Raw block copies may contain opaque pointer values; the client never dereferences them. Offsets inside frame entries are relative to their slot start. `type` in v2 is the EFX **attribute** type ID, not ephemeral ctx[0].

## Mapping header (256 bytes)

| Offset | Field |
|---|---|
| 0 | 4-byte magic R4FX |
| 4 | u32 protocol version 2 |
| 8 | u32 exe timestamp 0x69A8F652 |
| 12 | u32 slot count 3 |
| 16 | u32 slot bytes |
| 20 | u32 input offset |
| 24 | u32 track capacity |
| 28 | u32 complete mapping bytes |
| 32..255 | reserved zero |

Slots immediately follow header; slot i starts at `256+i*slot_bytes`.

## Frame slot

Header: tick u32 at +0, entry count u32 at +4, elapsed seconds f64 at +8, last dt f32 at +16, reserved zero through +63. Entry table begins +64, stride 128. Data follows table, with slot padded to 64 bytes.

Each entry contains 16 u32 fields (64 bytes):

`entry index, attribute type, capacity, particle stride (320), side stride, E+0x100, E+0x168, E+0x16c, E+0x170, E+0x174, particle offset, particle bytes, side offset, side bytes, TypeBillboard3D block offset, block bytes (0x2f8)`.

Then 16 f32 values (64 bytes): row-major emitter world matrix, translation in row 4. Entry index is the file's stored entry index; it is not inferred from fixture comments or offset order.

Particle bytes include all capacity slots, including empty ones. Side bytes include all capacity slots at E+0x148 stride. Raw data are copied with memmove directly into the mapping. P1 additionally exports the complete 0x2f8-byte Billboard3D type block every frame. Strip buffers, other attribute blocks, ctx[0] and n/N remain unexported.

Input begins at `input_offset`: dt f32[track_capacity], followed by row-major matrices f32[track_capacity][16]. Write all input before sending `track`. Unused matrix records are ignored when `matrices:false`.

## Ownership

The worker never reads a clock or camera. A frame reply publishes the completed slot. The client must finish reading before reusing that slot. Current C# client copies the slot into an owned frame before releasing its command gate; rendering can overlap the next worker update without keeping a mapped pointer. This sequential protocol does not implement free-running worker writes or require lock-free fences.

## P1 load and ownership extensions

Each explicit entry may include `stress_particles:0..10000`. Nonzero stress overrides Spawn while its loader is running, before later loaders size their tables, and sets Life KeepFrame to 1000. It does not write the source file. Omit for ordinary preview.

`uvs_resources` is keyed by the UVS internal path; keys are normalized to lowercase with forward slashes. Counts must be 1..256 per sequence, with 1..256 sequences. Supply before load; existing experiment fixtures retain their synthetic shapes when no real resource table is supplied.

C# frames now own pooled packet and particle buffers and implement IDisposable. Use `using` for test frames. The UI disposes its previous frame after replacing it, so worker writes cannot invalidate the rendered frame. ReadOnlyMemory slices into a frame become invalid for further use after Dispose; no native/shared-memory pointer is retained by the renderer.

## P2a editor publication (separate mailbox protocol)

The editor host retains its JSON-file mailbox protocol v1; native frame transport remains v2. `configure_offline_preview` selects `entryId` (must be unique) or positional `entryPosition` at the checked document revision. Successful changes automatically publish a full, validated immutable EFX snapshot plus `offline-preview/preview.json`. The manifest contains schema 1, documentId, revision, path, sha256, entryPosition, entryId, offset and restart:true. Offset comes from the serialized/reopened document and is not reused from the original file.

Responses expose `offlinePreview`, `offlinePreviewParameters`, and optionally `offlinePreviewError`. Publication errors do not reverse a successful document transaction. `publish_offline_preview` republishes without editing or consuming history. Existing set_fields, undo, redo and save remain authoritative. The preview app owns a private offline host with no game-root argument.

Native reloads are sequential. A newer desired document invalidates older results; the displayed revision changes only after the requested native frame succeeds. Failed resource/hash/load validation preserves the last displayed renderer/frame. See the P2a usage document for limits and recovery.
