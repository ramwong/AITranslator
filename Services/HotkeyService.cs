using System;
using AITranslator.Native;

namespace AITranslator.Services;

/// <summary>
/// 將「Ctrl+Alt+E」形式的快速鍵字串註冊為全系統熱鍵：
/// <see cref="HotkeyParser"/> 負責解析字串 →
/// user32 RegisterHotKey 以隱藏訊息視窗為目標註冊 →
/// 視窗 WndProc 收到 WM_HOTKEY 時觸發 <paramref name="onHotkey"/>
/// （該回呼在建立視窗的 UI 執行緒上執行）。
/// 註冊失敗（字串不合規、或熱鍵已被其他程式佔用）僅回傳 false，由呼叫端通知使用者。
/// </summary>
public class HotkeyService
{
    private const int HotkeyId = 1; // 同一視窗只註冊一組熱鍵

    private readonly MessageWindow _window;
    private readonly Action _onHotkey;
    private bool _registered;

    public HotkeyService(MessageWindow window, Action onHotkey)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _onHotkey = onHotkey ?? throw new ArgumentNullException(nameof(onHotkey));
    }

    /// <summary>
    /// 解析並註冊快速鍵字串。字串不合規、視窗代號無效或
    /// RegisterHotKey 失敗（例如熱鍵已被佔用）皆回傳 false。
    /// 若先前已有註冊，會先取消再註冊新的組合。
    /// </summary>
    public bool Register(string hotkeyString)
    {
        if (_registered)
        {
            Unregister();
        }

        var (success, mods, key) = HotkeyParser.Parse(hotkeyString);
        if (!success || _window.WindowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!Win32.RegisterHotKey(_window.WindowHandle, HotkeyId, mods, key))
        {
            return false;
        }

        _registered = true;
        return true;
    }

    /// <summary>
    /// 取消註冊。未註冊時呼叫為無動作（冪等）；取消失敗也僅視為已取消、不拋出。
    /// </summary>
    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        Win32.UnregisterHotKey(_window.WindowHandle, HotkeyId);
        _registered = false;
    }

    /// <summary>
    /// 依新的快速鍵字串重新註冊（先 Unregister 再 Register），
    /// 供設定變更時使用；冪等。
    /// </summary>
    public bool ReRegister(string hotkeyString)
    {
        Unregister();
        return Register(hotkeyString);
    }
}