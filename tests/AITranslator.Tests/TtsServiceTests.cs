using AITranslator.Native;
using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

/// <summary>
/// TtsService 狀態機的行為測試：以 FakeEngine（可控的假引擎：記錄呼叫、以事件控制幀節奏）
/// 與 FakeSink（記錄 Write 樣本、Stop/Dispose 旗標）取代真實 ptts 與 waveOut，
/// 全部以事件脈衝等待狀態轉移，不含任意 sleep（CI 穩定）。
/// </summary>
public class TtsServiceTests
{
    /// <summary>
    /// 假引擎：Load 依 LoadGate 可凍結、依 LoadError 回成功/失敗；Speak 逐幀呼叫 callback，
    /// 幀與幀之間等 FrameGate（測試可 Reset 以凍結引擎）或 Interrupt（模擬 sink.Stop 解除等待），
    /// callback 回 false 即提前結束（與 ptts_speak 的停止語意一致）。
    /// </summary>
    private sealed class FakeEngine : IPttsEngine
    {
        public const int FrameCount = 3;
        public const int SamplesPerFrame = 8;

        /// <summary>預設放行（載入與播放自然前進）；測試可 Reset 以凍結。</summary>
        public ManualResetEventSlim LoadGate = new(true);
        public ManualResetEventSlim FrameGate = new(true);

        /// <summary>非 null 時與 FrameGate 一起 WaitAny；由 FakeSink.Stop 的鉤子觸發（鏡射 sink.Stop 解除回呼阻塞）。</summary>
        public EventWaitHandle? Interrupt;
        public string? LoadError;
        /// <summary>非 null 時 Speak 回 false、LastError 帶回此訊息（null = 生成永不失敗，維持原行為）。</summary>
        public string? SpeakError;
        public int LoadCalls;
        public int SpeakCalls;
        public bool Disposed;
        public List<bool> CallbackReturns { get; } = new();

        public string? Load(string modelDir)
        {
            LoadCalls++;
            Assert.True(LoadGate.Wait(TimeSpan.FromSeconds(10)), "FakeEngine.Load 等待逾時");
            return LoadError;
        }

        public bool Speak(string text, Func<float[], bool> onAudio, out TtsSpeakResult result)
        {
            SpeakCalls++;
            if (SpeakError is not null)
            {
                // 模擬 ptts_speak 回 false 的失敗：不跑生成迴圈，LastError 帶回注入的訊息
                result = default;
                return false;
            }

            for (int i = 0; i < FrameCount; i++)
            {
                if (i > 0)
                {
                    var handles = Interrupt is null
                        ? new WaitHandle[] { FrameGate.WaitHandle }
                        : new WaitHandle[] { FrameGate.WaitHandle, Interrupt };
                    int woken = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(10));
                    if (woken == 1)
                    {
                        // 被中斷（等同 sink.Stop 解除了引擎的等待）：直接送下一幀，
                        // 讓服務的 callback 以 false 表達停止
                    }
                }

                var frame = new float[SamplesPerFrame];
                for (int s = 0; s < SamplesPerFrame; s++)
                {
                    frame[s] = i + 1; // 以幀號標記，便於辨識
                }

                bool cont = onAudio(frame);
                CallbackReturns.Add(cont);
                if (!cont)
                {
                    break;
                }
            }

            result = new TtsSpeakResult { Frames = 7, TtfaMs = 11.5, TotalMs = 22.5 };
            return true; // 假引擎「生成」永不失敗：提前停止不視為錯誤
        }

        public string? LastError => SpeakError;

        public void Dispose() => Disposed = true;
    }

    /// <summary>假 sink：記錄每次收到的樣本數、首筆以事件脈衝通知；Stop 後丟棄新資料（鏡射真 sink 的終止語意）。</summary>
    private sealed class FakeSink : IAudioSink
    {
        public ManualResetEventSlim FirstWrite = new(false);
        public List<int> Writes { get; } = new();
        public Action? OnStop;
        public bool StopCalled;
        public bool Disposed;

        public void Write(ReadOnlySpan<float> samples)
        {
            if (StopCalled || Disposed)
            {
                return; // 終止狀態：鏡射 WaveOutSink（不再出聲）
            }

            Writes.Add(samples.Length);
            FirstWrite.Set();
        }

        public void Stop()
        {
            StopCalled = true;
            OnStop?.Invoke(); // 解除可能被引擎（FakeEngine 閘門）阻塞的回呼
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>測試組裝座：建好 service（FakeEngine + 每次播放建立一個 FakeSink）並記錄所有狀態轉移。</summary>
    private sealed class Harness : IDisposable
    {
        public FakeEngine Engine = new();
        public List<FakeSink> Sinks { get; } = new();
        public List<TtsState> States { get; } = new();
        public TtsService Service;

        public Harness()
        {
            Service = new TtsService(
                "model-dir",
                () => Engine,
                () =>
                {
                    var sink = new FakeSink();
                    Sinks.Add(sink);
                    // sink.Stop 時解除 FakeEngine 的閘門等待：鏡射真實情境（Stop 讓阻塞中的
                    // 引擎回呼前進、下一輪 callback 以 false 停止引擎）
                    sink.OnStop = () => Engine.Interrupt?.Set();
                    return sink;
                });
            Service.StateChanged += state => States.Add(state);
        }

        /// <summary>先訂閱再觸發（同一執行緒同步訂閱，不會漏掉後續脈衝）：回傳等待目標狀態的事件。</summary>
        public ManualResetEventSlim ExpectState(TtsState target)
        {
            var evt = new ManualResetEventSlim(false);
            Service.StateChanged += state =>
            {
                if (state == target)
                {
                    evt.Set();
                }
            };
            return evt;
        }

        public void Dispose() => Service.Dispose();
    }

    private static void Wait(ManualResetEventSlim evt, string what)
    {
        Assert.True(evt.Wait(TimeSpan.FromSeconds(10)), $"等待逾時：{what}");
    }

    [Fact]
    public void Toggle_WhenReady_StartsSpeak_DeliversFrames() // State=Speaking、frames 進 sink
    {
        using var h = new Harness();
        var loaded = h.ExpectState(TtsState.Ready);
        h.Service.EnsureLoaded();
        Wait(loaded, "載入完成（Ready）");

        var speaking = h.ExpectState(TtsState.Speaking);
        var done = h.ExpectState(TtsState.Ready);
        h.Service.Toggle("你好");
        Wait(speaking, "進入 Speaking");
        // 首幀進 sink 再等結束：確保引擎的推進經由 callback → sink 的路徑觀察到
        Assert.True(h.Sinks[0].FirstWrite.Wait(TimeSpan.FromSeconds(10)), "等待首筆樣本進 sink");
        Wait(done, "播放結束回 Ready");

        Assert.Equal(1, h.Engine.SpeakCalls);
        Assert.Equal(new[] { 8, 8, 8 }, h.Sinks[0].Writes); // 3 幀 × 每幀 8 樣本全數送到 sink
        Assert.All(h.Engine.CallbackReturns, ret => Assert.True(ret)); // 未被停止：callback 一路回 true
        Assert.Equal(TtsState.Ready, h.Service.State);
        Assert.Null(h.Service.LastError);
    }

    [Fact]
    public void Toggle_WhileSpeaking_CommandsStop_BackToReady() // callback 收到 false、State=Ready
    {
        using var h = new Harness();
        h.Engine.FrameGate.Reset(); // 凍結引擎：第一幀後停在閘門，等 sink.Stop 的中斷
        var interrupt = new ManualResetEvent(false);
        h.Engine.Interrupt = interrupt;

        var loaded = h.ExpectState(TtsState.Ready);
        h.Service.EnsureLoaded();
        Wait(loaded, "載入完成（Ready）");

        var speaking = h.ExpectState(TtsState.Speaking);
        var stopping = h.ExpectState(TtsState.Stopping);
        var ready = h.ExpectState(TtsState.Ready);

        h.Service.Toggle("第一段");
        Wait(speaking, "進入 Speaking");
        Assert.True(h.Sinks[0].FirstWrite.Wait(TimeSpan.FromSeconds(10)), "等待首幀送進 sink");

        h.Service.Toggle("第二段"); // 播放中再按 → 停止

        Wait(stopping, "進入 Stopping");
        Wait(ready, "worker 收尾回 Ready");
        interrupt.Set(); // 兜底：解除假引擎（正常應已由 sink.Stop 的鉤子解除）

        Assert.Equal(1, h.Engine.SpeakCalls);              // 沒有第二輪 Speak（停止 ≠ 重播）
        Assert.Contains(false, h.Engine.CallbackReturns);  // callback 曾回 false（停止語意）
        Assert.Single(h.Sinks[0].Writes);                  // 停止後不再有樣本進 sink（僅停前那一幀）
        Assert.Equal(TtsState.Ready, h.Service.State);
        Assert.Equal(new[] { TtsState.Speaking, TtsState.Stopping, TtsState.Ready }, h.States.TakeLast(3));
    }

    [Fact]
    public void Toggle_WhileLoading_DoesNotSpeak() // engine.Speak 從未被呼叫
    {
        using var h = new Harness();
        h.Engine.LoadGate.Reset(); // 凍結 Load：停留 Loading
        var loading = h.ExpectState(TtsState.Loading);
        h.Service.EnsureLoaded();
        Wait(loading, "進入 Loading");

        h.Service.Toggle("載入中等待"); // 依 brief：載入中的按壓刻意丟棄

        var ready = h.ExpectState(TtsState.Ready);
        h.Engine.LoadGate.Set(); // 放行載入
        Wait(ready, "載入完成（Ready）");

        Assert.Equal(0, h.Engine.SpeakCalls); // 從頭到尾（含載入完成後）都沒播放
        Assert.Empty(h.Sinks);
        Assert.Equal(1, h.Engine.LoadCalls);  // 也沒有重載
    }

    [Fact]
    public void EnsureLoaded_IsAsync_LoadingThenReady()
    {
        using var h = new Harness();
        var loading = h.ExpectState(TtsState.Loading);
        var ready = h.ExpectState(TtsState.Ready);
        h.Service.EnsureLoaded();
        Wait(loading, "進入 Loading");
        Wait(ready, "載入完成");

        Assert.Equal(new[] { TtsState.Loading, TtsState.Ready }, h.States);
        Assert.Equal(1, h.Engine.LoadCalls);
    }

    [Fact]
    public void LoadFailure_GoesFailed_KeepsLastError() // State=Failed、LastError 帶回
    {
        using var h = new Harness();
        h.Engine.LoadError = "模擬：模型載入失敗";
        var failed = h.ExpectState(TtsState.Failed);
        h.Service.EnsureLoaded();
        Wait(failed, "進入 Failed");

        Assert.Equal(new[] { TtsState.Loading, TtsState.Failed }, h.States);
        Assert.Equal("模擬：模型載入失敗", h.Service.LastError);
        Assert.Equal(1, h.Engine.LoadCalls); // 失敗不自動重試（重試僅由 Toggle/EnsureLoaded 明確觸發）
    }

    [Fact]
    public void SpeakFailure_GoesFailed_KeepsLastError() // ptts_speak 回 false → State=Failed + 保留 last_error
    {
        using var h = new Harness();
        var ready = h.ExpectState(TtsState.Ready);
        h.Service.EnsureLoaded();
        Wait(ready, "載入完成（Ready）");

        const string speakError = "模擬：引擎生成失敗";
        h.Engine.SpeakError = speakError;
        var failed = h.ExpectState(TtsState.Failed);
        h.Service.Toggle("觸發播放失敗");
        Wait(failed, "播放失敗轉入 Failed");

        Assert.Equal(1, h.Engine.SpeakCalls);
        Assert.Equal(speakError, h.Service.LastError);   // 引擎的 LastError 帶回給呼叫端
        Assert.Equal(TtsState.Failed, h.Service.State);
        Assert.Equal(TtsState.Failed, h.States.Last());  // 事件序列以 Failed 收尾
    }

    [Fact]
    public void Toggle_WhenFailed_Reloads()
    {
        using var h = new Harness();
        h.Engine.LoadError = "第一次載入失敗";
        var failed = h.ExpectState(TtsState.Failed);
        h.Service.EnsureLoaded();
        Wait(failed, "進入 Failed");

        h.Engine.LoadError = null; // 第二次載入成功
        var ready = h.ExpectState(TtsState.Ready);
        h.Service.Toggle("再試一次"); // Failed → EnsureLoaded()（僅重載，不播放）
        Wait(ready, "重載完成回 Ready");

        Assert.Equal(2, h.Engine.LoadCalls);
        Assert.Equal(0, h.Engine.SpeakCalls);      // 重載觸發本身不播放
        Assert.Empty(h.Engine.CallbackReturns);

        // 重載完成後再按一次才真的開始播放（狀態機語意回到 Ready）
        var speaking = h.ExpectState(TtsState.Speaking);
        var done = h.ExpectState(TtsState.Ready);
        h.Engine.FrameGate.Set();
        h.Service.Toggle("重載後播放");
        Wait(speaking, "進入 Speaking");
        Assert.True(h.Sinks[0].FirstWrite.Wait(TimeSpan.FromSeconds(10)), "等待首筆樣本進 sink");
        Wait(done, "播放結束回 Ready");
        Assert.Equal(new[] { 8, 8, 8 }, h.Sinks[0].Writes);
    }

    [Fact]
    public void Dispose_WhileSpeaking_JoinsAndDisposes_NoThrow()
    {
        using var h = new Harness();
        h.Engine.FrameGate.Reset(); // 凍結引擎：確保 Dispose 時仍在播放
        var interrupt = new ManualResetEvent(false);
        h.Engine.Interrupt = interrupt;

        var loaded = h.ExpectState(TtsState.Ready);
        h.Service.EnsureLoaded();
        Wait(loaded, "載入完成（Ready）");

        var speaking = h.ExpectState(TtsState.Speaking);
        h.Service.Toggle("播放中釋放");
        Wait(speaking, "進入 Speaking");
        Assert.True(h.Sinks[0].FirstWrite.Wait(TimeSpan.FromSeconds(10)), "等待首幀送進 sink");

        h.Service.Dispose(); // 播放中 Dispose：sink.Stop 解除引擎等待 → callback 回 false → 職員收尾 → join，不丟例外
        interrupt.Set();     // 兜底：解除假引擎（正常已由 sink.Stop 的鉤子解除）

        Assert.True(h.Sinks[0].StopCalled, "播放中的 sink 應被 Stop");
        Assert.True(h.Sinks[0].Disposed, "播放中的 sink 應被 Dispose");
        Assert.True(h.Engine.Disposed, "引擎應被 Dispose");
        Assert.Equal(1, h.Engine.SpeakCalls); // 釋放過程沒有啟動第二輪播放
    }
}