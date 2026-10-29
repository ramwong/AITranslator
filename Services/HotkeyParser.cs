namespace AITranslator.Services;

/// <summary>
/// 將「Ctrl+Alt+E」形式的快速鍵字串拆解為 Win32 RegisterHotKey 用的
/// 修飾鍵與虛擬按鍵碼。分隔符僅接受 '+'；字串需含至少一個修飾鍵
/// （Ctrl/Alt/Shift，順序與大小寫不拘），最後一個 token 為單一英文字母
/// 或 F1-F12。任何不合規的字串皆回傳 success=false。
/// </summary>
public static class HotkeyParser
{
    private const uint ModAlt = 0x1;      // MOD_ALT
    private const uint ModControl = 0x2;  // MOD_CONTROL
    private const uint ModShift = 0x4;    // MOD_SHIFT

    public static (bool success, uint mods, uint key) Parse(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return (false, 0, 0);
        }

        var tokens = hotkey.Split('+');
        if (tokens.Length < 2)
        {
            return (false, 0, 0);
        }

        uint mods = 0;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].Trim().ToUpperInvariant())
            {
                case "CTRL":
                    mods |= ModControl;
                    break;
                case "ALT":
                    mods |= ModAlt;
                    break;
                case "SHIFT":
                    mods |= ModShift;
                    break;
                default:
                    return (false, 0, 0);
            }
        }

        if (mods == 0)
        {
            return (false, 0, 0);
        }

        var keyToken = tokens[^1].Trim();
        var key = ParseKey(keyToken);
        return key.HasValue ? (true, mods, key.Value) : (false, 0, 0);
    }

    private static uint? ParseKey(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            return c >= 'A' && c <= 'Z' ? c : null;
        }

        if (token.Length == 2
            && (token[0] == 'F' || token[0] == 'f')
            && char.IsDigit(token[1]))
        {
            var n = token[1] - '0';
            return n >= 1 && n <= 9 ? 0x70u + (uint)n - 1 : null;
        }

        if (token.Length == 3
            && (token[0] == 'F' || token[0] == 'f')
            && char.IsDigit(token[1])
            && char.IsDigit(token[2]))
        {
            var n = (token[1] - '0') * 10 + (token[2] - '0');
            return n >= 10 && n <= 12 ? 0x70u + (uint)n - 1 : null;
        }

        return null;
    }
}