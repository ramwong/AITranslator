using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AITranslator.Models;
using AITranslator.Native;

namespace AITranslator.UI;

/// <summary>
/// 無啟動（no-activate）翻譯彈窗，紙感（paper/ink）淺色主題（配色見 <see cref="Theme"/>，
/// spec：docs/superpowers/specs/2026-10-31-ui-redesign-design.md「彈窗」節）。
/// 版面為單一「雙卡片」結構：
///
/// <para><b>① 原文內嵌面板</b>（<see cref="_sourcePanel"/>，Dock=Top、高由內容 clamp）：
/// Panel 色內嵌卡＋Hairline 邊框、內距約 <see cref="Theme.PanelPadding"/>；上緣小標
/// 「選取文字」（Caption 色、11px 近似 letter-spacing 感）與右側兩顆幽靈文字鈕
/// 「🔊 朗讀」「⧉ 複製原文」（MutedBtn 色文字）；原文文字以 SourceText 色顯示，
/// 面板內 <see cref="TextBox"/> 自身可捲（OCR 長文完整性優先）。原文為空或只有空白時
/// 兩鈕禁用。</para>
///
/// <para><b>② 譯文區</b>（<see cref="_translationBox"/>，Dock=Fill）：無背景大字
/// （Ink 色為主），字級 = 設定值 + 2pt，視覺主角、可捲。</para>
///
/// <para><b>③ 狀態條</b>（<see cref="_statusBar"/>，Dock=Bottom、高
/// <see cref="Theme.StatusHeight"/>、FooterBg 底＋上緣 Hairline 線）：獨立於譯文的細條，
/// 8px 圓點（忙碌=Ink／錯誤=Error）＋狀態文字；錯誤分兩級——忙碌與「朗讀中」類提示
/// 持續顯示到下一步動作覆寫；錯誤提示（TTS 失敗）與 toast 於 2500ms 後自動關閉彈窗；
/// <see cref="SetText(string,bool)"/> 的 <paramref name="isError"/> 失敗訊息為持久顯示
/// （不自動關，需使用者關閉彈窗或下一步動作覆寫），且同時把完整內容放進譯文區
/// （狀態條僅一條細字，長錯誤訊息以譯文區原文可捲呈現，兩處皆 Error 色）。
/// 無狀態時整條 <see cref="Control.Hide"/>。</para>
///
/// <para><b>④ 動作列</b>（<see cref="_footer"/>，Dock=Bottom、高
/// <see cref="Theme.FooterHeight"/>、FooterBg 底＋上緣 Hairline 線）：靛青實心
/// 「📋 複製」主鈕靠左（Accent 底＋Paper 色字）、「⚙」幽靈鈕靠右；無徽章（不做）。
/// 複製＝複製譯文，無譯文時禁用；複製成功（無論剪貼簿是否被獨佔）即關閉彈窗；
/// 「⧉ 複製原文」複製後<b>不</b>關閉彈窗；兩顆複製共用同一個
/// <see cref="CopyToClipboard"/> 容錯 helper。</para>
///
/// <para><b>不搶焦點</b>：<see cref="FormBorderStyle"/> 為 None，
/// <see cref="Form.ShowWithoutActivation"/> 回傳 true，並於 <see cref="CreateParams"/>
/// 疊加 <c>WS_EX_NOACTIVATE</c>｜<c>WS_EX_TOOLWINDOW</c>｜<c>WS_EX_TOPMOST</c>——
/// 顯示時與滑鼠點擊視窗本身都不會取得焦點，使用者打字中的應用程式不受干擾
/// （兩顆複製鈕皆以程式方式寫剪貼簿，不經由選取）。</para>
///
/// <para><b>定位</b>：<see cref="ShowTranslation"/> 與 <see cref="ShowToast"/> 以
/// 滑鼠位置右移 16px、下移 16px 為左上角，並 clamp 進
/// <see cref="Screen.FromPoint(Point)"/> 所在螢幕的 WorkingArea（多螢幕以滑鼠所在螢幕
/// 為準），彈窗永不溢出可視範圍。</para>
///
/// <para><b>捲動</b>：原文與譯文皆以多行唯讀 <see cref="TextBox"/>（WordWrap、縱向捲軸
/// 常駐、無邊框）顯示，由原生 EDIT 控制項自管折行、捲動與繪製。強制縱向捲軸常駐的
/// 理由同前版設計：短文僅右側多一條空捲軸，換取不需要自行量測/長高 Label 的設計——
/// Label（AutoSize）的量測（TextRenderer.MeasureText）與繪製是兩條程式路徑，遇 PDF
/// 擷取帶入的特殊字元（如 U+00AD 軟連字符）可能量測低估。唯讀 TextBox 的背/前景色
/// 須在 <c>ReadOnly=true</c> 之後設定，否則被系統灰底蓋掉。</para>
///
/// <para><b>原文面板高度</b>：面板 Dock=Top，高度隨原文內容換算並 clamp
/// （短文不佔滿、長文以上限為頂、內容本身可捲）——換算抽為純函式
/// <see cref="ComputeSourcePanelHeight"/>；量測低估的影響僅是面板稍短（原文仍可捲，
/// 不缺字），故另補半行 slack。原為空的 toast/過渡狀態照樣畫出完整骨架
/// （小標、空面板、狀態條、動作列），不另開更小的卡。</para>
///
/// <para><b>字體</b>：全文統一 <see cref="Theme.FontName"/>；建議字族不存在時退回
/// 系統預設字族。按鈕另備 Segoe UI Symbol 字型（📋/🔊/⧉/⚙ 等 U+1F4CB/U+1F50A/
/// U+29C9/U+2699 不在多數中文字族內，GDI 量測繪製會落成 .notdef 空框）；兩個主字型
/// （本文＋譯文 +2pt）與小標/按鈕字型於 <see cref="Dispose"/> 一律釋放。</para>
///
/// <para><b>關閉</b>：第一次顯示時啟用 <see cref="DismissHook"/>
/// （點擊彈窗以外任意處或按 Esc → Close）；<see cref="Form.OnClosed"/> 對應呼叫
/// <see cref="DismissHook.End"/> 解除 hook 並重設 <c>_dismissHooked</c>（關閉後被重用的
/// 彈窗會於 OnShown 重新安裝）。已顯示中再次呼叫 <see cref="ShowTranslation"/> 僅重新
/// 定位並更新狀態（重用同一視窗，不重複 Begin）。</para>
///
/// <para><b>提示（toast）</b>：<see cref="ShowToast"/> 改把提示文字（如「未選取文字」）
/// 落在狀態條（不再塞進主文區），畫出完整骨架、清空原文與譯文、停用全部文字類按鈕，
/// 2500ms 後自動關閉（沿用 toast 語意）；期間改以 <see cref="ShowTranslation"/>
/// 重用彈窗即取消自動關閉，手動關閉亦於 <see cref="Form.OnClosed"/> 清掉計時器。</para>
///
/// <para><b>TTS 狀態</b>：彈窗內點「🔊 朗讀」後，初始化中／朗讀中／失敗等狀態由
/// AppController 經 <see cref="ShowTtsStatus"/> 落在狀態條（表單不硬編 TTS 文案）：
/// muted 文字持續顯示；失敗（isError）以 Error 色呈現並於 2500ms 後自動關閉（沿用
/// toast timer 機制）；<see cref="ShowTtsStatus"/> 不覆蓋譯文與原文內容、不改變按鈕
/// 狀態。「⚙ 設定」鈕一律可用，點擊時若翻譯/辨識進行中由訂閱者先放棄本次結果再開設定。</para>
///
/// <para><see cref="IsBusy"/> 於 <see cref="ShowTranslation"/> 起為 true，直到
/// <see cref="SetText"/>（或關閉）；<see cref="IsShowingResult"/> 於 <see cref="SetText"/>
/// 後為 true（正呈現譯文/錯誤），由 <see cref="ShowTranslation"/>／<see cref="ShowToast"/>
/// 或關閉重設為 false。「📋 複製」「⧉ 複製原文」「🔊 朗讀」在對應文字為空（或僅空白）
/// 或尚未取得前保持禁用（「⚙」永遠可用）。</para>
/// </summary>
public class PopupForm : Form
{
    private const int Offset = 16; // 滑鼠位置到彈窗左上角的方向偏移（px）
    private const int ToastAutoCloseMs = 2500; // toast 顯示多久後自動關閉（spec：顯示提示後自動關閉）
    private const int StatusDotSize = 8;       // 狀態條圓點直徑（px，spec：8px 圓點）
    private const int HairlineThickness = 1;   // 面板邊框／上緣髮絲線厚度（px）
    private const int ScrollBarWidthSlack = 2; // 量測寬度再扣的安全餘裕（px）
    private const int HeaderFallbackHeight = 26; // 標列未完成版面時的保守高度（px）
    private const float SourcePanelMaxFraction = 0.45f; // 原文面板高度上限佔彈窗高度比例

    // ---- 骨架控制項（構建後為唯讀）----
    private readonly Panel _sourcePanel;      // ① 原文內嵌面板（Panel 色 + Hairline 邊框）
    private readonly TableLayoutPanel _sourceHeader; // 面板上緣列：小標 + 幽靈鈕兩顆
    private readonly Label _sourceCaption;    // 小標「選取文字」（Caption 色 11px）
    private readonly TextBox _sourceBox;      // 原文文字（SourceText 色、自身可捲）
    private readonly Button _ttsButton;       // 「🔊 朗讀」幽靈鈕
    private readonly Button _sourceCopyButton; // 「⧉ 複製原文」幽靈鈕（複製後不關彈窗）
    private readonly Panel _translationHost;  // 譯文區外框（套用卡片內距）
    private readonly TextBox _translationBox; // ② 譯文大字（Ink 色 / 錯誤=Error 色）
    private readonly Panel _statusBar;        // ③ 狀態條（無狀態時整條 Hide）
    private readonly Label _statusDot;        // 8px 圓點（忙碌=Ink／錯誤=Error）
    private readonly Label _statusText;       // 狀態文字
    private readonly Panel _footer;           // ④ 動作列
    private readonly Button _copyButton;      // 「📋 複製」主鈕（Accent 底，取得譯文前禁用）
    private readonly Button _settingsButton;  // 「⚙」幽靈鈕（永遠可用）

    // ---- 字型（Dispose 一律釋放；按鈕字型退回 _font 同實例時不重複釋放）----
    private readonly Font _font;             // 本文／狀態條／小標用（設定字級）
    private readonly Font _translationFont;  // 譯文大字（設定字級 + 2pt）
    private readonly Font _captionFont;      // 面板上緣小標（11px）
    private readonly Font _buttonFont;       // 按鈕（Segoe UI Symbol 缺字防護，見類別註解）

    private Color _statusDotColor = Theme.Ink; // 圓點目前顏色（Paint 事件用）
    private System.Windows.Forms.Timer? _toastTimer; // toast 自動關閉計時器；重用入口取消
    private string? _result;   // 目前譯文；SetText 前為 null（isError 時恆 null）
    private string? _source;   // 原始原文字串（未經顯示層正規化）：複製原文/朗讀一律用此，不得用 _sourceBox.Text
    private bool _busy;        // ShowTranslation → SetText/Close 之間為 true
    private bool _isShowingResult; // 正呈現譯文/錯誤內容（非 toast、非 busy）
    private bool _dismissHooked;

    /// <summary>以設定（彈窗尺寸、字級）建立紙感彈窗，初始為隱藏。</summary>
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
        BackColor = Theme.Paper; // 紙面底色：譯文區「白紙上的大字」

        _font = TryCreateFont(Theme.FontName, settings.FontSize)
            ?? new Font(Control.DefaultFont.FontFamily, settings.FontSize);
        Font = _font; // 其餘控制項繼承彈窗字級
        _translationFont = TryCreateFont(Theme.FontName, settings.FontSize + 2)
            ?? new Font(Control.DefaultFont.FontFamily, settings.FontSize + 2);
        _captionFont = TryCreatePixelFont(Theme.FontName, 11f)
            ?? new Font(Control.DefaultFont.FontFamily, 11f, GraphicsUnit.Pixel);
        // 按鈕字型：中文主字族缺 📋/🔊/⧉/⚙（U+1F4CB/U+1F50A/U+29C9/U+2699）的字圖，
        // GDI 量測繪製時落成 .notdef 空框（字級照舊，僅換含單色符號字圖的字族）；
        // 系統無此字族時沿用彈窗字型（退回文字仍可讀，僅符號為空框）。
        _buttonFont = TryCreateFont("Segoe UI Symbol", settings.FontSize) ?? _font;

        // ---- ① 原文內嵌面板（Panel 色 + Hairline 邊框，內距約 PanelPadding）----
        _sourceCaption = new Label
        {
            Text = "選取文字",
            AutoSize = true,
            ForeColor = Theme.Caption,
            Font = _captionFont,
            Margin = new Padding(0),
        };

        _ttsButton = CreateGhostButton(Theme.Panel, Theme.Hairline);
        _ttsButton.Text = "🔊 朗讀";
        _ttsButton.Click += OnTtsButtonClick;
        _sourceCopyButton = CreateGhostButton(Theme.Panel, Theme.Hairline);
        _sourceCopyButton.Text = "⧉ 複製原文";
        _sourceCopyButton.Click += OnSourceCopyButtonClick;

        _sourceHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 1,
        };
        _sourceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); // 小標撐開左行
        _sourceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 🔊 幽靈鈕
        _sourceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // ⧉ 幽靈鈕
        _sourceHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _sourceHeader.Controls.Add(_sourceCaption, 0, 0);
        _sourceHeader.Controls.Add(_ttsButton, 1, 0);
        _sourceHeader.Controls.Add(_sourceCopyButton, 2, 0);

        _sourceBox = CreateTextBox(Theme.Panel, Theme.SourceText, _font);

        _sourcePanel = new Panel
        {
            Dock = DockStyle.Top,
            BackColor = Theme.Panel,
            Padding = new Padding(Theme.PanelPadding), // 原文面板內距（spec 間距 token）
            Margin = new Padding(0),
        };
        // 加入順序即 dock 順序（WinForms 由集合尾端起 dock）：先加入 Fill 的原文框，
        // 再加入 Dock=Top 的標列——標列佔面板上緣，原文框填其餘。
        _sourcePanel.Controls.Add(_sourceBox);
        _sourcePanel.Controls.Add(_sourceHeader);
        _sourcePanel.Paint += OnSourcePanelPaint; // Hairline 邊框（四邊 1px 追框）

        // ---- ② 譯文區（外框套用卡片內距，內含 Fill 的譯文框）----
        _translationBox = CreateTextBox(Theme.Paper, Theme.Ink, _translationFont);
        _translationHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Paper,
            Padding = new Padding(Theme.CardPadding), // 卡片內距 token（spec 間距表）
            Margin = new Padding(0),
        };
        _translationHost.Controls.Add(_translationBox);

        // ---- ③ 狀態條（無狀態時整條 Hide）----
        _statusDot = new Label
        {
            AutoSize = false,
            Size = new Size(StatusDotSize, StatusDotSize), // 8px 圓點（Paint 畫圓）
            Anchor = AnchorStyles.Left, // 單一水平錨：儲存格內垂直置中
            Visible = false, // muted 文字（如「朗讀中…」）不帶點（spec）
            Margin = new Padding(0),
        };
        _statusDot.Paint += OnStatusDotPaint;

        _statusText = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            AutoEllipsis = true, // 細條裝不下的文字以刪節號收尾；長錯誤另以譯文區全文呈現
            Margin = new Padding(0),
        };

        var statusLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = Theme.FooterBg,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12, 0, 8, 0), // 左右留白（docked 子控制項讓開 Padding）
        };
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, StatusDotSize + 10));
        statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        statusLayout.Controls.Add(_statusDot, 0, 0);
        statusLayout.Controls.Add(_statusText, 1, 0);

        _statusBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.StatusHeight,
            BackColor = Theme.FooterBg,
            Margin = new Padding(0),
        };
        _statusBar.Visible = false; // 無狀態時整條 Hide；各入口按需 SetStatusBar 顯示
        // 子控制項加入順序（尾端先 dock）：狀態列內容先佔 Fill，上緣 Hairline 線再壓頂。
        _statusBar.Controls.Add(statusLayout);
        _statusBar.Controls.Add(CreateHairline());

        // ---- ④ 動作列（複製主鈕靠左、⚙ 幽靈鈕靠右）----
        _copyButton = new Button
        {
            Text = "📋 複製",
            Dock = DockStyle.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Theme.Paper, // 靛青底 + 紙色字
            UseVisualStyleBackColor = false,
            AutoSize = true,
            MinimumSize = new Size(96, 0), // Dock 下的寬度下限（AutoSize 失效時文字仍完整）
            Margin = new Padding(0),
            Font = _buttonFont,
            Enabled = false, // 取得非空譯文前不可複製
        };
        _copyButton.FlatAppearance.BorderSize = 0;
        _copyButton.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(Theme.Accent, 0.08f);
        _copyButton.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(Theme.Accent, 0.08f);
        _copyButton.Click += OnCopyButtonClick;

        _settingsButton = CreateGhostButton(Theme.FooterBg, Theme.Panel); // hover 以 Panel 色近似 mockup
        _settingsButton.Text = "⚙";
        _settingsButton.Dock = DockStyle.Right;
        _settingsButton.MinimumSize = new Size(36, 0);
        _settingsButton.Enabled = true; // 設定鈕永遠可用
        _settingsButton.Click += OnSettingsButtonClick;

        var footerContent = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.FooterBg,
            Padding = new Padding(10, 6, 10, 6), // 主鈕不頂滿列高（上下各留 6px 襯線）
            Margin = new Padding(0),
        };
        // 子控制項加入順序不影響左右互斥的 Left/Right dock；上下留白由外框 Padding 提供。
        footerContent.Controls.Add(_copyButton);
        footerContent.Controls.Add(_settingsButton);

        _footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.FooterHeight,
            BackColor = Theme.FooterBg,
            Margin = new Padding(0),
        };
        // 子控制項加入順序（尾端先 dock）：動作列內容先佔 Fill，上緣 Hairline 線再壓頂。
        _footer.Controls.Add(footerContent);
        _footer.Controls.Add(CreateHairline());

        // 表單層加入順序（尾端先 dock）：譯文區（Fill）→ 狀態條（Bottom）→ 動作列（Bottom）
        // → 原文面板（Top）：原文面板先佔上緣、動作列佔最底、狀態條在動作列正上方、
        // 譯文區填其餘（「先 Fill 再 Bottom」同前版註解慣例）。
        Controls.Add(_translationHost);
        Controls.Add(_statusBar);
        Controls.Add(_footer);
        Controls.Add(_sourcePanel);

        _sourceBox.TextChanged += OnSourceBoxTextChanged;
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

    /// <summary>以「彈窗內的原文」觸發 TTS（🔊 朗讀鈕）；原文為空時不發事件。AppController 訂閱。</summary>
    public event Action<string>? TtsRequested;

    /// <summary>「⚙」鈕請求開啟設定視窗（進行中先放棄結果由訂閱者處理）。AppController 訂閱。</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 於滑鼠位置附近（右移/下移 16px、clamp 進所在螢幕 WorkingArea）顯示彈窗：
    /// busy 點（Ink 色）與 <paramref name="statusText"/> 落在狀態條、譯文清空、
    /// 複製鈕禁用。帶 <paramref name="source"/> 時同步放進原文面板；不帶（null）則
    /// 清空原文面板（新請求語意）。已顯示中再次呼叫僅重新定位並更新狀態（重用同一視窗）。
    /// 重設內容入口：收起 toast 計時器（抓走彈窗後絕不可再自動關閉）。
    /// </summary>
    public void ShowTranslation(Point anchor, string statusText, string? source = null)
    {
        Location = ComputePosition(anchor);
        _result = null;
        _busy = true;
        _isShowingResult = false;
        _translationBox.Text = string.Empty; // 忙碌期間譯文清空，狀態由狀態條呈現
        _translationBox.ForeColor = Theme.Ink;
        _copyButton.Enabled = false;
        SetSource(source ?? string.Empty);
        SetStatusBar(statusText ?? string.Empty, showDot: true, Theme.Ink, Theme.MutedBtn);
        StopToastTimer();

        if (!Visible)
        {
            Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE：不會取得焦點
        }
    }

    /// <summary>
    /// 就地更新原文面板：不清譯文、不重定位、不動狀態條（合併 OCR 的原文與譯文同時
    /// 到位時，由 <see cref="SetText"/> 之前呼叫）。原文為空或僅空白時
    /// 「🔊 朗讀」「⧉ 複製原文」禁用。
    /// </summary>
    public void SetSource(string source)
    {
        // 顯示用字串需正規化（EDIT 控制項只渲染 CRLF；微軟正黑的 U+2019/U+2018 以
        // 全形前進寬度渲染成 "' s"——見 DisplayText）；原始字串另存 _source，
        // 複製原文與朗讀一律走 _source，剪貼簿與 TTS 不被顯示層代換污染。
        _source = source;
        _sourceBox.Text = DisplayText.Source(_source ?? string.Empty); // TextChanged 同步按鈕狀態與面板高度
        UpdateSourceButtons();
        UpdateSourcePanelHeight();
    }

    /// <summary>
    /// 以譯文更新譯文區。<paramref name="isError"/> 為 false 時（成功）以 Ink 色
    /// 顯示譯文、狀態條收起；<paramref name="result"/> 為 null 或空字串時
    /// 「📋 複製」保持禁用，非空時啟用（複製成功即關彈窗）。
    /// <paramref name="isError"/> 為 true 時（LLM/HTTP/OCR 等持續性錯誤）：譯文區以
    /// <see cref="Theme.Error"/> 色顯示完整訊息（可捲、看得完）、
    /// 狀態條以 Error 色點＋文字<b>持久顯示</b>（不自動關，需使用者關閉或下次動作
    /// 覆寫）、複製禁用、<paramref name="result"/> 視為無效（不會被複製）。兩級皆<b>不</b>
    /// 觸發 2500ms 計時器（暫時性提示走 <see cref="ShowToast"/>／
    /// <see cref="ShowTtsStatus"/>）。原文面板與相關按鈕狀態維持不變。
    /// </summary>
    public void SetText(string result, bool isError = false)
    {
        _result = isError ? null : result;
        _busy = false;
        _isShowingResult = true;
        _translationBox.Text = DisplayText.Translation(result); // EDIT 只渲染 CRLF；複製走原始 _result
        _translationBox.ForeColor = isError ? Theme.Error : Theme.Ink;

        if (isError)
        {
            // 持續性錯誤：狀態條 Error 色點＋文字持久顯示（不自動關）；完整訊息另放
            // 譯文區（可捲、看得完）——狀態條高 26px 僅容一線，長錯誤靠譯文區呈現。
            SetStatusBar(result ?? string.Empty, showDot: true, Theme.Error, Theme.Error);
        }
        else
        {
            HideStatusBar(); // 譯文就緒：狀態條任務結束（無狀態整條 Hide）
        }

        _copyButton.Enabled = !isError && !string.IsNullOrEmpty(_result);
    }

    /// <summary>
    /// 顯示提示文字（如「未選取文字」「選取文字超過 N 字上限」）並於 2500ms 後自動關閉
    /// （spec：顯示提示後自動關閉）。提示落在狀態條（muted、無點），不再塞進主文區；
    /// toast 覆蓋全部內容——清空原文面板與譯文區、停用全部文字類按鈕，但完整骨架
    /// （小標、空面板、狀態條、動作列）照樣呈現。定位/重用方式與
    /// <see cref="ShowTranslation"/> 相同（非 busy 狀態）；toast 期間改呼叫
    /// <see cref="ShowTranslation"/> 或手動關閉都會取消計時器，不會有殘留的自動關閉
    /// 計時或洩漏的 Timer。
    /// </summary>
    public void ShowToast(Point mousePos, string text)
    {
        Location = ComputePosition(mousePos);
        _result = null;
        _busy = false;
        _isShowingResult = false;
        _translationBox.Text = string.Empty;
        _translationBox.ForeColor = Theme.Ink;
        _copyButton.Enabled = false;
        SetSource(string.Empty); // toast 期間原文面板空白、相關鈕禁用（完整骨架照畫）
        SetStatusBar(text ?? string.Empty, showDot: false, Theme.Ink, Theme.MutedBtn);

        if (!Visible)
        {
            Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE：不會取得焦點
        }

        ArmToastTimer(); // 每次 toast 都重建計時器（重觸發時重置倒數）
    }

    /// <summary>
    /// 在狀態條顯示 TTS 狀態文字（初始化中／朗讀中／失敗原因；文案由 AppController
    /// 傳入，表單不硬編 TTS 文字）。不覆蓋譯文與原文、不觸發 toast、不自動關窗：
    /// <paramref name="text"/> 為 null 或空字串時整條狀態條收起；
    /// <paramref name="isError"/> 為 true 時以 Error 色點＋文字顯示並於 2500ms 後
    /// 自動關閉（沿用 toast timer 機制）；其餘為 muted 文字
    /// （如「TTS 初始化中…」「朗讀中…」，無點，持續顯示到下一步動作覆寫）。
    /// </summary>
    public void ShowTtsStatus(string? text, bool isError = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            StopToastTimer(); // 收起狀態條等同覆寫狀態：不留懸空的自動關閉計時
            HideStatusBar();
            return;
        }

        if (isError)
        {
            SetStatusBar(text, showDot: true, Theme.Error, Theme.Error);
            ArmToastTimer(); // 失敗提示 2500ms 後自動關閉（沿用 toast timer 機制）
        }
        else
        {
            StopToastTimer(); // 狀態被覆寫（朗讀中／初始化中）：先前的自動關閉倒數不得再關窗
            SetStatusBar(text, showDot: false, Theme.Ink, Theme.MutedBtn);
        }
    }

    /// <summary>toast 倒數結束：先停掉計時器再關閉表單。</summary>
    private void OnToastTick(object? sender, EventArgs e)
    {
        StopToastTimer();
        Close();
    }

    /// <summary>
    /// 重建自動關閉計時器（2500ms 後 Close，重觸發時重置倒數）。
    /// <see cref="ShowToast"/>／<see cref="ShowTtsStatus(string?,bool)"/> 的失敗分支
    /// 走此處；表單關閉/釋放必經 <see cref="StopToastTimer"/>。
    /// </summary>
    private void ArmToastTimer()
    {
        StopToastTimer();
        _toastTimer = new System.Windows.Forms.Timer
        {
            Interval = ToastAutoCloseMs,
        };
        _toastTimer.Tick += OnToastTick;
        _toastTimer.Start();
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

    /// <summary>目前譯文寫入剪貼簿後關閉；無譯文時直接關閉。</summary>
    private void OnCopyButtonClick(object? sender, EventArgs e)
    {
        CopyToClipboard(_result);
        Close(); // 複製即關彈窗（沿用）
    }

    /// <summary>目前原文寫入剪貼簿，但<b>不</b>關閉彈窗（核對原文多半還要看譯文）。</summary>
    private void OnSourceCopyButtonClick(object? sender, EventArgs e)
    {
        // 剪貼簿走原始字串（未經顯示層正規化），與「複製譯文」同套資料契約。
        CopyToClipboard(_source);
    }

    /// <summary>朗讀鈕：以「彈窗內的原文」（原始字串）請求 TTS；空原文不發事件。</summary>
    private void OnTtsButtonClick(object? sender, EventArgs e)
    {
        var source = _source ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(source))
        {
            TtsRequested?.Invoke(source);
        }
    }

    /// <summary>設定鈕：請求開啟設定視窗（進行中放棄結果由訂閱者處理）。</summary>
    private void OnSettingsButtonClick(object? sender, EventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    /// <summary>
    /// 兩顆複製鈕共用的剪貼簿 helper：剪貼簿被其他程序獨佔時 SetText 可能丟
    /// <see cref="ExternalException"/>，靜默略過不因此讓彈窗卡住。
    /// </summary>
    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
        }
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

        // 版面已完成：以正式寬度重算原文面板高度（建構期的暫估可能偏掉）。
        UpdateSourcePanelHeight();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _busy = false;
        _isShowingResult = false;
        HideStatusBar(); // 重用的彈窗不留殘留狀態（下次入口重新決定）
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
            _translationFont.Dispose();
            _captionFont.Dispose();
            if (!ReferenceEquals(_buttonFont, _font))
            {
                _buttonFont.Dispose(); // 退回彈窗字型（同一實例）時不重複釋放
            }

            _dismissHooked = false;
        }

        base.Dispose(disposing);
    }

    // ---- 狀態條 ------------------------------------------------------------

    /// <summary>套用狀態條內容（圓點/文字色）並顯示；無狀態的收起走 <see cref="HideStatusBar"/>。</summary>
    private void SetStatusBar(string text, bool showDot, Color dotColor, Color textColor)
    {
        _statusText.Text = text;
        _statusText.ForeColor = textColor;
        _statusDotColor = dotColor;
        _statusDot.Invalidate();
        _statusDot.Visible = showDot;
        _statusBar.Visible = true;
    }

    /// <summary>收起狀態條（無狀態時整條 Hide；隱藏的控制項不佔 dock 空間）。</summary>
    private void HideStatusBar()
    {
        _statusBar.Visible = false;
    }

    /// <summary>8px 圓點的繪製（反鋸齒橢圓填滿圓點範圍，顏色依狀態切換）。</summary>
    private void OnStatusDotPaint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_statusDotColor);
        e.Graphics.FillEllipse(brush, 0, 0, StatusDotSize - 1, StatusDotSize - 1);
    }

    /// <summary>原文面板 Hairline 邊框（四邊 1px 追框，畫在邊框外圈）。</summary>
    private void OnSourcePanelPaint(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Theme.Hairline);
        e.Graphics.DrawRectangle(pen, 0, 0, _sourcePanel.Width - 1, _sourcePanel.Height - 1);
    }

    // ---- 原文面板高度與按鈕狀態 --------------------------------------------

    /// <summary>原文文字變更：原文按鈕可用狀態與面板高度一起重算。</summary>
    private void OnSourceBoxTextChanged(object? sender, EventArgs e)
    {
        UpdateSourceButtons();
        UpdateSourcePanelHeight();
    }

    /// <summary>
    /// 依目前原文內容更新「🔊 朗讀」「⧉ 複製原文」按鈕：原文為空或僅空白時禁用
    /// （空字串/白字串語意按 spec；兩鈕共用同一判斷）。
    /// </summary>
    private void UpdateSourceButtons()
    {
        var hasSource = !string.IsNullOrWhiteSpace(_sourceBox.Text);
        _ttsButton.Enabled = hasSource;
        _sourceCopyButton.Enabled = hasSource;
    }

    /// <summary>
    /// 依原文內容換算並套用原文面板高度（Dock=Top 的 Height）：
    /// 短文不佔滿（只留小可觀的最小高度）、長文以彈窗高約 45% 為頂（內容自身可捲）。
    /// </summary>
    private void UpdateSourcePanelHeight()
    {
        // 面板可視寬度扣掉上下髮絲線、面板左右內距與一條常駐縱向捲軸（+ 安全餘裕）。
        int availableWidth = ClientSize.Width
            - _sourcePanel.Padding.Horizontal
            - HairlineThickness * 2
            - SystemInformation.VerticalScrollBarWidth
            - ScrollBarWidthSlack;
        int headerHeight = Math.Max(_sourceHeader.Height, HeaderFallbackHeight);
        int chromeHeight = headerHeight + _sourcePanel.Padding.Vertical + HairlineThickness * 2;
        int lineHeight = _sourceBox.Font.Height;
        int minHeight = lineHeight + chromeHeight; // 空文/短文的最小面板高：一線原文高
        int maxHeight = Math.Max(minHeight, (int)(ClientSize.Height * SourcePanelMaxFraction));

        _sourcePanel.Height = ComputeSourcePanelHeight(
            _sourceBox.Text, _sourceBox.Font, availableWidth, chromeHeight, minHeight, maxHeight);
    }

    /// <summary>
    /// 由原文內容換算原文面板目標高度（px）：以 <paramref name="availableWidth"/>
    /// 折行量測全文總高、另補半行 slack（量測與繪製是兩條程式路徑，遇軟連字符等
    /// 特殊字元可能低估；低估僅使面板偏短、原生捲軸保證內容可達，見類別註解），
    /// 加上 <paramref name="chromeHeight"/>（標列＋面板內距＋髮絲線）後
    /// clamp 於 [min, max]。抽為純函式以便於必要時單測 clamp 行為。
    /// </summary>
    internal static int ComputeSourcePanelHeight(string text, Font textFont, int availableWidth,
        int chromeHeight, int minHeight, int maxHeight)
    {
        var proposed = new Size(Math.Max(availableWidth, 1), int.MaxValue);
        var measured = TextRenderer.MeasureText(
            text ?? string.Empty, textFont, proposed,
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
        int slack = (textFont.Height + 1) / 2; // 半行量測保險（上限 clamp 兜底）
        int desired = measured.Height + slack + chromeHeight;

        return Math.Clamp(desired, minHeight, Math.Max(minHeight, maxHeight));
    }

    // ---- 建構 helper -------------------------------------------------------

    /// <summary>
    /// 多行唯讀文字區（原文與譯文共用樣式，僅背景/前景/字型不同）：
    /// 建構子內已設 ReadOnly，背景/前景排在 ReadOnly 之後，避免系統灰底蓋掉自訂色。
    /// </summary>
    private static TextBox CreateTextBox(Color backColor, Color foreColor, Font font)
    {
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
            Font = font,
            ForeColor = foreColor,
            BackColor = backColor,
        };
    }

    /// <summary>
    /// 幽靈文字鈕：無框（僅 hover 時浮出近似 mockup 的色塊）、MutedBtn 文字色、
    /// 自動依文字量寬。文字類幽靈鈕初值禁用（「⚙」另行開啟）。
    /// </summary>
    private Button CreateGhostButton(Color backColor, Color hoverBackColor)
    {
        var button = new Button
        {
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Theme.MutedBtn,
            UseVisualStyleBackColor = false,
            AutoSize = true,
            Margin = new Padding(0),
            Font = _buttonFont,
            Enabled = false, // 文字類按鈕初值禁用；⚙ 由呼叫端開啟
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hoverBackColor;
        button.FlatAppearance.MouseDownBackColor = hoverBackColor;
        return button;
    }

    /// <summary>1px 髮絲線（狀態條/動作列的上緣線，高度由常數決定）。</summary>
    private static Panel CreateHairline()
    {
        return new Panel
        {
            Dock = DockStyle.Top,
            Height = HairlineThickness,
            BackColor = Theme.Hairline,
            Margin = new Padding(0),
        };
    }

    /// <summary>建立指定字族、點數字級的字型；系統無該字族時回傳 null（呼叫端退回預設字族）。</summary>
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

    /// <summary>建立指定字族、像素字級的字型（小標 11px）；系統無該字族時回傳 null。</summary>
    private static Font? TryCreatePixelFont(string familyName, float size)
    {
        try
        {
            using var familyProbe = new Font(familyName, size, GraphicsUnit.Pixel);
            if (string.Equals(familyProbe.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return new Font(familyName, size, GraphicsUnit.Pixel);
            }
        }
        catch (ArgumentException)
        {
            // 字族不存在：Font 建構子會退回預設字族，以下 Name 比對已擋掉，僅防禦例外。
        }

        return null;
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