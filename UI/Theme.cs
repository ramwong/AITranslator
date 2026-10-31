using System.Drawing;

namespace AITranslator.UI;

/// <summary>
/// 全案配色的唯一來源（spec「配色 token」表：docs/superpowers/specs/2026-10-31-ui-redesign-design.md）：
/// 紙感（paper/ink）淺色主題的顏色、尺寸與字體 token。
/// 表單不再各自寫死顏色——彈窗、設定表單與框選 overlay 一律引用本類別的 token；
/// token 值逐一對應 spec 的十六進位表，改色只改這裡。
/// </summary>
/// <remarks>
/// 樣式 helper（flat 按鈕等共用樣式）刻意<b>不</b>收在本類別——待對應表單任務
/// （彈窗／設定重繪）成形後再由使用處抽入,避免過早抽象。
/// </remarks>
internal static class Theme
{
    // ---- 顏色 token（值來自 spec「配色 token」表）----

    /// <summary>紙面底色 <c>#FCFAF6</c>：彈窗、設定表單的底色。</summary>
    internal static readonly Color Paper = Color.FromArgb(252, 250, 246);

    /// <summary>內嵌面板色 <c>#F3F0E8</c>：原文內嵌面板（「紙上有紙」）。</summary>
    internal static readonly Color Panel = Color.FromArgb(243, 240, 232);

    /// <summary>髮絲線色 <c>#E7E2D6</c>：面板邊框、小節分隔線。</summary>
    internal static readonly Color Hairline = Color.FromArgb(231, 226, 214);

    /// <summary>墨色 <c>#26221B</c>：譯文等主按鈕以外的最深色文字。</summary>
    internal static readonly Color Ink = Color.FromArgb(38, 34, 27);

    /// <summary>原文文字色 <c>#8A8578</c>：原文面板的原文文字。</summary>
    internal static readonly Color SourceText = Color.FromArgb(138, 133, 120);

    /// <summary>小標色 <c>#A8A294</c>：小節標題、原文面板上緣小標「選取文字」。</summary>
    internal static readonly Color Caption = Color.FromArgb(168, 162, 148);

    /// <summary>動作列底色 <c>#F7F5EE</c>：彈窗動作列、設定按鈕列的底色。</summary>
    internal static readonly Color FooterBg = Color.FromArgb(247, 245, 238);

    /// <summary>靛青點綴色 <c>#2B5FCD</c>：主按鈕底色、框選矩形的醒目邊框、錄製提示關鍵字。</summary>
    internal static readonly Color Accent = Color.FromArgb(43, 95, 205);

    /// <summary>赭紅錯誤色 <c>#C2410C</c>：錯誤語意（全案唯一暖色歸屬，錯誤以外不得使用暖色）。</summary>
    internal static readonly Color Error = Color.FromArgb(194, 65, 12);

    /// <summary>幽靈按鈕文字色 <c>#6E6A5E</c>：無底色的次要按鈕文字。</summary>
    internal static readonly Color MutedBtn = Color.FromArgb(110, 106, 94);

    // ---- 尺寸 token（spec 間距 token 表；builder 可依實測微調的使用處不在此列）----

    /// <summary>彈窗動作列高度（px）。</summary>
    internal const int FooterHeight = 36;

    /// <summary>彈窗狀態條高度（px）。</summary>
    internal const int StatusHeight = 26;

    /// <summary>原文面板內距（px）。</summary>
    internal const int PanelPadding = 10;

    /// <summary>卡片內距（px）。</summary>
    internal const int CardPadding = 14;

    // ---- 字體 token ----

    /// <summary>全文統一字體（spec 字體 token）。</summary>
    internal const string FontName = "Microsoft JhengHei";
}