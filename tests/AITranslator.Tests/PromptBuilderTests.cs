using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

public class PromptBuilderTests
{
    [Fact]
    public void Placeholder_IsReplacedByText()
    {
        const string template = "翻譯以下內容:\n{text}";
        const string text = "Hello world";

        var result = PromptBuilder.Build(template, text);

        Assert.Equal("翻譯以下內容:\nHello world", result);
    }

    [Fact]
    public void MultiplePlaceholders_AreAllReplaced()
    {
        const string template = "{text}\n---\n{text}";

        var result = PromptBuilder.Build(template, "ABC");

        Assert.Equal("ABC\n---\nABC", result);
    }

    [Fact]
    public void TemplateWithoutPlaceholder_AppendsTextOnNewLine()
    {
        const string template = "請翻譯以下內容";

        var result = PromptBuilder.Build(template, "ABC");

        Assert.Equal("請翻譯以下內容\nABC", result);
    }

    [Fact]
    public void TextPreservedVerbatim()
    {
        const string template = "{text}";

        var result = PromptBuilder.Build(template, "line1\nline2\ttab");

        Assert.Equal("line1\nline2\ttab", result);
    }
}