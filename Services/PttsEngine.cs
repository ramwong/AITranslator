using System.Runtime.InteropServices;
using AITranslator.Native;

namespace AITranslator.Services;

/// <summary>播放狀態機的狀態（TtsService 對外呈現）。</summary>
public enum TtsState
{
    /// <summary>引擎尚未載入（初始狀態）。</summary>
    NotLoaded,

    /// <summary>模型載入中（背景 Task.Run 執行）。</summary>
    Loading,

    /// <summary>引擎載入完成、待播放。</summary>
    Ready,

    /// <summary>串流播放中（worker thread 於 engine.Speak 內）。</summary>
    Speaking,

    /// <summary>停止中（已下停止指令，worker 尚未收尾回 Ready）。</summary>
    Stopping,

    /// <summary>載入失敗（LastError 帶回原因）。</summary>
    Failed,
}

/// <summary>TTS 引擎抽象：TtsService 只依賴此介面（測試以 fake 替換）。</summary>
public interface IPttsEngine : IDisposable
{
    /// <summary>載入模型資料夾。回傳 null=成功；非 null=錯誤訊息。</summary>
    string? Load(string modelDir);

    /// <summary>
    /// 阻擋式串流發聲：<paramref name="onAudio"/> 依序收到各批 24 kHz 單聲道 float32 樣本
    /// （僅在回呼期間有效，實作須自行複製）；回 false 即提前停止生成。
    /// 回傳 false 表示失敗（細節見 LastError）。
    /// </summary>
    bool Speak(string text, Func<float[], bool> onAudio, out TtsSpeakResult result);

    /// <summary>最近一次失敗原因（對映 ptts_last_error；成功情境不保證內容）。</summary>
    string? LastError { get; }
}

/// <summary>ptts_speak 的統計結果。</summary>
public struct TtsSpeakResult
{
    /// <summary>音訊「框」數（引擎產出單位數，僅供狀態顯示/除錯）。</summary>
    public uint Frames;

    /// <summary>首音時間（毫秒）。</summary>
    public double TtfaMs;

    /// <summary>整段生成總耗時（毫秒）。</summary>
    public double TotalMs;
}

/// <summary>
/// <see cref="IPttsEngine"/> 的真實實作：Pure 包裝層，僅做 marshaling 與託管回呼轉接、
/// 不含任何狀態機或播放邏輯（狀態機見 <see cref="TtsService"/>；本類別無測試，契約由
/// Task 5 的原生層與 TtsService 的 fake 替換測試涵蓋）。
///
/// <para>使用前須先 <see cref="Native.PttsNative.RegisterResolver"/>（AppController 於
/// TtsAssetStore 解壓後、建構本類別前呼叫）。執行緒模型：Load/Speak/Dispose 由
/// <see cref="TtsService"/> 依其狀態機序列化呼叫（載入於背景 task、播放於 worker thread、
/// Dispose 於 worker Join 之後），本類別不再自備鎖。</para>
/// </summary>
internal sealed class PttsEngine : IPttsEngine
{
    /// <summary>ptts 引擎句柄；IntPtr.Zero = 未載入/已釋放。</summary>
    private IntPtr _handle;

    /// <summary>最近一次失敗原因（對映 ptts_last_error）。</summary>
    private string? _lastError;

    /// <summary>
    /// 載入模型資料夾（阻擋式慢呼叫，呼叫端須於背景執行緒）。
    /// 回傳 null=成功；非 null=錯誤訊息（取自 ptts_last_error）。
    /// </summary>
    public string? Load(string modelDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDir);

        var handle = PttsNative.New(modelDir);
        if (handle == IntPtr.Zero)
        {
            // New 失敗時無句柄可查：原生端以建立情境記錄原因（ptts_last_error(NULL) 可查得）；
            // 無論如何都給出一條非空錯誤訊息供上層顯示。
            _lastError = PttsNative.LastError(IntPtr.Zero) ?? "ptts_new 失敗（無法建立 TTS 引擎）";
            if (string.IsNullOrEmpty(_lastError))
            {
                _lastError = "ptts_new 失敗（無法建立 TTS 引擎）";
            }

            return _lastError;
        }

        _handle = handle;
        _lastError = null;
        return null;
    }

    /// <summary>
    /// 阻擋式串流發聲（同一時間至多一個 speak，由 TtsService 狀態機保證）：
    /// 各批樣本在原生回呼執行緒上複製為受控陣列後交給 <paramref name="onAudio"/>
    /// （原生緩衝僅在回呼期間有效，不可持留指標）。
    ///
    /// <para>回呼絕不拋出例外：例外穿越反向 P/Invoke 邊界會直接終結處理程序，
    /// 任何錯誤一律以回傳 false（停止生成）表達。</para>
    /// </summary>
    public bool Speak(string text, Func<float[], bool> onAudio, out TtsSpeakResult result)
    {
        ArgumentNullException.ThrowIfNull(onAudio);
        if (_handle == IntPtr.Zero)
        {
            result = default;
            _lastError = "引擎尚未載入，無法發聲";
            return false;
        }

        PttsNative.PttsFrameFn frameFn = (IntPtr pcm, UIntPtr n, IntPtr user) =>
        {
            try
            {
                // 原生端為 usize 樣本數；超過 int.MaxValue 的單批（理論上不會發生）截斷防護。
                long count64 = (long)n;
                if (count64 <= 0)
                {
                    return true; // 空批：直接續行
                }

                int count = (int)Math.Min(count64, int.MaxValue);
                var samples = new float[count];
                Marshal.Copy(pcm, samples, 0, count); // 立即複製（原生緩衝僅本次回呼有效）
                return onAudio(samples);
            }
            catch
            {
                return false; // 原生回呼不可拋例外：以停止生成表達失敗
            }
        };

        bool ok = PttsNative.Speak(_handle, text, frameFn, IntPtr.Zero, out PttsNative.PttsResult native);
        _lastError = ok ? null : PttsNative.LastError(_handle);
        result = new TtsSpeakResult { Frames = native.Frames, TtfaMs = native.TtfaMs, TotalMs = native.TotalMs };
        return ok;
    }

    /// <summary>最近一次失敗原因（成功情境為 null；對映 ptts_last_error）。</summary>
    public string? LastError => _lastError;

    /// <summary>釋放原生句柄（冪等；ptts_free(0) 由原生端容錯）。</summary>
    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        PttsNative.Free(handle);
    }
}