using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AITranslator.UI;

/// <summary>
/// 快速鍵「點擊錄製」控制項（spec：docs/superpowers/specs/2026-10-31-ui-redesign-design.md
/// 「設定表單」節）：靜止態顯示現值（右側小字註記「點擊更改」）；點擊進入錄製態——
/// 虛線框、以 <see cref="Theme.Accent"/> 色顯示偵測到的組合鍵；按下實際組合鍵
/// （至少一修飾鍵 + 單一英文字母或 F1-F12）即錄入並引發 <see cref="ValueChanged"/>；
/// <see langword="Esc"/> 取消錄製、靜止態還原為現值顯示。
/// </summary>
/// <remarks>
/// <para><b>錄製規則</b>：組合鍵由 <see cref="HotkeyRecorder.FormatKeys"/> 拆解——
/// 純修飾鍵（Ctrl、Shift 單按、Win 鍵等）與不含修飾鍵的單鍵<b>靜默忽略</b>
/// （不會被錄成半成品，顯示維持錄製提示），白名單外的組合（方向鍵、空白、數字等
/// <see cref="Services.HotkeyParser"/> 不接受者）同樣靜默忽略；錄製態仍以修飾鍵
/// 即時提示（如「Ctrl + …」）回饋。錄入字串以 <see cref="Value"/> 存放、
/// <see cref="Services.HotkeyParser.Parse"/> 必定解析成功（見 <see cref="HotkeyRecorder"/>）。</para>
///
/// <para><b>重複擋下</b>：錄入時呼叫 <see cref="DuplicateValidator"/>，回
/// <see langword="true"/> 即切換為錯誤態（<see cref="Theme.Error"/> 色邊框與欄內
/// inline 錯誤行，文案 <see cref="DuplicateErrorText"/>），保持原 <see cref="Value"/>
/// 並<b>不</b>引發 <see cref="ValueChanged"/>；錯誤態持續到下一次進入錄製、還原或
/// 外部重設 <see cref="Value"/>。</para>
///
/// <para><b>Tab／方向鍵的取捨（documented choice）</b>：錄製態經
/// <see cref="OnPreviewKeyDown"/> 把 <see cref="Keys.Tab"/> 與方向鍵標記為輸入鍵，
/// 抑制 focus navigation 在錄製期間移走焦點；這些鍵本身不可錄製，
/// <see cref="OnKeyDown"/> 採取<b>忽略</b>（而非取消）——錄製態保持、顯示不變。
/// 選擇忽略而非「Tab 取消錄製」：取消後焦點仍在本控制項（Tab 已被抑制），再按 Tab
/// 會陷入「進入↔取消」循環，焦點永遠走不出去。離開錄製只有三條正規路徑：錄入成功、
/// <see langword="Esc"/> 取消、點擊本控制項以外處（見 <see cref="OnLostFocus"/>）——
/// 狀態機沒有卡住的分支。</para>
///
/// <para><b>自繪</b>：繼承 <see cref="Control"/> 自繪（UserPaint + DoubleBuffer），
/// 配色全部取自 <see cref="Theme"/>（Paper 底、Hairline 靜止邊框、Ink 現值文字、
/// Caption 提示小字、Accent 錄製態、Error 錯誤態），不硬編色值；字體統一
/// <see cref="Theme.FontName"/>（字族不存在時退回系統預設字族，
/// <see langword="Dispose(bool)"/> 一律釋放）。高度依是否出現錯誤行自行重算
/// （<see cref="UpdateHeight"/>），供 TableLayoutPanel 依內容收放。</para>
/// </remarks>
public class HotkeyCaptureBox : Control
{
    // ---- 文案常數 ----
    private const string HintText = "點擊更改";                   // 靜止態右側 muted 小字
    private const string RecordingPromptText = "請按下新的快速鍵…"; // 錄製態未偵測到組合鍵時的提示
    private const string PlaceholderText = "（未設定）";           // Value 為空時的佔位文字
    private const string DuplicateErrorTextConst = "此快捷鍵已與其他欄位重複"; // 重複擋下的 inline 文案

    // ---- 佈局常數（spec 間距權「builder 微調」，非 token 項目）----
    private const int BoxPaddingX = 10; // 文字與左右框線的距離（px）
    private const int BoxPaddingY = 6;  // 文字列與上下框線的距離（px）
    private const int ErrorGap = 3;     // 現值列與錯誤行的距離（px）

    // ---- 字型（Dispose 一律釋放）----
    private readonly Font _fontToken; // 統一主題字體（字族不存在時退回系統預設字族）

    // ---- 狀態機：rest ⇄ recording（+ error 附加態，皆可還原 rest）----
    private string _value = string.Empty; // 現值（正規化字串，如「Ctrl+Alt+E」）
    private bool _recording;              // 錄製態（點擊進入；錄入/Esc/失焦結束，無卡住分支）
    private string? _liveText;            // 錄製態的即時顯示（偵測到的組合鍵或「Ctrl + …」修飾鍵提示）
    private string? _errorText;           // 非 null 即錯誤態（重複擋下），持續到下一次互動

    /// <summary>以預設佈局建立錄製控制項（配色／字體套 <see cref="Theme"/>）。</summary>
    public HotkeyCaptureBox()
    {
        _fontToken = TryCreateFont(Theme.FontName, 9f)
            ?? new Font(Control.DefaultFont.FontFamily, 9f);

        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);

        Font = _fontToken;
        Cursor = Cursors.Hand;
        TabStop = true;
        Height = ComputeHeight();
    }

    /// <summary>錄入時的重複驗證器：回 <see langword="true"/> 即 inline 擋下、保持原值。</summary>
    /// <remarks>
    /// 呼叫端（Task 6：設定表單）注入「與其他快速鍵欄位相比較」的邏輯——本控制項
    /// 不自帶欄位之間的知識；保持 <see langword="null"/> 表示不做重複驗證。
    /// </remarks>
    public Func<string, bool>? DuplicateValidator { get; set; }

    /// <summary>重複擋下的 inline 錯誤文案（以 <see cref="Theme.Error"/> 色呈現）。</summary>
    public string DuplicateErrorText => DuplicateErrorTextConst;

    /// <summary>現值（正規化字串，如「Ctrl+Alt+E」）；重設即清錯誤態並重繪靜止態顯示。</summary>
    /// <remarks>
    /// 錄製期間不會更動本屬性（半成品組合鍵已被 <see cref="HotkeyRecorder"/> 擋在
    /// <see langword="null"/>），因此取消／還原只是「改回顯示現值」，不需另存
    /// restore 備份。
    /// </remarks>
    public string Value
    {
        get => _value;
        set
        {
            _value = value ?? string.Empty;
            _errorText = null; // 外部重設現值代表問題（含錯誤態所指的衝突）已解
            UpdateHeight();
            Invalidate();
        }
    }

    /// <summary>錄入成功（通過重複驗證）時引發，帶正規化後的新值（與 <see cref="Value"/> 同）。</summary>
    public event EventHandler<string>? ValueChanged;

    /// <summary>錄製態下把 Tab／方向鍵標記為輸入鍵，抑制 focus navigation 移走焦點
    /// （鍵本身不可錄製、於 <see cref="OnKeyDown"/> 忽略；見類別說明的取捨）。</summary>
    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_recording && IsFocusNavigationKey(e.KeyCode))
        {
            e.IsInputKey = true;
        }
    }

    /// <summary>錄製態的按鍵處理：<see langword="Esc"/> 取消還原；可組合鍵錄入；
    /// 其他（含純修飾鍵、Tab、方向鍵）靜默忽略、不離開錄製態。靜止態不攔鍵。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_recording)
        {
            return; // 靜止態不攔鍵（Tab/方向鍵走 focus navigation）
        }

        if (e.KeyCode == Keys.Escape)
        {
            CancelRecording();
            e.Handled = true;
            return;
        }

        var formatted = HotkeyRecorder.FormatKeys(e.KeyData);
        if (formatted != null)
        {
            CompleteRecording(formatted);
            e.Handled = true;
            return;
        }

        // 純修飾鍵與白名單外組合：靜默忽略（不錄半成品），但顯示修飾鍵即時提示回饋
        // （無修飾鍵就維持錄製提示文字，不顯示空字串）。
        var hint = FormatModifierHint(e.Modifiers);
        _liveText = hint.Length == 0 ? null : hint;
        e.Handled = true; // 錄製期間吃掉全部按鍵，避免觸發表單層預設行為（如 Enter）
        Invalidate();
    }

    /// <summary>點擊進入錄製態（同時取得焦點；已在錄製態僅重新聚焦）。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled)
        {
            return;
        }

        Focus();
        if (_recording)
        {
            return;
        }

        _recording = true;
        _liveText = null;
        _errorText = null;
        UpdateHeight();
        Invalidate();
    }

    /// <summary>錄製態中焦點被移走（點擊控制項以外處）：還原為現值顯示（不引入新錯誤態）。</summary>
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        if (_recording)
        {
            CancelRecording();
        }
    }

    /// <summary>自繪：Paper 底、依狀態的邊框（Hairline 靜止實線／Accent 錄製虛線／
    /// Error 錯誤實線）、現值或錄製即時顯示、提示小字與 inline 錯誤行。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.None;

        using (var background = new SolidBrush(Theme.Paper))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        DrawBorder(graphics);
        DrawValueRow(graphics);
        if (_errorText != null)
        {
            DrawErrorRow(graphics);
        }
    }

    /// <summary>釋放自管的主題字型。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fontToken.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---- 錄製狀態機 --------------------------------------------------------

    /// <summary>結束錄製並還原靜止態顯示（現值）。</summary>
    /// <remarks>
    /// 錄製期間 <see cref="Value"/> 未被改動過，取消／失焦不需另存備份即為還原。
    /// </remarks>
    private void CancelRecording()
    {
        _recording = false;
        _liveText = null;
        _errorText = null;
        UpdateHeight();
        Invalidate();
    }

    /// <summary>錄入完成：通過重複驗證則更新現值並引發 <see cref="ValueChanged"/>；
    /// 重複則切換錯誤態（inline 錯誤行 + Error 邊框）、保持原值、不引發事件。</summary>
    private void CompleteRecording(string formatted)
    {
        _recording = false;
        _liveText = null;

        if (DuplicateValidator is { } validator && validator(formatted))
        {
            // 重複：當場 inline 擋下（spec：提示並保持原值，不等按「儲存」才發現）。
            _errorText = DuplicateErrorTextConst;
            UpdateHeight();
            Invalidate();
            return;
        }

        _errorText = null;
        _value = formatted;
        UpdateHeight();
        Invalidate();
        ValueChanged?.Invoke(this, formatted); // 同值錄入也照發（呼叫端自行判斷變更）
    }

    /// <summary>更新高度以容納現值列（與 inline 錯誤行，若顯示中）。</summary>
    private void UpdateHeight()
    {
        var height = ComputeHeight();
        if (Height != height)
        {
            Height = height;
        }
    }

    /// <summary>目前狀態下的整體高度：現值列 +（錯誤態時）inline 錯誤行。</summary>
    private int ComputeHeight()
    {
        var valueRow = Math.Max(MeasureLineHeight(_value.Length > 0 ? _value : PlaceholderText), 16);
        var height = BoxPaddingY * 2 + valueRow;
        if (_errorText != null)
        {
            height += ErrorGap + MeasureLineHeight(_errorText);
        }

        return height;
    }

    /// <summary>以主題字體量測單行文字的像素高度（無 padding）。</summary>
    private int MeasureLineHeight(string text)
        => TextRenderer.MeasureText(text, _fontToken, Size.Empty, TextFormatFlags.NoPadding).Height;

    // ---- 繪製 ---------------------------------------------------------------

    /// <summary>以狀態決定邊框樣式（錄製=Accent 虛線；錯誤=Error 實線；静止=Hairline 實線）。</summary>
    private void DrawBorder(Graphics graphics)
    {
        var recording = _recording;
        using var pen = new Pen(recording ? Theme.Accent : _errorText != null ? Theme.Error : Theme.Hairline);
        if (recording)
        {
            pen.DashStyle = DashStyle.Dash; // spec：錄製態為虛線框
        }

        graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>現值列：錄製=Accent 即時顯示；静止=Ink 現值（空值=Caption 佔位文字）。</summary>
    private void DrawValueRow(Graphics graphics)
    {
        if (_recording)
        {
            DrawText(graphics, _liveText ?? RecordingPromptText, Theme.Accent, BoxPaddingX, BoxPaddingY);
            return;
        }

        if (_value.Length == 0)
        {
            DrawText(graphics, PlaceholderText, Theme.Caption, BoxPaddingX, BoxPaddingY);
        }
        else
        {
            DrawText(graphics, _value, Theme.Ink, BoxPaddingX, BoxPaddingY);
        }

        // spec：靜止態以 muted 小字註記「點擊更改」（右側對齊、與現值同列）。
        var hintWidth = TextRenderer.MeasureText(
            HintText, _fontToken, Size.Empty, TextFormatFlags.NoPadding).Width;
        DrawText(graphics, HintText, Theme.Caption, Width - BoxPaddingX - hintWidth, BoxPaddingY);
    }

    /// <summary>inline 錯誤行（現值列下方、Error 色；同列可與現值列並存提示）。</summary>
    private void DrawErrorRow(Graphics graphics)
    {
        var valueRow = Math.Max(MeasureLineHeight(_value), 16);
        var y = BoxPaddingY + valueRow + ErrorGap;
        DrawText(graphics, _errorText!, Theme.Error, BoxPaddingX, y);
    }

    /// <summary>以主題字體、無 padding 繪製單行文字。</summary>
    private void DrawText(Graphics graphics, string text, Color color, int x, int y)
        => TextRenderer.DrawText(
            graphics, text, _fontToken, new Point(x, y), color,
            TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);

    // ---- helpers -----------------------------------------------------------

    /// <summary>錄製期間修飾鍵即時提示（如「Ctrl + …」）；無修飾鍵回空字串。</summary>
    /// <remarks>僅為顯示回饋；錄入判定一律走 <see cref="HotkeyRecorder.FormatKeys"/>。</remarks>
    private static string FormatModifierHint(Keys modifiers)
    {
        var hint = string.Empty;
        if (modifiers.HasFlag(Keys.Control))
        {
            hint += "Ctrl + ";
        }

        if (modifiers.HasFlag(Keys.Alt))
        {
            hint += "Alt + ";
        }

        if (modifiers.HasFlag(Keys.Shift))
        {
            hint += "Shift + ";
        }

        return hint.Length == 0 ? string.Empty : hint + "…"; // 無修飾鍵回空字串（呼叫端以長度判斷是否顯示）
    }

    /// <summary>判斷是否為離開本控制項（focus navigation）用鍵：Tab 與四個方向鍵。</summary>
    private static bool IsFocusNavigationKey(Keys code)
        => code is Keys.Tab or Keys.Up or Keys.Down or Keys.Left or Keys.Right;

    /// <summary>以主題字族建立字型；系統無該字族時回 null（呼叫端退回系統預設字族）。</summary>
    /// <remarks>模式沿用 <see cref="PopupForm.TryCreateFont"/>：<see cref="Font"/> 建構子
    /// 對不存在的字族不會拋例外而是退回預設字族，以 <see cref="Font.Name"/> 比對擋掉。</remarks>
    private static Font? TryCreateFont(string familyName, float size)
    {
        try
        {
            using var familyProbe = new Font(familyName, size);
            if (string.Equals(familyProbe.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return new Font(familyName, size);
            }
        }
        catch (ArgumentException)
        {
            // 字族不存在：Font 建構子會退回預設字族，以下 Name 比對已擋掉，僅防禦例外。
        }

        return null;
    }
}