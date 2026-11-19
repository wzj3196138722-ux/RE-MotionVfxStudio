# Re4Native render-host protocol v3

Start `python -I -X utf8 -u re4host.py --exe <re4.exe>`. stdin/stdout are UTF-8 JSON lines; stderr is reserved for logs. Every command can carry `id`, echoed in the reply. Success: `{id, ok:true, ...}`. Error: `{id, ok:false, fatal, error:{kind,message,entry,rva?,backtrace?}}`. After a fatal error the worker exits and its image must never be reused. `load` errors are fatal. Unknown / invalid commands are nonfatal. A non-native error while `frame` advances the emitters rewinds the worker to the load snapshot (the next frame replays from tick 0) and is reported as nonfatal `RecoveredError`; if the rewind itself fails the error is fatal. The host assigns the worker to a kill-on-close job object, so the worker ends with the host even while it is busy.

One command is in flight at a time. The client owns clock and slot selection.

| Command | Request | Response fields |
|---|---|---|
| load | `fixture:"bb"/"bb2"` or `entries:[{path,offset,drop_types?}]` (offset is a byte offset), optional `uvs_resources:{path:[frameCount,...]}`, `global_seed:[u32,u32,u32,u32]`; fixture mode also accepts `drop_types` | `entries`, `slot_bytes`, `mapping_bytes`, `input_offset`, `track_capacity`, `snapshot_bytes`, `load_ms`, `reused`, `base_restore_ms`, `skipped` |
| bind | `name` of mapping already created by client, size from load | `bound:true` |
| track | `length`, `matrices:false/true` after writing shared input | `length`; resets simulation to loaded state |
| frame | `tick` in 0..track length, `slot` in 0..2, optional `diagnostics:true` | `tick,slot,sim_ms,restore_ms,total_ms,hashes,alive`; last two arrays populated only with diagnostics |
| shutdown | none | exits after successful response |
| test_fault | `exit:false/true`, only with `--allow-test-faults` | native fault response then exit, or immediate exit code 73 |

Child Actions (P6): an entry spec may carry `logic:true` (a TypeNoDraw 75 entry, optionally with 76, simulated but not drawn) and `triggers:[{type:131, action}]` (its PtLife fires that Action); the load command's `actions:[{index, entries:[{path, offset}]}]` lists the embedded entries of each Action (absolute offsets inside the file). Each (Action, embedded entry) pair becomes one frame entry after the top-level ones, in `actions` order, holding the particles of all running instances (capacity = MaxParticles × instance slots; up to 32 slots per trigger) with an identity matrix; its load metadata adds `action`, `child`, `instances`, `per_instance`. PtLife's Action step 3d3e0a0 is a no-op: the host starts a slot's child emitters when the engine marks it pending (flag 2), follows the slot matrix every tick and frees the slot when every child has spawned its last batch (particle +4 bit 0x20000) and has no live particle. Child emitters built after the load snapshot are dropped on every restore. Embedded entries that cannot be built are listed in `skipped` with `action` and `child` instead of a usable `entry`.

Each entry must have exactly one drawable Type: Billboard2D (21, optionally with 22), Billboard3D (23, optionally with 24), Polygon (63, optionally with 64/65) RibbonFollow (31) or RibbonLength (32, optionally with 58); the two ribbon types need `ribbon_points` in their spec. The frame entry's `attribute type` field carries that id and the exported block is that Type's block (0x2f8 bytes copied; Polygon3DConstant at +0x54 for Billboard3D, +0x58 for Polygon; Billboard2D has no such layout). An entry whose build fails at the Python level (an attribute without a native factory, an unsupported Type, a bad value) is left out and listed in `skipped:[{entry, kind, message}]`, where `entry` is its position in the request; the frame entry table then has only the built entries, in request order. A native fault while building is still fatal. The load fails only when no entry could be built.

`load` may be repeated. The first load maps the exe, installs the runtime, builds the factory table and records a base snapshot (no emitter). A later load restores that base snapshot (writable sections, arena, kept ctypes buffers, resource cache), resets the UVS tables and the global generator (zero, or `global_seed`), then parses and builds the new entries and takes a new load snapshot; the reply has `reused:true`. Each load closes the previous shared mapping: the client creates and binds a new one before the next `track` or `frame`. A load error is still fatal; the client then retries once in a new worker. Fixed default track: 3600 float32 values of 1/60. Maximum track length: 36000. Ticks are the number of completed native updates; tick 0 is the loaded, play-requested state. Backward seeks restore and replay the exact float32 track. World matrix tracks are service-level plumbing; P0 UI uses identity. New documents currently rebuild all entries.

All binary fields are little endian. Raw block copies may contain opaque pointer values; the client never dereferences them. Offsets inside frame entries are relative to their slot start. `type` in v2 is the EFX **attribute** type ID, not ephemeral ctx[0].

## Mapping header (256 bytes)

| Offset | Field |
|---|---|
| 0 | 4-byte magic R4FX |
| 4 | u32 protocol version 3 |
| 8 | u32 exe timestamp 0x69A8F652 |
| 12 | u32 slot count 3 |
| 16 | u32 slot bytes |
| 20 | u32 input offset |
| 24 | u32 track capacity |
| 28 | u32 complete mapping bytes |
| 32..255 | reserved zero |

Slots immediately follow header; slot i starts at `256+i*slot_bytes`.

## Frame slot

Header: tick u32 at +0, entry count u32 at +4, elapsed seconds f64 at +8, last dt f32 at +16, reserved zero through +63. Entry table begins +64, stride 160. Data follows table, with slot padded to 64 bytes.

Each entry contains 16 u32 fields (64 bytes):

`entry index, attribute type, capacity, particle stride (320), side stride, E+0x100, E+0x168, E+0x16c, E+0x170, E+0x174, particle offset, particle bytes, side offset, side bytes, TypeBillboard3D block offset, block bytes (0x2f8)`.

Then 16 f32 values (64 bytes): row-major world matrix of the track, translation in row 4. Particle world positions use each particle's reference matrix P at +0x100 instead (world = +0x28 · P); this matrix is the fallback for records without a valid P. Entry index is the file's stored entry index; it is not inferred from fixture comments or offset order.

Then 8 u32 (32 bytes, v3): ribbon point pool offset, bytes, point stride (0x54), points per particle, state offset, bytes, state stride (0x68), reserved. All zero for entries without ribbon data. For RibbonFollow (31) and RibbonLength (32) the pool is a copy of type block +0x38 (capacity × points-per-particle records: +0x00 world position, +0x18 tangent, +0x40 V, +0x44 RGBA8, +0x48 size factor) and the states a copy of type block +0x40 (capacity × 0x68; +0x18 valid points). Points per particle come from the load spec's `ribbon_points` (RibbonFollow: (ShapeDivision−1)·SplineDivision + ShapeDivision; RibbonLength and RibbonFixEnd: ShapeDivision; PolygonTrail: (NumTrailDivision+1)·NumSplineDivision + NumTrailDivision + 2), because the state records are filled only on the first update. RibbonFixEnd (34) and PolygonTrail (70) are re-packed into the same layout: FixEnd's 0x70-byte states (+0x08 point pointer, +0x18 count) become 0x68-byte records with the count at +0x18; PolygonTrail exports one record per column of the engine's vertex grid (type block +0x308): +0x00 base vertex, +0x18 tip vertex, +0x40 U, +0x44 RGBA8, +0x48 1.0, and the state's +0x18 holds the live column count (the first column whose U is 1.0, plus one).

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
