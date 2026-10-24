-- Manual helper: replace only textbox drawing; preserve the live editor,
-- active input ID, drafts, action forms, recordings and resource bindings.
local core=_G.MotionVfxLive
assert(core and core.active and core.workbench,"请先打开 MotionVfx Live 界面。")
assert(core.version=="0.7.0" or core.version=="0.7.1","此修复脚本仅适用于 0.7.0 / 0.7.1。")
assert(not (core.state and core.state.reloading),"编辑器正在重载，请等待完成。")
local widgets=assert(core.workbench.widgets,"D2D 输入界面尚未初始化。")
local factory=assert(loadfile("reframework/autorun/MotionVfxLive/fx_d2d_widgets.lua"))()
local replacement=factory.new(widgets.real,widgets.api)
assert(type(replacement.textbox)=="function","新版输入框未能载入；当前界面未改变。")
widgets.textbox=replacement.textbox
package.loaded["MotionVfxLive.fx_d2d_widgets"]=factory
core.version="0.7.1"
core.state.message="输入框已更新：仅保留蓝色文字光标，当前输入与记录已保留。"
if log and log.info then log.info("[MotionVfxLive] 0.7.1 textbox refreshed; editor state preserved.") end
