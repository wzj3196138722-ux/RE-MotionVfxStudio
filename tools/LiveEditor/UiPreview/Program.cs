using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using Hexa.NET.ImGui;
using Lua;
using Lua.Standard;
using Gui = Hexa.NET.ImGui.ImGui;

// This utility executes the production Lua draw functions against real Dear ImGui.
// All game/backend capabilities are disconnected. Sample data is labelled in-image.
internal static unsafe class Program
{
    private const int Width = 1180;
    private static int Height = 910;
    private static readonly Dictionary<nint, Texture> Textures = new();
    private sealed record Texture(int Width, int Height, byte[] Pixels);

    static void Main(string[] args)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
        bool tall = args.Length > 2 && args[2] == "tall";
        if (tall) Height = 1190;
        string source = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "game-plugin/MotionVfxLive.lua");
        string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root, "tools/LiveEditor/UiPreview/offline-ui-preview.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Gui.CreateContext();
        Gui.StyleColorsDark();
        var io = Gui.GetIO();
        io.IniFilename = null;
        io.DisplaySize = new Vector2(Width, Height);
        io.DeltaTime = 1f / 60f;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;
        string fontPath = @"C:\Windows\Fonts\msyh.ttc";
        byte[] fontName = Encoding.UTF8.GetBytes(fontPath + '\0');
        fixed (byte* name = fontName) io.Fonts.AddFontFromFileTTF(name, 16f);
        var lua = LuaState.Create();
        lua.OpenStandardLibraries();
        var imgui = new LuaTable();
        lua.Environment["imgui"] = imgui;
        Bind(imgui);
        lua.DoStringAsync(Bootstrap).GetAwaiter().GetResult();
        var result = lua.DoStringAsync(File.ReadAllText(source, Encoding.UTF8), source).GetAwaiter().GetResult();
        lua.Environment["editor"] = result[0];
        lua.DoStringAsync(Fixture).GetAwaiter().GetResult();
        if (tall) lua.DoStringAsync("editor.state.window_initialized = true").GetAwaiter().GetResult();
        byte[]? pixels = null;
        int vertices = 0, triangles = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            Gui.NewFrame();
            Gui.SetNextWindowPos(new Vector2(16, 8));
            Gui.SetNextWindowSize(new Vector2(Width - 32, 48));
            Gui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(.10f, .13f, .20f, 1));
            Gui.Begin("Offline provenance", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
            Gui.TextUnformatted("离线 UI 预览 · 执行真实 Lua 界面 · 样例参数 · 非游戏截图");
            Gui.TextUnformatted("仅检查布局与文字；不连接游戏、不读取或修改正在运行的编辑后台。");
            Gui.End();
            Gui.PopStyleColor();
            Gui.SetNextWindowPos(new Vector2(30, 68), ImGuiCond.Always);
            if (tall) Gui.SetNextWindowSize(new Vector2(1120, 1100), ImGuiCond.Always);
            lua.DoStringAsync("editor.draw_window(); if editor.state.ui_error then error(editor.state.ui_error) end").GetAwaiter().GetResult();
            Gui.Render();
            var data = Gui.GetDrawData();
            UpdateTextures(data);
            pixels = Render(data, out vertices, out triangles);
        }
        WritePng(output, Width, Height, pixels!);
        File.WriteAllText(Path.ChangeExtension(output, ".json"), JsonSerializer.Serialize(new {
            kind = "offline_actual_lua_imgui_render", gameScreenshot = false, sampleData = true,
            source, sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
            output, width = Width, height = Height, windowWidth = 1120, windowHeight = tall ? 1100 : 820, vertices, triangles,
            renderer = "LuaCSharp 0.5.3 -> Hexa.NET.ImGui 2.2.9 -> software triangle rasterizer",
            scene = "Sample RGBCommon values, two unsubmitted drafts, one readonly identifier; no real game/backend calls.",
            createdAtUtc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine(output);
        Console.WriteLine($"Draw data: {vertices} vertices, {triangles} triangles. No game or mailbox operations.");
        Gui.DestroyContext();
    }

    static LuaTable Vec(Vector2 v) => new() { ["x"] = v.X, ["y"] = v.Y };
    static Vector2 Vec(LuaTable t) => new(t[1].Read<float>(), t[2].Read<float>());
    static void Bind(LuaTable t)
    {
        t["text"] = new LuaFunction((c, _) => { Gui.TextUnformatted(c.GetArgument<string>(0)); return new(0); });
        t["get_default_font_size"] = new LuaFunction((c, _) => new(c.Return(16)));
        t["get_window_size"] = new LuaFunction((c, _) => new(c.Return(Vec(Gui.GetWindowSize()))));
        t["get_cursor_pos"] = new LuaFunction((c, _) => new(c.Return(Vec(Gui.GetCursorPos()))));
        t["get_display_size"] = new LuaFunction((c, _) => new(c.Return(Vec(Gui.GetIO().DisplaySize))));
        t["calc_text_size"] = new LuaFunction((c, _) => new(c.Return(Vec(Gui.CalcTextSize(c.GetArgument<string>(0))))));
        t["set_next_window_size"] = new LuaFunction((c, _) => { Gui.SetNextWindowSize(Vec(c.GetArgument<LuaTable>(0))); return new(0); });
        t["set_next_item_width"] = new LuaFunction((c, _) => { Gui.SetNextItemWidth(c.GetArgument<float>(0)); return new(0); });
        t["set_next_item_open"] = new LuaFunction((c, _) => { Gui.SetNextItemOpen(c.GetArgument<bool>(0)); return new(0); });
        t["push_style_color"] = new LuaFunction((c, _) => { Gui.PushStyleColor((ImGuiCol)c.GetArgument<int>(0), unchecked((uint)c.GetArgument<int>(1))); return new(0); });
        t["pop_style_color"] = new LuaFunction((c, _) => { Gui.PopStyleColor(c.GetArgument<int>(0)); return new(0); });
        t["push_font_size"] = new LuaFunction((c, _) => { Gui.PushFont((ImFontPtr)null, c.GetArgument<float>(0)); return new(0); });
        t["pop_font_size"] = new LuaFunction((c, _) => { Gui.PopFont(); return new(0); });
        t["begin_window"] = new LuaFunction((c, _) => { bool open = c.GetArgument<bool>(1); Gui.Begin(c.GetArgument<string>(0), ref open); return new(c.Return(open)); });
        t["end_window"] = new LuaFunction((c, _) => { Gui.End(); return new(0); });
        t["begin_child_window"] = new LuaFunction((c, _) => new(c.Return(Gui.BeginChild(c.GetArgument<string>(0), Vec(c.GetArgument<LuaTable>(1)), c.GetArgument<bool>(2) ? ImGuiChildFlags.Borders : ImGuiChildFlags.None))));
        t["end_child_window"] = new LuaFunction((c, _) => { Gui.EndChild(); return new(0); });
        t["tree_node"] = new LuaFunction((c, _) => new(c.Return(Gui.TreeNode(c.GetArgument<string>(0)))));
        t["tree_pop"] = new LuaFunction((c, _) => { Gui.TreePop(); return new(0); });
        t["same_line"] = new LuaFunction((c, _) => { Gui.SameLine(); return new(0); });
        t["separator"] = new LuaFunction((c, _) => { Gui.Separator(); return new(0); });
        t["button"] = new LuaFunction((c, _) => new(c.Return(Gui.Button(c.GetArgument<string>(0), Vec(c.GetArgument<LuaTable>(1))))));
        t["checkbox"] = new LuaFunction((c, _) => { bool v = c.GetArgument<bool>(1); bool changed = Gui.Checkbox(c.GetArgument<string>(0), ref v); return new(c.Return(changed, v)); });
        t["input_text"] = new LuaFunction((c, _) => {
            string label = c.GetArgument<string>(0), value = c.GetArgument<string>(1);
            byte[] labelBytes = Encoding.UTF8.GetBytes(label + '\0'), valueBytes = new byte[Math.Max(4096, Encoding.UTF8.GetByteCount(value) + 1024)];
            Encoding.UTF8.GetBytes(value, valueBytes);
            bool changed;
            fixed (byte* lp = labelBytes) fixed (byte* vp = valueBytes) changed = Gui.InputText(lp, vp, (nuint)valueBytes.Length);
            return new(c.Return(changed, value));
        });
    }

    private const string Bootstrap = """
        _G = _G or _ENV
        -- LuaCSharp uses .NET character indexing; this iterator follows that
        -- interpreter's string.sub/# positions, preserving production wrapping.
        utf8 = { codes = function(s)
            local i = 0
            return function() i = i + 1; if i <= #s then return i, string.byte(s, i) end end
        end }
        re = { on_frame=function() end, on_draw_ui=function() end, on_pre_application_entry=function() end, on_script_reset=function() end }
        reframework = { is_drawing_ui=function() return true end }
        log = { warn=function(message) print(message) end }
        local client = { get_instance=function() return nil end }
        fx_preview_runtime_v2 = { version='offline-disconnected', new=function() return client end }
        MotionVfxActionCapture = { version='offline-disconnected', snapshot=function() return {} end }
        MotionVfxActionBindings = { version='offline-disconnected', configure=function() return true end, snapshot=function() return {} end }
        -- Any accidental backend operation fails immediately; no mailbox exists.
        json = { load_file=function() return nil end, dump_file=function() error('Offline preview cannot write to a game mailbox') end }
        """;
    private const string Fixture = """
        local s = editor.state
        s.active_tab = 'edit'
        s.status = {ready=true, protocol=1}
        s.message = '离线演示：属性编辑布局（以下参数为示例）'
        s.action_message = '样例数据：修改字段后先提交，再应用到角色真实资源。'
        s.document = {name='11_it02_000.efx · 离线样例', source='sample/11_it02_000.efx', revision=11, dirty=true,
          entries={
            {index=0,name='Root',attributes={{index=0,type='UnitCulling'},{index=1,type='EmitterPriority'}}},
            {index=4,name='0_IMP_Top_fire',attributes={{index=0,type='Transform3D'},{index=8,type='Life'},{index=15,type='RgbCommon'}}},
            {index=5,name='0_IMP_P_fire1',attributes={{index=0,type='Transform3D'},{index=12,type='RgbCommon'}}},
            {index=6,name='0_IMP_P_fire2',attributes={{index=0,type='ScaleAnim'},{index=14,type='RgbCommon'}}}
          }, actions={}, attributeTypes={'RgbCommon','Transform3D','Life'}, limitations={}}
        s.target_resource_path='art/vfx/effecteditor/weapon/it02/11_it02_000.efx'
        s.target_document_source=s.document.source
        s.selected={kind='entry',index=4,attributeIndex=15}
        s.structure_filter='RgbCommon'
        s.fields={
          {path='color.r',label='红色通道 / R',type='Single',value='1',editable=true},
          {path='brightness',label='亮度 / Brightness',type='Single',value='1',editable=true},
          {path='UniqueID',label='属性标识 / UniqueID',type='UInt32',value='9048',editable=false,reason='身份标识由文件维护'},
          {path='color.g',label='绿色通道 / G',type='Single',value='0.6',editable=true},
          {path='color.b',label='蓝色通道 / B',type='Single',value='0.2',editable=true}}
        s.values={['color.r']='0.25',brightness='2.5',UniqueID='9048',['color.g']='0.6',['color.b']='0.2'}
        s.bindings_snapshot={phase='offline_sample',appliedCount=0}
        """;

    static void UpdateTextures(ImDrawDataPtr data)
    {
        for (int i = 0; i < data.Textures.Size; i++)
        {
            var tex = data.Textures[i];
            if (tex.Status == ImTextureStatus.WantCreate || tex.Status == ImTextureStatus.WantUpdates)
            {
                nint id = (nint)tex.TexID.Handle;
                if (id == 0) { id = (nint)(Textures.Count + 1); tex.SetTexID(id); }
                var bytes = new byte[tex.Width * tex.Height * 4];
                Marshal.Copy((nint)tex.Pixels, bytes, 0, bytes.Length);
                Textures[id] = new(tex.Width, tex.Height, bytes);
                tex.SetStatus(ImTextureStatus.Ok);
            }
        }
    }
    static byte[] Render(ImDrawDataPtr data, out int vertices, out int triangles)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 11; pixels[i + 1] = 15; pixels[i + 2] = 23; pixels[i + 3] = 255; }
        vertices = triangles = 0;
        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            vertices += list.VtxBuffer.Size;
            for (int ci = 0; ci < list.CmdBuffer.Size; ci++)
            {
                var cmd = list.CmdBuffer[ci];
                if (cmd.UserCallback != null) continue;
                if (!Textures.TryGetValue((nint)cmd.TexRef.GetTexID().Handle, out var tex)) throw new Exception("Unknown ImGui texture");
                for (uint i = 0; i < cmd.ElemCount; i += 3)
                {
                    var a = list.VtxBuffer[(int)(list.IdxBuffer[(int)(cmd.IdxOffset + i)] + cmd.VtxOffset)];
                    var b = list.VtxBuffer[(int)(list.IdxBuffer[(int)(cmd.IdxOffset + i + 1)] + cmd.VtxOffset)];
                    var c = list.VtxBuffer[(int)(list.IdxBuffer[(int)(cmd.IdxOffset + i + 2)] + cmd.VtxOffset)];
                    Triangle(pixels, tex, a, b, c, cmd.ClipRect); triangles++;
                }
            }
        }
        return pixels;
    }
    static float Edge(Vector2 a, Vector2 b, Vector2 p) => (p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X);
    static Vector4 Color(uint c) => new(c & 255, (c >> 8) & 255, (c >> 16) & 255, c >> 24);
    static void Triangle(byte[] pixels, Texture tex, ImDrawVert a, ImDrawVert b, ImDrawVert c, Vector4 clip)
    {
        float area = Edge(a.Pos, b.Pos, c.Pos);
        if (Math.Abs(area) < .0001f) return;
        int x0 = Math.Max(0, (int)Math.Max(clip.X, Math.Floor(Math.Min(a.Pos.X, Math.Min(b.Pos.X, c.Pos.X)))));
        int y0 = Math.Max(0, (int)Math.Max(clip.Y, Math.Floor(Math.Min(a.Pos.Y, Math.Min(b.Pos.Y, c.Pos.Y)))));
        int x1 = Math.Min(Width, (int)Math.Min(clip.Z, Math.Ceiling(Math.Max(a.Pos.X, Math.Max(b.Pos.X, c.Pos.X)))));
        int y1 = Math.Min(Height, (int)Math.Min(clip.W, Math.Ceiling(Math.Max(a.Pos.Y, Math.Max(b.Pos.Y, c.Pos.Y)))));
        Vector4 ca = Color(a.Col), cb = Color(b.Col), cc = Color(c.Col);
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        {
            Vector2 p = new(x + .5f, y + .5f);
            float wa = Edge(b.Pos, c.Pos, p) / area, wb = Edge(c.Pos, a.Pos, p) / area, wc = 1 - wa - wb;
            if (wa < 0 || wb < 0 || wc < 0) continue;
            Vector2 uv = a.Uv * wa + b.Uv * wb + c.Uv * wc;
            Vector4 color = ca * wa + cb * wb + cc * wc;
            Vector4 sample = Sample(tex, uv);
            float alpha = color.W * sample.W / (255f * 255f);
            if (alpha <= 0) continue;
            int at = (y * Width + x) * 4;
            pixels[at] = (byte)Math.Clamp(color.X * sample.X / 255 * alpha + pixels[at] * (1 - alpha), 0, 255);
            pixels[at + 1] = (byte)Math.Clamp(color.Y * sample.Y / 255 * alpha + pixels[at + 1] * (1 - alpha), 0, 255);
            pixels[at + 2] = (byte)Math.Clamp(color.Z * sample.Z / 255 * alpha + pixels[at + 2] * (1 - alpha), 0, 255);
        }
    }
    static Vector4 Sample(Texture tex, Vector2 uv)
    {
        float x = uv.X * tex.Width - .5f, y = uv.Y * tex.Height - .5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector4 Read(int px, int py) { int i = (Math.Clamp(py, 0, tex.Height - 1) * tex.Width + Math.Clamp(px, 0, tex.Width - 1)) * 4; return new(tex.Pixels[i], tex.Pixels[i + 1], tex.Pixels[i + 2], tex.Pixels[i + 3]); }
        return Vector4.Lerp(Vector4.Lerp(Read(x0, y0), Read(x0 + 1, y0), fx), Vector4.Lerp(Read(x0, y0 + 1), Read(x0 + 1, y0 + 1), fx), fy);
    }
    static void WritePng(string path, int width, int height, byte[] pixels)
    {
        using var output = File.Create(path);
        output.Write(new byte[] {137,80,78,71,13,10,26,10});
        void Chunk(string type, byte[] bytes) { WriteBe(output, (uint)bytes.Length); byte[] header = Encoding.ASCII.GetBytes(type); output.Write(header); output.Write(bytes); uint crc = 0xffffffff; foreach (byte b in header.Concat(bytes)) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; } WriteBe(output, ~crc); }
        byte[] ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; Chunk("IHDR", ihdr);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) for (int y = 0; y < height; y++) { z.WriteByte(0); z.Write(pixels, y * width * 4, width * 4); }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", Array.Empty<byte>());
    }
    static void WriteBe(Stream s, uint value) { Span<byte> b = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, value); s.Write(b); }
}
