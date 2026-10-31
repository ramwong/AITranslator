using System;
using System.Drawing;
using System.Windows.Forms;
using AITranslator.Models;
using AITranslator.Native;

namespace AITranslator.UI;

/// <summary>
/// 無啟動（no-activate）翻譯彈窗，深色主題（配色見 <see cref="PopupTheme"/>），
/// 建構時依 <see cref="AppSettings.PopupLayout"/> 擇一配置三種版面：
///
/// <para><b>① Split 左右分欄</b>：左欄原文＋「📋 複製」（左）「🔊 朗讀」（右）按鈕列、
/// 右欄譯文＋「📋 複製」（左）「⚙ 設定」（右）按鈕列，中間 1px 分隔線。</para>
///
/// <para><b>② Stack 上下分段</b>：上區原文＋「📋 複製」「🔊 朗讀」成組靠右的按鈕列、
/// 下區譯文＋「📋 複製」「⚙ 設定」成組靠右的按鈕列，中間 1px 分隔線。</para>
///
/// <para><b>③ TranslationOnly 純譯文</b>：無原文區（原文文字區與朗讀鈕為 null），
/// 譯文＋「📋 複製」（左）「⚙ 設定」（右）按鈕列。</para>
///
/// <para><b>不搶焦點</b>：<see cref="FormBorderStyle"/> 為 None，
/// <see cref="Form.ShowWithoutActivation"/> 回傳 true，並於 <see cref="CreateParams"/>
/// 疊加 <c>WS_EX_NOACTIVATE</c>｜<c>WS_EX_TOOLWINDOW</c>｜<c>WS_EX_TOPMOST</c>——
/// 顯示時與滑鼠點擊視窗本身都不會取得焦點，使用者打字中的應用程式不受干擾
/// （點擊按鈕以程式方式寫剪貼簿，不經由選取）。</para>
///
/// <para><b>定位</b>：<see cref="ShowTranslation"/> 以滑鼠位置右移 16px、下移 16px 為
/// 左上角，並 clamp 進 <see cref="Screen.FromPoint(Point)"/> 所在螢幕的 WorkingArea
/// （多螢幕以滑鼠所在螢幕為準），彈窗永不溢出可視範圍。</para>
///
/// <para><b>捲動</b>：原文與譯文皆以多行唯讀 <see cref="TextBox"/>（WordWrap、縱向捲軸、
/// 無邊框、Dock=Fill）顯示，由原生 EDIT 控制項自管折行、捲動與繪製。
/// 強制縱向捲軸常駐（ScrollBars=Vertical 為常顯性）：短文僅右側多一條空捲軸，
/// 換取不需要自行量測/長高 Label 的設計——Label（AutoSize）的量測（
/// TextRenderer.MeasureText）與繪製是兩條程式路徑，遇 PDF 擷取帶入的特殊字元
/// （如 U+00AD 軟連字符）可能量測低估，導致尾部整段被裁切且捲動亦不可見。
/// 唯讀 TextBox 的背/前景色須在 <c>ReadOnly=true</c> 之後設定，否則被系統灰底蓋掉。</para>
///
/// <para><b>TTS 狀態列</b>：彈窗內點「🔊 朗讀」時，初始化中／失敗等狀態以譯文按鈕列
/// 上方的 <see cref="Label"/>（高 24px、Dock、字級同彈窗）短暫顯示——出現時擠壓文字區
/// 高度而不覆蓋內容，不觸發 toast、不自動關窗；失敗訊息以 <see cref="PopupTheme.ErrorText"/>
/// 顯示。任何重設內容的入口（<see cref="ShowTranslation"/>、<see cref="SetText"/>、
/// <see cref="ShowToast"/>、關閉）都會收起狀態列。</para>
///
/// <para><b>關閉</b>：第一次顯示時啟用 <see cref="DismissHook"/>
/// （點擊彈窗以外任意處或按 Esc → Close）；<see cref="Form.OnClosed"/> 對應呼叫
/// <see cref="DismissHook.End"/> 解除 hook 並重設 <c>_dismissHooked</c>
/// （關閉後被重用的彈窗會於 OnShown 重新安裝）。已顯示中再次呼叫 <see cref="ShowTranslation"/>
/// 僅重新定位並更新狀態文字（重用同一視窗，不重複 Begin）。</para>
///
/// <para><b>提示（toast）</b>：<see cref="ShowToast"/> 顯示提示文字（如「未選取文字」），
/// 覆蓋全部內容（含清空原文區）並停用全部文字類按鈕，2500ms 後自動關閉；
/// 期間改以 <see cref="ShowTranslation"/> 重用彈窗即取消自動關閉，手動關閉亦於
/// <see cref="Form.OnClosed"/> 清掉計時器。</para>
///
/// <para><see cref="IsBusy"/> 於 <see cref="ShowTranslation"/> 起為 true，直到
/// <see cref="SetText"/> 或關閉；<see cref="IsShowingResult"/> 於 <see cref="SetText"/>
/// 後為 true（正呈現譯文/錯誤），重置入口或關閉時轉 false。「📋 複製」「🔊 朗讀」
/// 在對應文字為空或尚未取得前保持禁用（「⚙ 設定」永遠可用）。</para>
/// </summary>
public class PopupForm : Form
{
    private const int Offset = 16; // 滑鼠位置到彈窗左上角的方向偏移（px）
    private const int ToastAutoCloseMs = 2500; // toast 顯示多久後自動關閉（spec：顯示提示後自動關閉）
    private const int ButtonBarHeight = 36;  // 按鈕列高（沿用既有單一複製鈕的列高）
    private const int TtsStatusHeight = 24;  // TTS 狀態列高（spec：約 24px）
    private const int DividerThickness = 1;  // 左右/上下分隔線厚度（px）
    private const int ButtonWidthMin = 88;   // 按鈕寬下限：三種按鈕文字長度相近，統一大小

    private readonly TextBox _translationBox; // 譯文文字區（三種版面皆存在）
    private TextBox? _sourceBox;              // 原文文字區（版面③為 null）
    private readonly Button _translationCopyButton;
    private Button? _sourceCopyButton;
    private Button? _ttsButton;
    private readonly Label _ttsStatusLabel;
    private readonly Font _font;
    private readonly Font _buttonFont;

    private System.Windows.Forms.Timer? _toastTimer; // toast 自動關閉計時器；ShowTranslation 重用時取消
    private string? _result;   // 目前譯文；SetText 前為 null
    private string? _source;   // 目前原文（版面③恒為 null）
    private bool _busy;        // ShowTranslation → SetText/Close 之間為 true
    private bool _isShowingResult; // 正呈現譯文/錯誤內容（非 toast、非 busy）
    private bool _dismissHooked;

    /// <summary>以設定（彈窗尺寸、字級、版面模式）建立彈窗，初始為隱藏。</summary>
    public PopupForm(AppSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(settings.PopupWidth, settings.PopupHeight);
        BackColor = PopupTheme.Background;

        _font = new Font(Control.DefaultFont.FontFamily, settings.FontSize);
        Font = _font; // 按鈕、狀態列等皆繼承彈窗字級
        // 按鈕字型：Segoe UI 缺 📋/🔊/⚙（U+1F4CB/U+1F50A/U+2699）的字圖，
        // GDI 量測繪製時落成 .notdef 空框（字級照舊，僅換含單色符號字圖的字族）；
        // 系統無此字族時沿用彈窗字型（退回文字仍可讀，僅符號為空框）。
        _buttonFont = TryCreateFont("Segoe UI Symbol", settings.FontSize) ?? _font;
        Padding = new Padding(12); // 12px 位移內距（Dock 的子控制項會讓開 Padding）

        // 多行唯讀 TextBox：折行、捲動、繪製皆由原生 EDIT 控制項自管，
        // 不再有「量測高度 vs 實際繪製」的兩條路徑可分歧（Label AutoSize 的已知弱點）。
        // 版面③沒有原文區：_sourceBox/_sourceCopyButton/_ttsButton 維持 null。
        _translationBox = CreateTextBox(PopupTheme.MainText);
        _ttsStatusLabel = CreateTtsStatusLabel();
        _translationCopyButton = CreateButton("📋 複製", enabled: false);
        _translationCopyButton.Click += OnTranslationCopyButtonClick;

        switch (settings.PopupLayout)
        {
            case Models.PopupLayout.Stack:
                BuildStack();
                break;

            case Models.PopupLayout.TranslationOnly:
                BuildTranslationOnly();
                break;

            default:
                BuildSplit();
                break;
        }
    }

    /// <summary>
    /// 疊加擴充樣式：不啟動、工具視窗（不出現在 Alt+Tab）、置頂。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 // WS_EX_NOACTIVATE
                        | 0x00000080 // WS_EX_TOOLWINDOW
                        | 0x00000008; // WS_EX_TOPMOST
            return cp;
        }
    }

    /// <summary>顯示時不啟動（不搶走使用者目前視窗的焦點）。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// <see cref="ShowTranslation"/> 之後、<see cref="SetText"/>（或關閉）之前為 true。
    /// </summary>
    public bool IsBusy => _busy;

    /// <summary>彈窗正呈現翻譯結果/錯誤內容（非 toast、非 busy）；關閉時重設 false。</summary>
    public bool IsShowingResult => _isShowingResult;

    /// <summary>以「彈窗內的原文」觸發 TTS（🔊 朗讀鈕）；參數可能為空字串。AppController 訂閱。</summary>
    public event Action<string>? TtsRequested;

    /// <summary>「⚙ 設定」鈕請求開啟設定視窗。AppController 訂閱。</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 於滑鼠位置附近（右移/下移 16px、clamp 進所在螢幕 WorkingArea）顯示彈窗，
    /// 內容為狀態文字（「翻譯中…」或錯誤訊息，以次級 <see cref="PopupTheme.SourceText"/>
    /// 顯示）。已顯示中再次呼叫僅重新定位並更新狀態。帶 <paramref name="source"/>
    /// 時同步放進原文區（顯示原文的版面①②），不帶（null）則清空原文區——新請求語意；
    /// 版面③忽略 <paramref name="source"/>（原文區不存在，<see cref="_source"/> 恒為 null）。
    /// </summary>
    public void ShowTranslation(Point anchor, string statusText, string? source = null)
    {
        Location = ComputePosition(anchor);
        _result = null;
        _busy = true;
        _isShowingResult = false;
        _translationBox.Text = statusText ?? string.Empty;
        _translationBox.ForeColor = PopupTheme.SourceText; // busy/status 期間譯文用次級色
        _translationCopyButton.Enabled = false;

        _source = (_sourceBox != null) ? source : null;
        if (_sourceBox != null)
        {
            _sourceBox.Text = _source ?? string.Empty;
        }

        UpdateSourceButtons();
        HideTtsStatus(); // 重設內容的入口：收起 TTS 狀態列
        StopToastTimer(); // 抓走 toast 彈窗改顯譯文/狀態後，絕不可再自動關閉

        if (!Visible)
        {
            Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE：不會取得焦點
        }
    }

    /// <summary>
    /// 就地更新原文區：不清譯文、不重定位（合併 OCR 的原文與譯文同時到位時，
    /// 由 <see cref="SetText"/> 之前呼叫）。原文相關按鈕（複製/朗讀）依內容有無更新。
    /// </summary>
    public void SetSource(string source)
    {
        _source = source;
        if (_sourceBox != null)
        {
            _sourceBox.Text = source ?? string.Empty;
        }

        UpdateSourceButtons();
    }

    /// <summary>
    /// 以譯文更新內容並允許「複製」。譯文為空字串或 null 時按鈕保持禁用。
    /// <paramref name="isError"/> 為 true 時（LLM/HTTP 錯誤等失敗訊息）以
    /// <see cref="PopupTheme.ErrorText"/> 顯示，否則以主要文字色
    /// <see cref="PopupTheme.MainText"/>。原文區內容與按鈕狀態維持不變。
    /// </summary>
    public void SetText(string result, bool isError = false)
    {
        _result = result;
        _busy = false;
        _isShowingResult = true;
        _translationBox.Text = result ?? string.Empty;
        _translationBox.ForeColor = isError ? PopupTheme.ErrorText : PopupTheme.MainText;
        _translationCopyButton.Enabled = !string.IsNullOrEmpty(result);
        HideTtsStatus(); // 重設內容的入口：收起 TTS 狀態列
    }

    /// <summary>
    /// 顯示提示文字（如「未選取文字」「選取文字超過 N 字上限」）並於 2500ms 後自動關閉
    /// （spec：顯示提示後自動關閉）。定位/重用方式與 <see cref="ShowTranslation"/> 相同
    /// （非 busy 狀態）；toast 覆蓋全部內容——清空原文區並停用全部文字類按鈕。
    /// toast 期間改呼叫 <see cref="ShowTranslation"/> 或手動關閉都會取消計時器，
    /// 不會有殘留的自動關閉計時或洩漏的 Timer。
    /// </summary>
    public void ShowToast(Point mousePos, string text)
    {
        Location = ComputePosition(mousePos);
        _result = null;
        _busy = false;
        _isShowingResult = false;
        _source = null;
        _translationBox.Text = text ?? string.Empty;
        _translationBox.ForeColor = PopupTheme.MainText;
        _translationCopyButton.Enabled = false;

        if (_sourceBox != null)
        {
            _sourceBox.Text = string.Empty;
        }

        UpdateSourceButtons();
        HideTtsStatus(); // 重設內容的入口：收起 TTS 狀態列

        if (!Visible)
        {
            Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE：不會取得焦點
        }

        // 每次 toast 都重建計時器（重觸發時重置倒數）；表單關閉/釋放必經 StopToastTimer。
        StopToastTimer();
        _toastTimer = new System.Windows.Forms.Timer
        {
            Interval = ToastAutoCloseMs,
        };
        _toastTimer.Tick += OnToastTick;
        _toastTimer.Start();
    }

    /// <summary>
    /// 在譯文按鈕列上方的狀態列顯示 TTS 狀態文字（初始化中／失敗原因）：
    /// 不覆蓋文字區、不觸發 toast、不自動關窗。<paramref name="text"/> 為 null 時收起
    /// 狀態列；<paramref name="isError"/> 為 true 時以 <see cref="PopupTheme.ErrorText"/>
    /// 顯示，否則以主要文字色顯示。
    /// </summary>
    public void ShowTtsStatus(string? text, bool isError = false)
    {
        if (text == null)
        {
            HideTtsStatus();
            return;
        }

        _ttsStatusLabel.Text = text;
        _ttsStatusLabel.ForeColor = isError ? PopupTheme.ErrorText : PopupTheme.MainText;
        _ttsStatusLabel.Visible = true;
    }

    /// <summary>toast 倒數結束：先停掉計時器再關閉表單。</summary>
    private void OnToastTick(object? sender, EventArgs e)
    {
        StopToastTimer();
        Close();
    }

    /// <summary>
    /// 停止並釋放 toast 計時器（冪等）。表單任何關閉/釋放路徑都會經過此處，
    /// 保證不留活的 Timer、不留指向已釋放表單的計時器參考。
    /// </summary>
    private void StopToastTimer()
    {
        if (_toastTimer == null)
        {
            return;
        }

        var timer = _toastTimer;
        _toastTimer = null;
        timer.Stop();
        timer.Tick -= OnToastTick;
        timer.Dispose();
    }

    /// <summary>
    /// 依目前原文內容更新原文相關按鈕（原文複製、朗讀）的可用狀態：
    /// 原文為空或尚未取得前禁用。版面③（無原文區的按鈕）為 no-op。
    /// </summary>
    private void UpdateSourceButtons()
    {
        var hasSource = !string.IsNullOrEmpty(_source);

        if (_sourceCopyButton != null)
        {
            _sourceCopyButton.Enabled = hasSource;
        }

        if (_ttsButton != null)
        {
            _ttsButton.Enabled = hasSource;
        }
    }

    /// <summary>收起 TTS 狀態列（隱藏的控制項不佔 dock 空間，文字區自動回復高度）。</summary>
    private void HideTtsStatus()
    {
        _ttsStatusLabel.Visible = false;
    }

    // ---- 版面建構 ----------------------------------------------------------

    /// <summary>
    /// 版面① Split 左右分欄：外層 <see cref="TableLayoutPanel"/> 單列三欄
    /// （Percent 50 / Absolute 1 / Percent 50，中間 1px 分隔線），每欄一個
    /// <see cref="Panel"/> 內含文字區＋按鈕列；TTS 狀態列置於右欄（譯文）按鈕列上方。
    /// 左欄按鈕列「📋 複製」左、「🔊 朗讀」右；右欄「📋 複製」左、「⚙ 設定」右。
    /// </summary>
    private void BuildSplit()
    {
        CreateSourceControls();

        var leftPanel = BuildColumnPanel(_sourceBox!, ttsStatusLabel: null,
            BuildSpreadBar(_sourceCopyButton!, _ttsButton!));
        var rightPanel = BuildColumnPanel(_translationBox, _ttsStatusLabel,
            BuildSpreadBar(_translationCopyButton, CreateSettingsButton()));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            ColumnCount = 3,
            RowCount = 1,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DividerThickness));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.Controls.Add(leftPanel, 0, 0);
        layout.Controls.Add(CreateDivider(), 1, 0);
        layout.Controls.Add(rightPanel, 2, 0);
        Controls.Add(layout);
    }

    /// <summary>
    /// 版面② Stack 上下分段：外層 <see cref="TableLayoutPanel"/> 一欄三列
    /// （Percent 50 / Absolute 1 / Percent 50，中間 1px 分隔線）。上區原文＋
    /// 「📋 複製」「🔊 朗讀」成組靠右；下區譯文＋TTS 狀態列＋「📋 複製」「⚙ 設定」
    /// 成組靠右。
    /// </summary>
    private void BuildStack()
    {
        CreateSourceControls();

        var topPanel = BuildColumnPanel(_sourceBox!, ttsStatusLabel: null,
            BuildRightGroupBar(_sourceCopyButton!, _ttsButton!));
        var bottomPanel = BuildColumnPanel(_translationBox, _ttsStatusLabel,
            BuildRightGroupBar(_translationCopyButton, CreateSettingsButton()));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DividerThickness));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        layout.Controls.Add(topPanel, 0, 0);
        layout.Controls.Add(CreateDivider(), 0, 1);
        layout.Controls.Add(bottomPanel, 0, 2);
        Controls.Add(layout);
    }

    /// <summary>
    /// 版面③ TranslationOnly 純譯文：無原文區（原文文字區與朗讀鈕維持 null）——
    /// 譯文文字區＋TTS 狀態列＋「📋 複製」（左）「⚙ 設定」（右）按鈕列。
    /// </summary>
    private void BuildTranslationOnly()
    {
        // 先加入 Fill 的文字區，再加入 Dock=Bottom 的狀態列/按鈕列：
        // WinForms 由集合尾端起 dock，按鈕列先佔底、狀態列在其上、文字區填滿其餘。
        Controls.Add(_translationBox);
        Controls.Add(_ttsStatusLabel);
        Controls.Add(BuildSpreadBar(_translationCopyButton, CreateSettingsButton()));
    }

    /// <summary>
    /// 建立版面①②共通的原文區控制項：原文文字區、原文複製鈕與朗讀鈕
    /// （版面③不呼叫；原文區相關欄位維持 null）。
    /// </summary>
    private void CreateSourceControls()
    {
        var sourceBox = CreateTextBox(PopupTheme.SourceText);
        var sourceCopyButton = CreateButton("📋 複製", enabled: false);
        sourceCopyButton.Click += OnSourceCopyButtonClick;
        var ttsButton = CreateButton("🔊 朗讀", enabled: false);
        ttsButton.Click += OnTtsButtonClick;
        _sourceBox = sourceBox;
        _sourceCopyButton = sourceCopyButton;
        _ttsButton = ttsButton;
    }

    /// <summary>
    /// 一個版面「欄/區」的內容面板：文字區（Fill）＋TTS 狀態列（Bottom 24px，可為 null）
    /// ＋按鈕列（Bottom 36px）。加入順序即 dock 順序：集合尾端起 dock，
    /// 按鈕列先佔底、狀態列在其上（出現時擠壓文字區高度而不覆蓋文字）。
    /// </summary>
    private static Panel BuildColumnPanel(Control textArea, Label? ttsStatusLabel, Control buttonBar)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        panel.Controls.Add(textArea);

        if (ttsStatusLabel != null)
        {
            panel.Controls.Add(ttsStatusLabel);
        }

        panel.Controls.Add(buttonBar);
        return panel;
    }

    /// <summary>建立多行唯讀文字區（原文與譯文共用樣式，僅前景色不同）。</summary>
    private TextBox CreateTextBox(Color foreColor)
    {
        // 建構子內已設 ReadOnly：背景/前景色排在 ReadOnly 之後，避免系統灰底蓋掉自訂色。
        return new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical, // 常顯縱向捲軸；短文只是多一條空捲軸
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            TabStop = false,
            Cursor = Cursors.Arrow, // 無法就地編輯，不擬 I-beam
            BackColor = PopupTheme.Background,
            ForeColor = foreColor,
        };
    }

    /// <summary>TTS 狀態列：高 24px 的 Bottom Dock Label，字級繼承彈窗、初始隱藏。</summary>
    private static Label CreateTtsStatusLabel()
    {
        return new Label
        {
            AutoSize = false,
            Height = TtsStatusHeight,
            Dock = DockStyle.Bottom,
            Visible = false,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            BackColor = PopupTheme.Background,
        };
    }

    /// <summary>
    /// 彈窗按鈕統一樣式：Flat、深色底、分隔線邊框、hover 色、高 36（沿用既有按鈕列高）。
    /// 自動依文字量寬（下限 <see cref="ButtonWidthMin"/>）使三種按鈕統一大小；
    /// 字型用 <see cref="_buttonFont"/>（Segoe UI Symbol，見建構子註解）。
    /// </summary>
    private Button CreateButton(string text, bool enabled)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = PopupTheme.ButtonBack,
            ForeColor = PopupTheme.MainText,
            UseVisualStyleBackColor = false,
            AutoSize = true,
            MinimumSize = new Size(ButtonWidthMin, ButtonBarHeight),
            Margin = new Padding(0),
            Font = _buttonFont,
            Enabled = false, // 文字類按鈕初值禁用；⚙ 由 enabled=true 開啟
        };
        button.FlatAppearance.BorderColor = PopupTheme.Divider;
        button.FlatAppearance.MouseOverBackColor = PopupTheme.ButtonHover;
        button.Enabled = enabled;
        return button;
    }

    /// <summary>「⚙ 設定」鈕：永遠可用，點擊後由訂閱者開啟設定視窗。</summary>
    private Button CreateSettingsButton()
    {
        var button = CreateButton("⚙ 設定", enabled: true);
        button.Click += OnSettingsButtonClick;
        return button;
    }

    /// <summary>
    /// 「左右撐開」按鈕列：兩格 50/50 的 <see cref="TableLayoutPanel"/>（高 36），
    /// 左格按鈕靠左、右格按鈕靠右（左右撐開的視覺，spec mockup 版面①③與 Split 兩欄共用）。
    /// </summary>
    private static TableLayoutPanel BuildSpreadBar(Control left, Control right)
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = ButtonBarHeight,
            Margin = new Padding(0),
            ColumnCount = 2,
            RowCount = 1,
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        left.Dock = DockStyle.None;
        left.Anchor = AnchorStyles.Left;
        right.Dock = DockStyle.None;
        right.Anchor = AnchorStyles.Right;
        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(right, 1, 0);
        return bar;
    }

    /// <summary>
    /// 「成組靠右」按鈕列：兩顆按鈕以 <see cref="FlowLayoutPanel"/>
    /// （FlowDirection=LeftToRight、WrapContents=false）相鄰成組，
    /// 前格 Percent 100 的空格把整組推到最右（spec mockup 版面②的節奏）；
    /// 成組寬度由 AutoSize 欄位隨按鈕實際寬度決定。
    /// </summary>
    private static TableLayoutPanel BuildRightGroupBar(Control first, Control second)
    {
        var group = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        group.Controls.Add(first);
        group.Controls.Add(second);

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = ButtonBarHeight,
            Margin = new Padding(0),
            ColumnCount = 2,
            RowCount = 1,
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        bar.Controls.Add(group, 1, 0);
        return bar;
    }

    /// <summary>1px 分隔線面板（Split 的直線／Stack 的橫線，寬高皆由外層版面欄/列決定）。</summary>
    private static Panel CreateDivider()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = PopupTheme.Divider,
        };
    }

    /// <summary>建立指定字族、彈窗字級的字型；系統無該字族時回傳 null（呼叫端退回彈窗字型）。</summary>
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

    // ---- 事件處理 ----------------------------------------------------------

    /// <summary>目前譯文寫入剪貼簿後關閉；無譯文時直接關閉。</summary>
    private void OnTranslationCopyButtonClick(object? sender, EventArgs e)
    {
        CopyToClipboardAndClose(_result);
    }

    /// <summary>目前原文寫入剪貼簿後關閉（同構於譯文複製）；無原文時直接關閉。</summary>
    private void OnSourceCopyButtonClick(object? sender, EventArgs e)
    {
        CopyToClipboardAndClose(_source);
    }

    /// <summary>朗讀鈕：以「目前原文」請求 TTS（可能為空字串），不關彈窗、不清內容。</summary>
    private void OnTtsButtonClick(object? sender, EventArgs e)
    {
        TtsRequested?.Invoke(_source ?? string.Empty);
    }

    /// <summary>設定鈕：請求開啟設定視窗（彈窗關閉由訂閱者處理）。</summary>
    private void OnSettingsButtonClick(object? sender, EventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    /// <summary>文字寫入剪貼簿後關閉彈窗；無內容（null/空字串）時直接關閉不動剪貼簿。</summary>
    private void CopyToClipboardAndClose(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                // 剪貼簿被其他程序獨佔時 SetText 可能丟 ExternalException；不因此讓彈窗卡住。
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
        }

        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 第一次顯示才安裝 dismissal hook（冪等保護；重用顯示不重複安裝）。
        if (!_dismissHooked)
        {
            _dismissHooked = true;
            DismissHook.Begin(() => Close(), () => Bounds);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _busy = false;
        _isShowingResult = false;
        HideTtsStatus(); // 重設內容的入口：重用的彈窗不留殘留狀態列
        StopToastTimer(); // 手動關閉（DismissHook/Esc）取消自動關閉倒數
        // 解除 hook 的同時重設旗標：封閉後被 AppController.EnsurePopup 重用的彈窗
        // 需在 OnShown 重新 Begin（toast 自動關閉讓「關閉→重用」成為常態路徑，
        // 不重設會造成重用彈窗沒有 dismissal hook、無法被外部點擊或 Esc 關閉）。
        _dismissHooked = false;
        DismissHook.End();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopToastTimer(); // 防禦：任何釋放路徑都不留活的計時器（含 Timer 對本表單的參考）
            _font.Dispose();
            if (!ReferenceEquals(_buttonFont, _font))
            {
                _buttonFont.Dispose(); // 退回彈窗字型（同一實例）時不重複釋放
            }

            _dismissHooked = false;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 滑鼠位置右移/下移 16px 為左上角，clamp 進滑鼠所在螢幕（Screen.FromPoint）的
    /// WorkingArea，彈窗不溢出可視範圍。
    /// </summary>
    private Point ComputePosition(Point mousePos)
    {
        Rectangle area = Screen.FromPoint(mousePos).WorkingArea;

        int x = Math.Min(mousePos.X + Offset, area.Right - Width);
        x = Math.Max(x, area.Left);

        int y = Math.Min(mousePos.Y + Offset, area.Bottom - Height);
        y = Math.Max(y, area.Top);

        return new Point(x, y);
    }
}