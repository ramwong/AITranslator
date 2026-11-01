namespace AITranslator.Services;

/// <summary>
/// 將使用者文字套入提示詞模板：以 <c>{text}</c> 佔位符替換；
/// 模板若不含佔位符，則在模板後以換行附加文字。
///
/// <para><see cref="BuildTranslation"/> 額外附加「純文字輸出約束」（<see cref="MarkdownNoMarkupSuffix"/>）：
/// LLM 慣性輸出 Markdown/LaTeX/HTML 標記（如 <c>\rightarrow</c>、<c>&amp;rightarrow;</c>），
/// 彈窗要的是可直接閱讀/複製的純文字——約束**由 builder 附加而非存在使用者範本**，
/// 使用者 settings.json 裡的舊範本不需重置也吃到；漏網的由
/// <see cref="ReplyCleaner"/> 白名單轉換兜底（兩層防護，spec 2026-11-01 修訂）。</para>
/// </summary>
public static class PromptBuilder
{
    private const string Placeholder = "{text}";

    /// <summary>附加於譯文類 prompt 尾端的格式約束；原文引用部分要求原樣輸出。</summary>
    public const string MarkdownNoMarkupSuffix =
        "\n【輸出格式】只用純文字回答：不要使用任何 Markdown、LaTeX 或 HTML 標記"
        + "（例如 *強調*、**粗體**、#標題、\\rightarrow、\\to、&amp;rightarrow;、<br>）；"
        + "要表示箭頭時直接使用 Unicode 符號（→、←、⇒、⇔、↔、↑、↓）。"
        + "如要求同時輸出原文，原文一字不改地原樣保留，不受本條約束影響。";

    public static string Build(string template, string text)
    {
        if (string.IsNullOrEmpty(template))
        {
            return text ?? string.Empty;
        }

        return template.Contains(Placeholder)
            ? template.Replace(Placeholder, text)
            : template + "\n" + text;
    }

    /// <summary>翻譯類 prompt：套模板後附加純文字輸出約束（不寫入使用者範本）。</summary>
    public static string BuildTranslation(string template, string text)
        => Build(template, text) + MarkdownNoMarkupSuffix;
}