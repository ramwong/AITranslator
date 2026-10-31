using System.Drawing;

namespace AITranslator.UI;

/// <summary>彈窗深色配色常數（spec：2026-10-31-popup-layouts-design.md）。</summary>
internal static class PopupTheme
{
    public static readonly Color Background = Color.FromArgb(30, 31, 36);     // #1E1F24
    public static readonly Color Divider = Color.FromArgb(58, 61, 69);        // #3A3D45
    public static readonly Color ButtonBack = Color.FromArgb(42, 44, 51);     // #2A2C33
    public static readonly Color ButtonHover = Color.FromArgb(52, 56, 65);    // #343841
    public static readonly Color SourceText = Color.FromArgb(155, 167, 184);  // #9BA7B8
    public static readonly Color MainText = Color.FromArgb(242, 244, 248);    // #F2F4F8
    public static readonly Color ErrorText = Color.FromArgb(208, 122, 106);   // #D07A6A
}