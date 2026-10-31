using System.Text;
using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

public class TtsAssetStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cacheDir;

    public TtsAssetStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AITranslatorTests", Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_tempDir, "_cache");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private static MemoryStream FakeStream(string content) =>
        new(Encoding.UTF8.GetBytes(content));

    /// <summary>計次 provider：回傳以資產名衍生的假內容並累計呼叫次數。</summary>
    private (TtsAssetStore Store, Func<int> Calls) CountingStore(string stamp)
    {
        var calls = 0;
        var store = new TtsAssetStore(_cacheDir, name =>
        {
            calls++;
            return FakeStream("content-of-" + name);
        }, stamp);
        return (store, () => calls);
    }

    [Fact]
    public void EnsureExtracted_WritesAllAssets_VersionStamp() // 全檔存在 + version.txt 內容相符
    {
        const string stamp = "test-stamp-v1";
        var store = new TtsAssetStore(_cacheDir, name => FakeStream("content-of-" + name), stamp);

        var result = store.EnsureExtracted();

        Assert.Equal(_cacheDir, result);
        foreach (var asset in TtsAssetStore.AssetNames)
        {
            var path = Path.Combine(_cacheDir, asset);
            Assert.True(File.Exists(path), $"資產檔案未寫出：{path}");
            Assert.Equal("content-of-" + asset, File.ReadAllText(path));
        }
        Assert.Equal(stamp, File.ReadAllText(Path.Combine(_cacheDir, TtsAssetStore.StampFileName)));
    }

    [Fact]
    public void EnsureExtracted_MatchingStamp_DoesNotReextract() // provider 被呼叫 0 次
    {
        new TtsAssetStore(_cacheDir, _ => FakeStream("first"), "stamp").EnsureExtracted();

        var (second, calls) = CountingStore("stamp");
        second.EnsureExtracted();

        Assert.Equal(0, calls());
    }

    [Fact]
    public void EnsureExtracted_StampMismatch_Reextracts() // 改戳記→重寫
    {
        var (first, _) = CountingStore("new-stamp");
        first.EnsureExtracted();
        File.WriteAllText(Path.Combine(_cacheDir, TtsAssetStore.StampFileName), "old-stamp");
        // 留一個不屬於資產的殘留檔，驗證重解壓時整個目錄被清空重建
        File.WriteAllText(Path.Combine(_cacheDir, "stale.txt"), "stale");

        var (second, calls) = CountingStore("new-stamp");
        second.EnsureExtracted();

        // 計數屬於第二間 store：mismatch 觸發完整重解壓一輪（每資產開檔一次）
        Assert.Equal(TtsAssetStore.AssetNames.Length, calls());
        Assert.False(File.Exists(Path.Combine(_cacheDir, "stale.txt")), "殘留檔應隨目錄清空被移除");
        Assert.Equal("new-stamp", File.ReadAllText(Path.Combine(_cacheDir, TtsAssetStore.StampFileName)));
    }

    [Fact]
    public void EnsureExtracted_CorruptOnFirstRun_RetriesOnceThenThrows()
    // 第一輪模擬寫入中途拋出 → 仍再試一次，第二輪失敗才拋 IOException
    {
        var calls = 0;
        var store = new TtsAssetStore(_cacheDir, _ =>
        {
            calls++;
            throw new IOException("模擬寫入中途失敗");
        }, "stamp");

        Assert.Throws<IOException>(() => store.EnsureExtracted());

        // provider 在首個資產就拋出，故每輪僅開檔一次即中止：
        // 共 2 次呼叫 = 首輪 1 次 + 重試恰好 1 次，再失敗拋 IOException。
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ManifestResourceName_MatchesEmpiricalLogicalNames()
    // 實測鎖定：2026-10-31 於 Windows 以 dummy 資產建置後呼叫
    // GetManifestResourceNames()，得 LogicalName="AITranslator.Tts.%(RecursiveDir)%(Filename)%(Extension)"
    // 產出——根層檔案 RecursiveDir 為空，子目錄 RecursiveDir 以反斜線分隔（非 '/' 轉 '_'）。
    // 這些名稱為就地觀察所得的實際值，非理論推導。
    {
        var expected = new Dictionary<string, string>
        {
            ["ptts_ffi.dll"] = @"AITranslator.Tts.ptts_ffi.dll",
            ["model/config.json"] = @"AITranslator.Tts.model\config.json",
            ["model/tokenizer.json"] = @"AITranslator.Tts.model\tokenizer.json",
            ["model/model.q8.gguf"] = @"AITranslator.Tts.model\model.q8.gguf",
            ["model/default-voice.safetensors"] = @"AITranslator.Tts.model\default-voice.safetensors",
        };

        Assert.Equal(expected.Keys.OrderBy(k => k), TtsAssetStore.AssetNames.OrderBy(k => k));
        foreach (var (asset, resourceName) in expected)
        {
            Assert.Equal(resourceName, TtsAssetStore.ManifestResourceName(asset));
        }
    }
}