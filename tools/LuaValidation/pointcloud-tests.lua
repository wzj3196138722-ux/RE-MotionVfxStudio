-- Offline mocks only: this does not prove coordinate-space/channel semantics
-- or visible rendering in the game. Run with the Lua materials directory cwd.
local passed, failed, assertions = 0, 0, 0
local function check(condition, message)
    assertions = assertions + 1
    assert(condition, message or "assertion failed")
end
local function equal(actual, expected, message)
    check(actual == expected, (message or "value") .. ": expected " .. tostring(expected) .. ", got " .. tostring(actual))
end
local function test(name, fn)
    local ok, err = pcall(fn)
    if ok then passed = passed + 1; print("PASS " .. name)
    else failed = failed + 1; print("FAIL " .. name .. ": " .. tostring(err)) end
end
local function identity()
    local result = {}
    for column = 0, 3 do
        result[column] = {}
        for row, component in ipairs({"x", "y", "z", "w"}) do
            result[column][component] = column == row - 1 and 1.0 or 0.0
        end
    end
    return result
end
local function copy_matrix(value)
    local result = identity()
    for column = 0, 3 do
        for _, component in ipairs({"x", "y", "z", "w"}) do
            result[column][component] = value[column][component]
        end
    end
    return result
end
local function environment()
    local c = { objects = {}, resources = {}, callbacks = {}, calls = {}, scene = 1,
        arrays = 0, settings = 0, retained = 0, collected = 0, world_reads = 0,
        world = identity(), enum = {Start = 17, Kill = 23} }
    c.world[3].x, c.world[3].y, c.world[3].z = 12.5, 20.0, -9.0
    local env = setmetatable({}, {__index = _G}); env._G = env
    env.log = {warn = function() end}
    env.Vector3f = {new = function(x,y,z) return {x=x,y=y,z=z} end}
    env.Matrix4x4f = {identity = identity}
    env.re = {
        on_pre_application_entry = function(name, fn) c.callbacks[name] = fn end,
        on_script_reset = function(fn) c.callbacks.reset = fn end,
    }
    local function managed(value)
        function value:add_ref()
            check(not self.retained, "mock managed ref acquired twice")
            self.retained = true; c.retained = c.retained + 1
            return self
        end
        function value:force_release() error("force_release is forbidden") end
        function value:release() error("managed refs must be dropped for Lua GC") end
        return setmetatable(value, {__gc = function(object)
            if object.retained then c.retained = c.retained - 1; c.collected = c.collected + 1 end
        end})
    end
    local function record(name)
        c.calls[#c.calls + 1] = name
        if c.fail_method == name then error("injected failure: " .. name) end
    end
    local function game_object()
        local object = {destroyed = false, transform = {}, player = {speed = 1.0}}
        function object.transform:call(name)
            record(name)
            if name == "get_WorldMatrix" then
                c.world_reads = c.world_reads + 1
                if c.no_world then return nil end
                return copy_matrix(c.world)
            end
        end
        function object.player:call(name, value)
            record(name)
            if name == "set_PlaySpeed" then equal(math.type(value), "float"); self.speed = value
            elseif name == "get_PlaySpeed" then return self.speed
            elseif name == "set_PointCloudEmitterSettings(via.effect.PointCloudEmitterSettings)" then
                if not c.no_binding then self.point_settings = value end
            elseif name == "get_PointCloudEmitterSettings" then return self.point_settings
            elseif name == "set_Action(via.effect.PlayerAction)" then self.action = value end
        end
        function object:call(name)
            record(name)
            if name == "createComponent(System.Type)" then return self.player end
            if name == "get_Transform" then return self.transform end
            if name == "destroy" then
                if c.fail_destroy then error("injected destruction failure") end
                self.destroyed = true
                -- Model the engine releasing its settings when its own player
                -- is destroyed. c.objects intentionally keeps the mock shell.
                self.player.point_settings = nil
            end
        end
        c.objects[#c.objects + 1] = object
        return object
    end
    env.sdk = {
        find_type_definition = function(name)
            if name == "via.effect.PlayerAction" then
                return {get_field = function(_, field)
                    return {get_data = function() return c.enum[field] end}
                end}
            end
            return {name = name}
        end,
        get_native_singleton = function() return {} end,
        typeof = function(name) return name end,
        call_native_func = function(_, _, method)
            if method == "get_CurrentScene" then return {get_address = function() return c.scene end} end
            if method == "create(System.String)" then return game_object() end
            error("unmocked native method: " .. method)
        end,
        create_resource = function(_, path)
            local resource = {path = path, releases = 0}
            function resource:add_ref() end
            function resource:release() self.releases = self.releases + 1 end
            function resource:create_holder() return {add_ref = function() end} end
            c.resources[#c.resources + 1] = resource
            return resource
        end,
        create_managed_array = function(name, length)
            record("create_managed_array"); equal(name, "via.mat4"); equal(length, 1)
            c.arrays = c.arrays + 1
            if c.no_array then return nil end
            local points = managed({})
            function points:get_size() return 1 end
            function points:call(method, index, value)
                record(method); equal(index, 0); equal(math.type(index), "integer")
                if method == "Set(System.Int32, via.mat4)" then self.matrix = copy_matrix(value)
                elseif method == "Get(System.Int32)" then
                    if c.no_readback then return nil end
                    local result = copy_matrix(self.matrix)
                    if c.bad_readback then result[2].y = 1.0 end
                    return result
                else error("unmocked matrix-array method: " .. method) end
            end
            return points
        end,
        create_instance = function(name, simplify)
            record("create_instance"); equal(name, "via.effect.PointCloudEmitterSettings")
            equal(simplify, nil, "normal initialization is required")
            c.settings = c.settings + 1
            if c.no_settings then return nil end
            local settings = managed({indices = {}, indexed = {}})
            function settings:call(method, first, second)
                record(method)
                if method == "set_PointMatrixList(via.mat4[])" then self.points = first
                elseif method == "get_PointMatrixList" then return self.points
                elseif method == "setIndexedPointMatrixList(System.UInt32, via.mat4[])" then
                    equal(math.type(first), "integer", "UInt32 ABI")
                    self.indices[#self.indices + 1] = first; self.indexed[first] = second
                else error("unmocked point-cloud settings method: " .. method) end
            end
            return settings
        end,
    }
    c.runtime = assert(loadfile("fx_preview_runtime.lua", "t", env))()
    c.client = c.runtime.new("point-cloud-test")
    function c:drain() self.callbacks.LockScene() end
    function c:play(options)
        local result
        check(self.client:play("preview", options or {path = "test.efx"}, function(value) result = value end))
        self:drain()
        return result
    end
    function c:shutdown()
        self.client:shutdown(); self:drain()
        collectgarbage("collect"); collectgarbage("collect")
    end
    return c
end
local function enabled(space, indices)
    return {path = "test.efx", point_cloud = {space = space, indices = indices}}
end

test("capability is explicit and omitted option preserves playback", function()
    local c = environment()
    equal(c.runtime.version, "2.2"); equal(c.runtime.capabilities.point_cloud, true)
    equal(c:play().ok, true); equal(c.arrays, 0); equal(c.settings, 0)
    equal(c.world_reads, 0); equal(c.client:get_instance("preview").point_cloud, nil)
end)
test("local synthetic input is identity and configured before Start", function()
    local c = environment(); local result = c:play(enabled("local", {0, 2, 4}))
    equal(result.ok, true, result.message)
    local state = c.client:get_instance("preview").point_cloud
    equal(state.space, "local"); equal(c.world_reads, 0); equal(c.arrays, 1)
    equal(state.points.matrix[0].x, 1); equal(state.points.matrix[3].x, 0)
    equal(c.objects[1].player.point_settings, state.settings)
    equal(state.settings.indexed[4], state.points)
    local binding, start
    for index, call in ipairs(c.calls) do
        if call == "set_PointCloudEmitterSettings(via.effect.PointCloudEmitterSettings)" then binding = index end
        if call == "set_Action(via.effect.PlayerAction)" then start = index end
    end
    check(binding and start and binding < start, "point-cloud settings must precede Start")
end)
test("world input copies the instance matrix without changing transform", function()
    local c = environment(); equal(c:play(enabled("world", {})).ok, true)
    local matrix = c.client:get_instance("preview").point_cloud.points.matrix
    equal(c.world_reads, 1); equal(matrix[3].x, 12.5); equal(matrix[3].y, 20); equal(matrix[3].z, -9)
    c.world[3].x = 99; equal(matrix[3].x, 12.5, "sampled matrix is a copy")
end)
test("duplicate channels are collapsed and integer-valued floats become integers", function()
    local c = environment(); equal(c:play(enabled("local", {4.0, 0, 4, 31, 0})).ok, true)
    local state = c.client:get_instance("preview").point_cloud
    equal(#state.indices, 3); equal(state.indices[1], 4); equal(state.indices[2], 0); equal(state.indices[3], 31)
    equal(#state.settings.indices, 3)
end)
test("empty enabled options select identity with the unindexed list", function()
    local c = environment(); equal(c:play({path = "test.efx", point_cloud = {}}).ok, true)
    local state = c.client:get_instance("preview").point_cloud
    equal(state.space, "local"); equal(#state.indices, 0); equal(state.settings.points, state.points)
end)
test("invalid point-cloud options fail before resource and object creation", function()
    local invalid = {
        false, true, "enabled", {space = false}, {space = "camera"}, {indices = false}, {indices = "0,1"},
        {indices = {-1}}, {indices = {32}}, {indices = {0.5}}, {indices = {"0"}},
        {indices = {0/0}}, {indices = {math.huge}}, {indices = {[0] = 0}},
        {indices = {[2] = 1}}, {indices = {first = 0}},
    }
    for _, option in ipairs(invalid) do
        local c = environment(); equal(c:play({path = "test.efx", point_cloud = option}).ok, false)
        equal(#c.objects, 0); equal(#c.resources, 0); equal(c.arrays, 0)
    end
end)
test("invalid replacement preserves a running instance", function()
    local c = environment(); equal(c:play().ok, true); local old = c.client:get_instance("preview")
    equal(c:play(enabled("local", {99})).ok, false)
    equal(c.client:get_instance("preview"), old); equal(c.objects[1].destroyed, false); equal(#c.objects, 1)
end)
test("queued point-cloud options use a snapshot", function()
    local c = environment(); local options = enabled("local", {1})
    c.client:play("preview", options); options.point_cloud.indices[1] = 4; options.point_cloud.space = "world"
    c:drain(); local state = c.client:get_instance("preview").point_cloud
    equal(state.indices[1], 1); equal(state.space, "local")
end)
for _, mode in ipairs({"no_array", "no_settings", "no_world", "no_readback", "bad_readback", "no_binding"}) do
    test(mode .. " rolls back the preview and preserves prior ownership", function()
        local c = environment(); equal(c:play().ok, true); local old = c.client:get_instance("preview")
        c[mode] = true
        local result = c:play(enabled(mode == "no_world" and "world" or "local", {0}))
        equal(result.ok, false)
        equal(c.client:get_instance("preview"), old); equal(c.objects[1].destroyed, false)
        equal(c.objects[2].destroyed, true); equal(c.objects[2].player.action, 23)
        if mode == "bad_readback" then check(result.message:find("readback mismatch", 1, true)) end
        collectgarbage("collect"); collectgarbage("collect"); equal(c.retained, 0)
        c:shutdown(); equal(c.resources[1].releases, 1)
    end)
end
test("settings-binding failure destroys the partial instance and frees references", function()
    local c = environment(); c.fail_method = "set_PointCloudEmitterSettings(via.effect.PointCloudEmitterSettings)"
    equal(c:play(enabled("local", {0})).ok, false)
    equal(c.objects[1].destroyed, true); equal(c.resources[1].releases, 1)
    collectgarbage("collect"); collectgarbage("collect")
    equal(c.retained, 0); equal(c.collected, 2)
end)
test("shutdown drops managed references only after object destruction", function()
    local c = environment(); equal(c:play(enabled("local", {0})).ok, true)
    local instance = c.client:get_instance("preview")
    collectgarbage("collect"); equal(c.retained, 2)
    c:shutdown()
    equal(instance.point_cloud, nil); equal(c.objects[1].destroyed, true)
    equal(c.retained, 0); equal(c.collected, 2); equal(c.client.closed, true)
end)
test("failed destruction retains input references for cleanup retry", function()
    local c = environment(); equal(c:play(enabled("local", {0})).ok, true)
    local instance = c.client:get_instance("preview"); c.fail_destroy = true
    c:shutdown(); equal(c.retained, 2); check(instance.point_cloud); equal(c.client.closed, false)
    c.fail_destroy = false; c:shutdown()
    equal(c.retained, 0); equal(c.collected, 2); equal(instance.point_cloud, nil)
end)
test("scene changes clean synthetic input and cancel stale queued work", function()
    local c = environment(); equal(c:play(enabled("local", {0})).ok, true)
    local instance = c.client:get_instance("preview"); local result
    c.client:play("next", enabled("world", {1}), function(value) result = value end)
    c.scene = 2; c:drain(); collectgarbage("collect"); collectgarbage("collect")
    equal(result.ok, false); equal(#c.objects, 1); equal(instance.point_cloud, nil); equal(c.retained, 0)
end)
test("script reset removes owned synthetic input and deactivates runtime", function()
    local c = environment(); equal(c:play(enabled("local", {0})).ok, true)
    local instance = c.client:get_instance("preview"); c.callbacks.reset()
    collectgarbage("collect"); collectgarbage("collect")
    equal(instance.point_cloud, nil); equal(c.retained, 0); equal(c.client.closed, true)
end)
print(("RESULT pointcloud %d passed, %d failed, %d assertions"):format(passed, failed, assertions))
if failed > 0 then error("point-cloud regression failures") end
