using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

public class ScreenCaptureServiceTests
{
    [Theory]
    [InlineData(1000, 800, null)]          // 不縮
    [InlineData(2048, 1024, null)]         // 恰在界線不縮
    [InlineData(4096, 2048, 0.5)]          // 長邊縮到 2048、等比
    [InlineData(2049, 10, 2048.0 / 2049)]  // 稍超過也縮
    public void ComputeScale_EnforcesMaxSide(int w, int h, double? expected)
    {
        var scale = ScreenCaptureService.ComputeScale(w, h);

        // xunit 的 precision 多載僅提供非可空 double 版本，故先分支處理 null 再以 precision 比較。
        if (scale is null)
        {
            Assert.Null(expected);
            return;
        }

        Assert.Equal(expected!.Value, scale.Value, precision: 6);
    }
}