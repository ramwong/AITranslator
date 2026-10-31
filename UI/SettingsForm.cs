using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AITranslator.Models;
using AITranslator.Services;

namespace AITranslator.UI;

/// <summary>
/// 設定儲存後的回呼：帶回「已驗證、已存檔」的最新設定，
/// 供呼叫端重新註冊熱鍵、套用彈窗尺寸與字級等。
/// </summary>
public delegate void SettingsChangedCallback(AppSettings newSettings);

/// <summary>
/// 設定對話框：顯示並編輯 <see cref="AppSettings"/> 全部欄位。
///
/// <para><b>流程</b>：建構時自 <paramref name="settingsPath"/> 載入現行設定填入欄位
/// （檔案不存在時顯示預設值）；「儲存」逐欄驗證（數值範圍、格式、快捷鍵、非空白），
/// 任一不合規即在問題欄位旁顯示 <see cref="Theme.Error"/> 色紅點標記、並於按鈕列
/// 上方顯示同一行錯誤說明，再把焦點（含捲動到可見範圍）移回問題欄位——不彈出
/// MessageBox、不存檔；全數通過後寫回設定檔、依勾選狀態同步開機自動啟動登錄值、
/// 引發 <paramref name="onChanged"/> 帶回更新後設定，最後關閉表單。存檔 IO 失敗
/// 同樣以按鈕列上方那行 inline 錯誤提示、不關表單（讓使用者保留編輯內容）。
/// 不按「儲存」直接結束，設定不會有任何變更。</para>
///
/// <para><b>版面</b>：小節標題列（<see cref="Theme.Caption"/> 色 11px 小標 + 髮絲
/// 分隔線，跨兩欄）與欄位列排成 <see cref="TableLayoutPanel"/> 兩欄標籤/控制項，
/// 排序依 spec D2（快捷鍵優先）：快捷鍵 → 彈窗外觀 → 翻譯 → 辨識 OCR → 其他。
/// 快捷鍵為「點擊錄製」的 <see cref="HotkeyCaptureBox"/>：錄到與其他快捷鍵重複的
/// 組合鍵當場 inline 擋下（<see cref="HotkeyCaptureBox.DuplicateValidator"/>），
/// 不等按「儲存」才發現。表單配色／字體套 <see cref="Theme"/>（Paper 底、Ink 字、
/// FooterBg 按鈕列、Accent 主按鈕），AutoSize；內容高於螢幕工作區時改為固定高度，
/// 捲動容器（只包內容、按鈕列 dock 在容器外的 form 上）啟用其 AutoScroll——按鈕列
/// 於 clamp 模式仍固定在視窗底部（「滑動到第一個錯誤」於此模式下生效）。
/// </summary>
public class SettingsForm : Form
{
    // ---- 驗證文案（沿用既有規則的訊息字面，格式不合的三組快捷鍵共用）----
    private const string HotkeyFormatErrorMessage =
        "快捷鍵格式不正確。需含 Ctrl、Alt 或 Shift 與單一英文字母（或 F1–F12），例如 Ctrl+Alt+E。";

    private readonly string _settingsPath;
    private readonly SettingsChangedCallback _onChanged;
    private readonly AppSettings _settings;

    private readonly TextBox _widthBox;
    private readonly TextBox _heightBox;
    private readonly TextBox _fontSizeBox;
    private readonly TextBox _templateBox;
    private readonly TextBox _modelBox;
    private readonly TextBox _urlBox;
    private readonly HotkeyCaptureBox _hotkeyBox;
    private readonly HotkeyCaptureBox _ttsHotkeyBox;
    private readonly TextBox _ocrModelBox;
    private readonly HotkeyCaptureBox _ocrHotkeyBox;
    private readonly CheckBox _ocrTranslateInVisionBox;
    private readonly TextBox _maxTextLengthBox;
    private readonly CheckBox _startWithWindowsBox;

    // ---- 捲動容器：只包內容表；按鈕列 dock 在其外的 form 上，clamp 模式仍固定視窗底 ----
    private readonly Panel _scrollHost = new();

    // ---- inline 驗證指示（spec：移除逐欄 MessageBox）----
    private readonly Label _errorLabel; // 按鈕列上方的一行錯誤說明（隱藏＝無錯誤）
    private readonly Dictionary<Control, Label> _errorMarkers = new(); // 欄位右側紅點標記（key＝欄位）

    // ---- 主題字體（Dispose 一律釋放）----
    private readonly Font _bodyFont;    // 表單主字體（全文統一字族）
    private readonly Font _sectionFont; // 小節標題字體（11px）

    /// <summary>以設定檔路徑與存檔回呼建立設定對話框，欄位即載入現行設定。</summary>
    public SettingsForm(string settingsPath, SettingsChangedCallback onChanged)
    {
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        // 檔案不存在或損壞時 Load 回傳預設值，畫面即顯示預設設定。
        _settings = SettingsService.Load(_settingsPath);

        _widthBox = CreateTextBox();
        _heightBox = CreateTextBox();
        _fontSizeBox = CreateTextBox();
        _templateBox = CreateTextBox();
        _modelBox = CreateTextBox();
        _urlBox = CreateTextBox();
        _ocrModelBox = CreateTextBox();
        _maxTextLengthBox = CreateTextBox();

        _templateBox.Multiline = true; // Prompt 範本允許多行（Enter 換行，不觸發 OK）
        _templateBox.AcceptsReturn = true;
        _templateBox.ScrollBars = ScrollBars.Vertical; // 內含 {text} 佔位符，貼長範本可捲動

        _hotkeyBox = new HotkeyCaptureBox();
        _ttsHotkeyBox = new HotkeyCaptureBox();
        _ocrHotkeyBox = new HotkeyCaptureBox();
        WireDuplicateValidators();

        _startWithWindowsBox = new CheckBox
        {
            AutoSize = true,
            Text = "開機自動啟動",
        };
        // 登錄 Run 值才是「開機是否啟動」的事實來源（使用者可能手動刪值），
        // 勾選狀態以現況為準；存檔時 SetEnabled 會把設定與登錄同步回一致。
        _startWithWindowsBox.Checked = AutoStartService.IsEnabled();

        _ocrTranslateInVisionBox = new CheckBox
        {
            AutoSize = true,
            Text = "辨識模型直接翻譯",
        };

        _bodyFont = TryCreateFont(Theme.FontName, 9f) ?? new Font(Control.DefaultFont.FontFamily, 9f);
        _sectionFont = TryCreateFont(Theme.FontName, 11f) ?? new Font(Control.DefaultFont.FontFamily, 11f);

        _errorLabel = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Error,
            Visible = false, // 隱藏＝無錯誤；顯示時置於按鈕列上方
            Margin = new Padding(0, 4, 0, 2),
            MaximumSize = new Size(460, 0), // 過長訊息折行，避免撐寬表單
        };

        PopulateFields();
        BuildLayout();

        Text = "設定";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = _bodyFont;
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
    }

    /// <summary>釋放自管的主題字體；子控制項與元件交由基底處理。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bodyFont.Dispose();
            _sectionFont.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>顯示後依表單所在螢幕的工作區高度收合：內容過高時固定高度，超出部分
    /// 交由只包內容的捲動容器捲動（按鈕列 dock 在 form 上，始終固定在視窗底部）。</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        var workingArea = Screen.FromControl(this).WorkingArea;
        if (Height <= workingArea.Height)
        {
            return;
        }

        // 只解除表單 AutoSize 才能固定高度；AutoScroll 開在 _scrollHost（此處才切換），
        // 不開在表單上——表單一旦 AutoScroll，DisplayRectangle 會把 docked children 納進
        // 捲動文件，docked-bottom 按鈕列就被釘在捲動文件底而非視口底。
        // _scrollHost 的 AutoScroll 開啟會令其自我尺寸失效（AutoScroll 容器不再隨內容
        // 自適應），且 AutoScroll 容器不參與表單 AutoSize 偏好尺寸計算——故僅 clamp 時才開
        // （未 clamp 不需捲動；表單仍 AutoSize，按 Dock=Fill + AutoSize 自然收放）。
        _scrollHost.AutoSize = false;
        _scrollHost.AutoScroll = true;
        AutoSize = false;
        Height = workingArea.Height;
    }

    /// <summary>套用欄位驗證後存檔、同步自動啟動、觸發回呼並關閉；驗證失敗不離開表單。</summary>
    private void OnSaveButtonClick(object? sender, EventArgs e)
    {
        ClearValidationErrors();

        if (!ValidateIntField(_widthBox, "彈窗寬度", min: 200, max: 2000, out var popupWidth)
            || !ValidateIntField(_heightBox, "彈窗高度", min: 150, max: 1500, out var popupHeight)
            || !ValidateFloatField(_fontSizeBox, "彈窗字型大小", min: 8f, max: 48f, out var fontSize)
            || !ValidateIntField(_maxTextLengthBox, "選取文字上限字數", min: 100, max: 100000, out var maxTextLength))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_modelBox.Text))
        {
            ShowValidationError(_modelBox, "Ollama 模型名稱不可為空白。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_urlBox.Text))
        {
            ShowValidationError(_urlBox, "Ollama API 網址不可為空白。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_templateBox.Text))
        {
            ShowValidationError(_templateBox, "Prompt 翻譯範本不可為空白。");
            return;
        }

        // 快捷鍵格式驗證（需至少一個修飾鍵 + 單一英文字母或 F1-F12）。
        // 錄製盒靜止值一律為 HotkeyRecorder 正規化字串（或載入時的設定值），
        // 存檔時仍以防禦姿態複驗，不假設錄製流程零疏漏。
        var hotkey = _hotkeyBox.Value.Trim();
        if (!HotkeyParser.Parse(hotkey).success)
        {
            ShowValidationError(_hotkeyBox, HotkeyFormatErrorMessage);
            return;
        }

        // TTS 快捷鍵沿用主快捷鍵的格式規則與訊息文案。
        var ttsHotkey = _ttsHotkeyBox.Value.Trim();
        if (!HotkeyParser.Parse(ttsHotkey).success)
        {
            ShowValidationError(_ttsHotkeyBox, HotkeyFormatErrorMessage);
            return;
        }

        // 兩組熱鍵不可相同（Trim 後不分大小寫比較）。
        if (string.Equals(hotkey, ttsHotkey, StringComparison.OrdinalIgnoreCase))
        {
            ShowValidationError(_ttsHotkeyBox, "TTS 快捷鍵不可與翻譯快捷鍵相同。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_ocrModelBox.Text))
        {
            ShowValidationError(_ocrModelBox, "OCR 模型名稱不可為空白。");
            return;
        }

        // OCR 快捷鍵沿用主快捷鍵的格式規則與訊息文案。
        var ocrHotkey = _ocrHotkeyBox.Value.Trim();
        if (!HotkeyParser.Parse(ocrHotkey).success)
        {
            ShowValidationError(_ocrHotkeyBox, HotkeyFormatErrorMessage);
            return;
        }

        // OCR 快捷鍵不可與翻譯、TTS 快捷鍵相同（Trim 後不分大小寫比較）。
        if (string.Equals(ocrHotkey, hotkey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ocrHotkey, ttsHotkey, StringComparison.OrdinalIgnoreCase))
        {
            ShowValidationError(_ocrHotkeyBox, "OCR 快捷鍵不可與其他快捷鍵相同。");
            return;
        }

        // 全部通過才改寫設定實例。
        _settings.PopupWidth = popupWidth;
        _settings.PopupHeight = popupHeight;
        _settings.FontSize = fontSize;
        _settings.MaxTextLength = maxTextLength;
        _settings.Model = _modelBox.Text.Trim();
        _settings.OllamaUrl = _urlBox.Text.Trim();
        _settings.PromptTemplate = _templateBox.Text.Trim();
        _settings.Hotkey = hotkey;
        _settings.TtsHotkey = ttsHotkey;
        _settings.OcrModel = _ocrModelBox.Text.Trim();
        _settings.OcrHotkey = ocrHotkey;
        _settings.OcrTranslateInVision = _ocrTranslateInVisionBox.Checked;
        _settings.StartWithWindows = _startWithWindowsBox.Checked;

        try
        {
            SettingsService.Save(_settingsPath, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                 or System.Security.SecurityException or ArgumentException)
        {
            // 存檔失敗不關表單，讓使用者保留編輯內容自行修正或取消；以按鈕列上方
            // 那行 inline 錯誤提示（不再彈 MessageBox）。中和儲存鈕的
            // DialogResult.OK，避免 ShowDialog 直接收下 OK 關窗。
            DialogResult = DialogResult.None;
            ShowErrorLine($"設定檔儲存失敗：{ex.Message}");
            return;
        }

        // 自動啟動登錄值為 best-effort：失敗由 AutoStartService 靜默處理，不中斷存檔。
        AutoStartService.SetEnabled(_settings.StartWithWindows);

        _onChanged.Invoke(_settings);
        DialogResult = DialogResult.OK; // 明確宣告成功（驗證失敗路徑已將其歸 None）
        Close();
    }

    /// <summary>整數欄位驗證：可解析且落在 [min, max]。</summary>
    private bool ValidateIntField(TextBox box, string name, int min, int max, out int value)
    {
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || value < min || value > max)
        {
            ShowValidationError(box, $"{name}需為 {min} 至 {max} 之間的整數。");
            return false;
        }

        return true;
    }

    /// <summary>浮點數欄位驗證：可解析為有限數（排除 NaN/Infinity）且落在 [min, max]。</summary>
    private bool ValidateFloatField(TextBox box, string name, float min, float max, out float value)
    {
        // NumberStyles.Float 可解析 "NaN"/"Infinity"；而 NaN 落在 min/max 比較的兩側皆為
        // false，會讓下面第二個條件不成立——須以 float.IsFinite 明確擋下，否則
        // NaN 會被存進字級並讓 UI 建構 Font 時炸掉（整個 app 變磚）。
        if (!float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || !float.IsFinite(value))
        {
            ShowValidationError(box, $"{name}必須是有效數字。");
            return false;
        }

        if (value < min || value > max)
        {
            ShowValidationError(box, $"{name}需為 {min} 至 {max} 之間的數值。");
            return false;
        }

        return true;
    }

    /// <summary>inline 驗證錯誤：問題欄位右側紅點標記 + 按鈕列上方同一行錯誤說明；
    /// 焦點（含捲動到可見範圍）移回問題欄位，不存檔、不關表單。</summary>
    /// <remarks>
    /// <see cref="Form.DialogResult"/> 中和為 <see cref="DialogResult.None"/>：ShowDialog
    /// 下不取消就會驗證一失敗即關窗、連帶丟掉使用者尚未通過驗證的編輯內容。
    /// </remarks>
    private void ShowValidationError(Control field, string message)
    {
        DialogResult = DialogResult.None;
        _errorMarkers[field].Visible = true;
        ShowErrorLine(message);
        _scrollHost.ScrollControlIntoView(field); // 未超出視口（無捲動）時為無作用；clamp 模式即「滑動到第一個錯誤」
        field.Focus();
        if (field is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    /// <summary>只在按鈕列上方那行錯誤 Label 顯示訊息（inline 錯誤共用出口）。</summary>
    private void ShowErrorLine(string message)
    {
        _errorLabel.Text = message;
        _errorLabel.Visible = true;
    }

    /// <summary>清掉上一輪驗證的紅點標記與錯誤行（每次按下「儲存」重新計）。</summary>
    private void ClearValidationErrors()
    {
        foreach (var marker in _errorMarkers.Values)
        {
            marker.Visible = false;
        }

        _errorLabel.Visible = false;
    }

    /// <summary>三組錄製盒的重複攔截：錄入值與其他兩組現值相同（不分大小寫）即當場 inline 擋下。</summary>
    private void WireDuplicateValidators()
    {
        _hotkeyBox.DuplicateValidator = value => MatchesAnyOtherHotkey(value, _ttsHotkeyBox, _ocrHotkeyBox);
        _ttsHotkeyBox.DuplicateValidator = value => MatchesAnyOtherHotkey(value, _hotkeyBox, _ocrHotkeyBox);
        _ocrHotkeyBox.DuplicateValidator = value => MatchesAnyOtherHotkey(value, _hotkeyBox, _ttsHotkeyBox);
    }

    /// <summary>判斷快速鍵值是否與任一其他錄製盒現值相同（不分大小寫）。</summary>
    private static bool MatchesAnyOtherHotkey(string value, params HotkeyCaptureBox[] others)
        => Array.Exists(others, other => string.Equals(other.Value, value, StringComparison.OrdinalIgnoreCase));

    private void PopulateFields()
    {
        _widthBox.Text = _settings.PopupWidth.ToString(CultureInfo.InvariantCulture);
        _heightBox.Text = _settings.PopupHeight.ToString(CultureInfo.InvariantCulture);
        _fontSizeBox.Text = _settings.FontSize.ToString(CultureInfo.InvariantCulture);
        _templateBox.Text = _settings.PromptTemplate; // 保留 {text} 佔位符原樣
        _modelBox.Text = _settings.Model;
        _urlBox.Text = _settings.OllamaUrl;
        _hotkeyBox.Value = _settings.Hotkey;          // 顯示如「Ctrl+Alt+E」
        _ttsHotkeyBox.Value = _settings.TtsHotkey;    // 顯示如「Ctrl+Alt+S」
        _ocrModelBox.Text = _settings.OcrModel;
        _ocrHotkeyBox.Value = _settings.OcrHotkey;    // 顯示如「Ctrl+Alt+I」
        _ocrTranslateInVisionBox.Checked = _settings.OcrTranslateInVision;
        _maxTextLengthBox.Text = _settings.MaxTextLength.ToString(CultureInfo.InvariantCulture);
    }

    private void BuildLayout()
    {
        // 內容表（AutoSized）自身不 dock：在捲動容器內以偏好尺寸呈現，
        // clamp 模式超出視口的部分即在容器捲動文件上捲動呈現。
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            ColumnCount = 2,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // 標籤欄依最長標籤自適應
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260)); // 控制項欄固定寬

        var row = 0;
        row = AddSectionRow(table, row, "快捷鍵");
        row = AddHotkeyRow(table, row, "翻譯快捷鍵", _hotkeyBox);
        row = AddHotkeyRow(table, row, "朗讀 (TTS) 快捷鍵", _ttsHotkeyBox);
        row = AddHotkeyRow(table, row, "辨識 (OCR) 快捷鍵", _ocrHotkeyBox);

        row = AddSectionRow(table, row, "彈窗外觀");
        row = AddAutoFieldRow(table, row, "彈窗寬度", _widthBox);
        row = AddAutoFieldRow(table, row, "彈窗高度", _heightBox);
        row = AddAutoFieldRow(table, row, "彈窗字型大小", _fontSizeBox);

        row = AddSectionRow(table, row, "翻譯");
        // Prompt 範本：絕對高度列 + Dock=Fill，讓多行 TextBox 約 78px。
        row = AddFixedFieldRow(table, row, "Prompt 翻譯範本", _templateBox, rowHeight: 78).nextRow;
        row = AddAutoFieldRow(table, row, "Ollama 模型名稱", _modelBox);
        row = AddAutoFieldRow(table, row, "Ollama API 網址", _urlBox);
        row = AddAutoFieldRow(table, row, "選取文字上限字數", _maxTextLengthBox);

        row = AddSectionRow(table, row, "辨識 OCR");
        row = AddAutoFieldRow(table, row, "辨識 (OCR) 模型名稱", _ocrModelBox);
        row = AddCheckRow(table, row, _ocrTranslateInVisionBox);

        row = AddSectionRow(table, row, "其他");
        row = AddCheckRow(table, row, _startWithWindowsBox);

        // 錯誤說明行：按鈕列上方一行（儲存鈕已在表單底部的 FooterBg 按鈕列）。
        var errorRow = row++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(_errorLabel, 0, errorRow);
        table.SetColumnSpan(_errorLabel, 2);

        // 捲動容器只包內容；按鈕列 dock 在 form 外，clamp 時仍固定在視窗底部。
        // _scrollHost 常態 Dock=Fill + AutoSize（偏好尺寸隨內容表參與表單 AutoSize 計算：
        // 表單偏好高＝內容高＋按鈕列高，二者貼合不重疊）；AutoScroll 僅 OnShown clamp
        // 時開啟（AutoScroll 容器不再自我尺寸、也不參與偏好計算，常態開啟會令表單塌縮），
        // clamp 後按鈕列（加入次序在其後＝docking 最先套用）先佔 Bottom，其餘為捲動視口。
        _scrollHost.AutoScroll = false; // 僅 clamp 模式捲動；常態開啟會破壞 AutoSize 偏好計算
        _scrollHost.Dock = DockStyle.Fill;
        _scrollHost.AutoSize = true;
        _scrollHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _scrollHost.Margin = new Padding(0);
        _scrollHost.Padding = new Padding(0);
        _scrollHost.Controls.Add(table);
        var bar = BuildButtonBar(); // 同步設定 AcceptButton/CancelButton
        Controls.Add(_scrollHost);
        Controls.Add(bar); // 加於 _scrollHost 之後：docking 次序最先（bar 先佔 Bottom）且最上層繪製
    }

    /// <summary>加入小節標題列（跨兩欄）：Caption 色小標 + 貼列底髮絲分隔線。</summary>
    private int AddSectionRow(TableLayoutPanel table, int row, string title)
    {
        var caption = new Label
        {
            Text = title,
            Dock = DockStyle.Fill, // 佔髮絲線以上整塊（Margin/Padding 決定上下留白）
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            ForeColor = Theme.Caption,
            Font = _sectionFont, // spec：小節標題 11px
            Margin = new Padding(0),
        };
        var section = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0, 10, 0, 5), // 標題上方留白與髮絲線下方呼吸空間
        };
        // 子控制項加入順序（尾端先 dock，沿用 PopupForm 同註解慣例）：
        // 小標先加入佔 Fill，髮絲線後加入貼底。
        section.Controls.Add(caption);
        section.Controls.Add(CreateHairlinePanel(DockStyle.Bottom));

        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        table.Controls.Add(section, 0, row);
        table.SetColumnSpan(section, 2);
        return row + 1;
    }

    /// <summary>加入「點擊錄製」快速鍵一列；列高隨錄製盒（含 inline 錯誤行）伸縮同步。</summary>
    private int AddHotkeyRow(TableLayoutPanel table, int row, string labelText, HotkeyCaptureBox box)
    {
        // 錄製盒預設 Margin(3,3,3,3) 會讓 Dock 高度與列高差 6px：盒高→列高→盒高
        // 逐次排版縮 6px、遞迴塌縮到 1px——歸零 Margin，讓 Dock 高度＝列高。
        box.Margin = new Padding(0);
        var (nextRow, rowStyle) = AddFixedFieldRow(table, row, labelText, box, rowHeight: box.Height);
        // 錄製盒非 TextBox，TableLayoutPanel 不會自動跟隨其變高：盒高隨 inline
        // 錯誤行出現/消失伸縮時，同步調整絕對列高（否則錯誤行被裁切）。
        box.SizeChanged += (_, _) =>
        {
            if (table.IsDisposed)
            {
                return;
            }

            rowStyle.Height = box.Height;
            table.PerformLayout();
        };
        return nextRow;
    }

    /// <summary>加入「標籤 + 控制項 + 右側紅點標記」一列，列高依欄位本體自適應。</summary>
    private int AddAutoFieldRow(TableLayoutPanel table, int row, string labelText, Control field)
    {
        AddLabel(table, row, labelText);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(BuildFieldContainer(field, fixedRow: false), 1, row);
        return row + 1;
    }

    /// <summary>加入固定列高的欄位列（範本多行框、錄製盒等由呼叫端指定高度）。</summary>
    /// <returns>下一列索引與本列 RowStyle（錄製盒列需隨盒高微調）。</returns>
    private (int nextRow, RowStyle rowStyle) AddFixedFieldRow(
        TableLayoutPanel table, int row, string labelText, Control field, int rowHeight)
    {
        AddLabel(table, row, labelText);
        var rowStyle = new RowStyle(SizeType.Absolute, rowHeight);
        table.RowStyles.Add(rowStyle);
        table.Controls.Add(BuildFieldContainer(field, fixedRow: true), 1, row);
        return (row + 1, rowStyle);
    }

    /// <summary>加入勾選欄一列（無驗證規則，不需紅點標記；標籤欄留空）。</summary>
    private int AddCheckRow(TableLayoutPanel table, int row, CheckBox box)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.Dock = DockStyle.Fill;
        table.Controls.Add(box, 1, row);
        return row + 1;
    }

    /// <summary>欄位控制項包進「欄位本體 + 右側錯誤標記欄」的內嵌雙欄列。</summary>
    /// <remarks>
    /// 外層表格維持兩欄結構，紅點標記掛在欄位本體的內嵌列上：標記欄固定
    /// 18px 寬、平時隱藏（顯示/隱藏不擠動欄位寬度），紅色 <c>!</c> 靠左垂直置中。
    /// <paramref name="fixedRow"/> 為列高固定的列（範本／錄製盒）時，容器吃滿列高、
    /// 內列以 Percent 攤平；自適應列則容器本身 AutoSize、內列 AutoSize。
    /// </remarks>
    private TableLayoutPanel BuildFieldContainer(Control field, bool fixedRow)
    {
        var marker = new Label
        {
            Text = "!",
            AutoSize = true,
            Anchor = AnchorStyles.Left, // 僅錨左＝水平靠左、垂直置中
            ForeColor = Theme.Error,
            Margin = new Padding(4, 0, 0, 0),
            Visible = false, // 平時藏起；驗證失敗才亮紅點
        };

        var container = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill, // 寬度吃滿控制項欄（260），欄位本體才能跟著伸縮
            AutoSize = !fixedRow,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // 欄位本體
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18f)); // 紅點標記固定寬
        container.RowStyles.Add(fixedRow
            ? new RowStyle(SizeType.Percent, 100f)
            : new RowStyle(SizeType.AutoSize));
        field.Dock = DockStyle.Fill;
        container.Controls.Add(field, 0, 0);
        container.Controls.Add(marker, 1, 0);
        _errorMarkers[field] = marker;
        return container;
    }

    /// <summary>加入「標籤」一列的標籤本體（控制項/列型由呼叫端負責）。</summary>
    private void AddLabel(TableLayoutPanel table, int row, string labelText)
    {
        var label = new Label
        {
            AutoSize = true,
            Text = labelText,
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0),
        };
        table.Controls.Add(label, 0, row);
    }

    /// <summary>表單底部 FooterBg 按鈕列：靠右「儲存」（Accent 主鈕）＋「取消」（幽靈鈕），
    /// 上緣髮絲線；儲存/取消按鈕同步設為表單的 Accept/Cancel 鈕。</summary>
    private Panel BuildButtonBar()
    {
        var saveButton = new Button
        {
            Text = "儲存",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Theme.Paper, // 靛青底 + 紙色字（沿用彈窗主鈕樣式）
            UseVisualStyleBackColor = false,
            AutoSize = true,
            Margin = new Padding(6, 0, 0, 0),
        };
        saveButton.FlatAppearance.BorderSize = 0;
        saveButton.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(Theme.Accent, 0.08f);
        saveButton.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(Theme.Accent, 0.08f);
        saveButton.Click += OnSaveButtonClick;

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.FooterBg, // 幽靈鈕與按鈕列同底色（僅文字、hover 時浮色塊）
            ForeColor = Theme.MutedBtn,
            UseVisualStyleBackColor = false,
            AutoSize = true,
            Margin = new Padding(6, 0, 0, 0),
        };
        cancelButton.FlatAppearance.BorderSize = 0;
        cancelButton.FlatAppearance.MouseOverBackColor = Theme.Panel;
        cancelButton.FlatAppearance.MouseDownBackColor = Theme.Panel;
        cancelButton.Click += OnCancelButtonClick;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft, // 加入順序：儲存最右、取消次之
            WrapContents = false,
            BackColor = Theme.FooterBg,
            Padding = new Padding(10, 9, 10, 9),
            Margin = new Padding(0),
        };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);

        var bar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            BackColor = Theme.FooterBg,
            Margin = new Padding(0),
        };
        // 子控制項加入順序（尾端先 dock）：按鈕列內容先佔 Fill，上緣髮絲線再壓頂。
        bar.Controls.Add(buttons);
        bar.Controls.Add(CreateHairlinePanel(DockStyle.Top));

        // 取消＝不存檔結束；Esc 即取消鍵。多行範本 TextBox（AcceptsReturn=true）中
        // Enter 會插入換行、不會觸發 AcceptButton，故 Enter 送出與範本輸入不衝突。
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        return bar;
    }

    /// <summary>建立髮絲分隔線面板（1px、<see cref="Theme.Hairline"/>），dock 方向由呼叫端指定。</summary>
    private static Panel CreateHairlinePanel(DockStyle dock) => new()
    {
        Dock = dock,
        Height = 1,
        BackColor = Theme.Hairline,
        Margin = new Padding(0),
    };

    private void OnCancelButtonClick(object? sender, EventArgs e) => Close();

    private static TextBox CreateTextBox() => new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0),
    };

    /// <summary>以主題字族建立字型；系統無該字族時回 null（呼叫端退回系統預設字族）。</summary>
    /// <remarks>模式沿用 <see cref="HotkeyCaptureBox.TryCreateFont"/>：<see cref="Font"/> 建構子
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