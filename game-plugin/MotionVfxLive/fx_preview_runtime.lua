-- Shared playback backend. No HTTP service and no automatic playback.
-- Load beside the UI scripts, or require from reframework/autorun.
-- Mutations are drained at pre LockScene, matching the supplied scripts and
-- REFramework's bundled scene-editing examples. This is not a game-thread
-- safety guarantee for every game build: validate the callback in-game.
if _G.fx_preview_runtime_v2 then return _G.fx_preview_runtime_v2 end

local M = { version = "2.2", capabilities = { point_cloud = true }, clients = {}, instances = {}, cache = {}, queue = {}, hotkey_owner = nil,
    max_per_owner = 32, max_total = 128 }
local Client = {}
Client.__index = Client
local active = false
local sequence, last_scene = 0, nil

local function fail(message) error(message, 0) end
local function finite(value, name)
    local n = tonumber(value)
    if not n or n ~= n or n == math.huge or n == -math.huge then fail(name .. " must be finite") end
    -- REFramework's generic call bridge distinguishes Lua integers from Lua
    -- floats. An integer 1 passed to System.Single is otherwise raw bits, not
    -- float 1.0. Keep integer enums/byte channels outside this conversion.
    return n + 0.0
end
local function clone(value, seen)
    if type(value) ~= "table" then return value end
    seen = seen or {}
    if seen[value] then fail("Cyclic options table is unsupported") end
    seen[value] = true
    local out = {}
    for k, v in pairs(value) do out[k] = clone(v, seen) end
    seen[value] = nil
    return out
end
local function log_error(message)
    if log and log.warn then log.warn("[fx_preview] " .. tostring(message)) end
end
local function notify(command, ok, value)
    local result = { request_id = command.request_id, instance_id = command.id, operation = command.op,
        ok = ok, message = ok and tostring(value or "accepted by engine") or tostring(value),
        status = ok and "applied" or "failed" }
    command.client.last_result = result
    if command.callback then
        local called, err = pcall(command.callback, result)
        if not called then log_error("callback failed: " .. tostring(err)) end
    end
    if not ok then log_error(result.message) end
end
local function action_value(name)
    local td = sdk.find_type_definition("via.effect.PlayerAction")
    local field = td and td:get_field(name)
    local value = field and tonumber(field:get_data(nil))
    if value == nil then fail("PlayerAction unavailable: " .. tostring(name)) end
    return value
end
function M.get_actions()
    local out = {}
    for _, name in ipairs({ "Start", "Pause", "Resume", "Step", "Finish", "Kill" }) do
        local ok, value = pcall(action_value, name)
        if ok then out[name] = value end
    end
    return out
end
-- Read once per game update in callers, never use CPU time for playback timing.
function M.get_delta_time()
    local ok, value = pcall(function()
        local app = sdk.get_native_singleton("via.Application")
        local td = sdk.find_type_definition("via.Application")
        if not app or not td then fail("via.Application unavailable") end
        local dt = finite(sdk.call_native_func(app, td, "get_DeltaTime"), "DeltaTime")
        if dt < 0 then fail("DeltaTime cannot be negative") end
        return dt
    end)
    if not ok then return nil, "Game DeltaTime unavailable: " .. tostring(value) end
    return value
end

local function release_resource(resource)
    -- REResource is NOT an REManagedObject. We release only the one raw ref
    -- explicitly acquired below. create_holder adds a SEPARATE engine ref.
    -- holder:add_ref() is Lua-GC tracked (Sdk.cpp); never force_release it.
    if resource.raw_owned then
        local ok, err = pcall(function() resource.raw:release() end)
        if not ok then return false, "raw release failed: " .. tostring(err) end
        resource.raw_owned = false
    end
    resource.raw, resource.holder = nil, nil
    return true
end
local function clear_unused()
    local errors = {}
    for path, resource in pairs(M.cache) do
        if resource.users == 0 then
            local ok, err = release_resource(resource)
            if ok then M.cache[path] = nil else errors[#errors + 1] = err end
        end
    end
    if #errors > 0 then fail(table.concat(errors, "; ")) end
end
local function load_resource(path)
    if type(path) ~= "string" or path:match("^%s*$") then fail("EFX resource path is empty") end
    if path:match("^[A-Za-z]:") or path:match("^[/\\]") or path:find("..", 1, true) then
        fail("Use a game resource path, not a local filesystem path")
    end
    path = path:gsub("\\", "/")
    if not path:lower():match("%.efx$") then fail("Game resource path must end in .efx") end
    if M.cache[path] then return M.cache[path] end
    local resource = { path = path, users = 0, raw_owned = false }
    local ok, err = pcall(function()
        resource.raw = sdk.create_resource("via.effect.EffectResource", path)
        if not resource.raw then fail("Resource load returned nil: " .. path) end
        resource.raw:add_ref()
        resource.raw_owned = true
        resource.holder = resource.raw:create_holder("via.effect.EffectResourceHolder")
        if not resource.holder then fail("EffectResourceHolder creation failed: " .. path) end
        resource.holder:add_ref()
    end)
    if not ok then
        local cleaned, cleanup_error = release_resource(resource)
        if not cleaned then log_error(cleanup_error) end
        fail(err)
    end
    M.cache[path] = resource
    return resource
end
local function destroy_instance(instance)
    if not instance then return true end
    -- A scene unload may already have destroyed our object. Do not call an
    -- invalid pointer merely to satisfy cleanup bookkeeping.
    if instance.object and sdk.is_managed_object then
        local valid_check, valid = pcall(sdk.is_managed_object, instance.object)
        if valid_check and not valid then instance.object, instance.player = nil, nil end
    end
    -- Destroy is authoritative cleanup. Kill may be missing in a game version.
    if instance.player then
        pcall(function() instance.player:call("set_AutoStart", false) end)
        pcall(function() instance.player:call("set_Action(via.effect.PlayerAction)", action_value("Kill")) end)
    end
    if instance.object then
        local ok, err = pcall(function() instance.object:call("destroy", instance.object) end)
        if not ok then return false, "GameObject destruction failed: " .. tostring(err) end
    end
    instance.object, instance.player, instance.transform = nil, nil, nil
    -- Managed references acquired with add_ref are tracked by the Lua binding.
    -- Drop them only after the owning GameObject is destroyed; its settings may
    -- still consume the matrix list until then. Never force_release these.
    instance.point_cloud = nil
    if instance.resource then
        instance.resource.users = math.max(0, instance.resource.users - 1)
        instance.resource = nil
    end
    return true
end
local function key(client, id) return #client.owner .. ":" .. client.owner .. #id .. ":" .. id end
local function instance_counts(owner)
    local own, total = 0, 0
    for _, instance in pairs(M.instances) do
        total = total + 1
        if instance.owner == owner then own = own + 1 end
    end
    return own, total
end
local function stop(client, id)
    local k = key(client, id)
    local ok, err = destroy_instance(M.instances[k])
    if not ok then fail(err) end
    M.instances[k] = nil
    clear_unused()
    return "stopped"
end

function M.get_character()
    local pm = sdk.get_managed_singleton("app.PlayerManager")
    if not pm then return nil, "PlayerManager unavailable" end
    local master = pm:call("getMasterPlayer")
    if not master then return nil, "MasterPlayer unavailable" end
    local character = master:call("get_Character")
    if not character then return nil, "Character unavailable" end
    return character
end
function M.get_player_transform()
    local character, err = M.get_character()
    if not character then return nil, err end
    local go = character:call("get_GameObject")
    return go and go:call("get_Transform") or nil, "Player Transform unavailable"
end
function M.get_weapon_transform()
    local character, err = M.get_character()
    if not character then return nil, err end
    local weapon = character:call("get_Weapon")
    if not weapon then return nil, "Weapon unavailable" end
    local go = weapon:call("get_GameObject")
    return go and go:call("get_Transform") or nil, "Weapon Transform unavailable"
end

local function validate_point_cloud(options)
    if options == nil then return nil end
    if type(options) ~= "table" then fail("point_cloud must be a table") end
    local space = options.space == nil and "local" or options.space
    if space ~= "local" and space ~= "world" then fail("point_cloud.space must be local or world") end
    local input = options.indices == nil and {} or options.indices
    if type(input) ~= "table" then fail("point_cloud.indices must be a dense array") end
    local count, maximum = 0, 0
    for k, value in pairs(input) do
        if type(k) ~= "number" or k % 1 ~= 0 or k < 1 then
            fail("point_cloud.indices must be a dense array")
        end
        if type(value) ~= "number" or value % 1 ~= 0 or value < 0 or value > 31 then
            fail("point_cloud indices must be integers from 0 to 31")
        end
        count, maximum = count + 1, math.max(maximum, k)
    end
    if maximum ~= count then fail("point_cloud.indices must be a dense array") end
    local indices, seen = {}, {}
    for _, value in ipairs(input) do
        -- System.UInt32 requires a Lua integer, unlike System.Single values.
        local index = math.tointeger(value)
        if not seen[index] then indices[#indices + 1], seen[index] = index, true end
    end
    return { space = space, indices = indices }
end

local function verify_point_matrix(actual, expected)
    if not actual then fail("Point-cloud matrix readback unavailable") end
    for column = 0, 3 do
        for _, component in ipairs({ "x", "y", "z", "w" }) do
            local wanted = finite(expected[column][component], "point-cloud matrix component")
            local got = finite(actual[column][component], "point-cloud matrix readback")
            if math.abs(got - wanted) > math.max(0.00001, math.abs(wanted) * 0.00001) then
                fail("Point-cloud matrix readback mismatch")
            end
        end
    end
end

local function configure_point_cloud(instance, options)
    if not options then return end
    -- This is a synthetic single-point input, not a reconstruction of terrain
    -- or the game's grass system. The correct coordinate space and the meaning
    -- of each EFX's indexed channel require in-game validation.
    instance.point_cloud = { space = options.space, indices = options.indices }
    local state = instance.point_cloud
    local matrix
    if options.space == "world" then
        matrix = instance.transform:call("get_WorldMatrix")
    else
        matrix = Matrix4x4f.identity()
    end
    if not matrix then fail("Point-cloud source matrix unavailable") end
    state.points = sdk.create_managed_array("via.mat4", 1)
    if not state.points then fail("Point-cloud matrix array creation failed") end
    state.points:add_ref()
    -- The typed array API copies the value; no raw offsets or boxed GetValue
    -- mutations are involved. This source matrix is sampled once at Start.
    state.points:call("Set(System.Int32, via.mat4)", 0, matrix)
    verify_point_matrix(state.points:call("Get(System.Int32)", 0), matrix)
    state.settings = sdk.create_instance("via.effect.PointCloudEmitterSettings")
    if not state.settings then fail("PointCloudEmitterSettings creation failed") end
    state.settings:add_ref()
    state.settings:call("set_PointMatrixList(via.mat4[])", state.points)
    for _, index in ipairs(options.indices) do
        state.settings:call("setIndexedPointMatrixList(System.UInt32, via.mat4[])", index, state.points)
    end
    instance.player:call("set_PointCloudEmitterSettings(via.effect.PointCloudEmitterSettings)", state.settings)
    local bound = instance.player:call("get_PointCloudEmitterSettings")
    local bound_points = bound and bound:call("get_PointMatrixList")
    if not bound_points or bound_points:get_size() ~= 1 then
        fail("Point-cloud settings binding readback mismatch")
    end
    verify_point_matrix(bound_points:call("Get(System.Int32)", 0), matrix)
end

local function play(command)
    local options = command.options
    -- Reject malformed synthetic input before allocating resources or objects,
    -- preserving any previous preview with the same instance id.
    local point_cloud = validate_point_cloud(options.point_cloud)
    local start_action = action_value("Start")
    local scale = finite(options.scale or 1, "scale")
    if scale <= 0 then fail("scale must be greater than zero") end
    local p = options.offset or { x = 0, y = 0, z = 0 }
    local x, y, z = finite(p.x or 0, "x"), finite(p.y or 0, "y"), finite(p.z or 0, "z")
    local parent, parent_error
    if options.resolve_parent then
        parent, parent_error = options.resolve_parent()
        if not parent then fail(parent_error or "Attachment unavailable") end
    end
    local bone = options.bone_name
    if bone and bone ~= "" then
        if not parent then fail("A bone attachment requires a parent Transform") end
        if not parent:call("getJointByName", bone) then fail("Bone not found: " .. bone) end
    end
    if not M.instances[key(command.client, command.id)] then
        local own, total = instance_counts(command.client.owner)
        if own >= M.max_per_owner or total >= M.max_total then
            fail("Instance limit reached; stop unused instances before playing more")
        end
    end
    local resource = load_resource(options.path)
    local instance = { resource = resource, owner = command.client.owner, id = command.id, path = resource.path }
    resource.users = resource.users + 1
    local ok, err = pcall(function()
        local td = sdk.find_type_definition("via.GameObject")
        if not td then fail("via.GameObject unavailable") end
        instance.object = sdk.call_native_func(nil, td, "create(System.String)", "fx_preview_" .. key(command.client, command.id))
        if not instance.object then fail("GameObject creation failed") end
        instance.player = instance.object:call("createComponent(System.Type)", sdk.typeof("via.effect.EffectPlayer"))
        if not instance.player then fail("EffectPlayer creation failed") end
        instance.transform = instance.object:call("get_Transform")
        if not instance.transform then fail("Transform unavailable") end
        if parent then
            instance.transform:call("set_Parent(via.Transform)", parent)
            if bone and bone ~= "" then instance.transform:call("set_ParentJoint(System.String)", bone) end
            instance.transform:call("set_LocalPosition(via.vec3)", Vector3f.new(x, y, z))
        else
            instance.transform:call("set_Position(via.vec3)", Vector3f.new(x, y, z))
        end
        instance.transform:call("set_LocalScale(via.vec3)", Vector3f.new(scale, scale, scale))
        instance.player:call("set_AutoStart", false)
        instance.player:call("set_Resource", resource.holder)
        local speed = finite(options.play_speed == nil and 1.0 or options.play_speed, "play_speed")
        if speed < 0 then fail("play_speed cannot be negative") end
        instance.player:call("set_PlaySpeed", speed)
        local actual_speed = finite(instance.player:call("get_PlaySpeed"), "actual play_speed")
        if math.abs(actual_speed - speed) > math.max(0.000001, math.abs(speed) * 0.00001) then
            fail("EffectPlayer play speed readback mismatch: requested " .. tostring(speed) .. ", got " .. tostring(actual_speed))
        end
        instance.play_speed = actual_speed
        if options.mirror ~= nil then instance.player:call("set_MirrorX", options.mirror == true) end
        if options.color then
            -- Use the actual engine value type returned by the component;
            -- via.Color.new is not a standard REFramework Lua constructor.
            local color = instance.player:call("get_Color")
            if not color then fail("EffectPlayer.get_Color unavailable") end
            for _, channel in ipairs({ "r", "g", "b", "a" }) do
                local value = finite(options.color[channel], "color." .. channel)
                if value < 0 or value > 255 then fail("Color channels must be 0..255") end
                -- Verified in Wilds: via.Color exposes set_r/g/b/a(System.Byte),
                -- not r/g/b/a fields. Avoid assumptions about rgba byte order.
                local byte = math.floor(value + 0.5)
                color:call("set_" .. channel .. "(System.Byte)", byte)
                if color:call("get_" .. channel) ~= byte then fail("Color channel write failed: " .. channel) end
            end
            instance.player:call("set_Color", color)
        end
        configure_point_cloud(instance, point_cloud)
        instance.player:call("set_Action(via.effect.PlayerAction)", start_action)
    end)
    if not ok then
        local cleaned, cleanup_error = destroy_instance(instance)
        if not cleaned then
            -- Keep failed cleanup reachable for a later shutdown/retry.
            M.instances[key(command.client, command.id) .. "/failed-" .. command.request_id] = instance
            err = tostring(err) .. "; " .. tostring(cleanup_error)
        end
        clear_unused()
        fail(err)
    end
    local k = key(command.client, command.id)
    local cleaned, cleanup_error = destroy_instance(M.instances[k])
    if not cleaned then
        local rollback_ok, rollback_err = destroy_instance(instance)
        if not rollback_ok then M.instances[k .. "/failed-" .. command.request_id] = instance end
        clear_unused()
        fail(tostring(cleanup_error) .. (rollback_ok and "" or "; " .. tostring(rollback_err)))
    end
    M.instances[k] = instance
    clear_unused()
    return "Start submitted: " .. instance.path .. "; visual output not verified"
end

local function enqueue(client, op, id, options, callback)
    if not active then return nil, "Runtime is inactive" end
    if client.closed then return nil, "Client is closed" end
    if type(id) ~= "string" or id == "" then return nil, "instance_id must be a nonempty string" end
    if #M.queue >= 256 then return nil, "Preview command queue is full" end
    local copied, snapshot = pcall(clone, options or {})
    if not copied then return nil, tostring(snapshot) end
    sequence = sequence + 1
    M.queue[#M.queue + 1] = { client = client, op = op, id = id, options = snapshot, callback = callback, request_id = sequence }
    return sequence
end
function Client:play(id, options, callback) return enqueue(self, "play", id, options, callback) end
function Client:stop(id, callback) return enqueue(self, "stop", id, nil, callback) end
function Client:stop_all(callback) return enqueue(self, "stop_all", "*", nil, callback) end
function Client:action(id, name, callback) return enqueue(self, "action", id, { name = name }, callback) end
function Client:clear_cache(callback) return enqueue(self, "clear_cache", "*", nil, callback) end
function Client:shutdown(callback) return enqueue(self, "shutdown", "*", nil, callback) end
function Client:get_instance(id) return M.instances[key(self, id)] end
function Client:get_stats()
    local own, total = instance_counts(self.owner)
    return { instances = own, limit = M.max_per_owner, total = total, total_limit = M.max_total }
end
function Client:set_hotkeys(enabled)
    if enabled then M.hotkey_owner = self.owner elseif M.hotkey_owner == self.owner then M.hotkey_owner = nil end
end
function Client:owns_hotkeys() return M.hotkey_owner == self.owner end
function M.new(owner)
    assert(type(owner) == "string" and owner ~= "", "owner must be a nonempty string")
    if M.clients[owner] and not M.clients[owner].closed then return M.clients[owner] end
    local client = setmetatable({ owner = owner, closed = false }, Client)
    M.clients[owner] = client
    return client
end
local function stop_owner(owner)
    local errors = {}
    for k, instance in pairs(M.instances) do
        if not owner or instance.owner == owner then
            local ok, err = destroy_instance(instance)
            if ok then M.instances[k] = nil else errors[#errors + 1] = err end
        end
    end
    clear_unused()
    if #errors > 0 then fail(table.concat(errors, "; ")) end
end
local function execute(command)
    if command.client.closed then fail("Client is closed") end
    if command.op == "play" then return play(command) end
    if command.op == "stop" then return stop(command.client, command.id) end
    if command.op == "stop_all" then stop_owner(command.client.owner); return "all client instances stopped" end
    if command.op == "clear_cache" then
        clear_unused()
        return "Unused Lua cache cleared; engine cache is NOT invalidated. Use a versioned resource path."
    end
    if command.op == "shutdown" then
        stop_owner(command.client.owner)
        command.client.closed = true
        command.client:set_hotkeys(false)
        return "client shut down"
    end
    if command.op == "action" then
        if command.options.name == "Kill" then return stop(command.client, command.id) end
        local instance = command.client:get_instance(command.id)
        if not instance then fail("No active instance: " .. command.id) end
        instance.player:call("set_Action(via.effect.PlayerAction)", action_value(command.options.name))
        return "PlayerAction submitted: " .. command.options.name
    end
    fail("Unknown operation: " .. tostring(command.op))
end
local function current_scene()
    local td = sdk.find_type_definition("via.SceneManager")
    local manager = sdk.get_native_singleton("via.SceneManager")
    if not td or not manager then return nil end
    local scene = sdk.call_native_func(manager, td, "get_CurrentScene")
    return scene and scene:get_address() or nil
end
function M.drain()
    if not active then return end
    local scene_ok, scene = pcall(current_scene)
    if scene_ok and last_scene and scene ~= last_scene then
        local pending = M.queue
        M.queue = {}
        for _, command in ipairs(pending) do notify(command, false, "Scene changed; request cancelled") end
        local ok, err = pcall(stop_owner, nil)
        if not ok then log_error(err) end
    end
    if scene_ok then last_scene = scene end
    -- Bound work and process only this batch. Hook-triggered commands run on
    -- the next LockScene, never recursively inside an engine callback.
    local batch = M.queue
    M.queue = {}
    for i = 33, #batch do M.queue[#M.queue + 1] = batch[i] end
    for i, command in ipairs(batch) do
        if i <= 32 then
            local ok, result = pcall(execute, command)
            notify(command, ok, result)
        end
    end
end
function M.reset()
    active = false
    local pending = M.queue
    M.queue = {}
    for _, command in ipairs(pending) do notify(command, false, "Lua reset; request cancelled") end
    local ok, err = pcall(stop_owner, nil)
    if not ok then log_error("reset cleanup: " .. tostring(err)) end
    M.hotkey_owner = nil
    for _, client in pairs(M.clients) do client.closed = true end
    _G.fx_preview_runtime_v2 = nil
    return ok, err
end
re.on_pre_application_entry("LockScene", function() if active then M.drain() end end)
re.on_script_reset(function() if active then M.reset() end end)
active = true
_G.fx_preview_runtime_v2 = M
return M
