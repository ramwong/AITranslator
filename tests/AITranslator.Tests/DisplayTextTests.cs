using System;
using AITranslator.UI;
using Xunit;

namespace AITranslator.Tests;

/// <summary>
/// 顯示層文字正規化測試（bug-fix round 2026-11-01）：
/// Win32 EDIT 控制項只渲染 CRLF（LF-only 換行在彈窗不可見、複製出去卻正常），
/// 顯示層需把換行統一成 CRLF；微軟正黑對 U+2019/U+2018 排版撇號以近乎兩倍 ASCII
/// 的前進寬度渲染（實測 25px vs 13px @12pt），"’s" 呈現如 "' s"，
/// 原文框顯示層需代換回 ASCII 撇號。
/// </summary>
public class DisplayTextTests
{
    [Fact]
    public void CrLf_NullBecomesEmpty()
        => Assert.Equal(string.Empty, DisplayText.CrLf(null));

    [Fact]
    public void CrLf_LfAloneBecomesCrLf()
        => Assert.Equal("a\r\nb", DisplayText.CrLf("a\nb"));

    [Fact]
    public void CrLf_CrAloneBecomesCrLf()
        => Assert.Equal("a\r\nb", DisplayText.CrLf("a\rb"));

    [Fact]
    public void CrLf_MixedNewlinesNormalizeToCrLf()
        => Assert.Equal("a\r\nb\r\nc", DisplayText.CrLf("a\nb\r\nc"));

    [Fact]
    public void CrLf_AlreadyCrLfUnchanged()
        => Assert.Equal("a\r\n\r\nb", DisplayText.CrLf("a\r\n\r\nb"));

    [Fact]
    public void Source_TypographicApostrophesReplacedWithAscii()
        => Assert.Equal("can't — he said 'here'", DisplayText.Source("can’t — he said ‘here’"));

    [Fact]
    public void Source_CombinesCrLFAndQuoteNormalization()
        => Assert.Equal("a\r\n's", DisplayText.Source("a\n’s"));

    [Fact]
    public void Translation_NewlinesNormalizedButQuotesKept()
    {
        // 譯文為中文為主，全彎引號屬正常排版：不得被代換，僅換行正規化。
        Assert.Equal("他說「can’t」\r\n第二行", DisplayText.Translation("他說「can’t」\n第二行"));
    }
}