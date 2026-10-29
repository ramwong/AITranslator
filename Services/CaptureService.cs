using System.Diagnostics;
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

/// <summary>
/// 以「模擬 Ctrl+C → 輪詢剪貼簿序號」的方式從前景視窗擷取使用者選取的文字。
/// 流程：備份目前剪貼簿文字 → 取得序號 → SendInput Ctrl+C →
/// 輪詢 GetClipboardSequenceNumber（最長 800ms、每步 50ms）→ 讀取文字並分類 →
/// 任何路徑皆以 try/finally 還原備份（備份為 null 即還原）。
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
    /// 若剪貼簿文字被鎖住或無法寫回，僅盡力而為、不另行拋出。
    /// </summary>
    public (CaptureOutcome outcome, string text) Capture()
    {
        string? backup = null;
        try
        {
            backup = TryGetClipboardText();

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
            if (backup != null)
            {
                try
                {
                    Clipboard.SetText(backup);
                }
                catch
                {
                    // 剪貼簿可能被其他處理程序鎖住；還原失敗不影響回傳結果。
                }
            }
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