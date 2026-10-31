using AITranslator.Services;
using AITranslator.UI;
using Xunit;

namespace AITranslator.Tests;

public class HotkeyRecorderTests
{
    [Theory]
    [InlineData(Keys.Control | Keys.Alt | Keys.E, "Ctrl+Alt+E")]
    [InlineData(Keys.Control | Keys.F5, "Ctrl+F5")]
    [InlineData(Keys.Shift | Keys.ControlKey, null)]   // 純修飾鍵 → null
    [InlineData(Keys.Control | Keys.ShiftKey, null)]   // 同上（ShiftKey 是鍵身不是 Shift 修飾）
    [InlineData((Keys)0, null)]
    public void FormatKeys_NormalizesAndRejectsModifierOnly(Keys keys, string? expected)
        => Assert.Equal(expected, HotkeyRecorder.FormatKeys(keys));

    [Fact]
    public void FormatKeys_MatchesHotkeyParserRoundTrip()
    {
        var formatted = HotkeyRecorder.FormatKeys(Keys.Control | Keys.Alt | Keys.S);
        Assert.True(HotkeyParser.Parse(formatted!).success); // FormatKeys 的輸出必須被既有 Parser 接受
    }
}