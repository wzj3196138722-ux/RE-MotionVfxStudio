-- Run with the materials directory as cwd. No game, network or filesystem writes.
local passed, failed = 0, 0
local function equal(actual, expected, message)
    assert(actual == expected, (message or "value") .. ": expected " .. tostring(expected) .. ", got " .. tostring(actual))
end
local function test(name, fn)
    local ok, err = pcall(fn)
    if ok then passed = passed + 1; print("PASS " .. name)
    else failed = failed + 1; print("FAIL " .. name .. ": " .. tostring(err)) end
end

local function environment()
    local c = { scene = 101, objects = {}, resources = {}, callbacks = {}, logs = {},
        enum = { Start = 17, Kill = 23, Pause = 31, Resume = 37, Step = 41, Finish = 43 } }
    local env = setmetatable({}, { __index = _G }); env._G = env
    env.log = { warn = function(s) c.logs[#c.logs+1] = s end, info = function() end }
    env.Vector3f = { new = function(x,y,z)
        equal(math.type(x), "float", "vector x ABI"); equal(math.type(y), "float", "vector y ABI"); equal(math.type(z), "float", "vector z ABI")
        return { x=x,y=y,z=z }
    end }
    env.re = {
        on_pre_application_entry = function(name, fn) c.callbacks[name] = fn end,
        on_script_reset = function(fn) c.callbacks.reset = fn end,
    }
    local function object()
        local obj = { destroyed = false, calls = {}, transform = { calls = {} }, player = { calls = {}, color = {}, speed = 1.0, history = {} } }
        function obj.transform:call(name, value) self.calls[name] = value; return true end
        function obj.player:call(name, value)
            if c.fail_method == name then error("injected: " .. name) end
            if name == "get_Color" then return self.color end
            self.history[#self.history + 1] = name
            if name == "set_PlaySpeed" then
                equal(math.type(value), "float", "System.Single receives a Lua float")
                self.speed = value
            elseif name == "get_PlaySpeed" then return c.speed_readback == nil and self.speed or c.speed_readback end
            self.calls[name] = value
            return true
        end
        function obj:call(name, value)
            if name == "createComponent(System.Type)" then if c.no_component then return nil end; return self.player end
            if name == "get_Transform" then return self.transform end
            if name == "destroy" then
                if c.fail_destroy then error("injected destruction failure") end
                self.destroyed = true; return
            end
            error("unmocked object method: " .. name)
        end
        c.objects[#c.objects+1] = obj
        return obj
    end
    env.sdk = {
        find_type_definition = function(name)
            if name == "via.effect.PlayerAction" then
                return { get_field = function(_, key)
                    if c.enum[key] == nil then return nil end
                    return { get_data = function() return c.enum[key] end }
                end }
            end
            return { name = name }
        end,
        get_native_singleton = function() return {} end,
        call_native_func = function(_, _, name)
            if name == "get_CurrentScene" then
                if c.scene == nil then return nil end
                return { get_address = function() return c.scene end }
            end
            if name == "create(System.String)" then if c.no_object then return nil end; return object() end
            error("unmocked native method: " .. name)
        end,
        typeof = function(name) return name end,
        create_resource = function(_, path)
            if c.no_resource then return nil end
            local resource = { path = path, adds = 0, releases = 0 }
            function resource:add_ref() self.adds = self.adds + 1 end
            function resource:release() self.releases = self.releases + 1; assert(self.releases <= self.adds, "raw double release") end
            function resource:create_holder()
                if c.no_holder then return nil end
                return { add_ref = function() end }
            end
            c.resources[#c.resources+1] = resource
            return resource
        end,
        get_managed_singleton = function() return nil end,
    }
    c.runtime = assert(loadfile("fx_preview_runtime.lua", "t", env))()
    c.env = env
    function c:drain() self.callbacks.LockScene() end
    return c
end
local function play(c, client, id, options)
    local result
    assert(client:play(id, options or {path="test/effect.efx"}, function(r) result = r end))
    c:drain()
    return result
end

test("commands defer engine mutations until safe callback", function()
    local c = environment(); local client = c.runtime.new("editor")
    local r; assert(client:play("one", {path="a.efx"}, function(x) r=x end))
    equal(#c.objects, 0); equal(r, nil)
    c:drain(); equal(r.ok, true); equal(r.status, "applied")
    equal(c.objects[1].player.calls["set_Action(via.effect.PlayerAction)"], 17, "runtime enum, not 1")
end)
test("integer and integer-text speeds are converted before the native float call", function()
    local c = environment(); local a = c.runtime.new("editor")
    equal(c.runtime.version, "2.2")
    for index, speed in ipairs({ 1, "1", 0, "0", 2, "2", "0.125" }) do
        local result = play(c, a, tostring(index), { path="a.efx", play_speed=speed, scale=1, offset={x=0,y=1,z=-1} })
        equal(result.ok, true, result.message)
        local instance = a:get_instance(tostring(index))
        equal(math.type(instance.player.calls.set_PlaySpeed), "float")
        equal(instance.player.calls.set_PlaySpeed, tonumber(speed))
        equal(instance.play_speed, tonumber(speed))
        local read_index, start_index
        for i, name in ipairs(instance.player.history) do
            if name == "get_PlaySpeed" then read_index = i end
            if name == "set_Action(via.effect.PlayerAction)" then start_index = i end
        end
        assert(read_index and start_index and read_index < start_index, "speed must be verified before Start")
    end
end)
test("omitted speed explicitly initializes and verifies native float one", function()
    local c=environment(); local a=c.runtime.new("editor")
    equal(play(c,a,"one").ok,true)
    equal(c.objects[1].player.calls.set_PlaySpeed,1.0)
    equal(math.type(c.objects[1].player.calls.set_PlaySpeed),"float")
end)
test("incorrect native speed readback rejects the replacement and retains the old instance", function()
    local c=environment(); local a=c.runtime.new("editor")
    equal(play(c,a,"one").ok,true); local old=a:get_instance("one")
    c.speed_readback=0.0
    local result=play(c,a,"one",{path="new.efx",play_speed="1"})
    equal(result.ok,false); assert(result.message:find("readback mismatch",1,true))
    equal(a:get_instance("one"),old); equal(c.objects[1].destroyed,false); equal(c.objects[2].destroyed,true)
    equal(c.resources[2].releases,1)
end)
test("same-resource instances have independent lifetime", function()
    local c = environment(); local a = c.runtime.new("editor")
    assert(play(c,a,"one").ok); assert(play(c,a,"two").ok)
    equal(#c.resources,1); equal(c.resources[1].releases,0)
    a:stop("one"); c:drain()
    equal(c.objects[1].destroyed,true); equal(c.objects[2].destroyed,false)
    assert(a:get_instance("two")); equal(c.resources[1].releases,0)
    a:clear_cache(); c:drain(); equal(c.resources[1].releases,0)
    a:stop("two"); c:drain(); equal(c.resources[1].releases,1)
end)
test("owner shutdown never stops another owner", function()
    local c=environment(); local a,b=c.runtime.new("a"),c.runtime.new("b")
    assert(play(c,a,"one").ok); assert(play(c,b,"one").ok)
    a:shutdown(); c:drain(); equal(a.closed,true); assert(b:get_instance("one")); equal(c.objects[2].destroyed,false)
end)
test("owner and instance identifiers cannot collide", function()
    local c=environment(); local a,b=c.runtime.new("a/b"),c.runtime.new("a")
    assert(play(c,a,"c").ok); assert(play(c,b,"b/c").ok)
    assert(a:get_instance("c") ~= b:get_instance("b/c"), "ownership keys collided")
    a:shutdown(); c:drain(); assert(b:get_instance("b/c")); equal(c.objects[2].destroyed,false)
end)
test("local and traversal paths rejected without losing previous instance", function()
    local c=environment(); local a=c.runtime.new("a"); assert(play(c,a,"one").ok)
    local old=a:get_instance("one")
    for _,path in ipairs({"C:/temp/a.efx", "../a.efx", "/absolute.efx", "a.txt"}) do
        equal(play(c,a,"one",{path=path}).ok,false); equal(a:get_instance("one"),old)
    end
    equal(#c.resources,1)
end)
test("missing enum fails instead of assuming action integer", function()
    local c=environment(); c.enum.Start=nil
    equal(play(c,c.runtime.new("a"),"one").ok,false); equal(#c.objects,0)
end)
test("partial object creation failure cleans object and raw resource", function()
    local c=environment(); c.no_component=true
    equal(play(c,c.runtime.new("a"),"one").ok,false)
    equal(c.objects[1].destroyed,true); equal(c.resources[1].releases,1)
end)
test("holder failure releases acquired raw reference", function()
    local c=environment(); c.no_holder=true
    equal(play(c,c.runtime.new("a"),"one").ok,false); equal(c.resources[1].releases,1); equal(#c.objects,0)
end)
test("failed replacement keeps previous instance", function()
    local c=environment(); local a=c.runtime.new("a"); assert(play(c,a,"one").ok)
    local old=a:get_instance("one"); c.fail_method="set_Resource"
    equal(play(c,a,"one",{path="new.efx"}).ok,false)
    equal(a:get_instance("one"),old); equal(c.objects[1].destroyed,false); equal(c.objects[2].destroyed,true)
end)
test("missing bone rejected before resource creation", function()
    local c=environment(); local a=c.runtime.new("a")
    local parent={call=function() return nil end}
    equal(play(c,a,"one",{path="a.efx",bone_name="missing",resolve_parent=function() return parent end}).ok,false)
    equal(#c.resources,0)
end)
test("pause controls only the selected instance", function()
    local c=environment(); local a=c.runtime.new("a"); play(c,a,"one"); play(c,a,"two")
    a:action("one","Pause"); c:drain()
    equal(c.objects[1].player.calls["set_Action(via.effect.PlayerAction)"],31)
    equal(c.objects[2].player.calls["set_Action(via.effect.PlayerAction)"],17)
end)
test("queued options are snapshots of editable inputs", function()
    local c=environment(); local a=c.runtime.new("a"); local o={path="old.efx",offset={x=2,y=3,z=4}}
    a:play("one",o); o.path="new.efx"; o.offset.x=9; c:drain()
    equal(c.resources[1].path,"old.efx"); equal(c.objects[1].transform.calls["set_Position(via.vec3)"].x,2)
end)
test("scene change cancels obsolete pending commands and cleans instances", function()
    local c=environment(); local a=c.runtime.new("a"); play(c,a,"one")
    local r; a:play("two",{path="a.efx"},function(x) r=x end); c.scene=202; c:drain()
    equal(r.ok,false); equal(#c.objects,1); equal(c.objects[1].destroyed,true); equal(a:get_instance("one"),nil)
end)
test("reset cancels pending commands and cleans owned resources", function()
    local c=environment(); local a=c.runtime.new("a"); play(c,a,"one")
    local r; a:play("two",{path="a.efx"},function(x) r=x end); c.callbacks.reset()
    equal(r.ok,false); equal(c.objects[1].destroyed,true); equal(c.resources[1].releases,1)
    equal(a:play("three",{path="a.efx"}),nil)
end)
test("hotkey claim has a single active owner", function()
    local c=environment(); local a,b=c.runtime.new("a"),c.runtime.new("b")
    equal(a:owns_hotkeys(),false); a:set_hotkeys(true); equal(a:owns_hotkeys(),true)
    b:set_hotkeys(true); equal(a:owns_hotkeys(),false); equal(b:owns_hotkeys(),true)
    a:set_hotkeys(false); equal(b:owns_hotkeys(),true)
end)
test("queue is bounded with explicit backpressure", function()
    local c=environment(); local a=c.runtime.new("a")
    for i=1,256 do assert(a:clear_cache()) end
    local request,err=a:clear_cache(); equal(request,nil); assert(err:find("full",1,true))
    c:drain(); equal(#c.runtime.queue,224)
end)
test("older queued commands remain ahead of callback-generated commands", function()
    local c=environment(); local a=c.runtime.new("a"); local order={}
    a:clear_cache(function() a:clear_cache(function() order[#order+1]="new" end) end)
    for i=2,33 do a:clear_cache(function() order[#order+1]=i end) end
    c:drain(); c:drain(); equal(order[#order-1],33); equal(order[#order],"new")
end)
test("destruction failure retains tracked ownership for retry", function()
    local c=environment(); local a=c.runtime.new("a"); play(c,a,"one"); c.fail_destroy=true
    local r; a:stop("one",function(x) r=x end); c:drain()
    equal(r.ok,false); assert(a:get_instance("one")); equal(c.resources[1].releases,0)
    c.fail_destroy=false; a:stop("one"); c:drain(); equal(c.resources[1].releases,1)
end)
print(("RESULT playback %d passed, %d failed"):format(passed,failed))
if failed>0 then error("playback regression failures") end
