using System;
using System.Windows.Forms;

namespace AITranslator.Native;

/// <summary>
/// 隱藏的 message-only 視窗：僅用來接收訊息，不顯示、不進入 Z 順序、
/// 也不會出現在工作列或 Alt+Tab，適合做為 RegisterHotKey 的收訊目標。
/// 必須在 UI（STA）執行緒上建立；WndProc 在擁有視窗的執行緒上被呼叫，
/// 因此 WM_HOTKEY 觸發的 callback 自然 marshal 回主執行緒（無需另做 Invoke）。
/// </summary>
public sealed class MessageWindow : NativeWindow
{
    private const int HWND_MESSAGE = -3; // message-only 視窗的父視窗代號

    private readonly Action<int> _onHotkey;

    /// <paramref name="onHotkey"/> 收到 WM_HOTKEY 時的回呼，參數為
    /// RegisterHotKey 時使用的熱鍵 id（即訊息的 WParam），
    /// 會在建立本視窗的（UI）執行緒上被呼叫。
    public MessageWindow(Action<int> onHotkey)
    {
        _onHotkey = onHotkey ?? throw new ArgumentNullException(nameof(onHotkey));

        CreateHandle(new CreateParams
        {
            Caption = "AITranslator.MessageWindow",
            Parent = (IntPtr)HWND_MESSAGE,
            Style = 0,
            ExStyle = 0,
        });
    }

    /// <summary>
    /// 訊息視窗代號（繼承自 NativeWindow）；HotkeyService 以此為目標註冊快速鍵。
    /// </summary>
    public IntPtr WindowHandle => Handle;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            _onHotkey((int)m.WParam); // WParam 為註冊時的熱鍵 id，由呼叫端分派
            return; // WM_HOTKEY 已處理，不需轉交 base
        }

        base.WndProc(ref m);
    }
}