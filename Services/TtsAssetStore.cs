namespace AITranslator.Services;

/// <summary>
/// 將組件內嵌的 TTS 資產（<c>ptts_ffi.dll</c> 與模型檔）解壓到版本化快取目錄
/// （<c>%LOCALAPPDATA%\AITranslator\tts\</c>；dll 於根層、模型於 <c>model\</c>）。
///
/// <para>快取以 <see cref="StampFileName"/> 戳記版控：目錄存在且戳記相符則直接回傳，
/// 否則整個目錄刪除後逐資產重解壓並補寫戳記；首輪失敗（例如殘留損毀檔、防毒鎖檔）
/// 會清空後再解壓<b>一次</b>，仍失敗才拋 <see cref="IOException"/>。</para>
///
/// <para>資產來源由 <c>openAsset</c> 委派提供（參數為資產相對路徑），實際組件使用
/// <see cref="ResourcePrefix"/> 前綴的內嵌資源名稱——實測命名規則見
/// <see cref="ManifestResourceName"/>。</para>
/// </summary>
public sealed class TtsAssetStore
{
    /// <summary>需解壓的全部資產，相對於 <c>assets\tts\</c> 的路徑（以 <c>/</c> 分隔）。</summary>
    public static readonly string[] AssetNames =
    {
        "ptts_ffi.dll",
        "model/config.json",
        "model/tokenizer.json",
        "model/model.q8.gguf",
        "model/default-voice.safetensors",
    };

    /// <summary>版本戳記檔名；內容寫入建構子傳入的 versionStamp，作為快取有效性的依據。</summary>
    public const string StampFileName = "version.txt";

    /// <summary>
    /// 內建版本戳記；AppController 未另外指定時以此作為建構子的 versionStamp 傳入。
    /// 值為 <c>&lt;PttsTag&gt;+&lt;ModelCommit&gt;</c>——與 scripts/fetch-assets.ps1
    /// 鎖定的 xn-ptts tag（v0.6.0）及 phonon-31m-en 模型修訂（HF API sha 欄位，
    /// 2026-10-31 查得並寫死於 script 預設參數）一致。
    /// </summary>
    public const string InternalStamp = "0.6.0+92741c5b07d085ff840e26fea8ce43110b87852f";

    /// <summary>內嵌資源的 LogicalName 前綴，需與 csproj 的
    /// <c>EmbeddedResource LogicalName="AITranslator.Tts.%(RecursiveDir)..."</c> 對齊。</summary>
    internal const string ResourcePrefix = "AITranslator.Tts.";

    private readonly string _cacheDir;
    private readonly string _stampPath;
    private readonly Func<string, Stream?> _openAsset;
    private readonly string _versionStamp;

    public TtsAssetStore(string cacheDir, Func<string, Stream?> openAsset, string versionStamp)
    {
        _cacheDir = cacheDir;
        _stampPath = Path.Combine(cacheDir, StampFileName);
        _openAsset = openAsset;
        _versionStamp = versionStamp;
    }

    /// <summary>確保快取目錄已解壓到指定版本，回傳快取目錄路徑。</summary>
    /// <exception cref="IOException">重試解壓一次仍失敗。</exception>
    public string EnsureExtracted()
    {
        if (!IsUpToDate())
        {
            ExtractWithRetry();
        }

        return _cacheDir;
    }

    /// <summary>
    /// 資產相對路徑 → 內嵌資源名稱的純映射。命名為實測所得（Windows 機器上以
    /// dummy 資產建置後呼叫 <see cref="System.Reflection.Assembly.GetManifestResourceNames"/>）：
    /// <c>LogicalName="AITranslator.Tts.%(RecursiveDir)%(Filename)%(Extension)"</c> 產出
    /// 根層檔案 RecursiveDir 為空、子目錄以反斜線分隔（即 <c>model\config.json</c>，
    /// 非 csproj 縮寫猜測的 <c>/</c> 轉 <c>_</c>）。
    /// </summary>
    internal static string ManifestResourceName(string assetName)
    {
        return ResourcePrefix + assetName.Replace('/', '\\');
    }

    private bool IsUpToDate()
    {
        if (!Directory.Exists(_cacheDir) || !File.Exists(_stampPath))
        {
            return false;
        }

        // 戳記讀不到（防毒鎖檔、權限等）等同戳記不符：視為需重解壓，
        // 交由 ExtractWithRetry 的重試包裝處理，而非讓 IO 例外直接外洩。
        try
        {
            return File.ReadAllText(_stampPath) == _versionStamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>逐資產解壓、清空重建與寫戳記都算同一輪：任何一環失敗即整輪重來。</summary>
    private void ExtractAll()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }

        foreach (var asset in AssetNames)
        {
            // 資產路徑以 '/' 分隔；Path.Combine 在 Windows/Linux 皆可接受子路徑分隔符。
            var targetPath = Path.Combine(_cacheDir, asset);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var source = _openAsset(asset)
                ?? throw new IOException($"找不到內嵌 TTS 資源：{ManifestResourceName(asset)}");
            using var target = File.Create(targetPath);
            source.CopyTo(target);
        }

        // 戳記最後寫入：寫到一半斷掉時目錄沒有戳記，下次會整個重解壓。
        File.WriteAllText(_stampPath, _versionStamp);
    }

    /// <summary>
    /// 解壓一次；失敗則清空快取目錄後<b>再試一次</b>，仍失敗包成 <see cref="IOException"/> 拋出。
    ///
    /// <para>刻意以例外過濾排除 <see cref="OutOfMemoryException"/>、
    /// <see cref="AccessViolationException"/> 等 OOM 級嚴重例外——資源耗盡或程序狀態已損毀時，
    /// 清空重解壓只會雪上加霜，此類例外直接向外拋出不重試。</para>
    /// </summary>
    private void ExtractWithRetry()
    {
        try
        {
            ExtractAll();
        }
        catch (Exception ex) when (IsRetryable(ex))
        {
            try
            {
                ExtractAll();
            }
            catch (Exception retryEx) when (IsRetryable(retryEx))
            {
                throw new IOException($"TTS 資產解壓失敗（已重試一次）：{Path.GetFullPath(_cacheDir)}", retryEx);
            }
        }
    }

    /// <summary>嚴重例外（OOM 級）不可重試；其餘（IO、鎖檔、provider 拋出的錯誤等）視為可重試。</summary>
    private static bool IsRetryable(Exception ex)
    {
        return ex is not (OutOfMemoryException or AccessViolationException);
    }
}