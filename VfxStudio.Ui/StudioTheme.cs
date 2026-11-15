using System.Numerics;
using Hexa.NET.ImGui;

namespace VfxStudio.App.UI;

/// <summary>
/// Pixel-perfect dark-space theme matching the design mockup.
/// All hex colors are taken directly from the specification document.
/// </summary>
public static class StudioTheme
{
    // ── Core Palette (exact hex from spec) ──────────────────────────────────

    /// <summary>#090C15 — 极夜深蓝黑 (Deep midnight blue-black window base)</summary>
    public static readonly Vector4 MainBg       = HexToV4(0x090C15);

    /// <summary>#111624 — 深空靛蓝 (Card background)</summary>
    public static readonly Vector4 CardBg       = HexToV4(0x111624);

    /// <summary>#1E273D — 柔和蓝灰细边框 (Card border)</summary>
    public static readonly Vector4 CardBorder   = HexToV4(0x1E273D);

    /// <summary>#0C101B — 输入框/内凹槽背景</summary>
    public static readonly Vector4 InputBg      = HexToV4(0x0C101B);

    /// <summary>#222C44 — 输入框描边</summary>
    public static readonly Vector4 InputBorder  = HexToV4(0x222C44);

    /// <summary>#3B5BDB — 皇家电光蓝 (Primary blue base)</summary>
    public static readonly Vector4 PrimaryBlue  = HexToV4(0x3B5BDB);

    /// <summary>#4C6EF5 — 皇家电光蓝亮 (Primary blue bright – active capsule, slider progress, selected-row accent)</summary>
    public static readonly Vector4 PrimaryBlueBright = HexToV4(0x4C6EF5);

    /// <summary>#23325B — 激活导航项圆角背景</summary>
    public static readonly Vector4 ActiveNavBg  = HexToV4(0x23325B);

    /// <summary>#203260 — 选中行高亮背景</summary>
    public static readonly Vector4 SelectedRowBg = HexToV4(0x203260);

    /// <summary>#10B981 — 状态翠绿 (Connected badge, GPU dot)</summary>
    public static readonly Vector4 StatusGreen  = HexToV4(0x10B981);

    /// <summary>#34D399 — 青绿强调字 (right-lower "Selected Node: Root")</summary>
    public static readonly Vector4 AccentGreen  = HexToV4(0x34D399);

    // ── Text hierarchy ──────────────────────────────────────────────────────
    /// <summary>#F1F5F9 — 主标题/正文 纯白亮灰</summary>
    public static readonly Vector4 TextPrimary  = HexToV4(0xF1F5F9);

    /// <summary>#8A99AD — 冷钢灰 次要标签</summary>
    public static readonly Vector4 TextSecondary = HexToV4(0x8A99AD);

    // ── Timeline track colours ──────────────────────────────────────────────
    public static readonly Vector4 TrackMotion = HexToV4(0x6C5CE7); // 紫色 Motion
    public static readonly Vector4 TrackWeapon = HexToV4(0x358AFF);
    public static readonly Vector4 TrackBone   = HexToV4(0x00B894);
    public static readonly Vector4 TrackVfx    = HexToV4(0x526487);
    public static readonly Vector4 TrackSound  = HexToV4(0xFDCB6E); // 黄色 Sound
    public static readonly Vector4 TrackCamera = HexToV4(0xE17055); // 橙色 Camera

    // ── Viewport gradient endpoints ─────────────────────────────────────────
    public static readonly Vector4 ViewportGradCenter = HexToV4(0x1A2236);
    public static readonly Vector4 ViewportGradEdge   = HexToV4(0x0D111C);

    // ── Layout constants ────────────────────────────────────────────────────
    public const float HeaderHeight        = 44f;
    public const float LeftPanelWidth      = 341f;
    public const float RightPanelWidth     = 302f;
    public const float TimelineHeight      = 236f;
    public const float CardGap             = 9f;
    public const float CardRounding        = 8f;
    public const float InputRounding       = 6f;

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Convert 0xRRGGBB to Vector4 (0-1 floats, alpha=1).</summary>
    public static Vector4 HexToV4(uint rgb, float alpha = 1.0f)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f;
        float g = ((rgb >> 8)  & 0xFF) / 255f;
        float b = (rgb         & 0xFF) / 255f;
        return new Vector4(r, g, b, alpha);
    }

    /// <summary>Convert Vector4 (RGBA 0-1) to packed uint ABGR for ImDrawList.</summary>
    public static uint V4ToAbgr(Vector4 c)
    {
        byte r = (byte)(Math.Clamp(c.X, 0f, 1f) * 255f);
        byte g = (byte)(Math.Clamp(c.Y, 0f, 1f) * 255f);
        byte b = (byte)(Math.Clamp(c.Z, 0f, 1f) * 255f);
        byte a = (byte)(Math.Clamp(c.W, 0f, 1f) * 255f);
        return (uint)((a << 24) | (b << 16) | (g << 8) | r);
    }

    /// <summary>Apply the full theme to ImGui style and colours.</summary>
    public static void Apply()
    {
        var style = Hexa.NET.ImGui.ImGui.GetStyle();

        // ── Rounding ────────────────────────────────────────────────────
        style.WindowRounding   = 0.0f;   // Root window (fullscreen) has no rounding
        style.ChildRounding    = CardRounding;
        style.FrameRounding    = InputRounding;
        style.PopupRounding    = 8.0f;
        style.ScrollbarRounding = 6.0f;
        style.GrabRounding     = 4.0f;
        style.TabRounding      = 5.0f;

        // ── Spacing & Padding ───────────────────────────────────────────
        style.WindowPadding    = new Vector2(0f, 0f);  // Outer root has 0 padding; cards manage their own
        style.FramePadding     = new Vector2(8f, 5f);
        style.ItemSpacing      = new Vector2(8f, 6f);
        style.ItemInnerSpacing = new Vector2(6f, 4f);
        style.IndentSpacing    = 18f;
        style.ScrollbarSize    = 10f;
        style.WindowBorderSize = 0f;      // No outer border
        style.ChildBorderSize  = 1f;      // Card border 1px

        // ── Colour Map ──────────────────────────────────────────────────
        var colors = style.Colors;

        // Text
        colors[(int)ImGuiCol.Text]         = TextPrimary;
        colors[(int)ImGuiCol.TextDisabled] = TextSecondary;

        // Window / Child / Popup backgrounds
        colors[(int)ImGuiCol.WindowBg] = MainBg;
        colors[(int)ImGuiCol.ChildBg]  = CardBg;
        colors[(int)ImGuiCol.PopupBg]  = new Vector4(CardBg.X, CardBg.Y, CardBg.Z, 0.96f);

        // Borders
        colors[(int)ImGuiCol.Border]       = CardBorder;
        colors[(int)ImGuiCol.BorderShadow] = new Vector4(0, 0, 0, 0);

        // Frame (input fields, combos, etc.)
        colors[(int)ImGuiCol.FrameBg]        = InputBg;
        colors[(int)ImGuiCol.FrameBgHovered] = HexToV4(0x161C2E);
        colors[(int)ImGuiCol.FrameBgActive]  = HexToV4(0x1A2440);

        // Title Bar
        colors[(int)ImGuiCol.TitleBg]          = MainBg;
        colors[(int)ImGuiCol.TitleBgActive]    = MainBg;
        colors[(int)ImGuiCol.TitleBgCollapsed] = MainBg;

        // Menu Bar
        colors[(int)ImGuiCol.MenuBarBg] = MainBg;

        // Scrollbar
        colors[(int)ImGuiCol.ScrollbarBg]          = new Vector4(InputBg.X, InputBg.Y, InputBg.Z, 0.3f);
        colors[(int)ImGuiCol.ScrollbarGrab]        = HexToV4(0x2A3450);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = HexToV4(0x3A4A6A);
        colors[(int)ImGuiCol.ScrollbarGrabActive]  = PrimaryBlue;

        // Buttons
        colors[(int)ImGuiCol.Button]        = HexToV4(0x1A2240);
        colors[(int)ImGuiCol.ButtonHovered] = PrimaryBlue;
        colors[(int)ImGuiCol.ButtonActive]  = PrimaryBlueBright;

        // Check / Slider
        colors[(int)ImGuiCol.CheckMark]        = PrimaryBlueBright;
        colors[(int)ImGuiCol.SliderGrab]       = PrimaryBlue;
        colors[(int)ImGuiCol.SliderGrabActive] = PrimaryBlueBright;

        // Headers (Selectable, CollapsingHeader, etc.)
        colors[(int)ImGuiCol.Header]        = SelectedRowBg;
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(PrimaryBlue.X, PrimaryBlue.Y, PrimaryBlue.Z, 0.55f);
        colors[(int)ImGuiCol.HeaderActive]  = new Vector4(PrimaryBlueBright.X, PrimaryBlueBright.Y, PrimaryBlueBright.Z, 0.75f);

        // Separators
        colors[(int)ImGuiCol.Separator]        = CardBorder;
        colors[(int)ImGuiCol.SeparatorHovered] = PrimaryBlue;
        colors[(int)ImGuiCol.SeparatorActive]  = PrimaryBlueBright;

        // Tabs
        colors[(int)ImGuiCol.Tab]              = HexToV4(0x131929);
        colors[(int)ImGuiCol.TabHovered]       = PrimaryBlue;
        colors[(int)ImGuiCol.TabSelected]      = ActiveNavBg;
        colors[(int)ImGuiCol.TabDimmed]        = HexToV4(0x0E1220);
        colors[(int)ImGuiCol.TabDimmedSelected] = HexToV4(0x1A2240);

        // Resize grip
        colors[(int)ImGuiCol.ResizeGrip]        = new Vector4(0, 0, 0, 0);
        colors[(int)ImGuiCol.ResizeGripHovered] = PrimaryBlue;
        colors[(int)ImGuiCol.ResizeGripActive]  = PrimaryBlueBright;
    }

    // ── Custom Drawing Helpers ──────────────────────────────────────────────

    /// <summary>Draw a card-style BeginChild with rounded corners and border.</summary>
    public static bool BeginCard(string id, Vector2 size, bool border = true)
    {
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, CardRounding);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, border ? 1f : 0f);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
        return Hexa.NET.ImGui.ImGui.BeginChild(id, size, ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
    }

    public static void EndCard()
    {
        Hexa.NET.ImGui.ImGui.EndChild();
        Hexa.NET.ImGui.ImGui.PopStyleVar(3);
        Hexa.NET.ImGui.ImGui.PopStyleColor(2);
    }

    /// <summary>Draw a styled section header with icon and title inside a card.</summary>
    public static void SectionHeader(string icon, string title)
    {
        var drawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        var pos = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();

        // Draw left accent bar
        drawList.AddRectFilled(pos, new Vector2(pos.X + 3f, pos.Y + 18f), V4ToAbgr(PrimaryBlueBright), 1.5f);
        Hexa.NET.ImGui.ImGui.SetCursorPosX(Hexa.NET.ImGui.ImGui.GetCursorPosX() + 10f);
        Hexa.NET.ImGui.ImGui.TextColored(TextPrimary, title);
        Hexa.NET.ImGui.ImGui.Spacing();
    }

    /// <summary>Draw a styled search input with icon placeholder.</summary>
    public static bool SearchInput(string id, ref string filter, string hint = "搜索...")
    {
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.FrameBg, InputBg);
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.Border, InputBorder);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, InputRounding);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1);
        bool changed = Hexa.NET.ImGui.ImGui.InputTextWithHint(id, hint, ref filter, 128);
        Hexa.NET.ImGui.ImGui.PopStyleVar(2);
        Hexa.NET.ImGui.ImGui.PopStyleColor(2);
        return changed;
    }

    /// <summary>Draw an iOS-style toggle switch using ImDrawList.</summary>
    public static bool ToggleSwitch(string id, ref bool value, float width = 38f, float height = 20f)
    {
        var drawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        var pos = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
        float radius = height * 0.5f;

        Hexa.NET.ImGui.ImGui.InvisibleButton(id, new Vector2(width, height));
        bool clicked = Hexa.NET.ImGui.ImGui.IsItemClicked();
        if (clicked) value = !value;

        // Track
        uint trackColor = value ? V4ToAbgr(PrimaryBlueBright) : V4ToAbgr(HexToV4(0x2A3450));
        drawList.AddRectFilled(pos, new Vector2(pos.X + width, pos.Y + height), trackColor, radius);

        // Knob
        float knobX = value ? pos.X + width - radius : pos.X + radius;
        drawList.AddCircleFilled(new Vector2(knobX, pos.Y + radius), radius - 2f, 0xFFFFFFFFu);

        return clicked;
    }

    /// <summary>Draw the brand icon "M" with gradient.</summary>
    public static void DrawBrandIcon(ImDrawListPtr drawList, Vector2 pos, float size)
    {
        // Gradient rounded rect background
        var tl = pos;
        var br = new Vector2(pos.X + size, pos.Y + size);
        uint colTop = V4ToAbgr(HexToV4(0x6C5CE7));    // Purple
        uint colBot = V4ToAbgr(PrimaryBlueBright);      // Blue
        drawList.AddRectFilled(tl, br, colTop, size * 0.22f);
        drawList.AddRectFilledMultiColor(tl, br, colTop, colTop, colBot, colBot);

        // "M" letter centered
        float fontSize = size * 0.6f;
        var textPos = new Vector2(pos.X + size * 0.25f, pos.Y + size * 0.18f);
        drawList.AddText(textPos, 0xFFFFFFFFu, "M");
    }

    /// <summary>Draw a capsule/pill-shaped filter button.</summary>
    public static bool CapsuleButton(string label, bool isActive, float height = 24f)
    {
        var drawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        var pos = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();

        // Measure text
        var textSize = Hexa.NET.ImGui.ImGui.CalcTextSize(label);
        float padding = 14f;
        float width = textSize.X + padding * 2f;
        float radius = height * 0.5f;

        Hexa.NET.ImGui.ImGui.InvisibleButton($"##capsule_{label}", new Vector2(width, height));
        bool clicked = Hexa.NET.ImGui.ImGui.IsItemClicked();
        bool hovered = Hexa.NET.ImGui.ImGui.IsItemHovered();

        uint bgColor;
        uint textColor;
        if (isActive)
        {
            bgColor = V4ToAbgr(PrimaryBlueBright);
            textColor = 0xFFFFFFFFu;
        }
        else if (hovered)
        {
            bgColor = V4ToAbgr(HexToV4(0x1A2440));
            textColor = V4ToAbgr(TextPrimary);
        }
        else
        {
            bgColor = V4ToAbgr(HexToV4(0x151B2E));
            textColor = V4ToAbgr(TextSecondary);
        }

        var br = new Vector2(pos.X + width, pos.Y + height);
        drawList.AddRectFilled(pos, br, bgColor, radius);
        if (!isActive)
        {
            drawList.AddRect(pos, br, V4ToAbgr(CardBorder), radius);
        }

        var textPos = new Vector2(pos.X + padding, pos.Y + (height - textSize.Y) * 0.5f);
        drawList.AddText(textPos, textColor, label);

        return clicked;
    }

    /// <summary>Draw a "底部状态栏" (Bottom Status Bar) footer strip inside a card.</summary>
    public static void FooterStatusBar(string text)
    {
        var drawList = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        Vector2 avail = Hexa.NET.ImGui.ImGui.GetContentRegionAvail();
        Vector2 pos = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
        float barH = 22f;

        // Position at bottom
        float footY = pos.Y + avail.Y - barH;
        drawList.AddRectFilled(
            new Vector2(pos.X - 12f, footY),
            new Vector2(pos.X + avail.X + 12f, footY + barH),
            V4ToAbgr(HexToV4(0x0A0E1A)));
        drawList.AddText(new Vector2(pos.X, footY + 3f), V4ToAbgr(TextSecondary), text);
    }
}
