using System;
using System.Drawing;
using AITranslator.UI;
using Xunit;

namespace AITranslator.Tests;

/// <summary>
/// <see cref="RegionSelectorForm"/> 的純決策函式測試（Normalize / Decide / MinRectSize）。
/// overlay／多螢幕 GDI 行為（CopyFromScreen 補畫、跨螢幕拖曳、DPI）不在此覆蓋，
/// 由 Task 4 Step 6 的手動驗證清單擔保。
/// </summary>
public class RegionSelectorTests
{
    [Fact]
    public void Normalize_ReordersOppositeCorners()
    {
        var r = RegionSelectorForm.Normalize(new Point(100, 200), new Point(40, 80));
        Assert.Equal(40, r.X);
        Assert.Equal(80, r.Y);
        Assert.Equal(60, r.Width);
        Assert.Equal(120, r.Height);
    }

    [Fact]
    public void Normalize_SamePointIsEmptySizedRect()
    {
        // 同點起訖：寬高為 0（Decide 應判 TooSmall）
        var r = RegionSelectorForm.Normalize(new Point(50, 60), new Point(50, 60));
        Assert.Equal(50, r.X);
        Assert.Equal(60, r.Y);
        Assert.Equal(0, r.Width);
        Assert.Equal(0, r.Height);
    }

    [Fact]
    public void MinRectSize_Is8()
    {
        Assert.Equal(8, RegionSelectorForm.MinRectSize);
    }

    [Theory]
    [InlineData(8, 9, RegionSelectionStatus.Selected)]   // 恰在下限
    [InlineData(7, 100, RegionSelectionStatus.TooSmall)]
    [InlineData(100, 7, RegionSelectionStatus.TooSmall)]
    public void Decide_EnforcesMinRectSize(int w, int h, RegionSelectionStatus expected)
        => Assert.Equal(expected, RegionSelectorForm.Decide(new Rectangle(0, 0, w, h)));
}