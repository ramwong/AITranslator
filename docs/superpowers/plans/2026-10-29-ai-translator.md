# AI Translator 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Windows 常駐翻譯工具：全域快捷鍵 Ctrl+Alt+E 擷取選字，呼叫 Ollama 本地模型，在滑鼠附近彈窗顯示繁體中文翻譯。

**Architecture:** 單一 WinForms 進程：`AppController` 協調全域快捷鍵（Win32 `RegisterHotKey`）→ 剪貼簿擷取（模擬 Ctrl+C + 序號驗證 + 還原）→ `PopupForm`（no-activate 彈窗，LL hook 觀察點擊外部/Esc）→ 非同步 `OllamaClient`。設定以 JSON 存 `%APPDATA%\AITranslator\settings.json`。

**Tech Stack:** C# / .NET 8 WinForms（無第三方 NuGet 依賴）、HttpListener-free `HttpClient`、xunit 測試、GitHub Actions windows-latest build。

**Spec:** `docs/superpowers/specs/2026-10-29-ai-translator-design.md`

## Global Constraints

- TargetFramework `net8.0-windows`，`UseWindowsForms=true`，`OutputType=WinExe`
- 主專案零第三方 NuGet 依賴（只有測試專案用 xunit）
- Publish：single-file、self-contained、win-x64
- 預設值（verbatim from spec）：OllamaUrl `http://localhost:11434`、Model `beam:cloud`、
  Hotkey `Ctrl+Alt+E`、Prompt `請將以下內容翻譯成繁體中文（台灣用語），只輸出譯文，不要解釋或加註:\n{text}`、
  Popup 420×260、FontSize 12、MaxTextLength 5000、StartWithWindows false
- 設定檔：`%APPDATA%\AITranslator\settings.json`，損壞/不存在 → 用預設值
- 反目標（不可違反）：剪貼簿在使用者程式碼任何失敗路徑都要還原；彈窗 hook 只觀察不攔截按键
- Commit message 用 conventional commits（`feat:` / `test:` / `chore:` / `ci:`）

## Review Focus

1. **剪貼簿還原**：Ctrl+C 超時/擷取失敗時備份必須還原 — `CaptureServiceTests.CopyTimeout_RestoresBackup`（Task 5；真實剪貼簿路徑由 Task 11 手動冒煙驗證）
2. **Ctrl+C 敵對應用**（受保護 PDF、終端機）：序號不變 → 回報「未選取文字」而非空字串照翻 — `CaptureServiceTests.SequenceUnchanged_MeansNoSelection`（Task 5）
3. **Ollama 連不上 / model 不存在**：彈窗顯示易讀中文錯誤，不 crash — `OllamaClientTests.ConnectionRefused_GivesReadableError`、`OllamaClientTests.ModelNotFound_GivesReadableError`（Task 4）
4. **hook 吞鍵**：keyboard hook 處理程序必須永遠 `CallNextHookEx`（不攔截）— Task 7 的程式碼檢查步驟明確驗證此點
5. **設定檔損壞**：半截 JSON / 型別錯誤 → 落回預設值、程式正常啟動 — `SettingsServiceTests.CorruptFile_FallsBackToDefaults`（Task 2）

---

### Task 1: 專案腳手架

**Files:**
- Create: `AITranslator.csproj`、`Program.cs`、`tests/AITranslator.Tests/AITranslator.Tests.csproj`、`tests/AITranslator.Tests/SanityTests.cs`

**Interfaces:**
- Produces: 可 build 的 WinExe 專案與 xunit 測試專案（引用主專案）

- [ ] **Step 1: 建立主專案檔**

`AITranslator.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>AITranslator</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
  </PropertyGroup>
</Project>
```

`Program.cs` 暫時只放 `static void Main()` → 顯示一個空 MessageBox？不改：用 `Application.Run()` 空跑（能啟動即關），Task 10 會重寫。

- [ ] **Step 2: 建立測試專案**（net8.0-windows、引用主專案、xunit + Microsoft.NET.Test.Sdk + xunit.runner.visualstudio）
- [ ] **Step 3:** `dotnet test` 全綠（一個 `SanityTests.AssertTrue` 心測）
- [ ] **Step 4: Commit** `git add AITranslator.csproj Program.cs tests/ && git commit -m "chore: scaffold WinForms project and test project"`

### Task 2: Settings 模型與存取服務

**Files:**
- Create: `Models/AppSettings.cs`、`Services/SettingsService.cs`
- Test: `tests/AITranslator.Tests/SettingsServiceTests.cs`

**Interfaces:**
- Produces:
  - `AppSettings`：屬性 `PopupWidth/PopupHeight(int)`、`FontSize(float)`、`PromptTemplate/Model/OllamaUrl/Hotkey(string)`、`MaxTextLength(int)`、`StartWithWindows(bool)`，含 Global Constraints 的預設值
  - `SettingsService.Load(path) -> AppSettings`（檔案不存在或 JSON 損壞 → 預設值）
  - `SettingsService.Save(path, AppSettings)`

- [ ] **Step 1: 寫失敗測試**：`Defaults_MatchSpec`（逐欄位驗證預設值）、`RoundTrip_PreservesValues`、`MissingFile_ReturnsDefaults`、`CorruptFile_FallsBackToDefaults`（寫入 `"{"`）
- [ ] **Step 2:** `dotnet test` → FAIL
- [ ] **Step 3: 實作**：`System.Text.Json`，`AppSettings` 預設值直接在屬性初始化器；Load 包 try/catch 回傳 `new AppSettings()`
- [ ] **Step 4:** `dotnet test` → PASS
- [ ] **Step 5: Commit** `feat: settings model and JSON persistence`

### Task 3: HotkeyParser

**Files:**
- Create: `Services/HotkeyParser.cs`
- Test: `tests/AITranslator.Tests/HotkeyParserTests.cs`

**Interfaces:**
- Produces: `(bool success, uint mods, uint key) HotkeyParser.Parse(string "Ctrl+Alt+E")`；`MOD_ALT=0x1, MOD_CONTROL=0x2, MOD_SHIFT=0x4`，鍵 = `char.ToUpperInvariant()` 的字碼（如 `'E'`→0x45）；接受任意 `Ctrl/Alt/Shift` 組合 + 單一字母或 F1-F12；無效字串 → success=false

- [ ] **Step 1: 失敗測試**：`Ctrl Alt E`、`Ctrl+Alt+Z`、`Shift+F5`、大小寫混用、`Invalid+Combo`（fail）、空字串（fail）
- [ ] **Step 2:** FAIL → **Step 3: 實作**（單純字串拆解）→ **Step 4:** PASS → **Step 5: Commit** `feat: hotkey string parser`

### Task 4: PromptBuilder 與 OllamaClient

**Files:**
- Create: `Services/PromptBuilder.cs`、`Services/OllamaClient.cs`
- Test: `tests/AITranslator.Tests/OllamaClientTests.cs`、`tests/AITranslator.Tests/PromptBuilderTests.cs`

**Interfaces:**
- Produces:
  - `PromptBuilder.Build(template, text) -> string`：`{text}` 佔位符替換；模板無佔位符 → 模板 + `\n` + text
  - `OllamaClient(HttpClient? handler 注入點)`；`Task<TranslateResult> TranslateAsync(AppSettings, string text, CancellationToken ct)`
  - `TranslateResult(bool Success, string Text, string Error)`；請求 `POST {OllamaUrl.TrimEnd('/')}/api/generate`、body `{"model":...,"prompt":...,"stream":false}`、讀回 `response` 欄位
  - 錯誤映射：無法連線（HttpRequestException）→ `無法連線到 Ollama ({url})，請確認 Ollama 已啟動`；400 且 body 含 `model.*not.*found` → `找不到模型 "{model}"，請確認名稱是否正確`；其他非 2xx → `Ollama 回應錯誤 (HTTP {code})`；取消 → Success=false, Error=""

- [ ] **Step 1: 失敗測試**（用 stub `HttpMessageHandler` 斷言 URL、body 內容、各錯誤訊息；含 stream:false 在 body 中）
- [ ] **Step 2:** FAIL → **Step 3: 實作** → **Step 4:** PASS → **Step 5: Commit** `feat: prompt builder and Ollama API client`

### Task 5: CaptureService（剪貼簿選字擷取）

**Files:**
- Create: `Services/CaptureService.cs`、`Native/Win32.cs`（P/Invoke 集中處：`GetClipboardSequenceNumber`、`SendInput`、`OpenClipboard/CloseClipboard`…）
- Test: `tests/AITranslator.Tests/CaptureServiceTests.cs`

**Interfaces:**
- Produces:
  - `CaptureOutcome { NoSelection, TooLong, Success }`
  - `CaptureService(int maxTextLength)`；`Capture() -> (CaptureOutcome outcome, string text)`：備份文字 → `GetClipboardSequenceNumber()` → SendInput Ctrl+C → 輪詢序號（最長 800ms，step 50ms）→ 對照 Review Focus 2：序號不變 → 還原備份 → `NoSelection`；長度超上限 → `TooLong`；全失敗路徑對照 Review Focus 1 一律先還原備份再回傳
  - 純決策方法供測試：`static CaptureOutcome Decide(uint seqBefore, uint seqAfter, string? text, int max)`
- [ ] **Step 1: 失敗測試**：`Decide` 四案例 — 序號相同→NoSelection、`text=null`→NoSelection、超長→TooLong、正常→Success（對照 Review Focus 1、2）
- [ ] **Step 2:** FAIL → **Step 3: 實作**（Decide + Capture：還原用 `Clipboard.SetText` 包 try/catch；SendInput 失敗同樣走 NoSelection）→ **Step 4:** PASS → **Step 5: Commit** `feat: clipboard-based text capture with backup and restore`

### Task 6: HotkeyService 與隱藏訊息視窗

**Files:**
- Create: `Services/HotkeyService.cs`、`Native/MessageWindow.cs`

**Interfaces:**
- Consumes: `HotkeyParser`（Task 3）
- Produces: `HotkeyService(IntPtr hwnd, Action onHotkey)`；`Register(string hotkeyString) -> bool`（失敗回 false，呼叫端負責通知使用者）；`Unregister()`；隱藏視窗 WndProc 攔 `WM_HOTKEY=0x0312` 觸發 callback（marshal 回主執行緒）

- [ ] **Step 1: 實作**（Win32，無單測 — 由 Task 10 整合與手動冒煙驗證）
- [ ] **Step 2:** `dotnet build` 無錯 → **Step 3: Commit** `feat: global hotkey registration service`

### Task 7: DismissHook（觀察型低層級 hook）

**Files:**
- Create: `Native/DismissHook.cs`

**Interfaces:**
- Produces: `DismissHook : IDisposable`；`static void Begin(Action dismiss)` / `static void End()`；`WH_MOUSE_LL=14`（滑鼠鍵按下且焦點不在彈窗）與 `WH_KEYBOARD_LL=13`（VK_ESCAPE）→ 呼叫 dismiss；**兩個 hook 處理程序一律回傳 `CallNextHookEx`，絕不回傳非零**。Hook 在 Begin/End 之間才安裝，一次只允許一組

- [ ] **Step 1: 實作**，程式碼檢查步驟：確認兩個 hook proc 的每個分支都 `CallNextHookEx`（對照 Review Focus 4，此為明確的檢查清單項目）
- [ ] **Step 2:** `dotnet build` → **Step 3: Commit** `feat: pass-through LL hooks for popup dismissal`

### Task 8: PopupForm（no-activate 翻譯彈窗）

**Files:**
- Create: `UI/PopupForm.cs`

**Interfaces:**
- Produces:
  - `PopupForm : Form`；建構參數 `AppSettings`
  - `void ShowTranslation(Point mousePos, string statusText)`（顯示「翻譯中…」或錯誤）
  - `void SetText(string result)`；`bool IsBusy`
  - 定位：滑鼠右偏 16px、下偏 16px，clamp 進螢幕可視範圍（Task 補：多螢幕以視窗所在螢幕 `Screen.FromPoint` 算）
  - 樣式：無邊框、`ShowWithoutActivation=true`、`CreateParams` 加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`、`ShowInTaskbar=false`
  - 內容：Label（AutoSize、字級用 settings.FontSize）＋「複製」Button（點擊 → `Clipboard.SetText` 目前譯文 → 關閉）
  - 顯示前 `DismissHook.Begin(Close)`；`OnClosed` 呼叫 `DismissHook.End()`

- [ ] **Step 1: 實作**（UI 無單測）
- [ ] **Step 2:** `dotnet build` → **Step 3: Commit** `feat: no-activate translation popup form`

### Task 9: SettingsForm 與 AutoStartService

**Files:**
- Create: `UI/SettingsForm.cs`、`Services/AutoStartService.cs`

**Interfaces:**
- Consumes: `AppSettings`、`SettingsService`（Task 2）、`HotkeyParser`（Task 3）
- Produces:
  - `AutoStartService.SetEnabled(bool)`：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 值 `AITranslator` = 執行檔完整路徑；`IsEnabled() -> bool`
  - `SettingsForm(Action onSettingsSaved)`：欄位 PopupWidth/Height(TextBox)、FontSize(TextBox)、PromptTemplate(多行 TextBox)、Model(TextBox)、OllamaUrl(TextBox)、Hotkey(TextBox，儲存前 `HotkeyParser.Parse` 驗證、失敗顯示 MessageBox 不儲存)、MaxTextLength(TextBox)、StartWithWindows(CheckBox)；整數/浮點欄位格式錯同樣擋下；儲存 = Save + `onSettingsSaved()`

- [ ] **Step 1: 實作**（WinForms UI，無單測）
- [ ] **Step 2:** `dotnet build` → **Step 3: Commit** `feat: settings dialog with autostart toggle`

### Task 10: AppController 與程式進入點（整合）

**Files:**
- Create: `AppController.cs`；Modify: `Program.cs`

**Interfaces:**
- Consumes: Task 2–9 全部（exact 來源見各 task 的 Interfaces 區）
- Produces:
  - `AppController(AppSettings, SettingsService)`：建立隱藏訊息視窗 + `HotkeyService`；快捷鍵觸發流程：
    1. 彈窗已顯示或請求進行中 → 取消舊 `CancellationTokenSource`
    2. `CaptureService.Capture()` → `NoSelection`/`TooLong` → 以 toast 文字顯示後不關（等待使用者點掉）；`TooLong` 顯示「選取文字超過 {max} 字上限」
    3. 彈窗 `ShowTranslation(mouse, "翻譯中…")` → `OllamaClient.TranslateAsync(settings, text, ct)` → `SetText` 或顯示 Error
  - 註冊失敗 → NotifyIcon balloon「快捷鍵註冊失敗，可能被其他程式占用」
  - 設定儲存後：Unregister → 以新 Hotkey 重新註冊、套用彈窗大小/字級
  - `Program.cs`：單一 `NotifyIcon`（系統匣）＋選單（設定 / 離開）；離開時 Unregister + Dispose hook

- [ ] **Step 1: 實作** → **Step 2:** `dotnet build` 無錯 → **Step 3:** 本機跑起來：系統匣圖示出現、設定視窗能開能存、log 無例外（快捷鍵觸發留待 Task 11 手動驗證）→ **Step 4: Commit** `feat: integrate app controller, tray icon, and popup lifecycle`

### Task 11: Release Workflow 與 CI 驗證

**Files:**
- Create: `.github/workflows/build.yml`

- [ ] **Step 1: 寫 workflow**：`on: push + workflow_dispatch`、`runs-on: windows-latest`、`actions/setup-dotnet@v4`（dotnet-version: 8.0.x）、`dotnet publish AITranslator.csproj -c Release -o publish`、`actions/upload-artifact@v4`（name `AITranslator-win-x64`，path `publish/*.exe`）；另加 tag 觸發時 `softprops/action-gh-release` 附 exe
- [ ] **Step 2:** 本機 `dotnet publish AITranslator.csproj -c Release -r win-x64 -o publish` 成功、`publish/AITranslator.exe` 存在（publish 資料夾加進 `.gitignore`）
- [ ] **Step 3: Commit + push**：`git add . && git commit -m "ci: build and upload exe artifact"`；首次 push 含此前所有 commits 與 docs/、memory 無關檔案一併推上 `origin main`
- [ ] **Step 4:** 用 git 憑證呼叫 API 或讓使用者開 https://github.com/ramwong/AITranslator/actions 確認 workflow 綠燈、artifact `AITranslator-win-x64` 內含 `AITranslator.exe`
- [ ] **Step 5: Commit 文件**：spec 與 plan（`docs/`）已隨 Step 3 push，不需額外 commit

### Task 12: 手動冒煙測試（使用者驗收）

**Files:** 無

- [ ] **Step 1: 使用者下載 artifact exe**（或本機 `publish/`）
- [ ] **Step 2: 使用者驗證**：Ollama 啟動且 `ollama list` 有設定中的 model → 任意程式選英文字 → `Ctrl+Alt+E` → 滑鼠旁彈窗「翻譯中…」→ 繁中譯文 → 複製按鈕可用 → 點外部/Esc 關閉 → 剪貼簿原內容仍在
- [ ] **Step 3: 邊界**：不選文字按快捷鍵 →「未選取文字」；Ollama 停掉 → 可讀錯誤；改設定中 model 為不存在名稱 →「找不到模型」