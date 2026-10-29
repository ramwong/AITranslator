using System.Runtime.InteropServices;

namespace AITranslator.Native;

/// <summary>
/// P/Invoke 集中宣告處：本專案所有對 user32.dll 的原生呼叫皆定義於此，
/// 避免各服務各自散落 DllImport。目前涵蓋剪貼簿序號查詢與 SendInput 模擬按鍵。
/// </summary>
internal static class Win32
{
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_C = 0x43;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint INPUT_KEYBOARD = 1;

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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
}