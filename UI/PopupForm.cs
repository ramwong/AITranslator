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
/// <para><b>關閉</b>：第一次顯示時啟用 <see cref="DismissHook"/>
/// （點擊彈窗以外任意處或按 Esc → Close）；<see cref="Form.OnClosed"/> 對應呼叫
/// <see cref="DismissHook.End"/> 解除 hook。已顯示中再次呼叫 <see cref="ShowTranslation"/>
/// 僅重新定位並更新狀態文字（重用同一視窗，不重複 Begin）。</para>
///
/// <para><see cref="IsBusy"/> 於 <see cref="ShowTranslation"/> 起為 true，
/// 直到 <see cref="SetText"/> 或關閉。「複製」按鈕在取得非空譯文前保持禁用。</para>
/// </summary>
public class PopupForm : Form
{
    private const int Offset = 16; // 滑鼠位置到彈窗左上角的方向偏移（px）

    private readonly Label _label;
    private readonly Button _copyButton;
    private readonly Font _font;
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

        _label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(12),
            Font = _font,
        };

        _copyButton = new Button
        {
            Text = "複製",
            Dock = DockStyle.Bottom,
            Height = 36,
            Enabled = false, // 取得非空譯文前不可複製
        };
        _copyButton.Click += OnCopyButtonClick;

        // 先加入 Fill 的 Label，再加入 Dock=Bottom 的 Button：
        // WinForms 由集合尾端起 dock，Button 先佔底部，Label 才填滿其餘區域。
        Controls.Add(_label);
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

        if (!Visible)
        {
            Show(); // ShowWithoutActivation + WS_EX_NOACTIVATE：不會取得焦點
        }
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
        DismissHook.End();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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