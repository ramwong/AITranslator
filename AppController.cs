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
/// 關閉舊彈窗使其以新的尺寸/字級重建。
/// TTS 熱鍵僅在其值變更時重新註冊（其餘設定變更不影響已註冊的朗讀熱鍵）。</para>
///
/// <para><b>朗讀（TTS）流程</b>（熱鍵處理同樣在 UI 執行緒）：
/// 先記滑鼠位置 → <see cref="CaptureService.Capture"/>（同步，先於任何模型載入——
/// Capture 使用實體剪貼簿，EnsureLoaded 不得搶在其前）→
/// 狀態 <see cref="TtsState.Loading"/>（或剛 EnsureLoaded）顯示「TTS 初始化中…」不重複觸發；
/// <see cref="TtsState.Ready"/> → <see cref="TtsService.Toggle"/> 開始串流出聲（靜默）；
/// <see cref="TtsState.Speaking"/>（或 Stopping）→ <see cref="TtsService.Toggle"/> 停止（靜默）；
/// <see cref="TtsState.Failed"/> → toast 上次失敗原因，並重試載入。
/// 首次按壓（狀態 <see cref="TtsState.NotLoaded"/>／Failed）：擷取到的文字存進
/// <c>_pendingTtsText</c> 後 <see cref="TtsService.EnsureLoaded"/>，待
/// <see cref="TtsService.StateChanged"/> 報 Ready 時再 Toggle 播放。
/// <see cref="TtsService"/> 的引擎工廠於首次使用（背景載入 worker）時才
/// <see cref="TtsAssetStore.EnsureExtracted"/> → <see cref="Native.PttsNative.RegisterResolver"/>，
/// 解壓失敗轉 <see cref="TtsState.Failed"/> 帶回訊息。</para>
///
/// <para><b>TTS 狀態封送</b>：<see cref="TtsService.StateChanged"/> 於轉移發生執行緒
/// 同步引發（可能為執行緒集區／播放 worker，也可能在 TtsService 鎖內），
/// Handler 僅做 <see cref="SynchronizationContext.Post"/>（於熱鍵處理時擷取 UI 執行緒的
/// <see cref="WindowsFormsSynchronizationContext"/>）封送回 UI 執行緒才觸碰 UI 狀態，
/// 且絕不在 Handler 內同步呼叫 <see cref="TtsService.Dispose"/>。</para>
/// </summary>
public sealed class AppController : IDisposable
{
    private const string RegisterFailureBalloon = "快捷鍵註冊失敗，可能被其他程式占用";
    private const string NoSelectionText = "未選取文字";
    private const string TranslatingText = "翻譯中…";
    private const string EmptyTranslationText = "未取得翻譯內容";
    private const string TtsInitializingText = "TTS 初始化中…";
    private const string TtsFailedText = "TTS 失敗：{0}";
    private const string TtsFailedUnknownReason = "未知原因";

    /// <summary>翻譯熱鍵的註冊 id（RegisterHotKey / WM_HOTKEY WParam 皆以此識別）。</summary>
    private const int HotkeyIdTranslate = 1;

    /// <summary>朗讀（TTS）熱鍵的註冊 id（RegisterHotKey / WM_HOTKEY WParam 皆以此識別）。</summary>
    private const int HotkeyIdTts = 2;

    private readonly string _settingsPath;
    private readonly MessageWindow _window;
    private readonly HotkeyService _hotkeyService;
    private readonly OllamaClient _ollamaClient;
    private readonly NotifyIcon _notifyIcon;
    private readonly TtsAssetStore _assetStore;
    private readonly TtsService _ttsService;

    private AppSettings _settings;
    private CaptureService _captureService;
    private PopupForm? _popup;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private int _requestId; // 每次熱鍵觸發遞增；過期請求的回應直接丟棄（翻譯專用，與 TTS 流程無關）

    /// <summary>
    /// 首次按壓（NotLoaded/Failed 態）擷取到的待播文字；待 Loading→Ready 轉移時播放並清空。
    /// 刻意不與翻譯的 <c>_requestId</c>/<c>_cts</c> 共用任何狀態。
    /// </summary>
    private string? _pendingTtsText;

    /// <summary>
    /// UI 執行緒的 <see cref="SynchronizationContext"/>（Windows Forms；於熱鍵處理時擷取，
    /// 供 <see cref="OnTtsStateChanged"/> 在 worker 執行緒上封送回 UI）。null = 尚未擷取
    /// （未曾按過 TTS 熱鍵前不會有 Failed/Ready 轉移，無事可封送）。
    /// </summary>
    private SynchronizationContext? _uiContext;

    public AppController(AppSettings settings, string settingsPath)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _captureService = new CaptureService(_settings.MaxTextLength);
        _ollamaClient = new OllamaClient(new HttpClient());

        // TTS：資產解壓與引擎建立皆延後到首次使用（EnsureLoaded 的背景載入 worker），
        // 建構後不做任何 IO、也不觸碰 ptts_*。modelDir 路徑由 cacheDir 推得（與
        // EnsureExtracted 回傳值一致），故可直接在此組好 TtsService。
        var ttsCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AITranslator",
            "tts");
        _assetStore = new TtsAssetStore(
            ttsCacheDir,
            asset => typeof(AppController).Assembly.GetManifestResourceStream(
                TtsAssetStore.ManifestResourceName(asset)), // LogicalName 映射（含子目錄反斜線形式）
            TtsAssetStore.InternalStamp);
        _ttsService = new TtsService(
            Path.Combine(ttsCacheDir, "model"),
            CreateTtsEngine,
            () => new WaveOutSink());
        _ttsService.StateChanged += OnTtsStateChanged;

        _window = new MessageWindow(OnHotkeyDispatch);
        _hotkeyService = new HotkeyService(_window);
        _notifyIcon = CreateNotifyIcon();

        if (!_hotkeyService.Register(HotkeyIdTranslate, _settings.Hotkey))
        {
            ShowRegisterFailureBalloon();
        }

        if (!_hotkeyService.Register(HotkeyIdTts, _settings.TtsHotkey))
        {
            ShowRegisterFailureBalloon(); // 與翻譯熱鍵共用同一則氣泡通知
        }
    }

    /// <summary>
    /// TtsService 的引擎工廠（於背景載入 worker 呼叫，UI 執行緒不卡）：
    /// 先 <see cref="TtsAssetStore.EnsureExtracted"/>（取得 dll/model 實際位置；失敗拋
    /// <see cref="IOException"/>，由 TtsService 轉 <see cref="TtsState.Failed"/> 帶回訊息），
    /// 之後、且在任何 ptts_* 呼叫之前 <see cref="Native.PttsNative.RegisterResolver"/>
    /// （.NET 會快取 DllImport 載入失敗，解析器必須在首次載入嘗試前指好 dll 資料夾），
    /// 最後才建立 <see cref="PttsEngine"/>。
    /// </summary>
    private IPttsEngine CreateTtsEngine()
    {
        var cacheDir = _assetStore.EnsureExtracted();
        PttsNative.RegisterResolver(cacheDir);
        return new PttsEngine();
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
        var oldTtsHotkey = _settings.TtsHotkey;
        _settings = newSettings;
        _captureService = new CaptureService(newSettings.MaxTextLength);

        if (!_hotkeyService.ReRegister(HotkeyIdTranslate, newSettings.Hotkey))
        {
            ShowRegisterFailureBalloon();
        }

        // TTS 熱鍵僅在值變更時重新註冊（其餘設定變更不動朗讀熱鍵）
        if (!string.Equals(newSettings.TtsHotkey, oldTtsHotkey, StringComparison.Ordinal)
            && !_hotkeyService.ReRegister(HotkeyIdTts, newSettings.TtsHotkey))
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
    /// <see cref="HotkeyIdTranslate"/> 走翻譯流程；<see cref="HotkeyIdTts"/> 走朗讀流程。
    /// </summary>
    private void OnHotkeyDispatch(int hotkeyId)
    {
        switch (hotkeyId)
        {
            case HotkeyIdTranslate:
                OnHotkey();
                break;

            case HotkeyIdTts:
                OnTtsHotkey();
                return;
        }
    }

    /// <summary>
    /// 朗讀熱鍵流程（UI 執行緒）：記滑鼠位置 → 同步擷取選取文字（先於任何模型載入——
    /// Capture 操作實體剪貼簿，EnsureLoaded 不得搶在其前，也不因載入而二次擷取）→
    /// 依 <see cref="CaptureOutcome"/> 與 <see cref="TtsService.State"/> 分派：
    /// <list type="bullet">
    /// <item>NoSelection/TooLong：沿用既有 toast 文案（位置為按壓時的游標）。</item>
    /// <item><see cref="TtsState.Loading"/>（或 Stopping）：toast「TTS 初始化中…」，不重複觸發、
    ///  也不覆寫待播文字（首次按壓的文字保留給即將到來的 Ready）。</item>
    /// <item><see cref="TtsState.NotLoaded"/>：首次按壓——文字存進 <c>_pendingTtsText</c> 後
    ///  <see cref="TtsService.EnsureLoaded"/>，toast「TTS 初始化中…」；Ready 轉移時才播放。</item>
    /// <item><see cref="TtsState.Failed"/>：toast 上次失敗原因（brief 的 Failed 分支），隨後重試載入
    ///  （brief 未定；依任務指示「NotLoaded/Failed 皆 EnsureLoaded 以免文字遺失」，且
    ///  <see cref="TtsService.Toggle"/> 於 Failed 態本就先載入）——新擷取的文字同樣存待播。</item>
    /// <item><see cref="TtsState.Ready"/> → <see cref="TtsService.Toggle"/> 開始播放（靜默）；
    ///  <see cref="TtsState.Speaking"/> → <see cref="TtsService.Toggle"/> 停止（靜默）。</item>
    /// </list>
    /// </summary>
    private void OnTtsHotkey()
    {
        _uiContext ??= SynchronizationContext.Current; // 擷取 UI 執行緒上下文，供 StateChanged 封送

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

        switch (_ttsService.State)
        {
            case TtsState.Loading:
            case TtsState.Stopping:
                EnsurePopup().ShowToast(mouse, TtsInitializingText); // 不重複觸發，也不換待播文字
                return;

            case TtsState.NotLoaded:
                // 首次按壓：先備妥文字再載入（載入在背景）；Ready 後由 OnTtsStateChangedUi
                // 播放。順序刻意為「存文字 → EnsureLoaded」，讓載入失敗（Failed 轉移）必然
                // 發生在文字就緒之後，不遺失、不說過期文字。
                _pendingTtsText = text;
                _ttsService.EnsureLoaded();
                EnsurePopup().ShowToast(mouse, TtsInitializingText);
                return;

            case TtsState.Failed:
                // brief 的 Failed 分支：toast 上次失敗原因；此後重試載入並備妥新文字
                // （任務指示：Failed 態按壓同樣 EnsureLoaded，文字才不會在重試完成時遺失）
                _pendingTtsText = text;
                _ttsService.EnsureLoaded();
                EnsurePopup().ShowToast(mouse, string.Format(TtsFailedText,
                    string.IsNullOrEmpty(_ttsService.LastError) ? TtsFailedUnknownReason : _ttsService.LastError));
                return;

            case TtsState.Ready:
            case TtsState.Speaking:
                _ttsService.Toggle(text); // Ready → Speaking 出聲；Speaking（/Stopping）→ 停止。皆靜默
                return;
        }
    }

    /// <summary>
    /// <see cref="TtsService.StateChanged"/>：可能於執行緒集區／播放 worker（甚至 TtsService
    /// 鎖內）同步引發——僅做封送（<see cref="System.Threading.SynchronizationContext.Post"/>），
    /// 不觸碰 UI、不做阻擋式呼叫，也<b>絕不同步呼叫 <see cref="TtsService.Dispose"/></b>
    /// （Task 6 review 契約）。UI 執行緒上下文尚未擷取前不會有 Failed/Ready 轉移（載入只能
    /// 由熱鍵觸發），故 <c>_uiContext</c> 為 null 時直接忽略。
    /// </summary>
    private void OnTtsStateChanged(TtsState state)
    {
        _uiContext?.Post(_ => OnTtsStateChangedUi(state), null);
    }

    /// <summary>
    /// 封送回 UI 執行緒後的 TTS 狀態處理：<see cref="TtsState.Failed"/> 立即以「當下」游標
    /// 位置 toast 失敗原因並丟棄待播文字（絕不說出過期文字）；
    /// <see cref="TtsState.Ready"/>：若仍有首次按壓備妥的文字且服務仍在 Ready（未被他按壓推進），
    /// 即刻 Toggle 播放；其餘 Ready 轉移（自然播完、停止完成）與其他狀態皆靜默。
    /// </summary>
    private void OnTtsStateChangedUi(TtsState state)
    {
        if (_disposed)
        {
            return;
        }

        switch (state)
        {
            case TtsState.Failed:
                _pendingTtsText = null;
                var error = _ttsService.LastError;
                EnsurePopup().ShowToast(
                    Cursor.Position, // 失敗通報以顯示當下（而非按壓時）的游標位置
                    string.IsNullOrEmpty(error)
                        ? string.Format(TtsFailedText, TtsFailedUnknownReason)
                        : string.Format(TtsFailedText, error));
                return;

            case TtsState.Ready:
                // 先取走並清空待播文字：消費至多一次，後到的重複 Ready 不會又說一遍
                var pending = _pendingTtsText;
                _pendingTtsText = null;
                if (pending is not null && _ttsService.State == TtsState.Ready)
                {
                    _ttsService.Toggle(pending);
                }

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

    /// <summary>
    /// 離開：先停止串流並釋放 TTS（先 <see cref="TtsService.Dispose"/>——內部為 sink.Stop ＋
    /// worker Join(2000) 的阻擋式收尾，須在結束訊息迴圈前完成），再走既有清理：
    /// 取消進行中翻譯請求、解除熱鍵、移除系統匣圖示、結束訊息迴圈。
    /// </summary>
    private void Exit()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ttsService.StateChanged -= OnTtsStateChanged; // 離開後不再有任何狀態封送
        _ttsService.Dispose(); // TTS 先收尾（brief 指定順序），其 NotLoaded 轉移已不再訂閱
        _pendingTtsText = null;
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        DiscardPopup();
        _hotkeyService.Unregister(HotkeyIdTranslate);
        _hotkeyService.Unregister(HotkeyIdTts);
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
        // TTS 同樣先收尾（停止串流、釋放引擎）再做其餘清理。
        _disposed = true;
        _ttsService.StateChanged -= OnTtsStateChanged;
        _ttsService.Dispose();
        _pendingTtsText = null;
        _hotkeyService.Unregister(HotkeyIdTranslate);
        _hotkeyService.Unregister(HotkeyIdTts);
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}