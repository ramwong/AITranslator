using System;
using System.Drawing;
using System.Windows.Forms;
using AITranslator.Models;
using AITranslator.Native;
using AITranslator.Services;
using AITranslator.UI;

namespace AITranslator;

/// <summary>
/// 應用程式控制器：整合系統匣圖示、隱藏訊息視窗與熱鍵服務、
/// <para><b>熱鍵流程</b>（全部在 UI 執行緒上執行）：
/// 既有請求進行中 → 先取消其 <see cref="CancellationTokenSource"/>；
/// 取得滑鼠位置（擷取前）→ <see cref="CaptureService.Capture"/>（同步，需 UI/STA 執行緒）→
/// <see cref="CaptureOutcome.NoSelection"/> 顯示「未選取文字」、
/// <see cref="CaptureOutcome.TooLong"/> 顯示長度上限提示（皆為 toast，2500ms 自動關閉）；
/// <see cref="CaptureOutcome.Success"/> 先顯示「翻譯中…」再 await 翻譯，
/// 完成後以 <see cref="PopupForm.SetText"/> 更新譯文或錯誤訊息。</para>
///
/// <para><b>過期回應防護</b>：每次熱鍵觸發遞增請求序號；await 返回後若序號已過期
/// （後續請求已接手彈窗）即丟棄結果，避免舊回應覆寫新彈窗或誤關新請求的彈窗。</para>
///
/// <para><b>設定變更</b>：<see cref="SettingsForm"/> 存檔後回呼帶回新設定 →
/// 重新註冊熱鍵（失敗顯示氣泡通知）、重建 <see cref="CaptureService"/>（新長度上限）、
/// 關閉舊彈窗使其以新的尺寸/字級重建。</para>
/// </summary>
public sealed class AppController : IDisposable
{
    private const string RegisterFailureBalloon = "快捷鍵註冊失敗，可能被其他程式占用";
    private const string NoSelectionText = "未選取文字";
    private const string TranslatingText = "翻譯中…";
    private const string EmptyTranslationText = "未取得翻譯內容";

    /// <summary>翻譯熱鍵的註冊 id（RegisterHotKey / WM_HOTKEY WParam 皆以此識別）。</summary>
    private const int HotkeyIdTranslate = 1;

    /// <summary>朗讀（TTS）熱鍵的註冊 id；處理邏輯於後續 Task 實作。</summary>
    private const int HotkeyIdTts = 2;

    private readonly string _settingsPath;
    private readonly MessageWindow _window;
    private readonly HotkeyService _hotkeyService;
    private readonly OllamaClient _ollamaClient;
    private readonly NotifyIcon _notifyIcon;

    private AppSettings _settings;
    private CaptureService _captureService;
    private PopupForm? _popup;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private int _requestId; // 每次熱鍵觸發遞增；過期請求的回應直接丟棄

    public AppController(AppSettings settings, string settingsPath)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _captureService = new CaptureService(_settings.MaxTextLength);
        _ollamaClient = new OllamaClient(new HttpClient());

        _window = new MessageWindow(OnHotkeyDispatch);
        _hotkeyService = new HotkeyService(_window);
        _notifyIcon = CreateNotifyIcon();

        if (!_hotkeyService.Register(HotkeyIdTranslate, _settings.Hotkey))
        {
            ShowRegisterFailureBalloon();
        }
    }

    /// <summary>建立系統匣圖示與右鍵選單（設定 / 離開）。</summary>
    private NotifyIcon CreateNotifyIcon()
    {
        var icon = new NotifyIcon
        {
            Text = "AITranslator",
            Icon = SystemIcons.Application,
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("設定", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("離開", null, (_, _) => Exit());

        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ShowSettings();
        return icon;
    }

    /// <summary>開啟（強制回）設定對話框；存檔成功後由回呼帶回新設定。</summary>
    private void ShowSettings()
    {
        using var form = new SettingsForm(_settingsPath, OnSettingsChanged);
        form.ShowDialog();
    }

    /// <summary>設定存檔後的套用：熱鍵重新註冊、擷取上限、彈窗尺寸/字級重建。</summary>
    private void OnSettingsChanged(AppSettings newSettings)
    {
        _settings = newSettings;
        _captureService = new CaptureService(newSettings.MaxTextLength);

        if (!_hotkeyService.ReRegister(HotkeyIdTranslate, newSettings.Hotkey))
        {
            ShowRegisterFailureBalloon();
        }

        DiscardPopup();
    }

    private void ShowRegisterFailureBalloon()
    {
        _notifyIcon.ShowBalloonTip(3000, "AITranslator", RegisterFailureBalloon, ToolTipIcon.Warning);
    }

    /// <summary>
    /// WM_HOTKEY 分派（UI 執行緒）：依註冊 id 導向對應流程。
    /// <see cref="HotkeyIdTranslate"/> 走翻譯流程；<see cref="HotkeyIdTts"/> 目前為佔位
    /// （朗讀流程由後續 Task 實作），收到時不做事。
    /// </summary>
    private void OnHotkeyDispatch(int hotkeyId)
    {
        switch (hotkeyId)
        {
            case HotkeyIdTranslate:
                OnHotkey();
                break;

            case HotkeyIdTts:
                // 佔位：朗讀流程由後續 Task 實作
                return;
        }
    }

    /// <summary>
    /// 翻譯熱鍵流程（UI 執行緒）：取消進行中的請求 → 同步擷取選取文字 →
    /// 依結果顯示 toast 或「翻譯中…」後啟動翻譯。
    /// 擷取（Clipboard + SendInput）必須同步在 UI 執行緒完成，await 之後才開始。
    /// </summary>
    private void OnHotkey()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        _requestId++;

        if (_popup != null && _popup.IsDisposed)
        {
            _popup = null;
        }

        var mouse = Cursor.Position; // 擷取前先記下位置（Capture 期間游標可能移動）

        var (outcome, text) = _captureService.Capture();
        if (outcome == CaptureOutcome.NoSelection)
        {
            EnsurePopup().ShowToast(mouse, NoSelectionText); // toast：2500ms 自動關閉
            return;
        }

        if (outcome == CaptureOutcome.TooLong)
        {
            EnsurePopup().ShowToast(mouse, $"選取文字超過 {_settings.MaxTextLength} 字上限");
            return;
        }

        EnsurePopup().ShowTranslation(mouse, TranslatingText);
        TranslateAndShowAsync(text, _requestId, NewCts().Token);
    }

    /// <summary>建立新的取消來源並指派給 _cts（供後續熱鍵觸發時取消）。</summary>
    private CancellationTokenSource NewCts()
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        return cts;
    }

    /// <summary>
    /// await 翻譯結果並更新彈窗。以請求序號防護過期回應；
    /// 整段包 try/catch，未預期例外只記到主控台，不讓 UI 執行緒中斷。
    /// </summary>
    private async void TranslateAndShowAsync(string text, int requestId, CancellationToken ct)
    {
        try
        {
            var result = await _ollamaClient.TranslateAsync(_settings, text, ct);

            if (requestId != _requestId)
            {
                return; // 已有更新請求接手（其取消來源已觸發），丟棄過期回應
            }

            if (result.Success)
            {
                DeliverToPopup(string.IsNullOrEmpty(result.Text) ? EmptyTranslationText : result.Text);
            }
            else if (result.Error.Length == 0)
            {
                // Error 為空 → 使用者取消；關閉彈窗。
                DiscardPopup();
            }
            else
            {
                DeliverToPopup(result.Error);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AITranslator: 翻譯流程未預期例外：{ex}");
        }
    }

    /// <summary>把譯文/錯誤訊息送到現存彈窗；彈窗已被使用者關閉則丟棄訊息。</summary>
    private void DeliverToPopup(string message)
    {
        if (_popup == null || _popup.IsDisposed)
        {
            return;
        }

        _popup.SetText(message);
    }

    /// <summary>取得可用的彈窗實例；舊實例已關閉（Disposed）則以目前設定重建。</summary>
    private PopupForm EnsurePopup()
    {
        if (_popup == null || _popup.IsDisposed)
        {
            _popup = new PopupForm(_settings);
        }

        return _popup;
    }

    /// <summary>關閉並捨棄現存彈窗（Close 即 Dispose，讓下次以新設定重建）。</summary>
    private void DiscardPopup()
    {
        if (_popup == null)
        {
            return;
        }

        if (!_popup.IsDisposed)
        {
            _popup.Close();
        }

        _popup = null;
    }

    /// <summary>離開：取消進行中請求、解除熱鍵、移除系統匣圖示、結束訊息迴圈。</summary>
    private void Exit()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        DiscardPopup();
        _hotkeyService.Unregister(HotkeyIdTranslate);
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 正常離開走 Exit()；此處僅做防禦性清理（例如未經選單的關閉路徑）。
        _disposed = true;
        _hotkeyService.Unregister(HotkeyIdTranslate);
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}