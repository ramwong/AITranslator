using System;
using System.Runtime.InteropServices;

namespace AITranslator.Native;

/// <summary>
/// 彈窗關閉用之「觀察型」低層級掛鉤：<see cref="Begin"/> 安裝
/// <c>WH_MOUSE_LL</c>（滑鼠左/右鍵按下）與 <c>WH_KEYBOARD_LL</c>（Esc 鍵）
/// 兩條全域低層級 hook，偵測到事件時呼叫 dismiss（由彈窗自行 Close），
/// hooks 則持續安裝至 <see cref="End"/> 為止；本類別不消耗、不攔截任何輸入。
///
/// <para><b>絕對不變量（Review Focus 4）</b>：兩個 hook proc 的「每一個分支」
/// 都必須以 <see cref="Win32.CallNextHookEx"/> 的回傳值作為回傳值，連觸發 dismiss
/// 的那一次輸入也不例外，絕不回傳非零或跳過 hook 鏈。低層級 hook 回呼回傳非零
/// 會把該事件「吞掉」——使用者按下的滑鼠鍵/Esc 就這樣消失（不論當下作用中的
/// 視窗屬於哪個程式）。因此本類別對輸入一律放行，只站在一旁觀察。</para>
///
/// <para><b>執行緒</b>：低層級 hook 的回呼由安裝執行緒的訊息迴圈分派，
/// 因此 <see cref="Begin"/> 必須在擁有 Windows Forms 訊息幫浦的 UI 執行緒上呼叫
/// （呼叫端即彈窗本身）；hook proc 內直接呼叫 dismiss，即會在該 UI 執行緒上
/// 執行，無須封送。dismiss 的例外以 try/catch 包住——例外若逃出 hook proc，
/// 可能造成全系統輸入死鎖或程序崩潰，故於此處攔下。</para>
///
/// <para><b>生命週期</b>：一次只允許一組 hook；已啟用時再呼叫 <see cref="Begin"/>
/// 視為冪等（直接返回）、未啟用時呼叫 <see cref="End"/> 亦為無動作。
/// dismiss 每次啟用最多觸發一次（防護旗標）。實作 <see cref="IDisposable"/>
/// 僅為使用端對稱性（Dispose → End），本類別仍為純靜態狀態、無需建構。</para>
/// </summary>
internal sealed class DismissHook : IDisposable
{
    private static bool _active;
    private static bool _dismissed;
    private static Action? _dismiss;

    // 保持 delegate 參考以防止 GC 回收已封送的 hook 函式指標
    private static Win32.LowLevelMouseProc? _mouseProc;
    private static Win32.LowLevelKeyboardProc? _keyboardProc;
    private static IntPtr _mouseHook;
    private static IntPtr _keyboardHook;

    private DismissHook()
    {
    }

    /// <summary>
    /// 安裝滑鼠/鍵盤低層級 hook（hMod=IntPtr.Zero、dwThreadId=0 → 全域 LL hook）。
    /// 須由 UI 執行緒（RunForms 訊息幫浦）呼叫；已啟用時為冪等、直接返回。
    /// </summary>
    public static void Begin(Action dismiss)
    {
        if (dismiss == null)
        {
            throw new ArgumentNullException(nameof(dismiss));
        }

        if (_active)
        {
            return; // 冪等 re-Begin：一次只允許一組 hook
        }

        _dismiss = dismiss;
        _dismissed = false;

        _mouseProc = MouseHookProc;
        _mouseHook = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        _keyboardProc = KeyboardHookProc;
        _keyboardHook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);

        _active = true;
    }

    /// <summary>
    /// 解除兩條 hook 並清空狀態。未啟用時呼叫為無動作（冪等）。
    /// 解除失敗（回傳 false）也不拋出——hook 已視為不存在，由下一次 Begin 重置。
    /// </summary>
    public static void End()
    {
        if (!_active)
        {
            return;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _mouseProc = null; // 已 Unhook，Windows 不會再呼叫，可安全釋放 delegate
        _keyboardProc = null;
        _dismiss = null;
        _dismissed = false;
        _active = false;
    }

    /// <summary>對稱性便利：等同 <see cref="End"/>，冪等。</summary>
    public void Dispose()
    {
        End();
    }

    /// <summary>
    /// 滑鼠 LL hook 觀察左/右鍵按下後呼叫 dismiss（每次啟用至多一次）。
    /// </summary>
    private static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 絕對不變量：本方法每一個分支都以 CallNextHookEx 的回傳值結束，
        // 絕不回傳非零（回傳非零會吞掉全系統的滑鼠輸入）。
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == Win32.WM_LBUTTONDOWN || msg == Win32.WM_RBUTTONDOWN)
            {
                InvokeDismissOnce();
            }
        }

        // 一律轉送 hook 鏈：連觸發 dismiss 的這次按下也照常放行。
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 鍵盤 LL hook 觀察 Esc 鍵按下後呼叫 dismiss（每次啟用至多一次）。
    /// </summary>
    private static IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 絕對不變量：本方法每一個分支都以 CallNextHookEx 的回傳值結束，
        // 絕不回傳非零（回傳非零會吞掉全系統的鍵盤輸入）。
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == Win32.VK_ESCAPE)
            {
                InvokeDismissOnce();
            }
        }

        // 一律轉送 hook 鏈：Esc 事件照常放行。
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 直接在（安裝 hook 的）UI 執行緒上呼叫 dismiss；防護旗標確保每次啟用至多觸發一次。
    /// </summary>
    private static void InvokeDismissOnce()
    {
        if (_dismissed)
        {
            return;
        }

        _dismissed = true;
        var dismiss = _dismiss;
        if (dismiss == null)
        {
            return;
        }

        try
        {
            // LL hook 回呼送達安裝執行緒的訊息迴圈；呼叫端即 UI 執行緒，直接呼叫即可。
            dismiss();
        }
        catch
        {
            // 例外不可逃出 hook proc：可能造成全系統輸入死鎖或程序崩潰。
            // （dismiss 內部的失敗由呼叫端自行處理，此處僅保護 hook 鏈。）
        }
    }
}