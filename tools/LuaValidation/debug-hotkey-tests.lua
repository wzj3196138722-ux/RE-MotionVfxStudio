local factory=assert(loadfile(arg[1]))()
local checks=0
local function eq(a,b) checks=checks+1; assert(a==b,"check "..checks..": "..tostring(a).." ~= "..tostring(b)) end
local function env(saved)
    local c={saved=saved,menu=false,blocked=false,toggles=0,native={version="fixture"}, sample={triggers=0,focused=true,pressed={}}}
    c.native.configure=function(enable,keys,record) c.enabled,c.keys,c.record=enable,keys,record; c.sample.enabled=enable; return true end
    c.native.sample=function() return c.sample end
    c.native.disable=function() c.enabled,c.record=false,false; c.disabled=(c.disabled or 0)+1 end
    c.controller=factory.new{native=c.native,
        read=function() return c.saved end,
        write=function(_,value) if c.fail_save then return false end; c.saved=value; return true end,
        menu_open=function() return c.menu end,blocked=function() return c.blocked end,
        text_input_active=function() return c.typing end,
        toggle=function() c.toggles=c.toggles+1; return true end}
    return c
end
local keys=factory.parse("Ctrl + Shift + Alt + P")
eq(#keys,4); eq(factory.label(keys),"Ctrl + Shift + Alt + P")
eq(#factory.parse("Ctrl+Shift+Alt+A+B+C+D+E"),8)
for _,value in ipairs({"","Ctrl+Ctrl+P","Ctrl+","Insert","Win+P","Alt+Tab","Alt+F4","Ctrl+Alt+Delete",
    "Ctrl+Esc","Ctrl+Shift+Alt+A+B+C+D+E+F"}) do eq(factory.parse(value),nil) end
local c=env(); eq(c.controller.enabled,false); eq(c.controller:enable(true),true)
c.controller:tick(); c.sample.triggers=1; c.controller:tick(); eq(c.toggles,1)
c.controller:tick(); eq(c.toggles,1)
c.menu=true; c.sample.triggers=2; c.controller:tick(); eq(c.toggles,2); eq(c.enabled,true)
eq(c.controller:status().gate,"active"); eq(c.controller:status().nativeEnabled,true)
c.typing=true; c.sample.triggers=3; c.controller:tick(); eq(c.toggles,2); eq(c.enabled,false)
eq(c.controller:status().gate,"text_input")
-- A stale text focus must not block keys after hiding the overlay.
c.menu=false; c.controller:tick(); eq(c.toggles,2); eq(c.enabled,true)
c.sample.triggers=4; c.controller:tick(); eq(c.toggles,3)
c.sample.focused=false; c.sample.triggers=5; c.controller:tick(); eq(c.toggles,3)
eq(c.controller:status().gate,"unfocused"); c.sample.focused=true
c.blocked=true; c.controller:tick(); eq(c.enabled,false); c.blocked=false
c.controller:disable(); c.sample.triggers=6; c.controller:tick(); eq(c.toggles,3)
c.controller.draft="Ctrl+Shift+Alt+P"; eq(c.controller:save(),true); eq(#c.saved.keys,4)
local fresh=env(c.saved); eq(#fresh.controller.keys,4); eq(fresh.controller.enabled,false)
c.fail_save=true; c.controller.draft="F9"; eq(c.controller:save(),false); eq(#c.controller.keys,4)
c.fail_save=false; c.controller.draft="Ctrl+Ctrl"; eq(c.controller:save(),false); eq(#c.controller.keys,4)
c.menu=true; c.controller:begin_record()
c.sample.pressed={17}; c.controller:tick()
c.sample.pressed={16,17,18,80}; c.controller:tick()
c.sample.pressed={16,17}; c.controller:tick()
c.sample.pressed={}; c.controller:tick()
eq(c.controller.recording,false); eq(c.controller.draft,"Ctrl + Shift + Alt + P")
eq(c.controller:save(),true); eq(c.toggles,3)
c.controller:begin_record(); c.sample.focused=false; c.controller:tick(); eq(c.controller.recording,false)
c.sample.focused=true; c.controller:begin_record(); c.menu=false; c.controller:tick(); eq(c.controller.recording,false)
local unavailable=factory.new{read=function() return nil end,write=function() end}
eq(unavailable:enable(true),false); unavailable:tick(); eq(unavailable.enabled,false)
local bad=env({schema=1,keys={999}}); eq(factory.label(bad.controller.keys),"Ctrl + Shift + P")
local old=env(); old.native.version="1.0.0"; eq(old.controller:enable(true),false); eq(old.controller.enabled,false)
c.native.sample=function() error("read failure") end; c.controller:enable(true); c.controller:tick()
eq(c.controller.enabled,false)
print("PASS: "..checks.." debug hotkey policy checks")
