using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AITranslator.Services;

/// <summary>
/// 擷取螢幕指定區域並編碼為 base64 PNG（供 OCR 帶圖呼叫使用）。
/// 流程：以 <see cref="Graphics.CopyFromScreen"/>（目的地為新建Bitmap 的
/// <see cref="Graphics"/>，來源為實體像素螢幕座標）擷取→ 依
/// <see cref="MaxImageSide"/> 等比縮小（若超過上限）→ 編碼為 PNG → base64 字串。
/// 全程僅在記憶體中進行，不寫入任何暫存檔。
/// </summary>
public static class ScreenCaptureService
{
    /// <summary>送出 OCR 前圖片允許的最長邊（像素）；超過者等比縮小到此上限。</summary>
    public const int MaxImageSide = 2048;

    /// <summary>
    /// 純決策邏輯，供單元測試：最長邊不大於 <see cref="MaxImageSide"/> 時回傳
    /// <see langword="null"/>（不縮放）；否則回傳等比縮小係數（正數且 ≤ 1），
    /// 使縮放後最長邊恰為 <see cref="MaxImageSide"/>。
    /// </summary>
    public static double? ComputeScale(int width, int height)
    {
        var longest = Math.Max(width, height);
        return longest <= MaxImageSide ? null : MaxImageSide / (double)longest;
    }

    /// <summary>
    /// 擷取 rect 所指定的螢幕區域並回傳 base64 編碼的 PNG。
    /// <paramref name="rect"/> 為「實體像素」座標（呼叫端保證，本方法不做任何 DPI 換算）；
    /// 若最長邊超過 <see cref="MaxImageSide"/>，先以 <see cref="ComputeScale"/>
    /// 求得的係數縮小再編碼。
    /// </summary>
    public static string CaptureToBase64(Rectangle rect)
    {
        using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        // 目的端 Graphics 必須由 bitmap 建立（Graphics.FromImage）：
        // Graphics.FromHwnd(IntPtr.Zero) 取得的是「桌面 DC」——CopyFromScreen 的目的端即
        // 螢幕本身，bitmap 完全不會被寫入（每一像素 alpha=0，編碼出全透明的 PNG），
        // 且來源矩形非 (0,0) 時還會把該螢幕區域 BitBlt 到螢幕左上角（可見 blit 痕跡）。
        // 來源座標照舊為實體像素（PerMonitorV2 下 CopyFromScreen 直接以實體座標抓圖）。
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size);
        }

        var scale = ComputeScale(rect.Width, rect.Height);
        if (scale == null)
        {
            return EncodePngToBase64(bitmap);
        }

        // 縮小尺寸：最長邊恰為 MaxImageSide，短邊按同一係數等比，以進位收尾避免不足一像素的縮隙。
        var newWidth = Math.Max(1, (int)Math.Round(rect.Width * scale.Value, MidpointRounding.AwayFromZero));
        var newHeight = Math.Max(1, (int)Math.Round(rect.Height * scale.Value, MidpointRounding.AwayFromZero));

        using var resized = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            // 高品質縮小：雙立方插值縮出的文字輪廓最乾淨，對 OCR 較有利。
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bitmap, new Rectangle(0, 0, newWidth, newHeight));
        }

        return EncodePngToBase64(resized);
    }

    private static string EncodePngToBase64(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToBase64String(stream.ToArray());
    }
}