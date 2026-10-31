using System.Runtime.InteropServices;

namespace AITranslator.Native;

/// <summary>
/// P/Invoke 集中宣告處：本專案所有對 user32.dll / kernel32.dll 的原生呼叫皆定義於此，
/// 避免各服務各自散落 DllImport。目前涵蓋剪貼簿序號查詢、剪貼簿原始格式備份/還原
/// （OpenClipboard / EnumClipboardFormats / GlobalLock 等）、SendInput 模擬按鍵、
/// 全域快速鍵註冊（RegisterHotKey / UnregisterHotKey / WM_HOTKEY），
/// 低層級滑鼠/鍵盤掛鉤（SetWindowsHookExW / UnhookWindowsHookEx / CallNextHookEx），
/// 以及用於 TTS 串流播放的 waveOut 音訊輸出（waveOutOpen / waveOutWrite / waveOutReset / waveOutClose）。
/// </summary>
internal static class Win32
{
    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_MENU = 0x12; // Alt
    internal const ushort VK_C = 0x43;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint INPUT_KEYBOARD = 1;

    // ---- 剪貼簿原始格式備份/還原（CaptureService）----

    // GlobalAlloc 的可移動配置旗標；SetClipboardData 接受 GMEM_MOVEABLE 句柄。
    internal const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    // ---- 低層級掛鉤（SetWindowsHookExW，供 DismissHook「旁觀」滑鼠/Esc 關閉彈窗）----

    internal const int WH_MOUSE_LL = 14;
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const ushort VK_ESCAPE = 0x1B;

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(
        int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookExW(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    // ---- 全域快速鍵（RegisterHotKey）----

    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x1;
    internal const uint MOD_CONTROL = 0x2;
    internal const uint MOD_SHIFT = 0x4;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>查詢實體（非僅邏輯）按鍵狀態；高字元 0x8000 = 當下壓著。</summary>
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(ushort vKey);

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    // ---- waveOut 音訊輸出（WaveOutSink）----

    /// <summary>波形格式：IEEE float32。</summary>
    internal const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    /// <summary>waveOutOpen 的裝置 ID：請系統自行挑選預設輸出裝置。</summary>
    internal const uint WAVE_MAPPER = 0xFFFFFFFF;
    /// <summary>waveOutOpen 的 fdwOpen：以函式回呼通報事件。</summary>
    internal const uint CALLBACK_FUNCTION = 0x30000;

    /// <summary>已送回應用程式的緩衝（播畢或被 Reset 收回），內容可複用。</summary>
    internal const uint WHDR_DONE = 0x0001;
    /// <summary>waveOutPrepareHeader 已備妥。</summary>
    internal const uint WHDR_PREPARED = 0x0002;
    /// <summary>緩衝仍在裝置佇列（不可釋放/複用）。</summary>
    internal const uint WHDR_INQUEUE = 0x0010;

    internal const uint WOM_OPEN = 0x3BB;
    internal const uint WOM_CLOSE = 0x3BC;
    /// <summary>一個緩衝播畢（或被 Reset 收回）並已送回應用程式。</summary>
    internal const uint WOM_DONE = 0x3BD;

    /// <summary>
    /// waveOut 回呼：由 winmm 自己的執行緒呼叫。<paramref name="dwParam1"/> 指向送回的 WAVEHDR。
    /// 實作端絕不可拋出例外，也不可在此呼叫 waveOut* 佇列類函式（winmm 的限制）。
    /// </summary>
    internal delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    /// <summary>
    /// 波形緩衝標頭（非託管記憶體中維護）：lpData 指向樣本記憶體、dwBufferLength 為位元組數。
    /// 已 waveOutWrite 的標頭與其樣本記憶體在收到 WOM_DONE 前不可釋放或複用。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    internal static extern uint waveOutOpen(
        out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, WaveOutProc dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    internal static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    internal static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    internal static extern uint waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    internal static extern uint waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    internal static extern uint waveOutClose(IntPtr hwo);
}