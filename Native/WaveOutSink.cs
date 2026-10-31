using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AITranslator.Native;

/// <summary>
/// 串流音訊輸出介面：TtsService 只依賴此介面（實作可替換、測試可用 fake）。
/// 音訊規格固定：24 kHz、單聲道、float32（[-1,1]），與 ptts_ffi 輸出一致。
/// </summary>
internal interface IAudioSink : IDisposable
{
    /// <summary>
    /// 阻擋式寫入一批樣本：內部為有界緩衝佇列，滿了就等空位再排入
    /// （生成端在回呼等待播放時自動暫停——spec 的有界緩衝設計）。
    ///
    /// <para>合約保證<b>不拋出例外</b>：本方法執行於 ptts_speak 的原生回呼內，
    /// 受控例外跨越反向 P/Invoke 邊界會直接終結處理程序；裝置異常時僅丟棄樣本
    /// 直接返回，由上層以 <see cref="Stop"/> 終止播放。</para>
    /// </summary>
    void Write(ReadOnlySpan<float> samples);

    /// <summary>
    /// 立即停止：<c>waveOutReset</c> 收回所有已排入緩衝並經 WOM_DONE 逐一歸還空位；
    /// 之後此實例進入終止狀態，後續 <see cref="Write"/> 一律直接返回（不再出聲）。
    /// 會自行等待 winmm 送完全部 WOM_DONE 回呼。不拋例外。
    /// </summary>
    void Stop();
}

/// <summary>
/// waveOut（WinMM）串流播放器：WAVE_FORMAT_IEEE_FLOAT / 24 kHz / 單聲道、
/// 4 個緩衝 × 6000 樣本（各 250ms）環狀佇列。
///
/// <para>運作：<see cref="Write"/> 以計數 <see cref="SemaphoreSlim"/>（初始 =
/// <see cref="BufferCount"/>）等空位，取到空位即填入樣本並 <c>waveOutWrite</c> 排入；
/// winmm 回呼執行緒收到 WOM_DONE 時歸還一個空位。<c>Stop</c> = waveOutReset +
/// 等送完全部 WOM_DONE；<see cref="Dispose"/> = Reset + Close。</para>
///
/// <para>緩衝生命週期（正確性關鍵）：已排入 waveOut 的緩衝，其樣本記憶體與 WAVEHDR
/// <b>不可釋放或複用</b>，直到 winmm 送回 WOM_DONE；Stop／Dispose 均先 waveOutReset
/// 把排入緩衝全數標記 done（逐一生 WOM_DONE）並等送完，之後才解除 prepare、
/// 釋放記憶體、關閉裝置——絕不在緩衝仍在佇列時釋放。</para>
/// </summary>
internal sealed class WaveOutSink : IAudioSink
{
    /// <summary>取樣率（Hz）：ptts_ffi 輸出規格。</summary>
    internal const int SampleRate = 24000;

    /// <summary>環狀緩衝個數；越大越可容忍寫入抖動，但延遲越高。</summary>
    internal const int BufferCount = 4;

    /// <summary>每個緩衝的樣本數（24000 Hz 下約 250ms）。</summary>
    internal const int SamplesPerBuffer = 6000;

    private const int BytesPerSample = 4;    // float32
    private const int StopDrainMs = 1500;    // 等 WOM_DONE 送完的上限兜底（驅動異常時避免永久卡住）
    private const int MaxCloseAttempts = 3;  // waveOutClose 反覆失敗（緩衝仍被持有）的重試上限

    private static readonly int HeaderSize = Marshal.SizeOf<Win32.WAVEHDR>();
    private static readonly int OffBufferLength =
        (int)Marshal.OffsetOf<Win32.WAVEHDR>(nameof(Win32.WAVEHDR.dwBufferLength));

    // 樣本資料配置於 Pinned Object Heap、再以 pinned GCHandle 取得固定基址交給 waveOut：
    // 位址全程不變且不壓縮常態堆積；Write 只複寫陣列內容（安全碼即可完成 copy）。
    private readonly float[] _ring;
    private readonly GCHandle _ringPin;
    private readonly IntPtr _ringBase;

    /// <summary>各緩衝之 WAVEHDR（非託管記憶體）；<see cref="IntPtr.Zero"/> = 未配置。</summary>
    private readonly IntPtr[] _headers;

    /// <summary>winmm 回呼的強引用：裝置開啟期間委派與其封送 thunk 不可被 GC 回收。</summary>
    private readonly Win32.WaveOutProc _callback;

    /// <summary>空位計數（初始 = BufferCount）：Write 取一、WOM_DONE 歸還一。</summary>
    private readonly SemaphoreSlim _freeSlots;

    /// <summary>守護裝置句柄存取與「查旗標 → waveOutWrite」的原子性；絕不在持鎖下等待。</summary>
    private readonly object _gate = new();

    private IntPtr _hWaveOut;      // waveOut 裝置句柄；Zero = 未開啟/已關閉
    private int _nextHeader;       // 下個要排入的緩衝索引（FIFO：WOM_DONE 依排入順序送回）
    private int _inFlight;         // 已排入、尚未收到 WOM_DONE 的緩衝數
    // Stop/Dispose 之後一律丟棄新資料、不再排入；一律以 Volatile.Read/Write 存取。
    private bool _stopping;

    /// <summary>
    /// 開啟預設輸出裝置（WAVE_MAPPER）並備妥環狀緩衝。
    /// 失敗時拋出 <see cref="InvalidOperationException"/>（裝置被占用、無音訊裝置等），
    /// 由 TtsService 的 sink 工廠端處理。
    /// </summary>
    internal WaveOutSink()
    {
        _ring = GC.AllocateArray<float>(BufferCount * SamplesPerBuffer, pinned: true);
        _ringPin = GCHandle.Alloc(_ring, GCHandleType.Pinned);
        _ringBase = _ringPin.AddrOfPinnedObject();
        _headers = new IntPtr[BufferCount];
        _freeSlots = new SemaphoreSlim(BufferCount, BufferCount);
        _callback = WaveCallback; // 欄位強引用：開啟期間 winmm 隨時可能呼叫

        var fmt = new Win32.WAVEFORMATEX
        {
            wFormatTag = Win32.WAVE_FORMAT_IEEE_FLOAT,
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * BytesPerSample,
            nBlockAlign = BytesPerSample,
            wBitsPerSample = BytesPerSample * 8,
            cbSize = 0,
        };

        uint mmr = Win32.waveOutOpen(out _hWaveOut, Win32.WAVE_MAPPER, ref fmt, _callback, IntPtr.Zero, Win32.CALLBACK_FUNCTION);
        if (mmr != 0)
        {
            // waveOutOpen 失敗時 out 參數內容未定義，先歸零再釋放已取得的資源。
            _hWaveOut = IntPtr.Zero;
            _ringPin.Free();
            _freeSlots.Dispose();
            throw new InvalidOperationException($"waveOutOpen 失敗（MMRESULT={mmr}）");
        }

        try
        {
            for (int i = 0; i < BufferCount; i++)
            {
                _headers[i] = Marshal.AllocHGlobal(HeaderSize);
                var hdr = new Win32.WAVEHDR
                {
                    lpData = IntPtr.Add(_ringBase, i * SamplesPerBuffer * BytesPerSample),
                    dwBufferLength = 0,
                };
                Marshal.StructureToPtr(hdr, _headers[i], fDeleteOld: false);
                // Prepare 一次、常駐使用到關閉（每次 Write 不需重做 prepare/unprepare）。
                uint pmr = Win32.waveOutPrepareHeader(_hWaveOut, _headers[i], (uint)HeaderSize);
                if (pmr != 0)
                {
                    throw new InvalidOperationException($"waveOutPrepareHeader 失敗（緩衝 {i}，MMRESULT={pmr}）");
                }
            }
        }
        catch
        {
            Dispose(); // 收回裝置與既有配置（Reset + Close + 釋放）後才讓例外傳出
            throw;
        }
    }

    /// <inheritdoc cref="IAudioSink.Write"/>
    public void Write(ReadOnlySpan<float> samples)
    {
        // 合約為絕不拋例外（執行於 ptts 原生回呼內，見 IAudioSink.Write）：整段 try/catch 兜底。
        try
        {
            int offset = 0;
            while (offset < samples.Length)
            {
                if (Volatile.Read(ref _stopping))
                {
                    return; // 已停止：丟棄剩餘樣本
                }

                _freeSlots.Wait(); // 阻擋式等空位（播畢緩衝由 WOM_DONE 歸還；不持 _gate，避免與 Stop 死結）

                if (Volatile.Read(ref _stopping))
                {
                    _freeSlots.Release(); // 等待期間被 Stop → 歸還空位後離開
                    return;
                }

                int n = Math.Min(SamplesPerBuffer, samples.Length - offset);
                lock (_gate)
                {
                    if (_hWaveOut == IntPtr.Zero || Volatile.Read(ref _stopping))
                    {
                        _freeSlots.Release();
                        return;
                    }

                    int idx = _nextHeader;
                    samples.Slice(offset, n).CopyTo(_ring.AsSpan(idx * SamplesPerBuffer, n));
                    // 僅更新長度：WAVEHDR 其餘欄位（dwFlags 等）由 winmm 維護，不可整份覆寫
                    // （覆寫會清掉 WHDR_PREPARED，waveOutWrite 將回 WAVERR_UNPREPARED）。
                    Marshal.WriteInt32(_headers[idx], OffBufferLength, n * BytesPerSample);

                    Interlocked.Increment(ref _inFlight);
                    if (Win32.waveOutWrite(_hWaveOut, _headers[idx], (uint)HeaderSize) != 0)
                    {
                        // 排入失敗：歸還空位、丟棄該批樣本（不拋例外，見方法註解）
                        Interlocked.Decrement(ref _inFlight);
                        _freeSlots.Release();
                        return;
                    }

                    _nextHeader = (idx + 1) % BufferCount;
                }

                offset += n;
            }
        }
        catch
        {
            // 僅可能是與 Dispose 的關機競態等極端情境；原生回呼絕不可拋例外，故吞掉。
        }
    }

    /// <inheritdoc cref="IAudioSink.Stop"/>
    public void Stop()
    {
        // 順序（緩衝生命週期關鍵）：先封口 Write → waveOutReset 把全部排入緩衝標記 done
        // （winmm 逐一生 WOM_DONE 歸還空位）→ 等回呼送完。
        // 回呼只碰計數/旗標、不取 _gate，故持 _gate 做 Reset 不會死結；
        // 正在 Wait 空位的 Write 也會因 Release 而醒來自行離開，本方法不與 Write 互相卡死。
        Volatile.Write(ref _stopping, true);
        lock (_gate)
        {
            if (_hWaveOut != IntPtr.Zero)
            {
                Win32.waveOutReset(_hWaveOut);
            }
        }

        DrainInFlight();
    }

    /// <summary>
    /// 釋放裝置與全部緩衝：<see cref="Stop"/>（Reset + 等送完）→ Unprepare → Close →
    /// Close 成功（不再有任何 winmm 回呼）後才釋放 WAVEHDR 與樣本記憶體。
    /// </summary>
    public void Dispose()
    {
        Stop(); // 兜底：呼叫端未先 Stop 也安全（先 Reset 收回排入緩衝）

        lock (_gate)
        {
            for (int attempt = 0; attempt < MaxCloseAttempts && _hWaveOut != IntPtr.Zero; attempt++)
            {
                if (attempt > 0)
                {
                    // 上一輪 Close 失敗（極罕見：仍有緩衝被 winmm 持有）→ 再收一次後重試
                    Win32.waveOutReset(_hWaveOut);
                    DrainInFlight();
                }

                for (int i = 0; i < BufferCount; i++)
                {
                    if (_headers[i] != IntPtr.Zero)
                    {
                        // Unprepare 失敗（WAVERR_UNPREPARED / WAVERR_STILLPLAYING）一律忽略，
                        // 由下方 Close 的成敗決定裝置與記憶體能否收回。
                        Win32.waveOutUnprepareHeader(_hWaveOut, _headers[i], (uint)HeaderSize);
                    }
                }

                if (Win32.waveOutClose(_hWaveOut) == 0)
                {
                    Volatile.Write(ref _hWaveOut, IntPtr.Zero);
                }
            }
        }

        if (_hWaveOut == IntPtr.Zero)
        {
            // Close 成功：winmm 已歸還硬體、保證不再有回呼，此時釋放記憶體才安全。
            for (int i = 0; i < BufferCount; i++)
            {
                if (_headers[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_headers[i]);
                    _headers[i] = IntPtr.Zero;
                }
            }

            if (_ringPin.IsAllocated)
            {
                _ringPin.Free();
            }
        }
        // Close 反覆失敗（驅動卡死，實務上不應發生）時刻意不釋放緩衝記憶體：
        // winmm 仍可能持有指標，釋放將造成存取違規；留待程序結束由 OS 回收。

        _freeSlots.Dispose();
    }

    /// <summary>等所有已排入緩衝收到 WOM_DONE（<see cref="StopDrainMs"/> 上限兜底）。</summary>
    private void DrainInFlight()
    {
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlight) != 0 && clock.ElapsedMilliseconds < StopDrainMs)
        {
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// winmm 回呼（winmm 專屬執行緒）：受控例外在此出現會終結處理程序，故整體
    /// try/catch 兜底、全面吞掉。僅處理 WOM_DONE 歸還空位；不在此呼叫任何 waveOut*。
    /// </summary>
    private void WaveCallback(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        try
        {
            if (uMsg == Win32.WOM_DONE)
            {
                Interlocked.Decrement(ref _inFlight); // 該緩衝已送回，其記憶體自此可複用
                _freeSlots.Release();                 // 歸還一個空位，讓 Write 的 Wait 前進
            }

            // WOM_OPEN / WOM_CLOSE 與緩衝記帳無關，忽略。
        }
        catch
        {
            // Release 滿溢理論上不可能（每次 WOM_DONE 恰對應先前消耗的一個空位）；
            // 無論如何原生回呼不可拋出例外，故吞掉。
        }
    }
}