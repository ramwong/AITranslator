namespace AITranslator.Services;

/// <summary>
/// 將使用者文字套入提示詞模板：以 <c>{text}</c> 佔位符替換；
/// 模板若不含佔位符，則在模板後以換行附加文字。
/// </summary>
public static class PromptBuilder
{
    private const string Placeholder = "{text}";

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
}