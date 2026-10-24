// Headless ImGui + real Lua workbench. No game, OS input hooks or renderer.
#include "imgui.h"
#include "imgui_internal.h"
#include <cstdio>
#include <string>
#include <algorithm>
extern "C" {
#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"
}
static ImVec2 vec(lua_State* L, int i) {
    lua_getfield(L,i,"x"); lua_getfield(L,i,"y");
    if (lua_isnumber(L,-2) && lua_isnumber(L,-1)) {
        ImVec2 v((float)lua_tonumber(L,-2),(float)lua_tonumber(L,-1));lua_pop(L,2);return v;
    }
    lua_pop(L,2);lua_rawgeti(L,i,1);lua_rawgeti(L,i,2);
    ImVec2 v((float)luaL_checknumber(L,-2),(float)luaL_checknumber(L,-1));lua_pop(L,2);return v;
}
static int ret(lua_State* L, ImVec2 v) {lua_newtable(L);lua_pushnumber(L,v.x);lua_setfield(L,-2,"x");lua_pushnumber(L,v.y);lua_setfield(L,-2,"y");return 1;}
static int truth(lua_State* L,bool v) {lua_pushboolean(L,v);return 1;}
static int reset(lua_State* L) {
    if(ImGui::GetCurrentContext()) ImGui::DestroyContext();
    ImGui::CreateContext();auto& io=ImGui::GetIO();io.IniFilename=nullptr;io.LogFilename=nullptr;
    io.DisplaySize=vec(L,1);io.DeltaTime=1.f/60;io.ConfigInputTrickleEventQueue=false;
    ImFontConfig cfg;cfg.SizePixels=(float)luaL_checknumber(L,2);io.Fonts->AddFontDefault(&cfg);
    unsigned char* pixels;int w,h;io.Fonts->GetTexDataAsRGBA32(&pixels,&w,&h);
    auto& s=ImGui::GetStyle();s.WindowPadding=vec(L,3);s.WindowMinSize=vec(L,4);
    s.WindowBorderSize=(float)luaL_checknumber(L,5);s.Alpha=(float)luaL_checknumber(L,6);return 0;
}
// Production bindings return the final resize callback's SelectionStart/End,
// not the true insertion point. Intentionally preserve that bug in this harness.
static std::string text_buffer;
static int selection_start,selection_end;
static ImRect input_rect;
static float input_font_size, input_padding, input_advance, cursor_x, cursor_y;
static int text_callback(ImGuiInputTextCallbackData* data) {
    if(data->EventFlag & ImGuiInputTextFlags_CallbackResize) {text_buffer.resize(data->BufTextLen);data->Buf=text_buffer.data();}
    selection_start=data->SelectionStart;selection_end=data->SelectionEnd;return 0;
}
static int input_text(lua_State* L) {
    text_buffer=luaL_checkstring(L,2);
    bool changed=ImGui::InputText(luaL_checkstring(L,1),text_buffer.data(),text_buffer.capacity()+1,
        ImGuiInputTextFlags_CallbackResize|ImGuiInputTextFlags_CallbackAlways,text_callback);
    input_rect=ImRect(ImGui::GetItemRectMin(),ImGui::GetItemRectMax());input_font_size=ImGui::GetFontSize();input_padding=ImGui::GetStyle().FramePadding.x;
    auto* state=ImGui::GetInputTextState(ImGui::GetItemID());
    input_advance=ImGui::CalcTextSize(text_buffer.data(),text_buffer.data()+(state?state->GetCursorPos():text_buffer.size())).x;
    cursor_x=input_rect.Min.x+input_padding+input_advance-(state?state->Scroll.x:0);
    cursor_y=input_rect.Min.y+ImGui::GetStyle().FramePadding.y;
    lua_pushboolean(L,changed);lua_pushstring(L,text_buffer.data());lua_pushinteger(L,selection_start);lua_pushinteger(L,selection_end);return 4;
}
static int input_snapshot(lua_State* L) {
    auto& s=ImGui::GetCurrentContext()->InputTextState;
    lua_newtable(L);
    auto n=[&](const char* k,double v){lua_pushnumber(L,v);lua_setfield(L,-2,k);};
    n("cursor",s.GetCursorPos());n("selection_start",s.GetSelectionStart());n("selection_end",s.GetSelectionEnd());
    n("returned_start",selection_start);n("returned_end",selection_end);n("scroll",s.Scroll.x);
    n("x",input_rect.Min.x);n("y",input_rect.Min.y);n("w",input_rect.GetWidth());n("h",input_rect.GetHeight());
    n("font_size",input_font_size);n("padding",input_padding);n("cursor_x",cursor_x);n("cursor_y",cursor_y);
    // Verify the actual rendered native caret, not a separately predicted position.
    auto* list=ImGui::GetWindowDrawList();ImU32 col=ImGui::GetColorU32(ImGuiCol_InputTextCursor);
    float minx=1e9f,maxx=-1e9f;int ink=0;
    for(const auto& v:list->VtxBuffer) if(v.col==col && v.pos.y>=input_rect.Min.y && v.pos.y<=input_rect.Max.y) {minx=std::min(minx,v.pos.x);maxx=std::max(maxx,v.pos.x);ink++;}
    n("caret_ink",ink);n("caret_minx",minx);n("caret_maxx",maxx);
    n("text_ink",list->VtxBuffer.Size);return 1;
}
static int text_reset(lua_State* L) {
    reset(L);auto& io=ImGui::GetIO();io.ConfigInputTextCursorBlink=false;
    ImGui::GetStyle().Colors[ImGuiCol_InputTextCursor]=ImVec4(0.387f,0.851f,0.914f,1);
    // A small Unicode range suffices for native editing/glyph hit-test coverage.
    if(lua_isstring(L,7)) {
        io.Fonts->Clear();static const ImWchar ranges[]={0x20,0xFF,0x5370,0x5370,0x706B,0x706B,0x7130,0x7130,0x7406,0x7406,0x7EB9,0x7EB9,0x811A,0x811A,0};
        io.Fonts->AddFontFromFileTTF(lua_tostring(L,7),(float)luaL_checknumber(L,2),nullptr,ranges);
        unsigned char* pixels;int w,h;io.Fonts->GetTexDataAsRGBA32(&pixels,&w,&h);
    }
    return 0;
}
static int text_event(lua_State* L) {ImGui::GetIO().AddInputCharactersUTF8(luaL_checkstring(L,1));return 0;}
static int key_event(lua_State* L) {
    std::string k=luaL_checkstring(L,1);ImGuiKey code=ImGuiKey_None;
    if(k=="left")code=ImGuiKey_LeftArrow;else if(k=="right")code=ImGuiKey_RightArrow;else if(k=="home")code=ImGuiKey_Home;else if(k=="end")code=ImGuiKey_End;
    else if(k=="shift")code=ImGuiMod_Shift;else if(k=="ctrl")code=ImGuiMod_Ctrl;else if(k=="a")code=ImGuiKey_A;else if(k=="z")code=ImGuiKey_Z;else if(k=="delete")code=ImGuiKey_Delete;
    if(code==ImGuiKey_None)return luaL_error(L,"unknown key");ImGui::GetIO().AddKeyEvent(code,lua_toboolean(L,2));return 0;
}
static int cursor_pos(lua_State* L) {return ret(L,ImGui::GetCursorScreenPos());}
static int width(lua_State* L) {ImGui::SetNextItemWidth((float)luaL_checknumber(L,1));return 0;}
static int font_size(lua_State* L) {ImGui::PushFont(nullptr,(float)luaL_checknumber(L,1));return 0;}
static int pop_font(lua_State*) {ImGui::PopFont();return 0;}
static int measure(lua_State* L) {ImGui::PushFont(nullptr,(float)luaL_checknumber(L,2));float w=ImGui::CalcTextSize(luaL_checkstring(L,1)).x;ImGui::PopFont();lua_pushnumber(L,w);return 1;}
static int begin_frame(lua_State* L) {auto& io=ImGui::GetIO();io.AddMousePosEvent((float)luaL_checknumber(L,1),(float)luaL_checknumber(L,2));io.AddMouseButtonEvent(0,lua_toboolean(L,3));ImGui::NewFrame();return 0;}
static int end_frame(lua_State*) {ImGui::Render();return 0;}
static int begin_window(lua_State* L) {bool open=lua_toboolean(L,2);ImGui::Begin(luaL_checkstring(L,1),&open,(int)luaL_optinteger(L,3,0));return truth(L,open);}
static int end_window(lua_State*) {ImGui::End();return 0;}
static int next_pos(lua_State* L) {ImGui::SetNextWindowPos(vec(L,1));return 0;}
static int next_size(lua_State* L) {ImGui::SetNextWindowSize(vec(L,1));return 0;}
static int cursor(lua_State* L) {ImGui::SetCursorScreenPos(vec(L,1));return 0;}
static int button(lua_State* L) {return truth(L,ImGui::InvisibleButton(luaL_checkstring(L,1),vec(L,2)));}
static int active(lua_State* L) {return truth(L,ImGui::IsItemActive());}
static int hover(lua_State* L) {return truth(L,ImGui::IsItemHovered());}
static int mouse(lua_State* L) {return ret(L,ImGui::GetMousePos());}
static int pos(lua_State* L) {return ret(L,ImGui::GetWindowPos());}
static int size(lua_State* L) {return ret(L,ImGui::GetWindowSize());}
static int display(lua_State* L) {return ret(L,ImGui::GetIO().DisplaySize);}
static int down(lua_State* L) {return truth(L,ImGui::IsMouseDown((int)luaL_checkinteger(L,1)));}
static int push_var(lua_State* L) {auto id=(ImGuiStyleVar)luaL_checkinteger(L,1);if(lua_istable(L,2)) ImGui::PushStyleVar(id,vec(L,2));else ImGui::PushStyleVar(id,(float)luaL_checknumber(L,2));return 0;}
static int pop_var(lua_State* L) {ImGui::PopStyleVar((int)luaL_optinteger(L,1,1));return 0;}
static int push_color(lua_State* L) {ImGui::PushStyleColor((ImGuiCol)luaL_checkinteger(L,1),(ImU32)luaL_checkinteger(L,2));return 0;}
static int pop_color(lua_State* L) {ImGui::PopStyleColor((int)luaL_optinteger(L,1,1));return 0;}
static int scroll_x(lua_State* L) {ImGui::SetScrollX((float)luaL_checknumber(L,1));return 0;}
static int scroll_y(lua_State* L) {ImGui::SetScrollY((float)luaL_checknumber(L,1));return 0;}
static int local_cursor(lua_State* L) {return ret(L,ImGui::GetCursorPos());}
static int same_line(lua_State* L) {ImGui::SameLine();return 0;}
static int indent(lua_State* L) {ImGui::Indent((float)luaL_checknumber(L,1));return 0;}
static int unindent(lua_State* L) {ImGui::Unindent((float)luaL_checknumber(L,1));return 0;}
static int get_scroll_y(lua_State* L) {lua_pushnumber(L,ImGui::GetScrollY());return 1;}
static int get_scroll_max_y(lua_State* L) {lua_pushnumber(L,ImGui::GetScrollMaxY());return 1;}
static int style(lua_State* L) {const auto& s=ImGui::GetStyle();lua_pushnumber(L,s.Alpha);lua_pushnumber(L,s.WindowPadding.x);lua_pushnumber(L,s.WindowPadding.y);lua_pushnumber(L,s.WindowMinSize.x);lua_pushnumber(L,s.WindowMinSize.y);lua_pushnumber(L,s.WindowBorderSize);return 6;}
// File-dialog coverage uses the production REFramework signatures. Keyboard
// constants come from this ImGui build instead of Win32 virtual-key numbers.
static const struct { const char* name; ImGuiKey key; } dialog_keys[]={
    {"Tab",ImGuiKey_Tab},{"LeftArrow",ImGuiKey_LeftArrow},{"RightArrow",ImGuiKey_RightArrow},
    {"UpArrow",ImGuiKey_UpArrow},{"DownArrow",ImGuiKey_DownArrow},{"Home",ImGuiKey_Home},{"End",ImGuiKey_End},
    {"PageUp",ImGuiKey_PageUp},{"PageDown",ImGuiKey_PageDown},{"Backspace",ImGuiKey_Backspace},
    {"Enter",ImGuiKey_Enter},{"KeypadEnter",ImGuiKey_KeypadEnter},{"Escape",ImGuiKey_Escape},
    {"LeftCtrl",ImGuiKey_LeftCtrl},{"RightCtrl",ImGuiKey_RightCtrl},{"LeftShift",ImGuiKey_LeftShift},{"RightShift",ImGuiKey_RightShift},
    {"LeftAlt",ImGuiKey_LeftAlt},{"RightAlt",ImGuiKey_RightAlt},{"A",ImGuiKey_A},{"L",ImGuiKey_L},{"F5",ImGuiKey_F5}
};
static int dialog_key_event(lua_State* L) {
    const std::string name=luaL_checkstring(L,1);const bool pressed=lua_toboolean(L,2);
    for(const auto& item:dialog_keys)if(name==item.name) {
        auto& io=ImGui::GetIO();io.AddKeyEvent(item.key,pressed);
        if(name=="LeftCtrl"||name=="RightCtrl")io.AddKeyEvent(ImGuiMod_Ctrl,pressed);
        if(name=="LeftShift"||name=="RightShift")io.AddKeyEvent(ImGuiMod_Shift,pressed);
        if(name=="LeftAlt"||name=="RightAlt")io.AddKeyEvent(ImGuiMod_Alt,pressed);
        return 0;
    }
    return luaL_error(L,"unknown dialog key");
}
static int begin_child(lua_State* L) {return truth(L,ImGui::BeginChild(luaL_checkstring(L,1),vec(L,2),lua_toboolean(L,3)?ImGuiChildFlags_Borders:0));}
static int end_child(lua_State*) {ImGui::EndChild();return 0;}
static int key_down(lua_State* L) {return truth(L,ImGui::IsKeyDown((ImGuiKey)luaL_checkinteger(L,1)));}
static int key_pressed(lua_State* L) {return truth(L,ImGui::IsKeyPressed((ImGuiKey)luaL_checkinteger(L,1)));}
static int mouse_clicked(lua_State* L) {return truth(L,ImGui::IsMouseClicked((int)luaL_checkinteger(L,1)));}
static int mouse_released(lua_State* L) {return truth(L,ImGui::IsMouseReleased((int)luaL_checkinteger(L,1)));}
static int mouse_double_clicked(lua_State* L) {return truth(L,ImGui::IsMouseDoubleClicked((int)luaL_checkinteger(L,1)));}
static int item_focused(lua_State* L) {return truth(L,ImGui::IsItemFocused());}
static int scroll_here_y(lua_State* L) {ImGui::SetScrollHereY((float)luaL_optnumber(L,1,.5));return 0;}
static int set_delta_time(lua_State* L) {ImGui::GetIO().DeltaTime=(float)luaL_checknumber(L,1);return 0;}
int main(int argc,char** argv) {
    if(argc<3)return 2;
    lua_State* L=luaL_newstate();luaL_openlibs(L);lua_newtable(L);
    for(int i=1;i<argc;i++){lua_pushstring(L,argv[i]);lua_rawseti(L,-2,i-1);}lua_setglobal(L,"arg");
    luaL_Reg api[]={{"reset",reset},{"begin_frame",begin_frame},{"end_frame",end_frame},{"style",style},
        {"begin_window",begin_window},{"end_window",end_window},{"set_next_window_pos",next_pos},{"set_next_window_size",next_size},
        {"set_cursor_screen_pos",cursor},{"invisible_button",button},{"is_item_active",active},{"is_item_hovered",hover},
        {"get_mouse",mouse},{"get_window_pos",pos},{"get_window_size",size},{"get_display_size",display},{"is_mouse_down",down},
        {"push_style_var",push_var},{"pop_style_var",pop_var},{"push_style_color",push_color},{"pop_style_color",pop_color},
        {"set_scroll_x",scroll_x},{"set_scroll_y",scroll_y},{"get_cursor_pos",local_cursor},{"same_line",same_line},
        {"indent",indent},{"unindent",unindent},{"get_scroll_y",get_scroll_y},{"get_scroll_max_y",get_scroll_max_y},{nullptr,nullptr}};
    luaL_newlib(L,api);lua_setglobal(L,"native_imgui");
    lua_getglobal(L,"native_imgui");luaL_Reg input_api[]={
        {"text_reset",text_reset},{"input_text",input_text},{"input_snapshot",input_snapshot},{"text_event",text_event},{"key_event",key_event},
        {"get_cursor_screen_pos",cursor_pos},{"set_next_item_width",width},{"push_font_size",font_size},{"pop_font_size",pop_font},{"measure",measure},{nullptr,nullptr}};
    luaL_setfuncs(L,input_api,0);lua_pop(L,1);
    lua_getglobal(L,"native_imgui");luaL_Reg dialog_api[]={
        {"begin_child_window",begin_child},{"end_child_window",end_child},{"is_key_down",key_down},{"is_key_pressed",key_pressed},
        {"is_mouse_clicked",mouse_clicked},{"is_mouse_released",mouse_released},{"is_mouse_double_clicked",mouse_double_clicked},
        {"is_item_focused",item_focused},{"set_scroll_here_y",scroll_here_y},{"dialog_key_event",dialog_key_event},{"set_delta_time",set_delta_time},{nullptr,nullptr}};
    luaL_setfuncs(L,dialog_api,0);lua_newtable(L);
    for(const auto& item:dialog_keys){lua_pushinteger(L,item.key);lua_setfield(L,-2,(std::string("Key_")+item.name).c_str());}
    lua_setfield(L,-2,"ImGuiKey");lua_pop(L,1);
    int err=luaL_dofile(L,argv[1]);if(err)std::fprintf(stderr,"%s\n",lua_tostring(L,-1));
    lua_close(L);if(ImGui::GetCurrentContext())ImGui::DestroyContext();return err?1:0;
}
