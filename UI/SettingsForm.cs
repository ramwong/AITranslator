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
/// （檔案不存在時顯示預設值）；「儲存」逐欄驗證（數值範圍、格式、快捷鍵、
/// 非空白），任一不合規即彈出 MessageBox（owner 為本表單）且不存檔；
/// 全數通過後寫回設定檔、依勾選狀態同步開機自動啟動登錄值、
/// 引發 <paramref name="onChanged"/> 帶回更新後設定，最後關閉表單。
/// 不勾「儲存」直接結束，設定不會有任何變更。</para>
///
/// <para><b>版面</b>：<see cref="TableLayoutPanel"/> 兩欄標籤/控制項，表單 AutoSize、
/// <see cref="FormBorderStyle.FixedSingle"/>、不可最大化。</para>
/// </summary>
public class SettingsForm : Form
{
    private readonly string _settingsPath;
    private readonly SettingsChangedCallback _onChanged;
    private readonly AppSettings _settings;

    private readonly TextBox _widthBox;
    private readonly TextBox _heightBox;
    private readonly TextBox _fontSizeBox;
    private readonly TextBox _templateBox;
    private readonly TextBox _modelBox;
    private readonly TextBox _urlBox;
    private readonly TextBox _hotkeyBox;
    private readonly TextBox _maxTextLengthBox;
    private readonly CheckBox _startWithWindowsBox;

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
        _modelBox = CreateTextBox();
        _urlBox = CreateTextBox();
        _hotkeyBox = CreateTextBox();
        _maxTextLengthBox = CreateTextBox();

        _templateBox = CreateTextBox();
        _templateBox.Multiline = true; // Prompt 範本允許多行（Enter 換行，不觸發 OK）
        _templateBox.AcceptsReturn = true;
        _templateBox.ScrollBars = ScrollBars.Vertical; // 內含 {text} 佔位符，貼長範本可捲動

        _startWithWindowsBox = new CheckBox
        {
            AutoSize = true,
            Text = "開機自動啟動",
        };
        // 登錄 Run 值才是「開機是否啟動」的事實來源（使用者可能手動刪值），
        // 勾選狀態以現況為準；存檔時 SetEnabled 會把設定與登錄同步回一致。
        _startWithWindowsBox.Checked = AutoStartService.IsEnabled();

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
    }

    /// <summary>套用欄位驗證後存檔、同步自動啟動、觸發回呼並關閉；驗證失敗不離開表單。</summary>
    private void OnSaveButtonClick(object? sender, EventArgs e)
    {
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
        var hotkey = _hotkeyBox.Text.Trim();
        if (!HotkeyParser.Parse(hotkey).success)
        {
            ShowValidationError(_hotkeyBox,
                "快捷鍵格式不正確。需含 Ctrl、Alt 或 Shift 與單一英文字母（或 F1–F12），例如 Ctrl+Alt+E。");
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
        _settings.StartWithWindows = _startWithWindowsBox.Checked;

        try
        {
            SettingsService.Save(_settingsPath, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                 or System.Security.SecurityException or ArgumentException)
        {
            // 存檔失敗不關表單，讓使用者保留編輯內容自行修正或取消；
            // 中和儲存鈕的 DialogResult.OK，避免 ShowDialog 直接收下 OK 關窗。
            DialogResult = DialogResult.None;
            MessageBox.Show(this, $"設定檔儲存失敗：{ex.Message}", "設定錯誤",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    /// <summary>浮點數欄位驗證：可解析且落在 [min, max]。</summary>
    private bool ValidateFloatField(TextBox box, string name, float min, float max, out float value)
    {
        if (!float.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || value < min || value > max)
        {
            ShowValidationError(box, $"{name}需為 {min} 至 {max} 之間的數值。");
            return false;
        }

        return true;
    }

    /// <summary>顯示驗證錯誤（owner 為本表單），焦點移回問題欄位，不存檔。</summary>
    private void ShowValidationError(TextBox box, string message)
    {
        // 中和儲存鈕的 DialogResult.OK：ShowDialog 下不取消就會驗證一失敗即關窗、
        // 連帶丟掉使用者尚未通過驗證的編輯內容。
        DialogResult = DialogResult.None;
        MessageBox.Show(this, message, "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        box.Focus();
        box.SelectAll();
    }

    private void PopulateFields()
    {
        _widthBox.Text = _settings.PopupWidth.ToString(CultureInfo.InvariantCulture);
        _heightBox.Text = _settings.PopupHeight.ToString(CultureInfo.InvariantCulture);
        _fontSizeBox.Text = _settings.FontSize.ToString(CultureInfo.InvariantCulture);
        _templateBox.Text = _settings.PromptTemplate; // 保留 {text} 佔位符原樣
        _modelBox.Text = _settings.Model;
        _urlBox.Text = _settings.OllamaUrl;
        _hotkeyBox.Text = _settings.Hotkey;           // 顯示如「Ctrl+Alt+E」
        _maxTextLengthBox.Text = _settings.MaxTextLength.ToString(CultureInfo.InvariantCulture);
    }

    private void BuildLayout()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12),
            ColumnCount = 2,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // 標籤欄依最長標籤自適應
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260)); // 控制項欄固定寬

        var row = 0;
        AddFieldRow(table, row++, "彈窗寬度", _widthBox);
        AddFieldRow(table, row++, "彈窗高度", _heightBox);
        AddFieldRow(table, row++, "彈窗字型大小", _fontSizeBox);

        // Prompt 範本：絕對高度列 + Dock=Fill，讓多行 TextBox 約 70px。
        AddLabel(table, row, "Prompt 翻譯範本");
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        table.Controls.Add(_templateBox, 1, row++);
        _templateBox.Dock = DockStyle.Fill;

        AddFieldRow(table, row++, "Ollama 模型名稱", _modelBox);
        AddFieldRow(table, row++, "Ollama API 網址", _urlBox);
        AddFieldRow(table, row++, "快捷鍵", _hotkeyBox);
        AddFieldRow(table, row++, "選取文字上限字數", _maxTextLengthBox);
        AddFieldRow(table, row++, string.Empty, _startWithWindowsBox);

        // 按鈕列：跨兩欄靠右，「儲存」存檔、「取消」直接關閉不存。
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
        };

        var saveButton = new Button { Text = "儲存", DialogResult = DialogResult.OK };
        saveButton.Click += OnSaveButtonClick;

        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        cancelButton.Click += OnCancelButtonClick;

        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(buttons, 0, row);
        table.SetColumnSpan(buttons, 2);

        // 取消＝不存檔結束；Esc 即取消鍵。多行範本 TextBox（AcceptsReturn=true）中
        // Enter 會插入換行、不會觸發 AcceptButton，故 Enter 送出與範本輸入不衝突。
        AcceptButton = saveButton;
        CancelButton = cancelButton;

        Controls.Add(table);
    }

    /// <summary>加入「標籤 + 控制項」一列；RowStyle 由本方法（或範本/按鈕等呼叫端）加入一次。</summary>
    private void AddFieldRow(TableLayoutPanel table, int row, string labelText, Control field)
    {
        AddLabel(table, row, labelText);
        field.Dock = DockStyle.Fill;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(field, 1, row);
    }

    /// <summary>只在標籤欄放 Label（不動 RowStyles，列高由各列呼叫端負責加入一次）。</summary>
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

    private void OnCancelButtonClick(object? sender, EventArgs e) => Close();

    private static TextBox CreateTextBox() => new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0),
    };
}