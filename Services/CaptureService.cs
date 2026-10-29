using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AITranslator.Native;

namespace AITranslator.Services;

/// <summary>Capture() 的結果分類。</summary>
public enum CaptureOutcome
{
    /// <summary>未取得文字（序號未變、剪貼簿非文字、SendInput 失敗等）。</summary>
    NoSelection,
    /// <summary>取得文字但超過長度上限；此時仍回傳完整文字。</summary>
    TooLong,
    /// <summary>成功取得文字。</summary>
    Success,
}

/// <summary>剪貼簿單一原始格式的備份項目（格式編號 + 原始位元組）。</summary>
public sealed class ClipboardEntry
{
    /// <summary>Win32 剪貼簿格式編號（CF_* 或 RegisterClipboardFormat 的值）。</summary>
    public uint Format { get; init; }

    /// <summary>該格式 HGLOBAL 的原始位元組內容。</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// 以「模擬 Ctrl+C → 輪詢剪貼簿序號」的方式從前景視窗擷取使用者選取的文字。
/// 流程：以原始格式備份目前剪貼簿全部內容 → 取得序號 → SendInput Ctrl+C →
/// 輪詢 GetClipboardSequenceNumber（最長 800ms、每步 50ms）→ 讀取文字並分類 →
/// 任何路徑皆以 try/finally 還原備份。
/// Clipboard 與 SendInput 皆須在 UI（STA）執行緒上呼叫。
/// </summary>
public class CaptureService
{
    private const int PollTimeoutMs = 800;
    private const int PollStepMs = 50;

    private readonly int _maxTextLength;

    public CaptureService(int maxTextLength)
    {
        _maxTextLength = maxTextLength;
    }

    /// <summary>
    /// 純決策邏輯，供單元測試：序號未變或無文字 → NoSelection；
    /// 文字超過 max → TooLong；否則 Success。
    /// </summary>
    public static CaptureOutcome Decide(uint seqBefore, uint seqAfter, string? text, int max)
    {
        if (seqAfter == seqBefore)
        {
            return CaptureOutcome.NoSelection;
        }

        if (string.IsNullOrEmpty(text))
        {
            return CaptureOutcome.NoSelection;
        }

        return text.Length > max ? CaptureOutcome.TooLong : CaptureOutcome.Success;
    }

    /// <summary>
    /// 在呼叫者（UI/STA）執行緒上擷取選取文字。任何路徑都會先還原備份再回傳；
    /// 若剪貼簿被鎖住或無法寫回，僅盡力而為、不另行拋出。
    /// </summary>
    public (CaptureOutcome outcome, string text) Capture()
    {
        // 原始格式備份：null 表示 OpenClipboard 失敗（剪貼簿被其他程序鎖住），
        // 此時退化為既有的「純文字備份」行為（僅備份/還原文字，非文字內容仍可能遺失），
        // 至少維持文字使用的舊有保護。
        List<ClipboardEntry>? rawBackup = null;
        string? textBackup = null;
        try
        {
            rawBackup = CaptureBackup();
            if (rawBackup == null)
            {
                textBackup = TryGetClipboardText();
            }

            var seqBefore = Win32.GetClipboardSequenceNumber();

            if (!SendCtrlC())
            {
                return (CaptureOutcome.NoSelection, string.Empty);
            }

            var seqAfter = PollSequenceNumber(seqBefore);
            if (seqAfter == seqBefore)
            {
                return (CaptureOutcome.NoSelection, string.Empty);
            }

            var text = TryGetClipboardText();
            var outcome = Decide(seqBefore, seqAfter, text, _maxTextLength);
            return (outcome, text ?? string.Empty);
        }
        finally
        {
            if (rawBackup != null)
            {
                RestoreBackup(rawBackup);
            }
            else if (textBackup != null)
            {
                try
                {
                    Clipboard.SetText(textBackup);
                }
                catch
                {
                    // 剪貼簿可能被其他處理程序鎖住；還原失敗不影響回傳結果。
                }
            }
        }
    }

    /// <summary>
    /// 逐格式備份剪貼簿的「全部」原始內容（不只文字）：OpenClipboard →
    /// EnumClipboardFormats 逐格式 GetClipboardData → GlobalLock → 複製位元組 →
    /// GlobalUnlock。任何失敗皆 best-effort：OpenClipboard 失敗回傳 null（呼叫端退化為
    /// 純文字備份），列舉/讀取中途失敗回傳空清單（還原時清空剪貼簿——比舊行為
    /// 「只救得回文字」更差者僅在列舉本身出錯的罕見情況）；永不拋出。
    /// <para>限制：複製的是 HGLOBAL 原始位元組，對大多數 GDI 格式（CF_TEXT/CF_UNICODETEXT、
    /// HDROP 等）同工作階段內還原有效；CF_BITMAP 等 GetClipboardData 直接回傳 GDI 句柄
    /// （非 HGLOBAL）者 GlobalLock 會失敗，該格式即被略過；延遲渲染（delay-rendering）
    /// 的特殊格式亦僅盡力而為。不做 DIB 轉換。</para>
    /// </summary>
    private static List<ClipboardEntry>? CaptureBackup()
    {
        if (!Win32.OpenClipboard(IntPtr.Zero))
        {
            return null; // 剪貼簿被鎖；呼叫端退化為純文字備份
        }

        var entries = new List<ClipboardEntry>();
        try
        {
            // format=0 從頭列舉；EnumClipboardFormats 回傳 0 表示已列舉完畢（或失敗）。
            uint format = 0;
            while ((format = Win32.EnumClipboardFormats(format)) != 0)
            {
                var handle = Win32.GetClipboardData(format);
                if (handle == IntPtr.Zero)
                {
                    continue; // 該格式取不到句柄（如延遲渲染中）；略過
                }

                var size = Win32.GlobalSize(handle);
                if (size == UIntPtr.Zero)
                {
                    continue; // 非 HGLOBAL（如 CF_BITMAP 回傳 GDI 句柄）；GlobalSize/GlobalLock 不適用
                }

                var ptr = Win32.GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var bytes = new byte[(int)(ulong)size];
                    Marshal.Copy(ptr, bytes, 0, bytes.Length);
                    entries.Add(new ClipboardEntry { Format = format, Data = bytes });
                }
                finally
                {
                    Win32.GlobalUnlock(handle);
                }
            }

            return entries;
        }
        catch
        {
            // 列舉/複製中途失敗：以「空清單」代表 best-effort 失敗（文件見上方 XML 註解）。
            return new List<ClipboardEntry>();
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    /// <summary>
    /// 把備份的原始格式逐筆寫回剪貼簿：OpenClipboard → EmptyClipboard →
    /// 逐筆 GlobalAlloc(GMEM_MOVEABLE) → 複製位元組 → SetClipboardData
    /// （成功後剪貼簿擁有該句柄，不得再 GlobalFree；SetClipboardData 失敗則自行釋放）
    /// → finally CloseClipboard。全程 best-effort，絕不拋出。
    /// </summary>
    private static void RestoreBackup(List<ClipboardEntry> backup)
    {
        if (!Win32.OpenClipboard(IntPtr.Zero))
        {
            return; // 剪貼簿被鎖；還原失敗（best-effort，不影響回傳結果）
        }

        try
        {
            // 清掉擷取時寫入的內容；EmptyClipboard 也把剪貼簿擁有者設為我們，
            // 使後續 SetClipboardData 可直接放入。
            if (!Win32.EmptyClipboard())
            {
                return;
            }

            foreach (var entry in backup)
            {
                var data = entry.Data;
                if (data.Length == 0)
                {
                    continue;
                }

                var handle = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)(ulong)data.Length);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                var ptr = Win32.GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                {
                    Win32.GlobalFree(handle);
                    continue;
                }

                try
                {
                    Marshal.Copy(data, 0, ptr, data.Length);
                }
                finally
                {
                    Win32.GlobalUnlock(handle);
                }

                if (Win32.SetClipboardData(entry.Format, handle) == IntPtr.Zero)
                {
                    Win32.GlobalFree(handle); // 剪貼簿未收下句柄，自行釋放避免洩漏
                }
                // SetClipboardData 成功：句柄所有權移轉給剪貼簿，不得再 GlobalFree。
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static bool SendCtrlC()
    {
        var inputs = new[]
        {
            KeyInput(Win32.VK_CONTROL, flags: 0),
            KeyInput(Win32.VK_C, flags: 0),
            KeyInput(Win32.VK_C, Win32.KEYEVENTF_KEYUP),
            KeyInput(Win32.VK_CONTROL, Win32.KEYEVENTF_KEYUP),
        };

        var sent = Win32.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>());
        return sent == inputs.Length;
    }

    private static Win32.INPUT KeyInput(ushort vk, uint flags)
    {
        return new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            u = new Win32.INPUTUNION
            {
                ki = new Win32.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = flags,
                },
            },
        };
    }

    private static uint PollSequenceNumber(uint seqBefore)
    {
        var deadline = Stopwatch.GetTimestamp() + (PollTimeoutMs * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(PollStepMs);
            var seq = Win32.GetClipboardSequenceNumber();
            if (seq != seqBefore)
            {
                return seq;
            }
        }

        return Win32.GetClipboardSequenceNumber();
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            // 剪貼簿可能被鎖住或持有非文字內容；視同無備份。
            return null;
        }
    }
}