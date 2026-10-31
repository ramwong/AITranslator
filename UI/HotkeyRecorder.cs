using System.Text;
using System.Windows.Forms;

namespace AITranslator.UI;

/// <summary>
/// 快速鍵組合的純邏輯拆解（<see cref="HotkeyCaptureBox"/> 與 <see cref="Services.HotkeyParser"/>
/// 之間的正規化層）：把 WinForms 傳來的 <see cref="Keys"/> 組合轉為
/// <c>Ctrl+Alt+E</c> 形式的字串，供 <see cref="Services.HotkeyParser.Parse"/> 註冊
/// Win32 熱鍵。
/// </summary>
/// <remarks>
/// <para><b>修飾鍵規則</b>：修飾鍵 token 依 <c>Ctrl</c>、<c>Alt</c>、<c>Shift</c> 固定順序
/// 輸出（不論實際按壓順序）；至少需一個修飾鍵——無修飾鍵的單鍵（如單按 <c>E</c>）
/// 會覆蓋掉全域系統熱鍵且語意過寬，回 <see langword="null"/> 交由呼叫端靜默忽略。</para>
///
/// <para><b>主鍵規則</b>：主鍵取 <c>keys &amp; <see cref="Keys.KeyCode"/></c>，且必須落在
/// <see cref="Services.HotkeyParser.Parse"/> 接受的白名單——單一英文字母（A–Z，輸出大寫）
/// 或 <c>F1</c>–<c>F12</c>；其餘一律回 <see langword="null"/>。spec 提及的方向鍵／空白鍵
/// 等「可命名鍵」刻意<b>不</b>納入：Parser 不接受（僅字母與 F1-F12），輸出將違反
/// 與 Parse 互逆的約定。</para>
///
/// <para><b>純修飾鍵判定</b>：主鍵若是修飾鍵的「鍵身」（<see cref="Keys.ControlKey"/>/
/// <see cref="Keys.ShiftKey"/>/<see cref="Keys.LWin"/> <see cref="Keys.RWin"/> 等——
/// 這些是 VK 碼本身，不是上方 <see cref="Keys.Control"/> <see cref="Keys.Alt"/>
/// <see cref="Keys.Shift"/> 修飾位元）視為純修飾鍵組合，回 <see langword="null"/>
/// ——避免錄到 <c>Ctrl</c> 單按這類半成品。空組合亦回 <see langword="null"/>。</para>
/// </remarks>
internal static class HotkeyRecorder
{
    /// <summary>
    /// 將 <paramref name="keys"/> 格式化為 <c>Ctrl+Alt+E</c> 形式的正規化字串；
    /// 純修飾鍵、無鍵或不在白名單內的組合回 <see langword="null"/>。
    /// 輸出保證以 <see cref="Services.HotkeyParser.Parse"/> 解析成功（互逆子集）。
    /// </summary>
    public static string? FormatKeys(Keys keys)
    {
        var ctrl = keys.HasFlag(Keys.Control);
        var alt = keys.HasFlag(Keys.Alt);
        var shift = keys.HasFlag(Keys.Shift);
        if (!ctrl && !alt && !shift)
        {
            return null; // 需至少一個修飾鍵（Parser 的合約）
        }

        var main = keys & Keys.KeyCode;
        if (main == 0 || IsModifierBody(main))
        {
            return null; // 純修飾鍵（Ctrl/Shift 單按、Win 鍵等）不錄
        }

        // 主鍵先過白名單再組字串：白名單未過一律回 null，不輸出 "Ctrl+" 這類半成品。
        string mainToken;
        if (main >= Keys.A && main <= Keys.Z)
        {
            mainToken = ((char)('A' + (main - Keys.A))).ToString(); // 單一英文字母（大寫）
        }
        else if (main >= Keys.F1 && main <= Keys.F12)
        {
            mainToken = $"F{(int)main - (int)Keys.F1 + 1}"; // F1–F12
        }
        else
        {
            return null; // 非 A–Z／F1–F12（Space、數字、方向鍵等）：Parser 不接受，靜默忽略
        }

        // 修飾鍵 token 依 Ctrl、Alt、Shift 固定順序輸出。
        var text = new StringBuilder();
        if (ctrl)
        {
            text.Append("Ctrl+");
        }

        if (alt)
        {
            text.Append("Alt+");
        }

        if (shift)
        {
            text.Append("Shift+");
        }

        text.Append(mainToken);
        return text.ToString();
    }

    /// <summary>判斷主鍵是否為修飾鍵的鍵身（此為主鍵時即為純修飾鍵組合）。</summary>
    /// <remarks>
    /// <see cref="Keys.Menu"/>（Alt 鍵身）及其左右變體（LMenu/RMenu）、左右 Shift/Ctrl、
    /// 以及左右 Win 鍵皆納入；這些鍵在 <see cref="Keys.Control"/>/<see cref="Keys.Alt"/>/
    /// <see cref="Keys.Shift"/> 修飾位元之外，以 VK 碼落在低位元的方式呈現。
    /// </remarks>
    private static bool IsModifierBody(Keys main)
        => main is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
            or Keys.LWin or Keys.RWin
            or Keys.RShiftKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu;
}