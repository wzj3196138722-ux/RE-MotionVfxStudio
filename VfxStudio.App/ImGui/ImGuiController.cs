using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.App.ImGui;

public class ImGuiController : IDisposable
{
    private GL _gl;
    private IView _view;
    private IInputContext _input;
    private bool _frameBegun;
    private readonly List<char> _pressedChars = new();
    private IKeyboard? _keyboard;

    private int _attribLocationTex;
    private int _attribLocationProjMtx;
    private int _attribLocationVtxPos;
    private int _attribLocationVtxUV;
    private int _attribLocationVtxColor;
    private uint _vboHandle;
    private uint _elementsHandle;
    private uint _vertexArrayObject;

    private Shader _shader = null!;

    private int _windowWidth;
    private int _windowHeight;

    public ImGuiContextPtr Context;
    private readonly Dictionary<ulong, uint> _fontTextures = new();

    public ImGuiController(GL gl, IView view, IInputContext input, Action? onConfigureIO = null)
    {
        _gl = gl;
        _view = view;
        _input = input;
        _windowWidth = view.Size.X;
        _windowHeight = view.Size.Y;

        Context = Hexa.NET.ImGui.ImGui.CreateContext();
        Hexa.NET.ImGui.ImGui.SetCurrentContext(Context);
        Hexa.NET.ImGui.ImGui.StyleColorsDark();

        var io = Hexa.NET.ImGui.ImGui.GetIO();
        LoadChineseFont(io);
        onConfigureIO?.Invoke();

        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;

        CreateDeviceResources();
        SetPerFrameImGuiData(1f / 60f);
        BeginFrame();
    }

    private static unsafe uint* s_unmanagedGlyphRanges = null;

    private static unsafe uint* GetChineseGlyphRanges()
    {
        if (s_unmanagedGlyphRanges != null) return s_unmanagedGlyphRanges;

        uint[] ranges = [
            0x0020, 0x00FF, // Basic Latin + Latin Supplement
            0x2000, 0x206F, // General Punctuation
            0x3000, 0x30FF, // CJK Symbols and Punctuation, Hiragana, Katakana
            0x31F0, 0x31FF, // Katakana Phonetic Extensions
            0xFF00, 0xFFEF, // Halfwidth and Fullwidth Forms
            0x4E00, 0x9FAF, // CJK Unified Ideographs
            0
        ];

        s_unmanagedGlyphRanges = (uint*)NativeMemory.AllocZeroed((nuint)(ranges.Length * sizeof(uint)));
        for (int i = 0; i < ranges.Length; i++)
        {
            s_unmanagedGlyphRanges[i] = ranges[i];
        }
        return s_unmanagedGlyphRanges;
    }

    private static unsafe void LoadChineseFont(ImGuiIOPtr io)
    {
        string[] fontCandidates = [
            @"C:\Windows\Fonts\msyh.ttc",   // 微软雅黑
            @"C:\Windows\Fonts\msyhl.ttc",
            @"C:\Windows\Fonts\simhei.ttf", // 黑体
            @"C:\Windows\Fonts\simsun.ttc", // 宋体
        ];

        string? fontPath = fontCandidates.FirstOrDefault(File.Exists);
        if (fontPath != null)
        {
            try
            {
                byte[] fontPathBytes = System.Text.Encoding.UTF8.GetBytes(fontPath + '\0');
                uint* ranges = GetChineseGlyphRanges();
                fixed (byte* pathPtr = fontPathBytes)
                {
                    io.Fonts.AddFontFromFileTTF(pathPtr, 17.0f, (ImFontConfigPtr)null, ranges);
                }
                Console.WriteLine($"[ImGuiController] Loaded Chinese font with unmanaged ranges: {fontPath}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImGuiController] Warning: Failed to load Chinese font: {ex.Message}");
            }
        }

        io.Fonts.AddFontDefault();
    }

    public void MakeCurrent()
    {
        Hexa.NET.ImGui.ImGui.SetCurrentContext(Context);
    }

    private void BeginFrame()
    {
        Hexa.NET.ImGui.ImGui.NewFrame();
        _frameBegun = true;
        _keyboard = _input.Keyboards[0];
        _view.Resize += WindowResized;
        _keyboard.KeyDown += OnKeyDown;
        _keyboard.KeyUp += OnKeyUp;
        _keyboard.KeyChar += OnKeyChar;
    }

    private static void OnKeyDown(IKeyboard keyboard, Key keycode, int scancode) =>
        OnKeyEvent(keyboard, keycode, scancode, down: true);

    private static void OnKeyUp(IKeyboard keyboard, Key keycode, int scancode) =>
        OnKeyEvent(keyboard, keycode, scancode, down: false);

    private static void OnKeyEvent(IKeyboard keyboard, Key keycode, int scancode, bool down)
    {
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        var imGuiKey = TranslateInputKeyToImGuiKey(keycode);
        if (imGuiKey != ImGuiKey.None)
        {
            io.AddKeyEvent(imGuiKey, down);
            io.SetKeyEventNativeData(imGuiKey, (int)keycode, scancode);
        }
    }

    private void OnKeyChar(IKeyboard arg1, char arg2)
    {
        _pressedChars.Add(arg2);
    }

    private void WindowResized(Vector2D<int> size)
    {
        _windowWidth = size.X;
        _windowHeight = size.Y;
    }

    public void Render()
    {
        if (_frameBegun)
        {
            var oldCtx = Hexa.NET.ImGui.ImGui.GetCurrentContext();
            if (oldCtx != Context) Hexa.NET.ImGui.ImGui.SetCurrentContext(Context);

            _frameBegun = false;
            Hexa.NET.ImGui.ImGui.Render();
            RenderImDrawData(Hexa.NET.ImGui.ImGui.GetDrawData());

            if (oldCtx != Context) Hexa.NET.ImGui.ImGui.SetCurrentContext(oldCtx);
        }
    }

    public void Update(float deltaSeconds)
    {
        var oldCtx = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        if (oldCtx != Context) Hexa.NET.ImGui.ImGui.SetCurrentContext(Context);

        if (_frameBegun)
        {
            Hexa.NET.ImGui.ImGui.Render();
        }

        SetPerFrameImGuiData(deltaSeconds);
        UpdateImGuiInput();

        _frameBegun = true;
        Hexa.NET.ImGui.ImGui.NewFrame();

        if (oldCtx != Context) Hexa.NET.ImGui.ImGui.SetCurrentContext(oldCtx);
    }

    private void SetPerFrameImGuiData(float deltaSeconds)
    {
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.DisplaySize = new Vector2(_windowWidth, _windowHeight);

        if (_windowWidth > 0 && _windowHeight > 0)
        {
            io.DisplayFramebufferScale = new Vector2(
                _view.FramebufferSize.X / (float)_windowWidth,
                _view.FramebufferSize.Y / (float)_windowHeight
            );
        }

        io.DeltaTime = MathF.Max(deltaSeconds, 0.0001f);
    }

    private void UpdateImGuiInput()
    {
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        if (_input.Mice.Count > 0)
        {
            var mouse = _input.Mice[0];
            io.MouseDown[0] = mouse.IsButtonPressed(MouseButton.Left);
            io.MouseDown[1] = mouse.IsButtonPressed(MouseButton.Right);
            io.MouseDown[2] = mouse.IsButtonPressed(MouseButton.Middle);

            io.MousePos = new Vector2(mouse.Position.X, mouse.Position.Y);

            if (mouse.ScrollWheels.Count > 0)
            {
                var wheel = mouse.ScrollWheels[0];
                io.MouseWheel = wheel.Y;
                io.MouseWheelH = wheel.X;
            }
        }

        foreach (var c in _pressedChars)
        {
            io.AddInputCharacter(c);
        }
        _pressedChars.Clear();

        if (_keyboard != null)
        {
            io.KeyCtrl = _keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight);
            io.KeyAlt = _keyboard.IsKeyPressed(Key.AltLeft) || _keyboard.IsKeyPressed(Key.AltRight);
            io.KeyShift = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);
            io.KeySuper = _keyboard.IsKeyPressed(Key.SuperLeft) || _keyboard.IsKeyPressed(Key.SuperRight);
        }
    }

    private unsafe void SetupRenderState(ImDrawDataPtr drawDataPtr, int framebufferWidth, int framebufferHeight)
    {
        _gl.Enable(GLEnum.Blend);
        _gl.BlendEquation(GLEnum.FuncAdd);
        _gl.BlendFuncSeparate(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, GLEnum.One, GLEnum.OneMinusSrcAlpha);
        _gl.Disable(GLEnum.CullFace);
        _gl.Disable(GLEnum.DepthTest);
        _gl.Disable(GLEnum.StencilTest);
        _gl.Enable(GLEnum.ScissorTest);
        _gl.Disable(GLEnum.PrimitiveRestart);
        _gl.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);

        float L = drawDataPtr.DisplayPos.X;
        float R = drawDataPtr.DisplayPos.X + drawDataPtr.DisplaySize.X;
        float T = drawDataPtr.DisplayPos.Y;
        float B = drawDataPtr.DisplayPos.Y + drawDataPtr.DisplaySize.Y;

        Span<float> orthoProjection = [
            2.0f / (R - L), 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / (T - B), 0.0f, 0.0f,
            0.0f, 0.0f, -1.0f, 0.0f,
            (R + L) / (L - R), (T + B) / (B - T), 0.0f, 1.0f,
        ];

        _gl.UseProgram(_shader.Handle);
        _gl.Uniform1(_attribLocationTex, 0);
        _gl.UniformMatrix4(_attribLocationProjMtx, 1, false, orthoProjection);

        _gl.BindSampler(0, 0);

        _vertexArrayObject = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArrayObject);

        _gl.BindBuffer(GLEnum.ArrayBuffer, _vboHandle);
        _gl.BindBuffer(GLEnum.ElementArrayBuffer, _elementsHandle);

        _gl.EnableVertexAttribArray((uint)_attribLocationVtxPos);
        _gl.EnableVertexAttribArray((uint)_attribLocationVtxUV);
        _gl.EnableVertexAttribArray((uint)_attribLocationVtxColor);

        uint stride = (uint)sizeof(ImDrawVert);
        _gl.VertexAttribPointer((uint)_attribLocationVtxPos, 2, GLEnum.Float, false, stride, (void*)0);
        _gl.VertexAttribPointer((uint)_attribLocationVtxUV, 2, GLEnum.Float, false, stride, (void*)8);
        _gl.VertexAttribPointer((uint)_attribLocationVtxColor, 4, GLEnum.UnsignedByte, true, stride, (void*)16);
    }

    private unsafe void RenderImDrawData(ImDrawDataPtr drawDataPtr)
    {
        int fbWidth = (int)(drawDataPtr.DisplaySize.X * drawDataPtr.FramebufferScale.X);
        int fbHeight = (int)(drawDataPtr.DisplaySize.Y * drawDataPtr.FramebufferScale.Y);
        if (fbWidth <= 0 || fbHeight <= 0) return;

        // Backup GL state
        _gl.GetInteger(GLEnum.ActiveTexture, out int lastActiveTexture);
        if (drawDataPtr.Textures.Data != null)
        {
            UpdateTextures(drawDataPtr);
        }

        _gl.ActiveTexture(GLEnum.Texture0);
        _gl.GetInteger(GLEnum.CurrentProgram, out int lastProgram);
        _gl.GetInteger(GLEnum.TextureBinding2D, out int lastTexture);
        _gl.GetInteger(GLEnum.ArrayBufferBinding, out int lastArrayBuffer);
        _gl.GetInteger(GLEnum.VertexArrayBinding, out int lastVao);

        Span<int> lastScissorBox = stackalloc int[4];
        _gl.GetInteger(GLEnum.ScissorBox, lastScissorBox);

        bool lastEnableBlend = _gl.IsEnabled(GLEnum.Blend);
        bool lastEnableCullFace = _gl.IsEnabled(GLEnum.CullFace);
        bool lastEnableDepthTest = _gl.IsEnabled(GLEnum.DepthTest);
        bool lastEnableScissorTest = _gl.IsEnabled(GLEnum.ScissorTest);

        SetupRenderState(drawDataPtr, fbWidth, fbHeight);

        Vector2 clipOff = drawDataPtr.DisplayPos;
        Vector2 clipScale = drawDataPtr.FramebufferScale;

        for (int n = 0; n < drawDataPtr.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawDataPtr.CmdLists[n];

            _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(cmdList.VtxBuffer.Size * sizeof(ImDrawVert)), (void*)cmdList.VtxBuffer.Data, GLEnum.StreamDraw);
            _gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(cmdList.IdxBuffer.Size * sizeof(ushort)), (void*)cmdList.IdxBuffer.Data, GLEnum.StreamDraw);

            for (int i = 0; i < cmdList.CmdBuffer.Size; i++)
            {
                ImDrawCmd cmd = cmdList.CmdBuffer[i];
                if (cmd.UserCallback != null) continue;

                Vector4 clip;
                clip.X = (cmd.ClipRect.X - clipOff.X) * clipScale.X;
                clip.Y = (cmd.ClipRect.Y - clipOff.Y) * clipScale.Y;
                clip.Z = (cmd.ClipRect.Z - clipOff.X) * clipScale.X;
                clip.W = (cmd.ClipRect.W - clipOff.Y) * clipScale.Y;

                if (clip.X < fbWidth && clip.Y < fbHeight && clip.Z >= 0f && clip.W >= 0f)
                {
                    _gl.Scissor((int)clip.X, (int)(fbHeight - clip.W), (uint)(clip.Z - clip.X), (uint)(clip.W - clip.Y));
                    uint texId = (uint)cmd.TexRef.GetTexID();
                    _gl.BindTexture(GLEnum.Texture2D, texId);
                    _gl.DrawElementsBaseVertex(GLEnum.Triangles, cmd.ElemCount, GLEnum.UnsignedShort, (void*)(cmd.IdxOffset * sizeof(ushort)), (int)cmd.VtxOffset);
                }
            }
        }

        _gl.DeleteVertexArray(_vertexArrayObject);
        _vertexArrayObject = 0;

        // Restore GL state
        _gl.UseProgram((uint)lastProgram);
        _gl.BindTexture(GLEnum.Texture2D, (uint)lastTexture);
        _gl.ActiveTexture((GLEnum)lastActiveTexture);
        _gl.BindVertexArray((uint)lastVao);
        _gl.BindBuffer(GLEnum.ArrayBuffer, (uint)lastArrayBuffer);

        if (lastEnableBlend) _gl.Enable(GLEnum.Blend); else _gl.Disable(GLEnum.Blend);
        if (lastEnableCullFace) _gl.Enable(GLEnum.CullFace); else _gl.Disable(GLEnum.CullFace);
        if (lastEnableDepthTest) _gl.Enable(GLEnum.DepthTest); else _gl.Disable(GLEnum.DepthTest);
        if (lastEnableScissorTest) _gl.Enable(GLEnum.ScissorTest); else _gl.Disable(GLEnum.ScissorTest);

        _gl.Scissor(lastScissorBox[0], lastScissorBox[1], (uint)lastScissorBox[2], (uint)lastScissorBox[3]);
    }

    private unsafe void UpdateTextures(ImDrawDataPtr drawDataPtr)
    {
        _gl.GetInteger(GLEnum.TextureBinding2D, out int prevTex);
        for (int i = 0; i < drawDataPtr.Textures.Size; ++i)
        {
            var tex = drawDataPtr.Textures[i];
            if (tex.Status == ImTextureStatus.WantCreate)
            {
                uint handle = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture2D, handle);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
                _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)tex.Width, (uint)tex.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)tex.Pixels);
                tex.SetTexID((nint)handle);
                tex.SetStatus(ImTextureStatus.Ok);
            }
            else if (tex.Status == ImTextureStatus.WantUpdates)
            {
                _gl.BindTexture(TextureTarget.Texture2D, (uint)tex.TexID.Handle);
                _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)tex.Width, (uint)tex.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)tex.Pixels);
                tex.SetStatus(ImTextureStatus.Ok);
            }
            else if (tex.Status == ImTextureStatus.WantDestroy)
            {
                uint handle = (uint)tex.TexID.Handle;
                if (handle != 0) _gl.DeleteTexture(handle);
                tex.SetTexID(ImTextureID.Null);
                tex.SetStatus(ImTextureStatus.Destroyed);
            }
        }
        _gl.BindTexture(TextureTarget.Texture2D, (uint)prevTex);
    }

    private void CreateDeviceResources()
    {
        string vs = @"#version 330 core
layout (location = 0) in vec2 Position;
layout (location = 1) in vec2 UV;
layout (location = 2) in vec4 Color;
uniform mat4 ProjMtx;
out vec2 Frag_UV;
out vec4 Frag_Color;
void main() {
    Frag_UV = UV;
    Frag_Color = Color;
    gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);
}";

        string fs = @"#version 330 core
in vec2 Frag_UV;
in vec4 Frag_Color;
uniform sampler2D Texture;
layout (location = 0) out vec4 Out_Color;
void main() {
    Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
}";

        _shader = new Shader(_gl, vs, fs);

        _attribLocationTex = _gl.GetUniformLocation(_shader.Handle, "Texture");
        _attribLocationProjMtx = _gl.GetUniformLocation(_shader.Handle, "ProjMtx");
        _attribLocationVtxPos = _gl.GetAttribLocation(_shader.Handle, "Position");
        _attribLocationVtxUV = _gl.GetAttribLocation(_shader.Handle, "UV");
        _attribLocationVtxColor = _gl.GetAttribLocation(_shader.Handle, "Color");

        _vboHandle = _gl.GenBuffer();
        _elementsHandle = _gl.GenBuffer();
    }

    private static ImGuiKey TranslateInputKeyToImGuiKey(Key key)
    {
        return key switch
        {
            Key.Tab => ImGuiKey.Tab,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.Insert => ImGuiKey.Insert,
            Key.Delete => ImGuiKey.Delete,
            Key.Backspace => ImGuiKey.Backspace,
            Key.Space => ImGuiKey.Space,
            Key.Enter => ImGuiKey.Enter,
            Key.Escape => ImGuiKey.Escape,
            Key.Apostrophe => ImGuiKey.Apostrophe,
            Key.Comma => ImGuiKey.Comma,
            Key.Minus => ImGuiKey.Minus,
            Key.Period => ImGuiKey.Period,
            Key.Slash => ImGuiKey.Slash,
            Key.Semicolon => ImGuiKey.Semicolon,
            Key.Equal => ImGuiKey.Equal,
            Key.LeftBracket => ImGuiKey.LeftBracket,
            Key.BackSlash => ImGuiKey.Backslash,
            Key.RightBracket => ImGuiKey.RightBracket,
            Key.GraveAccent => ImGuiKey.GraveAccent,
            Key.CapsLock => ImGuiKey.CapsLock,
            Key.ScrollLock => ImGuiKey.ScrollLock,
            Key.NumLock => ImGuiKey.NumLock,
            Key.PrintScreen => ImGuiKey.PrintScreen,
            Key.Pause => ImGuiKey.Pause,
            Key.Keypad0 => ImGuiKey.Keypad0,
            Key.Keypad1 => ImGuiKey.Keypad1,
            Key.Keypad2 => ImGuiKey.Keypad2,
            Key.Keypad3 => ImGuiKey.Keypad3,
            Key.Keypad4 => ImGuiKey.Keypad4,
            Key.Keypad5 => ImGuiKey.Keypad5,
            Key.Keypad6 => ImGuiKey.Keypad6,
            Key.Keypad7 => ImGuiKey.Keypad7,
            Key.Keypad8 => ImGuiKey.Keypad8,
            Key.Keypad9 => ImGuiKey.Keypad9,
            Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
            Key.KeypadDivide => ImGuiKey.KeypadDivide,
            Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
            Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
            Key.KeypadAdd => ImGuiKey.KeypadAdd,
            Key.KeypadEnter => ImGuiKey.KeypadEnter,
            Key.KeypadEqual => ImGuiKey.KeypadEqual,
            Key.ControlLeft => ImGuiKey.LeftCtrl,
            Key.ControlRight => ImGuiKey.RightCtrl,
            Key.ShiftLeft => ImGuiKey.LeftShift,
            Key.ShiftRight => ImGuiKey.RightShift,
            Key.AltLeft => ImGuiKey.LeftAlt,
            Key.AltRight => ImGuiKey.RightAlt,
            Key.SuperLeft => ImGuiKey.LeftSuper,
            Key.SuperRight => ImGuiKey.RightSuper,
            Key.Number0 => ImGuiKey.Key0,
            Key.Number1 => ImGuiKey.Key1,
            Key.Number2 => ImGuiKey.Key2,
            Key.Number3 => ImGuiKey.Key3,
            Key.Number4 => ImGuiKey.Key4,
            Key.Number5 => ImGuiKey.Key5,
            Key.Number6 => ImGuiKey.Key6,
            Key.Number7 => ImGuiKey.Key7,
            Key.Number8 => ImGuiKey.Key8,
            Key.Number9 => ImGuiKey.Key9,
            Key.A => ImGuiKey.A,
            Key.B => ImGuiKey.B,
            Key.C => ImGuiKey.C,
            Key.D => ImGuiKey.D,
            Key.E => ImGuiKey.E,
            Key.F => ImGuiKey.F,
            Key.G => ImGuiKey.G,
            Key.H => ImGuiKey.H,
            Key.I => ImGuiKey.I,
            Key.J => ImGuiKey.J,
            Key.K => ImGuiKey.K,
            Key.L => ImGuiKey.L,
            Key.M => ImGuiKey.M,
            Key.N => ImGuiKey.N,
            Key.O => ImGuiKey.O,
            Key.P => ImGuiKey.P,
            Key.Q => ImGuiKey.Q,
            Key.R => ImGuiKey.R,
            Key.S => ImGuiKey.S,
            Key.T => ImGuiKey.T,
            Key.U => ImGuiKey.U,
            Key.V => ImGuiKey.V,
            Key.W => ImGuiKey.W,
            Key.X => ImGuiKey.X,
            Key.Y => ImGuiKey.Y,
            Key.Z => ImGuiKey.Z,
            Key.F1 => ImGuiKey.F1,
            Key.F2 => ImGuiKey.F2,
            Key.F3 => ImGuiKey.F3,
            Key.F4 => ImGuiKey.F4,
            Key.F5 => ImGuiKey.F5,
            Key.F6 => ImGuiKey.F6,
            Key.F7 => ImGuiKey.F7,
            Key.F8 => ImGuiKey.F8,
            Key.F9 => ImGuiKey.F9,
            Key.F10 => ImGuiKey.F10,
            Key.F11 => ImGuiKey.F11,
            Key.F12 => ImGuiKey.F12,
            _ => ImGuiKey.None
        };
    }

    public unsafe void Dispose()
    {
        _view.Resize -= WindowResized;
        if (_keyboard != null)
        {
            _keyboard.KeyChar -= OnKeyChar;
            _keyboard.KeyDown -= OnKeyDown;
            _keyboard.KeyUp -= OnKeyUp;
        }

        if (_vboHandle != 0) { _gl.DeleteBuffer(_vboHandle); _vboHandle = 0; }
        if (_elementsHandle != 0) { _gl.DeleteBuffer(_elementsHandle); _elementsHandle = 0; }
        if (_vertexArrayObject != 0) { _gl.DeleteVertexArray(_vertexArrayObject); _vertexArrayObject = 0; }

        _shader?.Dispose();

        if (Context.Handle != null)
        {
            Hexa.NET.ImGui.ImGui.DestroyContext(Context);
        }
    }
}
