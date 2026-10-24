-- Real Dear ImGui input + production Lua widgets. No OS/game input is sent.
local project=assert(arg[1])
local N=native_imgui
Vector2f={new=function(x,y)return {x=x,y=y}end}
local widgets=assert(loadfile(project.."/game-plugin/MotionVfxLive/fx_d2d_widgets.lua"))()
local passed=0
local function check(v,message) assert(v,message);passed=passed+1;print("PASS "..message) end
local function near(a,b) return math.abs(a-b)<1.6 end
local cases={
    {scale=.75,font=32,alpha=1}, {scale=1,font=16,alpha=1},
    {scale=1.3,font=48,alpha=1}, {scale=1,font=32,alpha=0},
}
local function setup(config,prefix,initial,width)
    N.text_reset({1920,1080},config.font,{38,26},{32,32},8,config.alpha,"C:/Windows/Fonts/msyh.ttc")
    local u=widgets.new(N,{})
    local c={value=initial or "art/vfx/effecteditor/weapon/it02/11_it02_003.efx",x=320,y=240,scale=config.scale}
    local tick=0
    function c:frame(mx,my,down)
        tick=tick+1
        N.begin_frame(mx or -100,my or -100,down or false)
        N.set_next_window_pos({200,160});N.set_next_window_size({1400,650})
        N.push_style_var(0,1)
        for _,id in ipairs({0,2,3,5,7,8,9}) do N.push_style_color(id,0) end
        N.begin_window("Input test",true,1+2+4+8+32+256)
        if self.measure_request then self.measured=N.measure(self.measure_request,14*self.scale);self.measure_request=nil end
        u:begin(config.scale);u:at(c.x,c.y)
        local changed,next_value=u:textbox("##tested",self.value,(width or 480)*config.scale,prefix)
        self.value=next_value;self.changed=changed;self.snapshot=N.input_snapshot()
        self.commands=u.commands
        u:space(1)
        N.end_window();N.pop_style_color(7);N.pop_style_var(1);N.end_frame()
        local alpha=N.style();assert(alpha==config.alpha,"input style alpha leaked")
        return self.snapshot
    end
    function c:idle(n)for _=1,n or 1 do self:frame() end end
    function c:click(x)
        self:idle(25) -- separate clicks, not a synthetic double click
        self:frame(x,self.y+14*self.scale,false);self:frame(x,self.y+14*self.scale,true)
        return self:frame(x,self.y+14*self.scale,false)
    end
    function c:key(name,shift,ctrl)
        if shift then N.key_event("shift",true) end
        if ctrl then N.key_event("ctrl",true) end
        N.key_event(name,true);self:frame();N.key_event(name,false)
        if shift then N.key_event("shift",false) end
        if ctrl then N.key_event("ctrl",false) end
        return self:frame()
    end
    function c:insert(text)N.text_event(text);return self:frame()end
    function c:measure(s)
        self.measure_request=s;self:frame();return self.measured
    end
    function c:ink(label)
        local s=self.snapshot
        assert(s.caret_ink>0,label..": no native caret was rendered")
        assert(near((s.caret_minx+s.caret_maxx)/2,s.cursor_x),label..": caret ink differs from insertion point")
        for _,cmd in ipairs(self.commands) do
            assert(not(cmd.kind=="rect" and cmd.w==1 and cmd.color==widgets.colors.cyan),"duplicate D2D caret")
            assert(not(cmd.kind=="text" and cmd.text==self.value),"duplicate D2D editable text")
        end
        check(true,label)
    end
    c:idle(2)
    return c
end
for _,config in ipairs(cases) do
    local name="scale "..config.scale.." / host font "..config.font.." / alpha "..config.alpha
    local c=setup(config)
    local offset=12
    local x=c.snapshot.x+c.snapshot.padding+c:measure(c.value:sub(1,offset))+.1
    c:click(x)
    check(c.snapshot.cursor==offset,name.." mouse click uses displayed glyph position ("..c.snapshot.cursor..")")
    c:ink(name.." one native caret after click")
    local before=c.value
    c:insert("x")
    check(c.value==before:sub(1,offset).."x"..before:sub(offset+1),name.." inserted character is at click")
    check(c.snapshot.returned_end==0 and c.snapshot.cursor==offset+1,name.." reproduces resize callback zero without moving real caret")
    c:ink(name.." caret remains at inserted character immediately")
    c:key("end");c:insert("z");c:ink(name.." append remains at end")
    check(c.snapshot.cursor==#c.value,name.." insertion cursor is UTF-8 byte end")
    c:key("home");c:key("right",true);c:key("right",true)
    check(c.snapshot.selection_start==0 and c.snapshot.selection_end==2 and c.snapshot.cursor==2,name.." forward selection")
    c:insert("Q");check(c.value:sub(1,1)=="Q" and c.snapshot.cursor==1,name.." replace forward selection")
    c:key("end");local oldlen=#c.value;c:key("left",true);c:key("left",true)
    check(c.snapshot.selection_start==oldlen and c.snapshot.selection_end==oldlen-2 and c.snapshot.cursor==oldlen-2,name.." backward selection retains actual insertion direction")
    c:insert("尾");check(c.snapshot.cursor==#c.value,name.." replace backward selection with UTF-8")
    c:ink(name.." backward selection replacement caret")
end
-- Numeric/grouped inputs have a D2D prefix; the native hit area starts after it.
local p=setup({scale=1.3,font=32,alpha=1},"X","12345",145)
local px=p.snapshot.x+p.snapshot.padding+p:measure("12")+.1
p:click(px);p:insert("9")
check(p.value=="129345","prefixed vector field mouse placement excludes the X label")
p:ink("prefixed field has one correct caret")
-- Long paths scroll in the same coordinate system used for rendering and clicks.
local l=setup({scale=1,font=48,alpha=1},nil,"art/纹理/火焰/"..string.rep("longpath/",24).."脚印.tex",190)
l:click(l.snapshot.x+50);l:key("end")
check(l.snapshot.scroll>0 and l.snapshot.cursor_x<=l.snapshot.x+l.snapshot.w,"long Unicode path scrolls to native caret")
l:ink("long path native scroll and caret agree")
local end_len=#l.value
l:key("left");l:insert("_v2")
check(l.snapshot.cursor==end_len-1+3,"long path insertion remains correct after scrolling")
l:ink("scroll follows insertion immediately")
l:key("home")
check(l.snapshot.cursor==0 and l.snapshot.scroll==0,"Home resets horizontal scroll and caret")
l:key("a",false,true);l:insert("纹理火焰脚印")
check(l.value=="纹理火焰脚印" and l.snapshot.cursor==#l.value,"Chinese committed text replaces selection without byte splitting")
l:ink("Chinese UTF-8 replacement has native caret")
local cx=l.snapshot.x+l.snapshot.padding+l:measure("纹理")-l.snapshot.scroll+.1
l:click(cx)
check(l.snapshot.cursor==#"纹理","Chinese mouse placement uses actual glyph advances ("..l.snapshot.cursor..", "..cx..")")
l:key("right",true);l:insert("脚")
check(l.value=="纹理脚焰脚印","Chinese selected character replacement")
l:key("z",false,true)
check(l.value=="纹理焰脚印","native undo removes inserted Unicode text as its first edit step")
l:key("z",false,true)
check(l.value=="纹理火焰脚印","native undo restores Unicode selection replacement ("..l.value..")")
l:ink("native undo caret matches insertion")
print(string.format("Native text results: %d passed",passed))
