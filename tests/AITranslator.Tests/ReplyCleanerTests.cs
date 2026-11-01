using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

/// <summary>譯文兜底清潔（白名單純函式）測試：箭咀 LaTeX/entity → Unicode；格式符號不剝除。</summary>
public class ReplyCleanerTests
{
    [Theory]
    [InlineData("\\rightarrow", "→")]
    [InlineData("\\leftarrow", "←")]
    [InlineData("\\Rightarrow", "⇒")]
    [InlineData("\\Leftrightarrow", "⇔")]
    [InlineData("\\to", "→")]
    [InlineData("&rightarrow;", "→")]
    [InlineData("&rarr;", "→")]
    [InlineData("&lArr;", "⇒")]
    [InlineData("&amp;", "&")]
    [InlineData("&lt;a&gt;", "<a>")]
    [InlineData("A\\rightarrow B", "A→ B")]
    public void Clean_ConvertsCommonArrowMarkdownToUnicode(string input, string expected)
        => Assert.Equal(expected, ReplyCleaner.Clean(input));

    [Theory]
    [InlineData("純文字沒有需要轉換的內容")]
    [InlineData("*強調* 與 #標題 保持原樣")]
    [InlineData("a & b")]
    [InlineData("")]
    public void Clean_PassesThroughUnmappedText(string input)
        => Assert.Equal(input, ReplyCleaner.Clean(input));

    [Fact]
    public void Clean_NullReturnsEmpty()
        => Assert.Equal(string.Empty, ReplyCleaner.Clean(null));

    [Fact]
    public void PromptBuilder_BuildTranslation_AppendsNoMarkupSuffix()
    {
        var prompt = PromptBuilder.BuildTranslation("把 {text} 翻成繁中", "X → Y");
        Assert.StartsWith("把 X → Y 翻成繁中", prompt);
        Assert.Contains("不要使用任何 Markdown", prompt);
        Assert.Contains("&amp;rightarrow;", prompt); // suffix 本身的反例字串存在
    }

    [Theory]
    [InlineData("把 {text} 翻", "x", "把 x 翻")]     // 有佔位符：替換、無後綴
    [InlineData("直接翻譯", "x", "直接翻譯\nx")]      // 無佔位符：換行附加、無後綴
    public void PromptBuilder_Build_HasNoSuffix(string template, string text, string expected)
        => Assert.Equal(expected, PromptBuilder.Build(template, text));
}