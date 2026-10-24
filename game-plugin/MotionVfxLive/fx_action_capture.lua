-- Observes the game's real local-character effect requests. Never plays effects,
-- changes arguments, installs a replacement resource, or calls engine setters.
-- A MotionNode is attributed only through a live same-thread request scope.
if _G.MotionVfxActionCapture then return _G.MotionVfxActionCapture end
local M = { version = "1.4.2", recording = false }
local limits = { events = 500, resources = 2048, bindings = 4096, pending = 256,
    collection = 256, scans = 8, sourceFrames = 600, layers = 32, nodes = 128, players = 64, paths = 1024,
    resourceSources = 1024, actions = 256, actionLayers = 8, actionLinks = 16 }
M.limits = limits
local counters = { calls = 0, captured = 0, remoteIgnored = 0, unknownOwners = 0,
    errors = 0, evictedEvents = 0, resourceOverflow = 0, bindingOverflow = 0,
    pendingOverflow = 0, collectionTruncated = 0, pendingTimeouts = 0,
    contextMismatch = 0, ownerMismatch = 0, sceneChanges = 0, droppedSources = 0,
    playerOverflow = 0, pathOverflow = 0, mergedContainerObservations = 0, aliasOverflow = 0,
    collectionReadErrors = 0, providerReadErrors = 0, motionReadErrors = 0,
    emptyUnknownCalls = 0, suppressedUnknownEvents = 0,
    shellOwnerUnresolved = 0, shellOwnerCycles = 0, shellOwnerDepthExceeded = 0, sourceOwnershipEvictions = 0,
    resourceSourceEvictions = 0, resourceSourceReadErrors = 0, resourceSourceRevocations = 0,
    actionEvictions = 0, actionReadErrors = 0, actionLinkOverflow = 0, actionLoopWraps = 0 }
local errors, error_seen, events, resources, resource_map = {}, {}, {}, {}, {}
local bindings, binding_map, pending, pending_map = {}, {}, {}, {}
local container_events, container_sources, source_keys = {}, {}, {}
local aliases, stacks, installed, attempted = {}, {}, {}, false
-- Native resources are never dereferenced here. These are GC-tracked managed
-- holders obtained directly from the observed EffectPlayer, not recreated by path.
local actual_sources, source_order, source_sequence = {}, {}, 0
local alias_count = 0
local sequence, frame, scene_epoch, scan_cursor, installed_count, generation = 0, 0, 1, 1, 0, 1
local native_hook_count = 0
local current_scene, actor, actor_refs, active = nil, {available = false, sceneEpoch = 1}, {}, true
local specs = {}
local action_history, action_slots, current_actions, action_sequence = {}, {}, {}, 0
local action_context, action_status = nil, "尚未观察角色动画"
local function copy(value)
    if type(value) ~= "table" then return value end
    local out = {}; for key, item in pairs(value) do out[key] = copy(item) end; return out
end
local function note(message)
    counters.errors = counters.errors + 1
    message = tostring(message)
    if not error_seen[message] and #errors < 100 then
        errors[#errors + 1], error_seen[message] = message, true
    end
end
local function safe(fn, ...)
    local ok, value = pcall(fn, ...)
    if ok then return value end
    return nil
end
local function call(obj, name, ...)
    if not obj then return nil end
    local args = table.pack(...)
    return safe(function() return obj:call(name, table.unpack(args, 1, args.n)) end)
end
local function field(obj, name) return obj and safe(function() return obj:get_field(name) end) end
local function identity(obj)
    if not obj then return nil end
    local address = safe(function() return obj:get_address() end)
    return address and tostring(address) or nil
end
local function is_a(obj, name)
    local td = obj and safe(function() return obj:get_type_definition() end)
    for _ = 1, 12 do
        if not td then return false end
        if safe(function() return td:get_full_name() end) == name then return true end
        td = safe(function() return td:get_parent_type() end)
    end
    return false
end
local function managed(value, expected)
    local obj = value and safe(sdk.to_managed_object, value)
    if not obj or (expected and not is_a(obj, expected)) then return nil end
    return obj
end
local function uint(value)
    if value == nil then return nil end
    return safe(function() return sdk.to_int64(value) & 0xFFFFFFFF end)
end
local function scalar(obj, getter)
    local value = call(obj, getter)
    if type(value) == "string" or type(value) == "boolean" then return value end
    if type(value) == "number" and value == value and math.abs(value) ~= math.huge then return value end
end
local function raw_canonical(path)
    if type(path) ~= "string" then return nil end
    local normalized = path:gsub("^%s+", ""):gsub("%s+$", ""):gsub("^@+", "")
        :gsub("\\", "/"):gsub("/+", "/"):gsub("^/+", ""):lower()
    normalized = normalized:gsub("^natives/stm/", ""):gsub("^stm/", "")
    normalized = normalized:gsub("%.efx%.%d+$", ".efx")
    if not normalized:match("%.efx$") then return nil end
    return normalized
end
local function canonical(path)
    local key = raw_canonical(path)
    local seen = {}
    while key and aliases[key] and not seen[key] do seen[key] = true; key = aliases[key] end
    return key
end
local function game_object(obj) return call(obj, "get_GameObject") end
local function local_owner(go)
    local id = identity(go)
    if id and (id == actor.gameObjectIdentity or id == actor.weaponIdentity) then return "local", id end
    if not go then return "unknown", nil end
    -- An explicit different hunter is remote. A projectile or environmental
    -- target is merely unknown; a follow target must not override its initiator.
    local other = call(go, "getComponent(System.Type)", sdk.typeof("app.HunterCharacter"))
    if other and identity(other) ~= actor.characterIdentity then return "remote", id end
    return "unknown", id
end
local function shell_context(owner)
    -- Bowgun effects use a shared WpGunShellOEM and hit effects use the global
    -- Effect controller. Neither controller identifies the shooter. Read the
    -- typed source shell while its effect method is executing instead.
    local shell = field(owner, "<Shell>k__BackingField")
    local context = {ownership = "unknown", ownerEvidence = "shell_owner_unresolved",
        sourceKind = "shell", sourceShellIdentity = identity(shell), isInitiator = true}
    local seen = {}
    for depth = 1, 8 do
        if not is_a(shell, "ace.ShellBase") then break end
        local shell_id = identity(shell)
        if not shell_id or seen[shell_id] then counters.shellOwnerCycles = counters.shellOwnerCycles + 1; break end
        seen[shell_id] = true
        local shooter = call(shell, "get_ShellOwner")
        if not is_a(shooter, "via.GameObject") then break end
        local ownership, id = local_owner(shooter)
        context.ownerIdentity, context.sourceOwnerIdentity = id, id
        context.shellOwnerDepth = depth
        if ownership ~= "unknown" then
            context.ownership, context.ownerEvidence = ownership, "shell_owner"
            return context
        end
        -- A child shell may name its parent shell's object as its owner. Only
        -- follow another typed ShellBase; never a transform parent or target.
        shell = call(shooter, "getComponent(System.Type)", sdk.typeof("ace.ShellBase"))
        if depth == 8 and is_a(shell, "ace.ShellBase") then
            counters.shellOwnerDepthExceeded = counters.shellOwnerDepthExceeded + 1
        end
    end
    counters.shellOwnerUnresolved = counters.shellOwnerUnresolved + 1
    return context
end
local function refresh_actor()
    local manager = safe(sdk.get_managed_singleton, "app.PlayerManager")
    local master = call(manager, "getMasterPlayer")
    local character = call(master, "get_Character")
    local go, weapon = game_object(character), call(character, "get_Weapon")
    local weapon_go = game_object(weapon)
    actor_refs = {character = character, object = go, weapon = weapon, weaponObject = weapon_go}
    actor = {available = character ~= nil and go ~= nil, sceneEpoch = scene_epoch,
        characterIdentity = identity(character), gameObjectIdentity = identity(go), weaponIdentity = identity(weapon_go),
        weaponType = scalar(character, "get_WeaponType"), weaponId = scalar(character, "get_WeaponID")}
    return actor.available
end
local function retain(obj)
    if not obj then return nil end
    local ok = pcall(function() obj:add_ref() end)
    if not ok then counters.droppedSources = counters.droppedSources + 1; return nil end
    -- Lua's managed userdata ref is shared; don't release once per observation.
    return obj
end
local function source_available(record)
    return record and record.holder and record.event._retained and not record.event._excluded
        and record.event.localConfirmed and record.event.sceneEpoch == scene_epoch
        and record.descriptor.availability == "retained_observation"
end
local function refresh_latest_source(key)
    local aggregate = resource_map[key]
    if not aggregate then return end
    aggregate.latestSource = nil
    for index = #source_order, 1, -1 do
        local record = actual_sources[source_order[index]]
        if source_available(record) and canonical(record.descriptor.actualPath) == key then
            aggregate.latestSource = copy(record.descriptor)
            break
        end
    end
end
local function discard_source(id, reason)
    local record = actual_sources[id]
    if not record then return end
    record.descriptor.availability, record.descriptor.invalidReason = "unavailable", reason
    record.descriptor.localConfirmed = record.event.localConfirmed == true
    -- add_ref on a managed userdata is GC-tracked by REFramework; release the
    -- Lua reference only. force_release/native release would over-release it.
    record.holder, actual_sources[id] = nil, nil
    for index = #source_order, 1, -1 do
        if source_order[index] == id then table.remove(source_order, index); break end
    end
    refresh_latest_source(canonical(record.descriptor.actualPath))
end
local function discard_event_sources(ev, reason)
    for _, id in ipairs(ev._source_ids or {}) do discard_source(id, reason) end
    ev._source_ids = {}
end
local function retain_actual_source(ev, player_id, holder, path)
    if not ev._retained or ev._excluded or not holder then return end
    if limits.resourceSources < 1 then return end
    while #source_order >= limits.resourceSources do
        local victim = source_order[1]
        for _, id in ipairs(source_order) do
            local record = actual_sources[id]
            if record and not record.event.localConfirmed then victim = id; break end
        end
        discard_source(victim, "source_limit")
        counters.resourceSourceEvictions = counters.resourceSourceEvictions + 1
    end
    local held = retain(holder)
    local holder_id = held and identity(held)
    if not holder_id then counters.resourceSourceReadErrors = counters.resourceSourceReadErrors + 1; return end
    source_sequence = source_sequence + 1
    local descriptor = {sourceId = "resource-source-" .. scene_epoch .. "-" .. source_sequence,
        eventId = ev.id, sceneEpoch = ev.sceneEpoch, actualPath = path,
        actualHolderIdentity = holder_id, holderIdentityMeaning = "managed_wrapper_not_native_resource_hash",
        playerIdentity = player_id, observedFrame = frame, sharedTargetPath = canonical(path),
        sourceKind = "actual_effect_player_holder", availability = "retained_observation",
        localConfirmed = ev.localConfirmed == true, currentPlayerVerified = false, cacheBytesAvailable = false}
    actual_sources[descriptor.sourceId] = {descriptor = descriptor, holder = held, event = ev}
    source_order[#source_order + 1] = descriptor.sourceId
    ev._source_ids[#ev._source_ids + 1] = descriptor.sourceId
    ev.actualSources[#ev.actualSources + 1] = descriptor
    refresh_latest_source(canonical(path))
    return descriptor.sourceId
end
local function merge_actual_sources(destination, source)
    local accepted, player_count = {}, #destination.playerIdentities
    for player_id, observation in pairs(source._observed_players) do
        if not destination._observed_players[player_id] and player_count < limits.players then
            destination._observed_players[player_id] = observation
            if observation.sourceId then accepted[observation.sourceId] = true end
            player_count = player_count + 1
        end
    end
    for _, descriptor in ipairs(source.actualSources) do
        local record = actual_sources[descriptor.sourceId]
        if record then
            if not accepted[descriptor.sourceId] then
                discard_source(descriptor.sourceId, "duplicate_or_excess_event_player")
            elseif descriptor.localConfirmed and not destination.localConfirmed then
                discard_source(descriptor.sourceId, "ownership_revoked")
            else
                record.event = destination
                descriptor.eventId, descriptor.localConfirmed = destination.id, destination.localConfirmed == true
                destination._source_ids[#destination._source_ids + 1] = descriptor.sourceId
                destination.actualSources[#destination.actualSources + 1] = descriptor
                refresh_latest_source(canonical(descriptor.actualPath))
            end
        end
    end
    source._source_ids = {}
end
local function each(collection, fn)
    if not collection then return end
    -- Some SDK helpers return Lua arrays instead of managed wrappers. Accept
    -- only a verified contiguous zero/one-based sequence, never # on a sparse
    -- table or an arbitrary dictionary that happens to contain numeric keys.
    if type(collection) == "table" and not collection.call and not collection.get_type_definition and
        not collection.get_address and not collection.get_size and not collection.get_element then
        local count = 0
        for key in pairs(collection) do
            if type(key) ~= "number" or key ~= math.floor(key) or key < 0 or key == math.huge then
                counters.collectionReadErrors = counters.collectionReadErrors + 1; return
            end
            count = count + 1
        end
        local base = rawget(collection, 0) ~= nil and 0 or 1
        for index = base, base + count - 1 do
            if rawget(collection, index) == nil then counters.collectionReadErrors = counters.collectionReadErrors + 1; return end
        end
        if count > limits.collection then counters.collectionTruncated = counters.collectionTruncated + count - limits.collection end
        for index = 0, math.min(count, limits.collection) - 1 do
            local value = rawget(collection, base + index)
            if value then fn(value, index) end
        end
        return
    end
    local count = tonumber(call(collection, "get_Count") or call(collection, "get_Length") or
        safe(function() return collection:get_size() end))
    if not count or count ~= count or math.abs(count) == math.huge or count < 0 or count ~= math.floor(count) then
        counters.collectionReadErrors = counters.collectionReadErrors + 1; return
    end
    if count > limits.collection then counters.collectionTruncated = counters.collectionTruncated + count - limits.collection end
    for index = 0, math.min(count, limits.collection) - 1 do
        local value, readable
        -- A generic REManagedObject wrapping System.Array has no get_element.
        -- A successful nil result is a real null entry, not a read failure.
        for getter = 1, 3 do
            local ok, result = pcall(function()
                if getter == 1 then return collection:call("get_Item", index) end
                if getter == 2 then return collection:get_element(index) end
                return collection:call("GetValue(System.Int32)", index)
            end)
            if ok then
                readable = true
                if result ~= nil then value = result; break end
            end
        end
        if not readable then counters.collectionReadErrors = counters.collectionReadErrors + 1 end
        if value then fn(value, index) end
    end
end
local function motion_snapshot(node, motion)
    if not node then return nil end
    local out = {nodeIdentity = identity(node), componentIdentity = identity(motion),
        bankId = scalar(node, "get_MotionBankID"), bankType = scalar(node, "get_MotionBankType"),
        motionId = scalar(node, "get_MotionID"), name = scalar(node, "get_MotionName"),
        frame = scalar(node, "get_MotionFrame"), previousFrame = scalar(node, "get_MotionPrevFrame"),
        weight = scalar(node, "get_Weight"), resourceLongId = nil}
    local long_id = call(node, "get_MotionResourceDataLongId")
    if out.motionId == nil or out.bankId == nil then counters.motionReadErrors = counters.motionReadErrors + 1 end
    if long_id ~= nil then out.resourceLongId = tostring(long_id) end
    local layer_count = tonumber(call(motion, "getLayerCount")) or 0
    if layer_count > limits.layers then counters.collectionTruncated = counters.collectionTruncated + layer_count - limits.layers end
    for layer_index = 0, math.min(layer_count, limits.layers) - 1 do
        local layer = call(motion, "getLayer(System.UInt32)", layer_index)
        local node_count = tonumber(call(layer, "getMotionNodeCount")) or 0
        if node_count > limits.nodes then counters.collectionTruncated = counters.collectionTruncated + node_count - limits.nodes end
        for node_index = 0, math.min(node_count, limits.nodes) - 1 do
            if identity(call(layer, "getMotionNode(System.UInt32)", node_index)) == out.nodeIdentity then
                out.layer = layer_index; break
            end
        end
        if out.layer ~= nil then break end
    end
    local bank = out.bankId and call(motion, "findMotionBank(System.UInt32)", out.bankId)
    local list = call(bank, "get_MotionList")
    out.bankResourcePath = scalar(list, "get_ResourcePath")
    return out
end
local function close_actions(reason)
    for _, slot in pairs(action_slots) do
        if slot.record then slot.record.closed, slot.record.endReason = true, reason end
    end
    action_slots, current_actions = {}, {}
end
local function motion_key(m, kind)
    -- Do not identify a clip by MotionID alone: banks, weapons and layers reuse IDs.
    return table.concat({tostring(actor.weaponType or "?"), kind, tostring(m.layer),
        tostring(m.bankResourcePath or ""), tostring(m.bankId), tostring(m.bankType or "?"), tostring(m.motionId)}, "|")
end
local function observe_actions()
    local context = table.concat({tostring(scene_epoch), tostring(actor.characterIdentity),
        tostring(actor.weaponIdentity), tostring(actor.weaponType), tostring(actor.weaponId)}, "|")
    if action_context ~= context then close_actions("actor_or_equipment_changed"); action_context = context end
    local seen, now = {}, {}
    for _, source in ipairs({{object = actor_refs.character, go = actor_refs.object, kind = "character"},
        {object = actor_refs.weapon, go = actor_refs.weaponObject, kind = "weapon"}}) do
        local motion = call(source.object, "get_MotionComponent")
            or call(source.go, "getComponent(System.Type)", sdk.typeof("via.motion.Motion"))
        local component_id = identity(motion)
        local count = tonumber(call(motion, "getLayerCount")) or 0
        for layer_index = 0, math.min(count, limits.actionLayers) - 1 do
            local layer = call(motion, "getLayer(System.UInt32)", layer_index)
            local m = {componentIdentity = component_id, layer = layer_index,
                bankId = scalar(layer, "get_MotionBankID"), motionId = scalar(layer, "get_MotionID"),
                frame = scalar(layer, "get_Frame")}
            if type(m.bankId) == "number" and type(m.motionId) == "number" and
                m.bankId >= 0 and m.bankId < 4294967295 and m.motionId >= 0 and m.motionId < 4294967295 then
                local slot_key = source.kind .. ":" .. tostring(component_id) .. ":" .. layer_index
                local slot = action_slots[slot_key]
                local changed = not slot or slot.motion.bankId ~= m.bankId or slot.motion.motionId ~= m.motionId
                -- Node/name/bank lookups run only on clip changes, not for every game frame.
                if changed then
                    local nodes = tonumber(call(layer, "getMotionNodeCount")) or 0
                    for index = 0, math.min(nodes, limits.nodes) - 1 do
                        local node = call(layer, "getMotionNode(System.UInt32)", index)
                        if scalar(node, "get_MotionID") == m.motionId and scalar(node, "get_MotionBankID") == m.bankId then
                            m.name, m.bankType = scalar(node, "get_MotionName"), scalar(node, "get_MotionBankType")
                            m.nodeIdentity = identity(node)
                            break
                        end
                    end
                    local bank = call(motion, "findMotionBank(System.UInt32)", m.bankId)
                    m.bankResourcePath = scalar(call(bank, "get_MotionList"), "get_ResourcePath")
                else
                    for _, key in ipairs({"name", "bankType", "nodeIdentity", "bankResourcePath"}) do m[key] = slot.motion[key] end
                end
                local rewound = slot and type(m.frame) == "number" and type(slot.motion.frame) == "number"
                    and m.frame + 1 < slot.motion.frame
                -- Short weapon-gimmick loops otherwise evict hundreds of real
                -- hunter actions. Only explicitly named Loop clips coalesce;
                -- repeating a non-loop attack remains a separate occurrence.
                local loop_wrap = not changed and rewound and type(m.name) == "string" and m.name:lower():match("_loop$") ~= nil
                local reason = changed and "clip_changed" or rewound and "frame_rewound" or "recording_started"
                if changed or (rewound and not loop_wrap) or (M.recording and not slot.record) then
                    if slot and slot.record then slot.record.closed, slot.record.endReason = true, reason end
                    slot = {motion = m}
                    action_slots[slot_key] = slot
                    if M.recording then
                        action_sequence = action_sequence + 1
                        slot.record = {id = "motion-" .. scene_epoch .. "-" .. action_sequence,
                            key = motion_key(m, source.kind), sceneEpoch = scene_epoch, actorIdentity = actor.characterIdentity,
                            weaponType = actor.weaponType, weaponId = actor.weaponId, componentKind = source.kind,
                            motion = copy(m), firstObservedFrame = frame, lastObservedFrame = frame,
                            firstMotionFrame = m.frame, lastMotionFrame = m.frame, reason = reason, closed = false,
                            loopWraps = 0, loopDetection = "name_suffix_only"}
                        if #action_history >= limits.actions then
                            local victim
                            for i, old in ipairs(action_history) do
                                if old.closed then
                                    victim = victim or i
                                    if old.componentKind ~= "character" or old.motion.layer ~= 0 then victim = i; break end
                                end
                            end
                            table.remove(action_history, victim or 1); counters.actionEvictions = counters.actionEvictions + 1
                        end
                        action_history[#action_history + 1] = slot.record
                    end
                end
                slot.motion = m
                if slot.record and M.recording then
                    slot.record.lastObservedFrame, slot.record.lastMotionFrame = frame, m.frame
                    if loop_wrap then
                        slot.record.loopWraps = slot.record.loopWraps + 1
                        counters.actionLoopWraps = counters.actionLoopWraps + 1
                    end
                end
                seen[slot_key] = true
                now[#now + 1] = {key = motion_key(m, source.kind), recordId = slot.record and slot.record.id,
                    componentKind = source.kind, motion = copy(m), observedFrame = frame, sceneEpoch = scene_epoch}
            elseif m.bankId == nil or m.motionId == nil then
                -- UINT_MAX marks an inactive layer, not a failed engine read.
                counters.actionReadErrors = counters.actionReadErrors + 1
            end
        end
    end
    for key, slot in pairs(action_slots) do
        if not seen[key] then
            if slot.record then slot.record.closed, slot.record.endReason = true, "layer_unavailable" end
            action_slots[key] = nil
        end
    end
    current_actions = now
    action_status = #now > 0 and "已读取角色当前动画；帧回绕可能是循环或同动作重播"
        or "当前未读到可用动画层；请进入可操作角色的场景"
end
local function action_links(context)
    local out = {}
    if context.ownership ~= "local" then return out end
    for _, current in ipairs(current_actions) do
        local m, direct = current.motion, context.motion
        local exact = direct and direct.componentIdentity == m.componentIdentity and direct.layer == m.layer
            and direct.bankId == m.bankId and direct.motionId == m.motionId
        if current.recordId and current.sceneEpoch == scene_epoch and frame - current.observedFrame <= 1 and
            (exact or (not direct and current.componentKind == "character" and m.layer == 0)) then
            out[#out + 1] = {actionId = current.recordId, actionKey = current.key,
                association = exact and "direct_motion_request" or "contemporaneous_only",
                motionFrame = exact and direct.frame or m.frame, observedFrame = frame, observations = 1,
                lastMotionFrame = exact and direct.frame or m.frame, lastObservedFrame = frame,
                sourceKind = context.sourceKind}
        end
    end
    return out
end
local function merge_action_links(destination, source)
    for _, link in ipairs(source.actionLinks or {}) do
        local exists = false
        for _, old in ipairs(destination.actionLinks or {}) do
            if old.actionId == link.actionId and old.association == link.association then
                old.observations = (old.observations or 1) + (link.observations or 1)
                if (link.lastObservedFrame or link.observedFrame) >= (old.lastObservedFrame or old.observedFrame) then
                    old.lastMotionFrame = link.lastMotionFrame or link.motionFrame
                    old.lastObservedFrame = link.lastObservedFrame or link.observedFrame
                end
                exists = true; break
            end
        end
        if not exists then
            destination.actionLinks = destination.actionLinks or {}
            if #destination.actionLinks < limits.actionLinks then
                destination.actionLinks[#destination.actionLinks + 1] = copy(link)
            else counters.actionLinkOverflow = counters.actionLinkOverflow + 1 end
        end
    end
end
local function context_for(info, owner)
    local go, motion
    if info.owner == "shell" then return shell_context(owner)
    elseif info.owner == "oem" then
        motion = field(owner, "Motion"); go = game_object(motion) or game_object(owner)
    elseif info.owner == "controller" then go = field(owner, "_Owner")
    elseif info.owner == "hunter" then
        local character = call(owner, "get_HunterCharacter")
        go = game_object(character)
        if character and identity(character) ~= actor.characterIdentity then
            return {ownership = "remote", ownerIdentity = identity(go), ownerEvidence = "hunter_character"}
        end
    end
    local ownership, id = local_owner(go)
    return {ownership = ownership, ownerIdentity = id, ownerEvidence = info.owner, motionObject = motion,
        isInitiator = info.owner == "oem" or info.owner == "controller" or info.owner == "hunter"}
end
local function forget_event(index)
    local removed = table.remove(events, index)
    removed._retained = false
    discard_event_sources(removed, "event_evicted")
    if removed._container_key and container_events[removed._container_key] == removed then
        container_events[removed._container_key] = nil
    end
    return removed
end
local function publish_event(ev)
    if ev._excluded then return false end
    if ev._retained then return true end
    -- Deferred player scans must not reinsert every evicted record. A rejected
    -- unknown record may only return if direct evidence later confirms it local.
    if ev._published and not (ev.localConfirmed and ev._suppressed_unknown) then return false end
    if not ev._published then ev._published = true; counters.captured = counters.captured + 1 end
    if #events >= limits.events then
        local victim
        for index, old in ipairs(events) do if not old.localConfirmed then victim = index; break end end
        if not victim and not ev.localConfirmed then
            if not ev._suppressed_unknown then counters.suppressedUnknownEvents = counters.suppressedUnknownEvents + 1 end
            ev._suppressed_unknown = true
            return false
        end
        forget_event(victim or 1)
        counters.evictedEvents = counters.evictedEvents + 1
    end
    ev._retained, ev._suppressed_unknown = true, nil
    events[#events + 1] = ev
    return true
end
local function make_event(info, context, parent)
    if context.ownership == "remote" then
        counters.remoteIgnored = counters.remoteIgnored + 1
        if context.sourceKind ~= "shell" then return nil end
    end
    sequence = sequence + 1
    local ev = {id = "capture-" .. scene_epoch .. "-" .. sequence, seq = sequence, label = info.label,
        parentId = parent and parent.event and parent.event.id or nil, sceneEpoch = scene_epoch,
        ownerIdentity = context.ownerIdentity, ownerEvidence = context.ownerEvidence,
        sourceKind = context.sourceKind, sourceShellIdentity = context.sourceShellIdentity,
        sourceOwnerIdentity = context.sourceOwnerIdentity, shellOwnerDepth = context.shellOwnerDepth,
        localConfirmed = context.ownership == "local", ownership = context.ownership,
        actor = copy(actor), motion = copy(context.motion), actionLinks = action_links(context),
        motionAssociation = context.motion and "direct_motion_request" or "unknown",
        createdFrame = frame, requested = {}, created = {}, playerIdentities = {}, evidence = {}, pending = false,
        observationCount = 1, labels = {info.label},
        _excluded = context.ownership == "remote",
        actualSources = {}, _source_ids = {}, _observed_players = {},
        _path_seen = {}, _resource_seen = {}, _players = {}, _bindings = {}}
    if ev.ownership == "unknown" then counters.unknownOwners = counters.unknownOwners + 1 end
    -- Environment controllers poll empty loop requests thousands of times.
    -- Keep their same-thread scope, but publish only when output is observed.
    if ev.localConfirmed then publish_event(ev) end
    return ev
end
local function set_ownership(ev, ownership)
    local was_local, now_local = ev.localConfirmed, ownership == "local"
    ev.localConfirmed, ev.ownership, ev._excluded = now_local, ownership, ownership == "remote"
    if was_local and not now_local then
        counters.resourceSourceRevocations = counters.resourceSourceRevocations + #(ev._source_ids or {})
        discard_event_sources(ev, "ownership_revoked")
    else
        for _, id in ipairs(ev._source_ids or {}) do
            local record = actual_sources[id]
            if record then
                record.descriptor.localConfirmed = now_local
                refresh_latest_source(canonical(record.descriptor.actualPath))
            end
        end
    end
    if was_local ~= now_local then
        for key in pairs(ev._resource_seen) do
            local aggregate = resource_map[key]
            if aggregate then
                aggregate._localRecords = math.max(0, (aggregate._localRecords or 0) + (now_local and 1 or -1))
                aggregate.localConfirmed = aggregate._localRecords > 0
            end
        end
        for _, binding in ipairs(ev._bindings) do
            -- A binding has a mutable holder and must not retain revoked local
            -- evidence from this event. Other events can re-confirm it later.
            if binding.eventId == ev.id then binding.localConfirmed = now_local end
        end
    end
end
local function apply_source(ev, source)
    ev.sourceKind, ev.sourceShellIdentity = source.sourceKind, source.sourceShellIdentity
    ev.sourceOwnerIdentity, ev.shellOwnerDepth = source.sourceOwnerIdentity, source.shellOwnerDepth
    ev.ownerIdentity, ev.ownerEvidence = source.ownerIdentity, source.ownerEvidence
    set_ownership(ev, source.ownership)
end
local function add_unique(array, value)
    if value == nil then return end
    for _, old in ipairs(array) do if old == value then return end end
    array[#array + 1] = value
end
local function add_player(array, value)
    for _, old in ipairs(array) do if old == value then return end end
    if #array >= limits.players then counters.playerOverflow = counters.playerOverflow + 1; return end
    array[#array + 1] = value
end
local function observe_path(ev, path, kind, evidence, player)
    local key = canonical(path)
    if not key or not ev or ev._excluded then return end
    publish_event(ev)
    local identity_key = kind .. "|" .. path .. "|" .. evidence
    if ev._path_seen[identity_key] then return end
    if #ev.requested + #ev.created >= limits.paths then counters.pathOverflow = counters.pathOverflow + 1; return end
    ev._path_seen[identity_key] = true
    ev[kind][#ev[kind] + 1] = {path = path, canonicalPath = key, evidence = evidence, playerIdentity = identity(player)}
    local aggregate = resource_map[key]
    if not aggregate then
        if #resources >= limits.resources then counters.resourceOverflow = counters.resourceOverflow + 1; return end
        aggregate = {path = path, canonicalPath = key, count = 0, requestedCount = 0, createdCount = 0,
            evidence = {}, localConfirmed = false, motionAssociation = "unknown", playerIdentities = {},
            originalPaths = {}, _localRecords = 0, countMeaning = "distinct_capture_records_not_effect_spawns"}
        resources[#resources + 1], resource_map[key] = aggregate, aggregate
    end
    if not ev._resource_seen[key] then
        aggregate.count = aggregate.count + 1; ev._resource_seen[key] = true
        if ev.localConfirmed then aggregate._localRecords = aggregate._localRecords + 1 end
    end
    aggregate[kind == "created" and "createdCount" or "requestedCount"] = aggregate[kind == "created" and "createdCount" or "requestedCount"] + 1
    aggregate.localConfirmed = aggregate._localRecords > 0
    add_unique(aggregate.evidence, evidence)
    local seen_original = false
    for _, old in ipairs(aggregate.originalPaths) do if old == path then seen_original = true; break end end
    if not seen_original then
        if #aggregate.originalPaths < limits.paths then aggregate.originalPaths[#aggregate.originalPaths + 1] = path
        else counters.pathOverflow = counters.pathOverflow + 1 end
    end
    if player then add_player(aggregate.playerIdentities, identity(player)) end
    if ev.motion then aggregate.motion, aggregate.motionAssociation = copy(ev.motion), ev.motionAssociation end
    aggregate.lastEventId = ev.id
end
local function observe_provider(ev, provider)
    if not ev or ev._excluded or not is_a(provider, "via.effect.ProviderData") then return end
    local count = tonumber(call(provider, "getResourcesCount"))
    if not count or count < 0 or count ~= count or count == math.huge then
        counters.providerReadErrors = counters.providerReadErrors + 1; return
    end
    count = math.floor(count)
    if count > limits.collection then counters.collectionTruncated = counters.collectionTruncated + count - limits.collection end
    for index = 0, math.min(count, limits.collection) - 1 do
        local holder = call(provider, "getResources(System.UInt64)", index)
        local path = scalar(holder, "get_ResourcePath")
        local normalized = canonical(path)
        if normalized then
            observe_path(ev, path, "requested", "provider_resource")
            local key = tostring(identity(provider)) .. ":" .. index
            local binding = binding_map[key]
            if not binding and #bindings < limits.bindings then
                binding = {provider = retain(provider), index = index, holder = retain(holder), path = path,
                    originalPath = normalized, canonicalPath = normalized, localConfirmed = ev.localConfirmed,
                    eventId = ev.id, sceneEpoch = scene_epoch, players = {}}
                if binding.provider and binding.holder then
                    bindings[#bindings + 1], binding_map[key] = binding, binding
                else binding = nil end
            elseif not binding then counters.bindingOverflow = counters.bindingOverflow + 1 end
            if binding then
                binding.path, binding.canonicalPath, binding.originalPath = path, normalized, normalized
                binding.holder = retain(holder) or binding.holder
                binding.localConfirmed = binding.localConfirmed or ev.localConfirmed
                add_unique(ev._bindings, binding)
            end
        end
    end
end
local function observe_player(ev, player)
    if ev._excluded or not is_a(player, "via.effect.EffectPlayer") then return end
    local player_id = identity(player)
    if not player_id then return end
    -- Deferred scans and event merges may revisit a pooled EffectPlayer. Its
    -- first observation belongs to this event; never re-read it as old evidence.
    if ev._observed_players[player_id] then return end
    if #ev.playerIdentities >= limits.players then counters.playerOverflow = counters.playerOverflow + 1; return end
    local holder = call(player, "get_Resource")
    local holder_path = scalar(holder, "get_ResourcePath")
    local player_path = scalar(player, "get_ResourcePath")
    local path = holder_path or player_path
    if not canonical(path) then return end
    ev._observed_players[player_id] = {path = path}
    add_player(ev.playerIdentities, player_id)
    local held = retain(player)
    if held then
        add_player(ev._players, held)
        for _, binding in ipairs(ev._bindings) do add_player(binding.players, held) end
    end
    observe_path(ev, path, "created", "actual_effect_player", player)
    local agrees = not player_path or raw_canonical(player_path) == raw_canonical(holder_path)
    if is_a(holder, "via.effect.EffectResourceHolder") and canonical(holder_path) and agrees then
        ev._observed_players[player_id].sourceId = retain_actual_source(ev, player_id, holder, holder_path)
    else counters.resourceSourceReadErrors = counters.resourceSourceReadErrors + 1 end
end
local function capture_container(ev, container)
    if not ev or not is_a(container, "via.effect.script.CreatedEffectContainer") then return ev end
    local container_key = scene_epoch .. ":" .. tostring(identity(container)) .. ":" .. tostring(field(container, "SerialNo"))
    if ev.sourceKind == "shell" then
        if not container_sources[container_key] then
            if #source_keys >= limits.events then
                container_sources[table.remove(source_keys, 1)] = nil
                counters.sourceOwnershipEvictions = counters.sourceOwnershipEvictions + 1
            end
            source_keys[#source_keys + 1] = container_key
        end
        -- Store only strings/scalars, never a shell wrapper beyond its call.
        container_sources[container_key] = {sourceKind = ev.sourceKind, sourceShellIdentity = ev.sourceShellIdentity,
            sourceOwnerIdentity = ev.sourceOwnerIdentity, shellOwnerDepth = ev.shellOwnerDepth,
            ownerIdentity = ev.ownerIdentity, ownerEvidence = ev.ownerEvidence, ownership = ev.ownership}
    elseif container_sources[container_key] then apply_source(ev, container_sources[container_key]) end
    local existing = container_events[container_key]
    if ev._excluded then
        if existing then
            apply_source(existing, ev)
            for index = #pending, 1, -1 do
                local item = pending[index]
                if item.event == existing then
                    pending_map[item.key], existing.pending = nil, false
                    table.remove(pending, index)
                end
            end
            existing._players, existing._bindings = {}, {}
        end
        for index = #events, 1, -1 do
            if events[index] == existing or events[index] == ev then forget_event(index) end
        end
        return ev
    end
    if existing and existing ~= ev then
        existing.observationCount = existing.observationCount + ev.observationCount
        for _, label in ipairs(ev.labels) do add_unique(existing.labels, label) end
        if not existing.motion and ev.motion then
            existing.motion, existing.motionAssociation = copy(ev.motion), ev.motionAssociation
        end
        merge_action_links(existing, ev)
        -- A source shell is stronger evidence than a container's creator/follow
        -- object. Keep unknown shell ownership unknown even if the effect is
        -- attached to the local hunter.
        if ev.sourceKind == "shell" then
            apply_source(existing, ev)
        elseif ev.localConfirmed and existing.sourceKind ~= "shell" then
            set_ownership(existing, "local")
        end
        -- Remove the merged-away event's contribution before copying its paths
        -- to the surviving event; otherwise later revocation leaves a phantom
        -- local record in the resource aggregate.
        for key in pairs(ev._resource_seen) do
            local aggregate = resource_map[key]
            if aggregate then
                aggregate.count = math.max(0, aggregate.count - 1)
                if ev.localConfirmed then aggregate._localRecords = math.max(0, aggregate._localRecords - 1) end
                aggregate.localConfirmed = aggregate._localRecords > 0
            end
        end
        ev._resource_seen = {}
        for key in pairs(existing._resource_seen) do
            local aggregate = resource_map[key]
            if aggregate then
                if existing.motion then aggregate.motion, aggregate.motionAssociation = copy(existing.motion), existing.motionAssociation end
            end
        end
        for _, kind in ipairs({"requested", "created"}) do
            for _, item in ipairs(ev[kind]) do observe_path(existing, item.path, kind, item.evidence) end
        end
        for _, binding in ipairs(ev._bindings) do add_unique(existing._bindings, binding) end
        merge_actual_sources(existing, ev)
        for _, player in ipairs(ev._players) do
            add_player(existing._players, player)
            add_player(existing.playerIdentities, identity(player))
        end
        for index = #events, 1, -1 do if events[index] == ev then forget_event(index); break end end
        counters.mergedContainerObservations = counters.mergedContainerObservations + 1
        if ev._published then counters.captured = counters.captured - 1 end
        ev._merged = true
        ev = existing
    end
    ev.containerIdentity, ev.containerSerialNo = identity(container), tonumber(field(container, "SerialNo"))
    ev.creatorIdentity = identity(field(container, "Creator") or call(container, "get_Owner"))
    local owner = local_owner(field(container, "Creator") or call(container, "get_Owner"))
    if not ev.localConfirmed and ev.sourceKind ~= "shell" and owner == "local" then
        set_ownership(ev, "local"); ev.ownerEvidence = "container_creator"
    end
    publish_event(ev)
    -- Only history-retained records belong in this lookup; its size is bounded
    -- by the event ring even if unknown containers are rejected from history.
    if ev._retained then container_events[container_key], ev._container_key = ev, container_key end
    local key = ev.id .. ":" .. tostring(ev.containerIdentity)
    if pending_map[key] then return ev end
    if #pending >= limits.pending then counters.pendingOverflow = counters.pendingOverflow + 1; return ev end
    local held = retain(container)
    if not held then return ev end
    local item = {event = ev, container = held, key = key, startedFrame = frame, serial = ev.containerSerialNo}
    pending[#pending + 1], pending_map[key], ev.pending = item, item, true
    -- Capture short-lived players immediately as well as deferred population.
    each(field(container, "AllPlayers") or call(container, "getAllPlayers"), function(player) observe_player(ev, player) end)
    return ev
end
local function parent_frame(stack) return stack[#stack] end
local function argument(info, args, instance_index)
    -- Hook args[1] is the thread context. Static methods have no args[2] this.
    return args[instance_index - (info.isStatic and 1 or 0)]
end
local function pre(info, args)
    info.preCalls = info.preCalls + 1
    local storage = thread.get_hook_storage()
    local tid = tostring(thread.get_id())
    stacks[tid] = stacks[tid] or {}
    local stack = stacks[tid]
    local parent = parent_frame(stack)
    if parent and parent.generation ~= generation then parent = nil end
    local fr = {info = info, tid = tid, generation = generation}
    storage.MotionVfxActionCapture = fr; stack[#stack + 1] = fr
    if not active or not M.recording then return end
    counters.calls = counters.calls + 1
    info.recordedCalls = info.recordedCalls + 1
    local owner
    if not info.isStatic then
        owner = managed(args[2], info.type)
        if not owner then
            counters.ownerMismatch, info.ownerMismatch = counters.ownerMismatch + 1, info.ownerMismatch + 1
            return
        end
    end
    local context = context_for(info, owner)
    local request
    if info.kind == "engine" then
        request = managed(argument(info, args, 4), "via.effect.script.EffectManager.RequestInfo")
        local request_owner = field(request, "Owner")
        context.ownership, context.ownerIdentity = local_owner(request_owner or managed(argument(info, args, 3), "via.GameObject"))
        context.ownerEvidence = request_owner and "request_owner" or "create_effect_creator"
    end
    if parent and parent.context and info.owner ~= "shell" then
        local pc = parent.context
        -- An explicit different owner is a barrier even on the same thread.
        local compatible = not context.ownerIdentity or not pc.ownerIdentity or context.ownerIdentity == pc.ownerIdentity
            or (context.ownership == "local" and pc.ownership == "local")
        if pc.sourceKind == "shell" then
            -- Source methods delimit a real same-thread call, including remote
            -- and unresolved sources. A shared controller or local hit target
            -- cannot override the projectile's initiating owner.
            context.ownership, context.ownerIdentity, context.ownerEvidence = pc.ownership, pc.ownerIdentity, pc.ownerEvidence
            context.sourceKind, context.sourceShellIdentity = pc.sourceKind, pc.sourceShellIdentity
            context.sourceOwnerIdentity, context.shellOwnerDepth = pc.sourceOwnerIdentity, pc.shellOwnerDepth
            context.motion, context.isInitiator = nil, true
        elseif info.kind == "engine" and pc.isInitiator and (pc.ownerIdentity or pc.ownership == "remote") then
            -- Hit effects can be instantiated on another actor. The explicit
            -- initiating controller scope is distinct from creator/follow target.
            context.ownership, context.ownerIdentity, context.ownerEvidence = pc.ownership, pc.ownerIdentity, "synchronous_initiator"
            context.motion, context.isInitiator = pc.motion, true
        elseif compatible and context.ownership ~= "remote" and pc.ownership ~= "remote" then
            if context.ownership == "unknown" then
                context.ownership, context.ownerIdentity, context.ownerEvidence = pc.ownership, pc.ownerIdentity, "synchronous_owner_context"
            end
            context.motion = pc.motion
            context.isInitiator = context.isInitiator or pc.isInitiator
        end
    end
    fr.context = context
    if info.kind == "scope" then return end
    if info.kind == "motion" then
        context.motion = motion_snapshot(managed(argument(info, args, info.node), "via.motion.MotionNodeCtrl"), context.motionObject)
        return
    end
    local ev = make_event(info, context, parent); fr.event = ev
    if not ev then return end
    ev.threadId = tid
    if info.domain then ev.effectDomain, ev.effectId = info.domain, uint(argument(info, args, info.id)) end
    if info.kind == "event" or info.kind == "request" then
        local effect_id = managed(argument(info, args, 3), "via.effect.script.EffectID")
        ev.containerId = scalar(effect_id, "get_containerID")
        ev.elementId = scalar(effect_id, "get_elementID")
        ev.externalIndex = scalar(effect_id, "get_dataContainerIndex")
    end
    if info.kind == "event" then fr.event = capture_container(ev, managed(argument(info, args, 4), "via.effect.script.CreatedEffectContainer")) end
    if info.kind == "register" then
        ev.containerId, ev.elementId = uint(argument(info, args, 5)), uint(argument(info, args, 6))
        fr.event = capture_container(ev, managed(argument(info, args, 3), "via.effect.script.CreatedEffectContainer"))
    end
    if info.kind == "engine" then
        ev.requestIdentity, ev.followTargetIdentity = identity(request), identity(field(request, "FollowTarget"))
        ev.requestOwnerIdentity, ev.creatorIdentity = identity(field(request, "Owner")), identity(managed(argument(info, args, 3), "via.GameObject"))
        observe_provider(ev, field(request, "Data"))
    end
end
local function post(info, retval)
    info.postCalls = info.postCalls + 1
    local storage = thread.get_hook_storage()
    local fr = storage.MotionVfxActionCapture
    storage.MotionVfxActionCapture = nil
    if not fr then counters.contextMismatch = counters.contextMismatch + 1; return retval end
    local stack = stacks[fr.tid] or {}
    if stack[#stack] == fr then table.remove(stack)
    else
        counters.contextMismatch = counters.contextMismatch + 1
        for index = #stack, 1, -1 do if stack[index] == fr then table.remove(stack, index); break end end
    end
    if #stack == 0 then stacks[fr.tid] = nil end
    if fr.event and active and fr.generation == generation then
        if info.returns == "container" then fr.event = capture_container(fr.event, managed(retval, "via.effect.script.CreatedEffectContainer")) end
        if info.returns == "objects" then
            each(managed(retval) or (type(retval) == "table" and retval), function(go)
                observe_player(fr.event, call(go, "getComponent(System.Type)", sdk.typeof("via.effect.EffectPlayer")))
            end)
        end
        if not fr.event._published and not fr.event._merged and not fr.event._excluded then
            counters.emptyUnknownCalls, info.emptyUnknownCalls = counters.emptyUnknownCalls + 1, info.emptyUnknownCalls + 1
        end
    end
    return retval
end
local function spec(type_name, signature, kind, owner, extra)
    local info = {type = type_name, signature = signature, kind = kind, owner = owner, label = type_name .. "." .. signature,
        installed = false, preCalls = 0, postCalls = 0, recordedCalls = 0, ownerMismatch = 0, emptyUnknownCalls = 0, errors = 0}
    for key, value in pairs(extra or {}) do info[key] = value end
    specs[#specs + 1] = info
end
for _, type_name in ipairs({"via.effect.script.ObjectEffectManager", "via.effect.script.ObjectEffectManager2"}) do
    spec(type_name, "requestEffectVFTriggerTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", {node = 3})
    spec(type_name, "requestEffectVRangeTrackByMotionNode(via.motion.MotionNodeCtrl, System.UInt32)", "motion", "oem", {node = 3})
    spec(type_name, "requestEffectFromRangeSquence(via.effect.script.VFXRangeTrack, via.motion.MotionNodeCtrl, System.UInt32, System.UInt32)", "motion", "oem", {node = 4})
    spec(type_name, "registerCreatedEffectDataList(via.effect.script.CreatedEffectContainer, System.Int32, System.UInt32, System.UInt32)", "register", "oem")
    spec(type_name, "requestEffectInternal(via.effect.script.EffectID, via.GameObject, System.Int32, via.effect.script.EffectManager.WwiseTriggerInfo, via.effect.script.EffectManager.RequestParam)", "request", "oem", {returns = "container"})
end
spec("via.effect.script.ObjectEffectManagerBase", "callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer, via.effect.script.ObjectEffectManagerBase.CreateMotionSequenceEffectEventInfo.CallSequenceType)", "event", "oem")
spec("via.effect.script.EffectManager", "createEffectInternal(via.GameObject, via.effect.script.EffectManager.RequestInfo)", "engine", "creator", {returns = "objects"})
spec("app.cEffectController", "playEffectCore(System.UInt32, app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)", "play", "controller", {domain = "Common", id = 4, returns = "container"})
spec("app.cEffectController", "playEffectLoopCore(System.UInt32, app.EffectID_Common.ID, System.UInt64, via.GameObject, app.cEffectOverwriteParams, via.GameObject)", "play", "controller", {domain = "Common", id = 4, returns = "container"})
for _, domain in ipairs({"Hit", "Damage"}) do
    spec("app.cEffectController", "playEffectCore(app.EffectID_" .. domain .. ".ID, via.GameObject, app.cEffectOverwriteParams, via.GameObject)", "play", "controller", {domain = domain, id = 3, returns = "container"})
end
-- These are source-specific methods, not the shared effect controller. No
-- scope is retained after return, and no async attribution is guessed.
for _, signature in ipairs({
    "callEffect(app.EffectID_Common.ID, via.vec3, via.Quaternion, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>, app.cEffectOverwriteParams)",
    "callEffect(app.EffectID_Common.ID, via.GameObject, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>)",
    "callEffectLoop(app.EffectID_Common.ID, via.GameObject, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>)",
    "callEffectLoop(app.EffectID_Common.ID, via.vec3, via.Quaternion, System.UInt32, System.UInt32, System.Action`1<via.effect.script.CreatedEffectContainer>, app.cEffectOverwriteParams)"
}) do spec("app.mcShellCallEffect", signature, "scope", "shell") end
spec("app.mcShellCallEffect", "callShellExEffect(app.AppShellDef.EX_EFFECT_TYPE, via.vec3, via.Quaternion, app.cEffectOverwriteParams, System.Boolean, System.UInt32)", "play", "shell", {returns = "container"})
spec("app.mcShellCallEffect", "callShellHitMarkEffect(app.HitInfo, via.vec3, via.Quaternion)", "play", "shell", {returns = "container"})
local function install()
    if attempted then return installed_count > 0 end
    if not thread or not thread.get_id or not thread.get_hook_storage then return false, "REFramework thread hook storage is unavailable" end
    attempted = true
    local groups, by_address = {}, {}
    for index, info in ipairs(specs) do
        local ok, err = pcall(function()
            local td = sdk.find_type_definition(info.type)
            local method = td and td:get_method(info.signature)
            if not method then error("Missing hook: " .. info.label) end
            local is_static = method:is_static()
            if type(is_static) ~= "boolean" then error("Missing static method metadata: " .. info.label) end
            info.isStatic = is_static
            local return_type = method:get_return_type()
            info.returnType = return_type and return_type:get_full_name()
            if not info.returnType then error("Missing return type metadata: " .. info.label) end
            info.firstArgumentIndex = is_static and 2 or 3
            local address = method:get_function()
            if not address or address == 0 then error("Missing native function address: " .. info.label) end
            info.nativeAddress, info.ignoreJump = tostring(address), true
            info.parameterSignature = info.signature:match("%(.*%)$")
            local abi = tostring(is_static) .. "|" .. info.returnType .. "|" .. info.parameterSignature
            local group = by_address[info.nativeAddress]
            if not group then
                group = {method = method, abi = abi, infos = {}}
                groups[#groups + 1], by_address[info.nativeAddress] = group, group
            else
                local first = group.infos[1]
                -- Only type-distinct aliases with identical signatures and
                -- observation semantics can be dispatched using the receiver.
                -- Same-type or static aliases cannot be distinguished safely.
                if group.abi ~= abi or is_static or info.type == first.type or
                    info.signature ~= first.signature or info.kind ~= first.kind or
                    info.owner ~= first.owner or info.returns ~= first.returns then
                    group.conflict = "Native hook alias has incompatible ABI or ambiguous receiver: " .. info.nativeAddress
                end
            end
            info._index = index; group.infos[#group.infos + 1] = info
        end)
        if not ok then info.errors, info.lastError = info.errors + 1, tostring(err); note(err) end
    end
    for _, group in ipairs(groups) do
        local first = group.infos[1]
        local ok, err = pcall(function()
            if group.conflict then error(group.conflict) end
            sdk.hook(group.method, function(args)
                local selected = first
                if #group.infos > 1 then
                    local receiver = managed(args[2])
                    for _, candidate in ipairs(group.infos) do
                        if is_a(receiver, candidate.type) then selected = candidate; break end
                    end
                end
                local worked, why = pcall(pre, selected, args)
                if not worked then selected.errors, selected.lastError = selected.errors + 1, tostring(why); note(why) end
                return sdk.PreHookResult.CALL_ORIGINAL
            end, function(retval)
                local fr = safe(function() return thread.get_hook_storage().MotionVfxActionCapture end)
                local selected = fr and fr.info or first
                local worked, why = pcall(post, selected, retval)
                if not worked then selected.errors, selected.lastError = selected.errors + 1, tostring(why); note(why) end
                return retval
            end, true) -- REFramework ignore_jmp: keep each declared entry ABI.
            native_hook_count = native_hook_count + 1
            for _, info in ipairs(group.infos) do
                installed[info._index], installed_count, info.installed = true, installed_count + 1, true
                if info ~= first then info.sharedNativeHook = first.label end
            end
        end)
        if not ok then
            for _, info in ipairs(group.infos) do
                info.errors, info.lastError, info.aliasConflict = info.errors + 1, tostring(err), group.conflict ~= nil
            end
            note(err)
        end
    end
    return installed_count > 0, installed_count == 0 and "No capture hooks installed" or nil
end
local function drop_sources()
    while #source_order > 0 do discard_source(source_order[#source_order], "capture_context_cleared") end
    for _, item in ipairs(pending) do item.event.pending = false end
    pending, pending_map, bindings, binding_map, container_events, container_sources, source_keys = {}, {}, {}, {}, {}, {}, {}
    for _, ev in ipairs(events) do ev._players, ev._bindings = {}, {} end
end
local function update()
    if not active then return end
    frame = frame + 1
    local td = safe(sdk.find_type_definition, "via.SceneManager")
    local manager = safe(sdk.get_native_singleton, "via.SceneManager")
    local scene = td and manager and safe(sdk.call_native_func, manager, td, "get_CurrentScene")
    local scene_id = identity(scene)
    if current_scene and scene_id ~= current_scene then
        scene_epoch = scene_epoch + 1; counters.sceneChanges = counters.sceneChanges + 1
        generation = generation + 1
        close_actions("scene_changed"); action_context = nil
        drop_sources(); stacks = {}
    end
    current_scene = scene_id
    if M.recording or frame % 6 == 0 or actor.sceneEpoch ~= scene_epoch then
        refresh_actor()
        observe_actions()
    end
    local scanned = 0
    while #pending > 0 and scanned < math.min(#pending, limits.scans) do
        if scan_cursor > #pending then scan_cursor = 1 end
        local item = pending[scan_cursor]; scanned = scanned + 1
        local valid = not sdk.is_managed_object or safe(sdk.is_managed_object, item.container)
        local expired = frame - item.startedFrame >= limits.sourceFrames
        local serial = tonumber(field(item.container, "SerialNo"))
        if valid and (item.serial == nil or serial == item.serial) then
            each(field(item.container, "AllPlayers") or call(item.container, "getAllPlayers"), function(player) observe_player(item.event, player) end)
        else expired = true; counters.droppedSources = counters.droppedSources + 1 end
        local finished = field(item.container, "IsFinished") == true
        if expired or finished then
            if expired then counters.pendingTimeouts = counters.pendingTimeouts + 1 end
            pending_map[item.key], item.event.pending = nil, false
            table.remove(pending, scan_cursor)
        else scan_cursor = scan_cursor + 1 end
    end
end
function M.start()
    if not active then return false, "Capture module was reset" end
    if M.recording then return true end
    if not refresh_actor() then return false, "Local player is unavailable" end
    local ok, err = install(); if not ok then return false, err end
    M.recording = true
    observe_actions()
    return true
end
function M.stop() M.recording = false; close_actions("recording_stopped"); return true end
function M.clear()
    generation = generation + 1
    drop_sources(); events, resources, resource_map = {}, {}, {}; scan_cursor = 1
    close_actions("cleared"); action_history = {}
    return true
end
function M.shutdown() M.stop(); generation = generation + 1; drop_sources(); return true end
function M.list_bindings(path)
    local target, out = canonical(path), {}
    if not target then return out end
    for _, binding in ipairs(bindings) do
        if binding.sceneEpoch == scene_epoch and canonical(binding.path) == target then
            binding.canonicalPath, binding.originalPath = target, target
            out[#out + 1] = binding
        end
    end
    return out
end
-- Internal consumer API: returns a scalar descriptor plus its captured managed
-- holder, or nil + reason. This does not export EFX bytes or claim that a pooled
-- EffectPlayer still uses this holder now. No engine getters run on selection.
function M.get_resource_source(path, source_id)
    local target = canonical(path)
    if not target then return nil, "无效的 EFX 来源路径" end
    local record
    for index = #source_order, 1, -1 do
        local candidate = actual_sources[source_order[index]]
        if source_available(candidate) and canonical(candidate.descriptor.actualPath) == target then record = candidate; break end
    end
    if source_id ~= nil and (not record or record.descriptor.sourceId ~= source_id) then
        return nil, "来源选择已过期或已有更新的实际实例；请重新选择当前记录"
    end
    if not source_available(record) or canonical(record.descriptor.actualPath) ~= target then
        return nil, "没有本场景仍保留且确认归属的实际资源持有器；请重新记录角色动作"
    end
    local descriptor = copy(record.descriptor)
    descriptor.sharedTargetPath = target
    return descriptor, record.holder
end
function M.set_alias(generated, original)
    local from, to = raw_canonical(generated), canonical(original)
    if not from or not to then return false, "Aliases require complete .efx resource paths" end
    if from == to then return true end
    if aliases[to] == from then return false, "Alias cycle" end
    if not aliases[from] then
        if alias_count >= limits.bindings then counters.aliasOverflow = counters.aliasOverflow + 1; return false, "Alias limit reached" end
        alias_count = alias_count + 1
    end
    aliases[from] = to
    return true
end
local function public(value)
    if type(value) ~= "table" then return value end
    local out = {}
    for key, item in pairs(value) do
        if type(key) ~= "string" or key:sub(1, 1) ~= "_" then out[key] = public(item) end
    end
    return out
end
-- Small live view: copying history/events every rendered frame is unnecessary.
function M.current_snapshot()
    return {currentActions=copy(current_actions),actionStatus=action_status,sceneEpoch=scene_epoch,recording=M.recording}
end
function M.snapshot()
    return {version = M.version, recording = M.recording, actor = copy(actor), sceneEpoch = scene_epoch,
        events = public(events), resources = public(resources), counters = copy(counters), errors = copy(errors),
        actions = public(action_history), currentActions = copy(current_actions), actionStatus = action_status,
        actionCoverage = "sampled_animation_layers_not_gameplay_move_names_or_complete_dependencies",
        actionLayerLimit = limits.actionLayers,
        installedHooks = installed_count, requestedHooks = #specs, nativeHooks = native_hook_count,
        hooks = public(specs), pendingSources = #pending,
        retainedResourceSources = #source_order,
        coverage = "observed_requests_only_not_complete_action_dependencies"}
end
re.on_frame(function() local ok, err = pcall(update); if not ok then note(err) end end)
if re.on_script_reset then re.on_script_reset(function() M.shutdown(); active = false end) end
_G.MotionVfxActionCapture = M
return M
