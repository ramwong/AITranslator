namespace AITranslator.Services;

/// <summary>
/// 譯文輸出的兜底清潔（bug-fix round 2026-11-01）：LLM 不理會純文字約束時，
/// 把常見的 LaTeX / HTML entity 箭咀與必現亂碼的 entity 轉成 Unicode 可讀字元。
///
/// <para><b>白名單純函式</b>：只轉 <see cref="Replacements"/> 表內的精確字串
/// （長鍵在前，避免前綴誤傷），**不做** <c>*強調*</c>、<c>#</c> 等格式剝除
/// （誤判原文風險高）；**僅作用於譯文**——原文（OCR 辨識字串、選取文字）一律原樣。</para>
/// </summary>
public static class ReplyCleaner
{
    /// <summary>精確字串 → Unicode 替換（長鍵在前）；LaTeX 反斜線形與 HTML entity 形都收。</summary>
    private static readonly (string Key, string Value)[] Replacements =
    [
        ("\\Leftrightarrow", "⇔"), ("\\leftrightarrow", "↔"),
        ("\\Rightarrow", "⇒"), ("\\Leftarrow", "⇐"),
        ("\\longrightarrow", "⟶"), ("\\longleftarrow", "⟵"),
        ("\\rightarrow", "→"), ("\\leftarrow", "←"),
        ("\\uparrow", "↑"), ("\\downarrow", "↓"), ("\\to", "→"),
        ("&Rightarrow;", "⇒"), ("&Leftarrow;", "⇐"),
        ("&rightarrow;", "→"), ("&leftarrow;", "←"),
        ("&rArr;", "⇒"), ("&lArr;", "⇐"), ("&harr;", "↔"),
        ("&rarr;", "→"), ("&larr;", "←"),
        ("&uarr;", "↑"), ("&darr;", "↓"),
        ("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""),
        ("&amp;", "&"),
    ];

    /// <summary>把 <paramref name="text"/> 中所有白名單字串轉為可讀 Unicode；null 回空字串。</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0 && !text.Contains('&'))
        {
            return text ?? string.Empty; // 沒有 '\\' 也沒有 '&'：零配置快速路徑
        }

        foreach (var (key, value) in Replacements)
        {
            text = text.Replace(key, value, StringComparison.Ordinal);
        }

        return text;
    }
}