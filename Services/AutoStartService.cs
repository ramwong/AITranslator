using Microsoft.Win32;

namespace AITranslator.Services;

/// <summary>
/// 以登錄 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的
/// 「AITranslator」字串值控制開機自動啟動；值內容為目前執行檔的完整路徑。
///
/// <para><b>best-effort</b>：登錄存取（權限不足、防毒攔截等）可能失敗，
/// 所有方法皆吞掉例外——<see cref="IsEnabled"/> 失敗回傳 false，
/// <see cref="SetEnabled"/> 失敗靜默放棄，呼叫端不需也不得依賴例外處理。
/// </para>
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AITranslator";

    /// <summary>
    /// 設定或取消開機自動啟動。啟用時寫入值；停用時刪除值（值不存在亦不視為錯誤）。
    /// 任何存取失敗皆靜默吞掉（best-effort），不對呼叫端丟出例外。
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            // Run 機碼在正常系統上本就存在；OpenSubKey 失敗時退回 CreateSubKey 兜底。
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                return; // 機碼既開不了也建不了：best-effort 放棄
            }

            if (enabled)
            {
                // Environment.ProcessPath（.NET 8+）回傳目前程序的執行檔完整路徑；
                // PublishSingleFile 時取得的是使用者實際點擊的外層 .exe 本身（非
                // 單檔 bundle 暫存解壓出的 host），因此登錄值指向正確的啟動目標。
                // 路徑加引號，避免含空白目錄（如 Program Files）被開機程序誤拆參數。
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    return;
                }

                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // best-effort：登錄不可寫（權限/防毒）時靜默放棄，不干擾設定流程。
        }
    }

    /// <summary>
    /// 依 Run 機碼是否存在「AITranslator」值判定自動啟動與否；
    /// 讀取失敗（權限、機碼異常）一律回傳 false。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception)
        {
            return false; // best-effort：讀不到視同未啟用
        }
    }
}