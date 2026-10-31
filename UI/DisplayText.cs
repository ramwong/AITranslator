using System;

namespace AITranslator.UI;

/// <summary>
/// 彈窗顯示層文字正規化（bug-fix round 2026-11-01）。<b>僅用於顯示</b>：
/// 剪貼簿複製與 TTS 一律使用原始字串，正規化不得污染資料。
///
/// <para><b>CRLF</b>：WinForms TextBox 包裝的是 Win32 EDIT 控制項，只把
/// <c>\r\n</c> 渲染成換行——模型回傳的 LF-only（\n）文字在彈窗中完全不可見
/// （分行憑空消失），複製出剪貼簿又正常（拷的是原始字串）。顯示前統一：
/// <c>\r\n｜\r｜\n</c> → <c>\r\n</c>。</para>
///
/// <para><b>撇號</b>：微軟正黑對排版撇號 <c>U+2019</c>／<c>U+2018</c> 採用
/// 全形 CJK 前進寬度（TextRenderer 實測 25px，ASCII <c>U+0027</c> 僅 13px
/// @12pt），<c>"’s"</c> 呈現如 <c>"' s"</c>。原文（多為英文 OCR/選取文字）
/// 顯示時代換回 ASCII 撇號；譯文（中文為主，彎引號屬正常排版）不代換。</para>
/// </summary>
internal static class DisplayText
{
    /// <summary>所有換行形式（\r\n、\r、\n）統一為 \r\n；null 回傳空字串。</summary>
    public static string CrLf(string? text)
    {
        if (text == null)
        {
            return string.Empty;
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n')
                   .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>原文框顯示字串：換行正規化＋排版撇號代換為 ASCII。</summary>
    public static string Source(string? text)
        => CrLf(text).Replace('’', '\'').Replace('‘', '\'');

    /// <summary>譯文區顯示字串：僅換行正規化（彎引號保留，中文屬正常排版）。</summary>
    public static string Translation(string? text) => CrLf(text);
}