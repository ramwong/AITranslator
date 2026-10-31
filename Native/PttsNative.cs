using System.Reflection;
using System.Runtime.InteropServices;

namespace AITranslator.Native;

/// <summary>
/// <c>ptts_ffi</c>（phonon TTS 引擎，版本鎖定 ptts v0.6.0）的 P/Invoke 宣告層。
/// FFI 介面鎖定：<c>ptts_new(dir, unit, lang)</c>／<c>ptts_speak(h, text, callback, user, &amp;result)</c>／
/// <c>ptts_last_error(h)</c>／<c>ptts_free(h)</c>；音訊規格 24 kHz 單聲道 float32（[-1,1]）。
/// 全部呼叫 <c>CallingConvention = Cdecl</c>；字串一律以 UTF-8 封送（Rust 端為 UTF-8 C 字串，
/// 快取路徑與待唸文字皆可能含非 ASCII，不可用 ANSI 預設封送）。
///
/// <para>使用前必須先呼叫 <see cref="RegisterResolver"/> 指定 <c>ptts_ffi.dll</c> 所在資料夾，
/// 且不得在此之前觸發任何 ptts_* 呼叫——.NET 會快取 DllImport 的載入結果（含失敗），
/// 失敗過一次之後解析器就不再被諮詢。AppController 於 TtsAssetStore 解壓成功後、
/// 建構 PttsEngine 之前呼叫。</para>
/// </summary>
internal static class PttsNative
{
    private const string DllName = "ptts_ffi";
    private const string DllFileName = "ptts_ffi.dll";

    /// <summary><c>ptts_new</c> 的 unit 參數：CPU 推理。</summary>
    internal const uint PTTS_UNIT_CPU = 0;

    /// <summary>ptts_ffi.dll 的解壓快取資料夾（RegisterResolver 設定；null=尚未註冊）。</summary>
    private static string? _dllDir;

    static PttsNative()
    {
        InstallDllImportResolver();
    }

    /// <summary>
    /// 掛上本組件的 DllImport 解析器，使 ptts_ffi.dll 能從解壓快取資料夾載入
    /// （dll 不隨應用程式一起散布，預設探查路徑找不到）。
    ///
    /// <para>以反射呼叫：本專案目標 net8.0，但同組件的解析器安裝 API 在
    /// .NET 8 為 <c>SetDllImportResolver(Assembly, DllImportResolver, out previous)</c>、
    /// .NET 9+ 移除了 out 參數，建置環境（含 CI）兩種 SDK 皆可能遇見，
    /// 反射可同時相容兩種簽名。</para>
    /// </summary>
    private static void InstallDllImportResolver()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        var overloads = typeof(NativeLibrary).GetMethods(flags)
            .Where(m => m.Name == nameof(NativeLibrary.SetDllImportResolver))
            .Where(m => m.GetParameters().Length is 2 or 3)
            .ToArray();

        if (overloads.Length != 1)
        {
            throw new InvalidOperationException("未知的 NativeLibrary.SetDllImportResolver 簽名，無法安裝解析器");
        }

        var parameters = overloads[0].GetParameters();
        if (parameters[0].ParameterType != typeof(Assembly) || parameters[1].ParameterType != typeof(DllImportResolver))
        {
            throw new InvalidOperationException("NativeLibrary.SetDllImportResolver 簽名不符預期，無法安裝解析器");
        }

        // 三參數多載的第三參數為 out previous（我們不需要），以 null 起始即可。
        object?[] args = parameters.Length == 2
            ? new object?[] { typeof(PttsNative).Assembly, (DllImportResolver)Resolve }
            : new object?[] { typeof(PttsNative).Assembly, (DllImportResolver)Resolve, null };
        overloads[0].Invoke(null, args);
    }

    /// <summary>
    /// 註冊 <c>ptts_ffi.dll</c> 所在資料夾（TtsAssetStore 解壓後由 AppController 呼叫）。
    /// 必須在任何 ptts_* 呼叫之前執行；重複呼叫僅更新資料夾（解析器每次載入都會重讀）。
    /// </summary>
    internal static void RegisterResolver(string dllDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllDir);
        Volatile.Write(ref _dllDir, dllDir);
    }

    /// <summary>
    /// DllImport 解析器：名稱為 <c>ptts_ffi</c> 時改從註冊的資料夾載入；
    /// 其餘 DLL（user32/kernel32 等）與未註冊/尚未解壓情境一律回傳 <see cref="IntPtr.Zero"/>
    /// 交回 .NET 預設探查。
    /// </summary>
    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var dir = Volatile.Read(ref _dllDir);
        if (dir is null)
        {
            return IntPtr.Zero;
        }

        var fullPath = Path.Combine(dir, DllFileName);
        if (!File.Exists(fullPath))
        {
            return IntPtr.Zero; // 尚未解壓完成 → 交回預設探查（通常收 DllNotFoundException）
        }

        return NativeLibrary.Load(fullPath, assembly, DllImportSearchPath.ApplicationDirectory);
    }

    /// <summary>
    /// <c>ptts_speak</c> 的進度回呼：原生端在<b>呼叫執行緒</b>上分批送入 24 kHz 單聲道
    /// float32 樣本。<paramref name="pcm"/> 指向 <paramref name="n"/> 個 float，<b>僅在本次
    /// 回呼期間有效</b>，實作須立即複製後再行播放。
    /// 回傳 <c>false</c> 會提前停止整個生成（spec 的「callback 回 false 即停止」）；
    /// 回傳 <c>true</c> 繼續。實作<b>絕不可拋出例外</b>：受控例外跨越反向 P/Invoke 邊界
    /// 會直接終結處理程序，任何錯誤必須以回傳值（false）表達。
    /// </summary>
    // 與 ptts_speak 同契約：原生 bool 為 1 byte，委派回傳亦明確以 I1 封送（一行保險，
    // 委派 thunk 本就寫入乾淨的 0/1，不受 eax 殘值影響）。
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool PttsFrameFn(IntPtr pcm, UIntPtr n, IntPtr user);

    /// <summary>
    /// <c>ptts_speak</c> 結果（對映 Rust <c>#[repr(C)]</c> 結構；
    /// 順序/對齊不可更動：u32 → usize → f64 → f64，x64 下 32 bytes）。以 out 參數接收副本。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PttsResult
    {
        /// <summary>音訊「框」數（引擎的產出單位數，僅供狀態顯示/除錯）。</summary>
        public uint Frames;

        /// <summary>總樣本數（24 kHz 單聲道 float32）。</summary>
        public UIntPtr Samples;

        /// <summary>首音時間（毫秒）：從呼叫到第一批樣本送出的耗時。</summary>
        public double TtfaMs;

        /// <summary>整段生成總耗時（毫秒）。</summary>
        public double TotalMs;
    }

    // ---- 原始 P/Invoke（private；一律 Cdecl、ExactSpelling；字串 UTF-8 封送）----

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr ptts_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dir, uint unit, [MarshalAs(UnmanagedType.LPUTF8Str)] string lang);

    // 回傳 bool 以 I1 封送：Rust 的 extern "C" bool 只回 1 byte（al），而 managed bool 預設以
    // 4-byte BOOL 讀取整個 eax——al=0 失敗搭配 eax 前三 byte 的殘值會被誤判為成功。
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ptts_speak(
        IntPtr h,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        PttsFrameFn cb,
        IntPtr user,
        out PttsResult result);

    // 回傳值為 Rust 端的內部錯誤字串（UTF-8），封送層自行複製為受控 string、不釋放原生指標；
    // 指標僅保證到下一次 ptts_* 呼叫前有效——封送成副本後即不受此限。
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.LPUTF8Str)]
    private static extern string ptts_last_error(IntPtr h);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void ptts_free(IntPtr h);

    // ---- 對外包裝（PttsEngine 使用）----

    /// <summary>
    /// 載入模型資料夾並回傳句柄（阻擋式慢呼叫；對映鎖定呼叫 <c>ptts_new(dir, PTTS_UNIT_CPU, lang)</c>，
    /// 系統語音模型為英文，預設 <c>lang = "en"</c>）。呼叫端須在背景執行緒執行。
    /// 回傳 <see cref="IntPtr.Zero"/> 表示失敗，改以 <see cref="LastError"/> 查得訊息；
    /// 句柄由 <see cref="Free"/> 釋放，且以 <see cref="IntPtr.Zero"/> 再次釋放由原生端容錯。
    /// </summary>
    internal static IntPtr New(string modelDir, string lang = "en")
    {
        return ptts_new(modelDir, PTTS_UNIT_CPU, lang);
    }

    /// <summary>
    /// 阻擋式串流發聲（對映 <c>ptts_speak(h, text, cb, user, &amp;result)</c>）：
    /// 同一時間至多一個 speak；<paramref name="cb"/> 在<b>呼叫執行緒</b>上被逐一叫用送入
    /// 24 kHz 單聲道 float32 樣本，回傳 <c>false</c> 即提前停止生成（spec 的停止語意）；
    /// 生成端在回呼等待播放時自動暫停（有界緩衝，故呼叫端可阻擋式消化樣本）。
    /// 回傳 <c>false</c> 表示失敗，以 <see cref="LastError"/> 查得訊息。
    ///
    /// <para>委派存活：同步 P/Invoke 的引數雖由 JIT 在原生呼叫期間保持存活，
    /// 此處仍以 GC handle 明確持有 <paramref name="cb"/>，確保封送的函式指標
    /// 在整個呼叫期間絕不因記憶體回收而懸空。</para>
    /// </summary>
    internal static bool Speak(IntPtr h, string text, PttsFrameFn cb, IntPtr user, out PttsResult result)
    {
        var pin = GCHandle.Alloc(cb);
        try
        {
            return ptts_speak(h, text, cb, user, out result);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>
    /// 查詢最近一次失敗原因（對映 <c>ptts_last_error(h)</c>）；回傳副本，成功情境內容未指定。
    /// 原生端可能回傳 <see langword="null"/>，故允許 <see langword="null"/>。
    /// </summary>
    internal static string? LastError(IntPtr h)
    {
        return ptts_last_error(h);
    }

    /// <summary>釋放 <see cref="New"/> 取得的句柄（常駐模型：僅於程式離開時呼叫一次）。</summary>
    internal static void Free(IntPtr h)
    {
        ptts_free(h);
    }
}