using System;
using System.Drawing;
using System.Windows.Forms;
using AITranslator.Models;
using AITranslator.Native;

namespace AITranslator.UI;

/// <summary>
/// 無啟動（no-activate）翻譯結果彈窗：
/// 熱鍵觸發後於滑鼠附近顯示「翻譯中…」狀態，譯文就緒後以 <see cref="SetText"/> 更新內容，
/// 點擊「複製」按鈕把目前譯文寫入剪貼簿後關閉。
///
/// <para><b>不搶焦點</b>：<see cref="FormBorderStyle"/> 為 None，
/// <see cref="Form.ShowWithoutActivation"/> 回傳 true，並於 <see cref="CreateParams"/>
/// 疊加 <c>WS_EX_NOACTIVATE</c>｜<c>WS_EX_TOOLWINDOW</c>｜<c>WS_EX_TOPMOST</c>——
/// 顯示時與滑鼠點擊視窗本身都不會取得焦點，使用者打字中的應用程式不受干擾
/// （點擊「複製」按鈕以程式方式寫剪貼簿，不經由選取）。</para>
///
/// <para><b>定位</b>：<see cref="ShowTranslation"/> 以滑鼠位置右移 16px、下移 16px 為
/// 左上角，並 clamp 進 <see cref="Screen.FromPoint(Point)"/> 所在螢幕的 WorkingArea
/// （多螢幕以滑鼠所在螢幕為準），彈窗永不溢出可視範圍。</para>
///
/// <para><b>捲動</b>：譯文 Label 放在 <see cref="Panel"/>（AutoScroll=true、Dock=Fill）
/// 內，Label AutoSize 依內容換行長高；譯文超過彈窗高度時出現縱向捲軸而不被裁切。</para>
///
/// <para><b>關閉</b>：第一次顯示時啟用 <see cref="DismissHook"/>
/// （點擊彈窗以外任意處或按 Esc → Close）；<see cref="Form.OnClosed"/> 對應呼叫
/// <see cref="DismissHook.End"/> 解除 hook 並重設 <c>_dismissHooked</c>
/// （關閉後被重用的彈窗會於 OnShown 重新安裝）。已顯示中再次呼叫 <see cref="ShowTranslation"/>
/// 僅重新定位並更新狀態文字（重用同一視窗，不重複 Begin）。</para>
///
/// <para><b>提示（toast）</b>：<see cref="ShowToast"/> 顯示提示文字（如「未選取文字」）
/// 後 2500ms 自動關閉；期間改以 <see cref="ShowTranslation"/> 重用彈窗即取消自動關閉，
/// 手動關閉亦於 <see cref="OnClosed"/> 清掉計時器。</para>
///
/// <para><see cref="IsBusy"/> 於 <see cref="ShowTranslation"/> 起為 true，
/// 直到 <see cref="SetText"/> 或關閉。「複製」按鈕在取得非空譯文前保持禁用。</para>
/// </summary>
public class PopupForm : Form
{
    private const int Offset = 16; // 滑鼠位置到彈窗左上角的方向偏移（px）
    private const int ToastAutoCloseMs = 2500; // toast 顯示多久後自動關閉（spec：顯示提示後自動關閉）

    private readonly Panel _scrollPanel;
    private readonly Label _label;
    private readonly Button _copyButton;
    private readonly Font _font;
    private System.Windows.Forms.Timer? _toastTimer; // toast 自動關閉計時器；ShowTranslation 重用時取消
    private string? _result;   // 目前譯文；SetText 前為 null
    private bool _busy;        // ShowTranslation → SetText/Close 之間為 true
    private bool _dismissHooked;

    /// <summary>以設定（彈窗尺寸、字級）建立彈窗，初始為隱藏。</summary>
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

        _font = new Font(Control.DefaultFont.FontFamily, settings.FontSize);

        // 捲動容器：譯文超過彈窗可視高度時出現縱向捲軸（Label 不再被裁切）。
        _scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
        };
        _scrollPanel.ClientSizeChanged += OnScrollPanelClientSizeChanged;

        // AutoSize + MaximumSize（寬=面板可視寬、高不限）：文字依寬度換行、長度不限，
        // 超過面板高度時由 AutoScroll 捲動露出。Label 不得 Dock：
        // 被 dock 的子控制項會被拘限在可視區內，AutoScroll 不會為它出捲軸。
        _label = new Label
        {
            AutoSize = true,
            TextAlign = ContentAlignment.TopLeft,
            Location = new Point(12, 12), // 內距改以位移表示（AutoSize Label 的原 Padding 即視覺內距）
            Font = _font,
        };
        _scrollPanel.Controls.Add(_label);

        _copyButton = new Button
        {
            Text = "複製",
            Dock = DockStyle.Bottom,
            Height = 36,
            Enabled = false, // 取得非空譯文前不可複製
        };
        _copyButton.Click += OnCopyButtonClick;

        // 先加入 Fill 的 Panel，再加入 Dock=Bottom 的 Button：
        // WinForms 由集合尾端起 dock，Button 先佔底部，Panel 才填滿其餘區域。
        Controls.Add(_scrollPanel);
        Controls.Add(_copyButton);
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

    /// <summary>
    /// 於滑鼠位置附近（右移/下移 16px、clamp 進所在螢幕 WorkingArea）顯示彈窗，
    /// 內容為狀態文字（「翻譯中…」或錯誤訊息）。已顯示中再次呼叫僅重新定位並更新狀態。
    /// </summary>
    public void ShowTranslation(Point mousePos, string statusText)
    {
        Location = ComputePosition(mousePos);
        _result = null;
        _busy = true;
        _label.Text = statusText ?? string.Empty;
        _copyButton.Enabled = false;
        StopToastTimer(); // 抓走 toast 彈窗改顯譯文/狀態後，絕不可再自動關閉
        UpdateLabelWidth();

        if (!Visible)
        {
            Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE：不會取得焦點
        }
    }

    /// <summary>
    /// 顯示提示文字（如「未選取文字」「選取文字超過 N 字上限」）並於 2500ms 後自動關閉
    /// （spec：顯示提示後自動關閉）。定位/重用方式與 <see cref="ShowTranslation"/> 相同
    /// （非 busy 狀態、複製鈕禁用）；toast 期間改呼叫 <see cref="ShowTranslation"/> 或
    /// 手動關閉都會取消計時器，不會有殘留的自動關閉計時或洩漏的 Timer。
    /// </summary>
    public void ShowToast(Point mousePos, string text)
    {
        Location = ComputePosition(mousePos);
        _result = null;
        _busy = false;
        _label.Text = text ?? string.Empty;
        _copyButton.Enabled = false;
        UpdateLabelWidth();

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
    /// 依捲動面板可視寬度限制 Label 寬（高度不限，0 = 無上限），
    /// 讓文字換行並在過長時觸發 AutoScroll 縱向捲軸。
    /// </summary>
    private void UpdateLabelWidth()
    {
        int width = Math.Max(1, _scrollPanel.ClientSize.Width - 24); // 左右各 12px 內距
        if (_label.MaximumSize.Width != width)
        {
            _label.MaximumSize = new Size(width, 0);
        }
    }

    private void OnScrollPanelClientSizeChanged(object? sender, EventArgs e)
    {
        // 縱向捲軸出現/消失會改變可視寬，Label 寬度隨之調整（避免橫向捲軸）。
        UpdateLabelWidth();
    }

    /// <summary>
    /// 以譯文更新內容並允許「複製」。譯文為空字串或 null 時按鈕保持禁用。
    /// </summary>
    public void SetText(string result)
    {
        _result = result;
        _busy = false;
        _label.Text = result ?? string.Empty;
        _copyButton.Enabled = !string.IsNullOrEmpty(result);
        UpdateLabelWidth(); // 防禦：確保長譯文觸發捲動而非被裁切
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
            _dismissHooked = false;
        }

        base.Dispose(disposing);
    }

    /// <summary>目前譯文寫入剪貼簿後關閉；無譯文時直接關閉。</summary>
    private void OnCopyButtonClick(object? sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_result))
        {
            try
            {
                // 剪貼簿被其他程序獨佔時 SetText 可能丟 ExternalException；不因此讓彈窗卡住。
                Clipboard.SetText(_result);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
        }

        Close();
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