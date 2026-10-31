using System;
using System.Collections.Generic;
using AITranslator.Native;

namespace AITranslator.Services;

/// <summary>
/// 將「Ctrl+Alt+E」形式的快速鍵字串註冊為全系統熱鍵，可同時註冊多組（以 id 區分）：
/// <see cref="HotkeyParser"/> 負責解析字串 →
/// user32 RegisterHotKey 以隱藏訊息視窗為目標、以呼叫端指定的 id 註冊 →
/// 視窗 WndProc 收到 WM_HOTKEY 時由呼叫端依 WParam 的 id 分派。
/// 註冊失敗（字串不合規、或熱鍵已被其他程式佔用）僅回傳 false，由呼叫端通知使用者。
/// </summary>
public class HotkeyService
{
    private readonly MessageWindow _window;

    /// <summary>目前成功註冊中的熱鍵 id 集合（每個 id 對應一組組合鍵）。</summary>
    private readonly HashSet<int> _registeredIds = new();

    public HotkeyService(MessageWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>
    /// 解析並註冊快速鍵字串。字串不合規、視窗代號無效或
    /// RegisterHotKey 失敗（例如熱鍵已被佔用）皆回傳 false。
    /// 同一 id 若先前已有註冊，會先取消該 id 再註冊新的組合；其他 id 不受影響。
    public bool Register(int id, string hotkeyString)
    {
        if (_registeredIds.Contains(id))
        {
            Unregister(id);
        }

        var (success, mods, key) = HotkeyParser.Parse(hotkeyString);
        if (!success || _window.WindowHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!Win32.RegisterHotKey(_window.WindowHandle, id, mods, key))
        {
            return false;
        }

        _registeredIds.Add(id);
        return true;
    }

    /// <summary>
    /// 取消註冊指定的 id。未註冊時呼叫為無動作（冪等）；取消失敗也僅視為已取消、不拋出。
    /// </summary>
    public void Unregister(int id)
    {
        if (!_registeredIds.Contains(id))
        {
            return;
        }

        Win32.UnregisterHotKey(_window.WindowHandle, id);
        _registeredIds.Remove(id);
    }

    /// <summary>
    /// 依新的快速鍵字串重新註冊指定的 id（先 Unregister 再 Register），
    /// 供設定變更時使用；冪等。
    /// </summary>
    public bool ReRegister(int id, string hotkeyString)
    {
        Unregister(id);
        return Register(id, hotkeyString);
    }
}