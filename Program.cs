using AITranslator.Models;
using AITranslator.Services;

namespace AITranslator;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AITranslator",
            "settings.json");

        var settings = SettingsService.Load(settingsPath);

        using var controller = new AppController(settings, settingsPath);
        Application.Run(); // 訊息迴圈：熱鍵訊息與系統匣選單皆由此驅動；離開時 Application.Exit()
    }
}