using AITranslator.Native;

namespace AITranslator.Services;

/// <summary>
/// 串流 TTS 狀態機：熱鍵一按播放、再按即停。
///
/// <para>狀態流轉（詳見各方法）：<c>NotLoaded/Failed ──EnsureLoaded──▶ Loading ──載入完成──▶ Ready</c>；
/// <c>Ready ──Toggle──▶ Speaking（worker thread 跑 engine.Speak）</c>；
/// <c>Speaking ──Stop──▶ Stopping ──worker 收尾──▶ Ready</c>。
/// 內部以單一私有鎖守護狀態轉移；播放 worker 的引擎回呼以停止旗標決定
/// 續播（sink.Write + true）或請求引擎停止（false）。</para>
///
/// <para><see cref="StateChanged"/> 於<b>發生轉移的執行緒上同步</b>觸發（含鎖內引發，
/// 故 Handler 內同步再呼叫本類別方法不會漏看狀態）。載入完成/失敗與播放結束
/// 分別於執行緒集區執行緒與播放 worker 上引發——訂閱者若需更新 UI 必須自行
/// 封送（<c>Control.BeginInvoke</c> 等），不可假設事件總在 UI 執行緒。</para>
/// </summary>
public class TtsService : IDisposable
{
    /// <summary>守護所有狀態轉移；任何阻擋式呼叫（sink.Stop、engine.Speak、Join）一律不在此鎖內。</summary>
    private readonly object _lock = new();

    private readonly string _modelDir;
    private readonly Func<IPttsEngine> _engineFactory;
    private readonly Func<IAudioSink> _sinkFactory;

    /// <summary>已載入的引擎；null = 尚未載入成功（載入 worker 於完成時指派）。</summary>
    private IPttsEngine? _engine;

    /// <summary>本次播放的 sink（每次播放由工廠新建一份： sink.Stop 為終止語意、實例不可重播）。</summary>
    private volatile IAudioSink? _sink;

    /// <summary>進行中的播放 worker；null = 無。</summary>
    private Thread? _speakWorker;

    private TtsState _state = TtsState.NotLoaded;
    private string? _lastError;
    private bool _disposed;

    /// <summary>停止旗標：引擎回呼看到即跳過播放並回 false（native 情境絕不擲例外）。</summary>
    private volatile bool _stopRequested;

    /// <summary>狀態變更事件：於轉移發生的執行緒同步引發（執行緒契約見類別註解）。</summary>
    public event Action<TtsState>? StateChanged;

    /// <remarks>屬 internal 因參數涉及 internal 型別 IAudioSink；僅供同組件（AppController）使用。</remarks>
    internal TtsService(string modelDir, Func<IPttsEngine> engineFactory, Func<IAudioSink> sinkFactory)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _sinkFactory = sinkFactory ?? throw new ArgumentNullException(nameof(sinkFactory));
    }

    /// <summary>目前狀態。</summary>
    public TtsState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>最近一次失敗原因（載入/播放失敗時帶回給 UI；成功情境為 null）。</summary>
    public string? LastError
    {
        get { lock (_lock) return _lastError; }
    }

    /// <summary>
    /// 確保引擎已載入：NotLoaded/Failed → 轉 Loading 並於背景 Task.Run 載入（worker 自行
    /// 收尾轉 Ready/Failed）；Loading/Ready/Speaking/Stopping → 無動作（冪等，不重複載入）。
    /// </summary>
    public void EnsureLoaded()
    {
        lock (_lock)
        {
            EnsureLoadedLocked();
        }
    }

    /// <summary>
    /// 熱鍵語意：Ready → 開始播放（新建 sink、worker thread 跑 engine.Speak）；
    /// Speaking/Stopping → 停止（<see cref="Stop"/>）；Loading → 無動作（載入中的按壓刻意
    /// 丟棄：不排隊、不重複載入）；NotLoaded/Failed → 先載入（<see cref="EnsureLoaded"/>）。
    /// </summary>
    public void Toggle(string text)
    {
        IAudioSink? sinkToStop = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            switch (_state)
            {
                case TtsState.Ready:
                    StartSpeakLocked(text);
                    return;

                case TtsState.Speaking:
                case TtsState.Stopping:
                    // 只在鎖內設旗標/轉狀態；sink.Stop 阻擋式，必須在鎖外呼叫
                    // （worker 的回呼與收尾需要 _lock，持鎖等 sink 會形成停等風險）
                    sinkToStop = BeginStopLocked();
                    break;

                case TtsState.Loading:
                    return; // 依 brief：刻意丟棄

                case TtsState.NotLoaded:
                case TtsState.Failed:
                    EnsureLoadedLocked();
                    return;

                default:
                    return;
            }
        }

        sinkToStop?.Stop();
    }

    /// <summary>
    /// 停止播放：設停止旗標（回呼 false → 引擎提前結束）＋ sink.Stop（立即靜音）；
    /// Speaking → Stopping 的轉移在此引發，worker 於 speak 結束後轉回 Ready（或 Failed）。
    /// 其他狀態下為無動作（冪等；sink.Stop 終止語意，重複呼叫安全）。
    /// </summary>
    public void Stop()
    {
        IAudioSink? sink;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            sink = BeginStopLocked();
        }

        sink?.Stop();
    }

    /// <summary>
    /// 釋放服務：設停止旗標 → sink.Stop（解除 worker 可能的寫入等待）→ join 播放 worker
    /// （<c>Join(2000)</c>）→ 釋放引擎與 sink。冪等；未在播放時為無動作。
    /// </summary>
    public void Dispose()
    {
        IAudioSink? sink;
        Thread? worker;
        lock (_lock)
        {
            if (_disposed)
            {
                return; // 冪等
            }

            _disposed = true;
            _stopRequested = true;
            sink = _sink;
            worker = _speakWorker;
        }

        // 一律在鎖外做阻擋式收尾：worker 收尾與引擎回呼都需要 _lock
        sink?.Stop();
        worker?.Join(2000); // 逾時兜底：引擎早已接收到回呼 false，正常會遠快於此

        lock (_lock)
        {
            var engine = _engine;
            _engine = null;
            _sink = null;
            _state = TtsState.NotLoaded; // 釋放後回到未載入（僅餘觀察用途；訂閱者可據此清 UI）
            RaiseChangedLocked();
            engine?.Dispose();
        }

        sink?.Dispose(); // 正常途徑已由 worker 收尾釋放；二次 Dispose 冪等
    }

    /// <summary>呼叫端須持有 _lock：NotLoaded/Failed → Loading ＋ 背景載入。</summary>
    private void EnsureLoadedLocked()
    {
        if (_state is not (TtsState.NotLoaded or TtsState.Failed))
        {
            return; // 已載入或載入中：無動作（冪等）
        }

        _lastError = null;     // 重新載入時清掉上次的失敗訊息（失敗會再帶回）
        _state = TtsState.Loading;
        RaiseChangedLocked();
        _ = Task.Run(LoadWorker); // 慢呼叫放背景；完成/失敗由 worker 轉 Ready/Failed
    }

    /// <summary>背景載入：工廠建引擎 → engine.Load → 收尾轉 Ready/Failed（鎖僅涵蓋提交）。</summary>
    private void LoadWorker()
    {
        string? error;
        IPttsEngine? engine;
        try
        {
            engine = _engineFactory();
        }
        catch (Exception ex)
        {
            CommitLoadResult(null, ex.Message);
            return;
        }

        try
        {
            error = engine.Load(_modelDir);
        }
        catch (Exception ex)
        {
            CommitLoadResult(engine, ex.Message);
            return;
        }

        CommitLoadResult(engine, error);
    }

    /// <summary>載入收尾：指派引擎並轉 Ready/Failed；服務已釋放時僅回收本輪引擎、不改狀態。</summary>
    private void CommitLoadResult(IPttsEngine? engine, string? error)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                engine?.Dispose(); // Dispose 時本輪載入尚未提交，由本地回收
                return;
            }

            // 換新引擎前先釋放舊的：Failed → 重載的循環（播放失敗或 sink 工廠失敗後再按一次）
            // 會走到這裡替換 _engine，舊引擎的 native ptts_new handle（含模型記憶體）若不
            // Dispose 即流失（每次重試一個，約數十 MB）。Dispose 由狀態機序列化，此處
            // 呼叫不會與 worker 的引擎呼叫交錯；IPttsEngine.Dispose 拋例外視為實作錯誤，
            // 不另加防護。
            var previous = _engine;
            _engine = engine;
            _lastError = error;
            _state = error is null ? TtsState.Ready : TtsState.Failed;
            RaiseChangedLocked();
            previous?.Dispose();
        }
    }

    /// <summary>
    /// 呼叫端須持有 _lock 且狀態為 Ready：新建 sink → 轉 Speaking → 啟動播放 worker。
    /// sink 工廠失敗（無音訊裝置等）→ 轉 Failed 並帶回錯誤訊息、不啟動 worker。
    /// </summary>
    private void StartSpeakLocked(string text)
    {
        _stopRequested = false;
        IAudioSink sink;
        try
        {
            sink = _sinkFactory();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _state = TtsState.Failed;
            RaiseChangedLocked();
            return;
        }

        _sink = sink;
        _state = TtsState.Speaking;
        RaiseChangedLocked();

        // 工廠呼叫（可能在鎖內拋例外）不會與引擎回呼交錯；worker 只在 Ready 情境存在，
        // 先指派欄位再 Start：Dispose 捕捉到的一定是已啟動（或必然啟動）的執行緒。
        var worker = new Thread(() => SpeakWorker(text))
        {
            IsBackground = true,
            Name = "AITranslator.TtsSpeak",
        };
        _speakWorker = worker;
        worker.Start();
    }

    /// <summary>
    /// 呼叫端須持有 _lock：設停止旗標並在 Speaking → Stopping 時引發事件，回傳本次的 sink
    /// 供呼叫端<b>在鎖外</b>執行阻擋式 sink.Stop()（引擎回呼與 worker 收尾需要 _lock）。
    /// 狀態非播放中則不做事（回傳 null）。
    /// </summary>
    private IAudioSink? BeginStopLocked()
    {
        if (_state is not (TtsState.Speaking or TtsState.Stopping))
        {
            return null;
        }

        _stopRequested = true;
        if (_state == TtsState.Speaking)
        {
            _state = TtsState.Stopping; // worker 於 speak 結束後轉回 Ready/Failed
            RaiseChangedLocked();
        }

        return _sink;
    }

    /// <summary>
    /// 播放 worker：阻擋式跑 engine.Speak（回呼內見停止旗標即跳過播放、回 false 請引擎停）；
    /// speak 結束（自然完成、停止或失敗）一律於此收尾轉 Ready（或 Failed），隨後釋放本次 sink。
    /// </summary>
    private void SpeakWorker(string text)
    {
        IPttsEngine? engine;
        lock (_lock)
        {
            engine = _engine;
        }

        bool ok;
        try
        {
            if (engine is null)
            {
                _lastError = "引擎尚未載入，無法發聲";
                ok = false;
            }
            else
            {
                ok = engine.Speak(text, OnAudioFrame, out _); // 統計值僅供顯示，不帶出
            }
        }
        catch (Exception ex)
        {
            ok = false; // 引擎拋例外視同播放失敗：轉 Failed，不讓 worker 靜默消失
            _lastError = ex.Message;
        }

        IAudioSink? sink;
        lock (_lock)
        {
            sink = _sink;
            _sink = null;
            if (!ok && !_stopRequested)
            {
                // 真正的播放失敗（非停止）：帶回引擎的 LastError
                _lastError = _lastError ?? engine?.LastError ?? "播放失敗（引擎未回報原因）";
                _state = TtsState.Failed;
            }
            else
            {
                // 自然完成或依指令停止
                _lastError = null;
                _state = TtsState.Ready;
            }
            RaiseChangedLocked();
        }

        sink?.Dispose(); // sink 實例一次性（Stop 為終止語意）：播放結束即釋放
    }

    /// <summary>
    /// 引擎回呼（engine.Speak 的呼叫執行緒＝播放 worker）：依停止旗標決定續播或停止。
    /// 不取 _lock（高頻路徑；旗標與 sink 參照皆 volatile 讀取），也不做任何可能拋例外的事。
    /// </summary>
    private bool OnAudioFrame(float[] samples)
    {
        if (_stopRequested)
        {
            return false; // 引擎會提前結束生成（spec 的停止語意）
        }

        var sink = _sink;
        if (sink is null)
        {
            return false;
        }

        sink.Write(samples); // 阻擋式寫入（有界緩衝）；Stop/Dispose 於另一執行緒解除其等待
        return true;
    }

    /// <summary>呼叫端須持有 _lock：引發狀態變更事件（於鎖內同步觸發，轉移順序與事件順序一致）。</summary>
    private void RaiseChangedLocked() => StateChanged?.Invoke(_state);
}