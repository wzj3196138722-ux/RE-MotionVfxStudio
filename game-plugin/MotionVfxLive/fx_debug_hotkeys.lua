-- Debug input policy. The native adapter consumes only game-window keyboard
-- messages; this module routes an accepted chord to the existing scene queue.
local M = {}
local names = {[8]="Backspace",[9]="Tab",[13]="Enter",[16]="Shift",[17]="Ctrl",[18]="Alt",
    [19]="Pause",[20]="CapsLock",[27]="Esc",[32]="Space",[33]="PageUp",[34]="PageDown",
    [35]="End",[36]="Home",[37]="Left",[38]="Up",[39]="Right",[40]="Down",[46]="Delete",
    [106]="NumMultiply",[107]="NumAdd",[109]="NumSubtract",[110]="NumDecimal",[111]="NumDivide",
    [144]="NumLock",[145]="ScrollLock",[186]="Semicolon",[187]="Equals",[188]="Comma",
    [189]="Minus",[190]="Period",[191]="Slash",[192]="Backtick",[219]="LeftBracket",
    [220]="Backslash",[221]="RightBracket",[222]="Quote"}
for k=48,57 do names[k]=string.char(k) end
for k=65,90 do names[k]=string.char(k) end
for k=112,135 do names[k]="F"..(k-111) end
for k=96,105 do names[k]="Num"..(k-96) end
local codes = {}
for k,v in pairs(names) do codes[v:lower()]=k end
codes.control, codes.escape, codes["空格"], codes["回车"] = 17,27,32,13
local function valid(keys)
    if type(keys) ~= "table" or #keys < 1 or #keys > 8 then return nil, "请选择 1～8 个不同的键。" end
    local found, result = {}, {}
    for _,k in ipairs(keys) do
        if type(k) ~= "number" or not names[k] or found[k] then return nil, "存在不支持或重复的键。" end
        found[k]=true; result[#result+1]=k
    end
    if (found[18] and (found[9] or found[115])) or (found[17] and found[27]) or
        (found[17] and found[18] and found[46]) then return nil, "该组合由 Windows 保留，请换一组。" end
    table.sort(result); return result
end
function M.label(keys)
    local out={}
    -- Conventional modifier order, then other keys.
    for _,k in ipairs({17,16,18}) do for _,n in ipairs(keys or {}) do if n==k then out[#out+1]=names[k] end end end
    for _,k in ipairs(keys or {}) do if k~=16 and k~=17 and k~=18 then out[#out+1]=names[k] or ("VK"..k) end end
    return table.concat(out, " + ")
end
function M.parse(value)
    if type(value)~="string" or #value>160 then return nil,"快捷键文本过长。" end
    local keys={}
    for part in (value.."+"):gmatch("(.-)%+") do
        part=part:match("^%s*(.-)%s*$"):lower()
        if not codes[part] then return nil,"无法识别按键："..part.."。Insert 和 Windows 键不可用。" end
        keys[#keys+1]=codes[part]
    end
    return valid(keys)
end
M.validate=valid
function M.new(options)
    local native=options.native
    local self={enabled=false, recording=false, keys={16,17,80}, message="", last_serial=nil,
        record_keys={}, record_started=false, snapshot={}, toggles=0,
        active_frames=0, focused_frames=0, gate="disabled"}
    local config_path="MotionVfxLive/debug-hotkeys.json"
    local ok, saved=pcall(options.read,config_path)
    if ok and saved~=nil then
        local keys=type(saved)=="table" and saved.schema==1 and valid(saved.keys)
        if keys then self.keys=keys else self.message="保存的快捷键无效，已使用 Ctrl + Shift + P。" end
    end
    self.draft=M.label(self.keys)
    self.available=type(native)=="table" and type(native.configure)=="function" and type(native.sample)=="function"
        and type(native.disable)=="function"
    local function release()
        if self.available then pcall(native.disable) end
    end
    function self:disable()
        self.enabled,self.recording=false,false; self.gate="disabled"; release()
        self.message="调试快捷键已关闭；如场景仍暂停，可点击「继续当前场景」。"
    end
    function self:enable(value)
        if not value then self:disable(); return true end
        if not self.available then self.message="按键拦截组件尚未加载，安装后需重启游戏。"; return false end
        if native.version == "1.0.0" then
            self.message="输入组件仍为 1.0.0，无法完整覆盖游戏按键；更新组件后请重启游戏。"; return false
        end
        self.enabled=true; self.last_serial=nil
        self.message="调试模式已开启；每次触发后请松开全部组合键。在文本框输入时暂不触发。"
        return true
    end
    function self:save()
        if self.recording then self.message="先松开录入的按键，或取消录入。"; return false end
        local keys,err=M.parse(self.draft)
        if not keys then self.message=err; return false end
        local worked,result=pcall(options.write,config_path,{schema=1,keys=keys})
        if not worked or result==false then self.message="快捷键保存失败，原设置保留。"; return false end
        self.keys=keys; self.draft=M.label(keys); self.message="已保存暂停／继续快捷键："..self.draft
        release(); self.last_serial=nil
        return true
    end
    function self:begin_record()
        if not self.available then self.message="按键拦截组件尚未加载。"; return false end
        self.recording=true; self.record_keys={}; self.record_started=false
        self.message="请按住所需组合键，再全部松开；完成后点击保存。"
        return true
    end
    function self:cancel_record()
        self.recording=false; self.record_keys={}; release()
        self.message="已取消录入；原快捷键保持不变。"
    end
    function self:tick()
        if not self.available then return end
        if options.blocked() then release(); self.recording=false; self.gate="busy"; self.last_serial=nil; return end
        local menu=options.menu_open()
        -- An open REFramework menu is not proof of keyboard capture: users can
        -- close our window or enable UI passthrough and keep playing. Suspend
        -- only while a text field is being edited, not for the entire overlay.
        local typing=menu and options.text_input_active and options.text_input_active() == true
        self.menu_open=menu; self.text_input_active=typing == true
        if self.recording and not menu then self:cancel_record(); return end
        local enabled=self.enabled and not typing
        local worked, result, err=pcall(native.configure,enabled,self.keys,self.recording)
        if not worked or result~=true then
            self:disable(); self.message="按键拦截未就绪："..tostring(worked and err or result); return
        end
        local sampled,s=pcall(native.sample)
        if not sampled or type(s)~="table" or type(s.triggers)~="number" then
            self:disable(); self.message="按键状态读取失败。"; return
        end
        self.snapshot=s
        if s.focused then self.focused_frames=self.focused_frames+1 end
        if s.enabled and s.focused then self.active_frames=self.active_frames+1 end
        self.gate=not s.focused and "unfocused" or self.recording and "recording" or
            not self.enabled and "disabled" or typing and "text_input" or
            s.enabled == false and "native_disabled" or "active"
        if self.recording then
            if not s.focused then self:cancel_record(); return end
            local pressed=s.pressed or {}
            -- Record the largest simultaneous chord, not a sequence of unrelated keys.
            if #pressed>0 then
                self.record_started=true
                if #pressed>#self.record_keys then
                    self.record_keys={}; for _,k in ipairs(pressed) do self.record_keys[#self.record_keys+1]=k end
                end
                self.message="正在录入："..M.label(pressed)
            elseif self.record_started then
                self.recording=false
                local keys,why=valid(self.record_keys)
                if keys then self.draft=M.label(keys); self.message="已录入 "..self.draft.."；点击保存后生效。"
                else self.message=why end
                release()
            end
        elseif enabled and s.enabled ~= false and s.focused and self.last_serial and s.triggers>self.last_serial then
            local accepted,why=options.toggle()
            if accepted then self.toggles=self.toggles+1; self.message="已请求切换暂停／继续。"
            else self.message=why or "当前正在处理场景操作，请稍后再按。" end
        end
        self.last_serial=s.triggers
    end
    function self:status()
        return {enabled=self.enabled,recording=self.recording,available=self.available,
            keys=self.keys,label=M.label(self.keys),nativeVersion=native and native.version,
            blockedRaw=self.snapshot.blockedRaw,blockedLegacy=self.snapshot.blockedLegacy,
            blockedPolling=self.snapshot.blockedPolling,blockedBuffered=self.snapshot.blockedBuffered,
            asyncCalls=self.snapshot.asyncCalls,keyCalls=self.snapshot.keyCalls,keyboardCalls=self.snapshot.keyboardCalls,
            rawCalls=self.snapshot.rawCalls,directStateCalls=self.snapshot.directStateCalls,directDataCalls=self.snapshot.directDataCalls,
            win32Hooks=self.snapshot.win32Hooks,directHooks=self.snapshot.directHooks,
            nativeEnabled=self.snapshot.enabled,focused=self.snapshot.focused,triggers=self.snapshot.triggers,
            activeFrames=self.active_frames,focusedFrames=self.focused_frames,gate=self.gate,
            menuOpen=self.menu_open,textInputActive=self.text_input_active,
            toggles=self.toggles,message=self.message}
    end
    return self
end
return M
