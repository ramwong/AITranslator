using AITranslator.Models;
using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AITranslatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Defaults_MatchSpec()
    {
        var s = new AppSettings();

        Assert.Equal(420, s.PopupWidth);
        Assert.Equal(260, s.PopupHeight);
        Assert.Equal(12f, s.FontSize);
        Assert.Equal("請將以下內容翻譯成繁體中文（台灣用語），只輸出譯文，不要解釋或加註:\n{text}", s.PromptTemplate);
        Assert.Equal("beam:cloud", s.Model);
        Assert.Equal("http://localhost:11434", s.OllamaUrl);
        Assert.Equal("Ctrl+Alt+E", s.Hotkey);
        Assert.Equal(5000, s.MaxTextLength);
        Assert.False(s.StartWithWindows);
    }

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var original = new AppSettings
        {
            PopupWidth = 500,
            PopupHeight = 300,
            FontSize = 14.5f,
            PromptTemplate = " Translate this:\n{text} ",
            Model = "llama3",
            OllamaUrl = "http://127.0.0.1:8080",
            Hotkey = "Shift+F5",
            MaxTextLength = 1000,
            StartWithWindows = true,
        };

        SettingsService.Save(_settingsPath, original);
        var loaded = SettingsService.Load(_settingsPath);

        Assert.Equal(original.PopupWidth, loaded.PopupWidth);
        Assert.Equal(original.PopupHeight, loaded.PopupHeight);
        Assert.Equal(original.FontSize, loaded.FontSize);
        Assert.Equal(original.PromptTemplate, loaded.PromptTemplate);
        Assert.Equal(original.Model, loaded.Model);
        Assert.Equal(original.OllamaUrl, loaded.OllamaUrl);
        Assert.Equal(original.Hotkey, loaded.Hotkey);
        Assert.Equal(original.MaxTextLength, loaded.MaxTextLength);
        Assert.Equal(original.StartWithWindows, loaded.StartWithWindows);
    }

    [Fact]
    public void Load_MissingTtsHotkeyField_DefaultsCtrlAltS() // 現行 settings.json 無此欄
    {
        File.WriteAllText(_settingsPath, "{}");

        var loaded = SettingsService.Load(_settingsPath);

        Assert.Equal("Ctrl+Alt+S", loaded.TtsHotkey);
    }

    [Fact]
    public void SaveLoad_RoundTrips_TtsHotkey() // 寫 "Ctrl+Shift+T" 載回相同值
    {
        var original = new AppSettings { TtsHotkey = "Ctrl+Shift+T" };

        SettingsService.Save(_settingsPath, original);
        var loaded = SettingsService.Load(_settingsPath);

        Assert.Equal("Ctrl+Shift+T", loaded.TtsHotkey);
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var loaded = SettingsService.Load(_settingsPath);

        var expected = new AppSettings();
        Assert.Equal(expected.PopupWidth, loaded.PopupWidth);
        Assert.Equal(expected.PopupHeight, loaded.PopupHeight);
        Assert.Equal(expected.FontSize, loaded.FontSize);
        Assert.Equal(expected.PromptTemplate, loaded.PromptTemplate);
        Assert.Equal(expected.Model, loaded.Model);
        Assert.Equal(expected.OllamaUrl, loaded.OllamaUrl);
        Assert.Equal(expected.Hotkey, loaded.Hotkey);
        Assert.Equal(expected.MaxTextLength, loaded.MaxTextLength);
        Assert.Equal(expected.StartWithWindows, loaded.StartWithWindows);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(_settingsPath, "{");

        var loaded = SettingsService.Load(_settingsPath);

        var expected = new AppSettings();
        Assert.Equal(expected.PopupWidth, loaded.PopupWidth);
        Assert.Equal(expected.PopupHeight, loaded.PopupHeight);
        Assert.Equal(expected.FontSize, loaded.FontSize);
        Assert.Equal(expected.PromptTemplate, loaded.PromptTemplate);
        Assert.Equal(expected.Model, loaded.Model);
        Assert.Equal(expected.OllamaUrl, loaded.OllamaUrl);
        Assert.Equal(expected.Hotkey, loaded.Hotkey);
        Assert.Equal(expected.MaxTextLength, loaded.MaxTextLength);
        Assert.Equal(expected.StartWithWindows, loaded.StartWithWindows);
    }
}