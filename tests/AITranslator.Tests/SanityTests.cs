using System.Drawing;
using AITranslator.UI;
using Xunit;

namespace AITranslator.Tests;

public class SanityTests
{
    [Fact]
    public void AssertTrue()
    {
        Assert.True(true);
    }

    [Fact]
    public void Theme_TokensMatchSpec()
    {
        ColorAssert(Theme.Paper,      252, 250, 246); // #FCFAF6
        ColorAssert(Theme.Panel,      243, 240, 232); // #F3F0E8
        ColorAssert(Theme.Hairline,   231, 226, 214); // #E7E2D6
        ColorAssert(Theme.Ink,         38,  34,  27); // #26221B
        ColorAssert(Theme.SourceText, 138, 133, 120); // #8A8578
        ColorAssert(Theme.Caption,    168, 162, 148); // #A8A294
        ColorAssert(Theme.FooterBg,   247, 245, 238); // #F7F5EE
        ColorAssert(Theme.Accent,      43,  95, 205); // #2B5FCD
        ColorAssert(Theme.Error,      194,  65,  12); // #C2410C
        ColorAssert(Theme.MutedBtn,   110, 106,  94); // #6E6A5E
    }

    /// <summary>斷言 <paramref name="c"/> 的 R/G/B 分量逐一相等（Alpha 不釘，token 全為不透明色）。</summary>
    private static void ColorAssert(Color c, int r, int g, int b)
    {
        Assert.Equal(r, c.R);
        Assert.Equal(g, c.G);
        Assert.Equal(b, c.B);
    }
}