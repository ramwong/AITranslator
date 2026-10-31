# UI 重新設計（紙感）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把被 revert 的彈窗行為層帶回 main，並依紙感（paper/ink 淺色）設計完全重繪彈窗、設定表單與框選框。

**Architecture:** 三層：`UI/Theme.cs` 集中 token；行為接線從被 revert 的 `23d6fbe` blob 移植（OllamaClient 合併 OCR prompt、AppController 的 SetSource/TTS/設定路由、PopupForm 公開 API——只取行為，視覺全依 spec 重寫）；兩個表單重排（D2 快捷鍵優先、inline 驗證）。不新增設定欄位、不改設定檔 schema。

**Tech Stack:** .NET 8 C# / WinForms（內建無新套件）

**Spec:** `docs/superpowers/specs/2026-10-31-ui-redesign-design.md`（配色 token 表、彈窗結構、錯誤兩級、設定 D2 排序都在 spec 裡，任務不重抄）

## Global Constraints

- 環境：Windows 10 19045、`HighDpiMode.PerMonitorV2` 已啟用，不得移除
- 執行測試一律加 `DOTNET_ROLL_FORWARD=LatestMajor`（本機 SDK 高於專案 target）
- 不新增 NuGet 套件；不新增設定檔欄位（`AppSettings` 維持 main 現況，`PopupLayout` 不復活）
- 彈窗 no-activate 合約不可回歸：`ShowWithoutActivation`、`CreateParams` 三常數（0x08000000|0x00000080|0x00000008）原樣保留
- 所有顏色只能出自 `Theme`；表單程式碼內不得再出現新的 `Color.FromArgb`/`Color.White` 魔術值（`RegionSelectorForm.CurtainColor` 除外，值不變）
- 中文註解與 XML doc 跟隨 codebase 現有風格
- commit 訊息結尾加 `Co-Authored-By: Claude Code <noreply@anthropic.com>`

## Review Focus

1. **持久錯誤被 timer 關掉**：LLM/HTTP/OCR 失敗（`SetText(isError:true)`）與 TTS 初始化失敗**不得**掛 2500ms 自動關閉 timer；只有 `ShowToast`／TTS 失敗提示走自動關。任務 3 驗收：手動觸發一次翻譯失敗，彈窗持續顯示錯誤直到自行關閉。
2. **按鈕搶焦點**：彈窗加入按鈕後，點擊任何按鈕仍不得取得焦點（`WS_EX_NOACTIVATE` 完整性）。任務 3 手動驗收：彈窗顯示中切換應用程式打字不中斷。
3. **合併輸出解析容錯**：模型輸出缺原文段、分隔符前後 CRLF、無分隔符——解析必須 fallback 而非丟例外。任務 2 測試釘死。
4. **錄製態的雜鍵**：純修飾鍵（Ctrl 單按、Shift 單按）必須被忽略而非錄成半成品；`Esc` 取消錄製、恢復現值。任務 5 測試釘死 FormatKeys、手測 Esc。
5. **複製原文的剪貼簿獨佔**：`Clipboard.SetText` 抛 `ExternalException` 時兩顆複製鈕都只靜默略過、不讓彈窗卡死。任務 3 實作同一 try/catch helper，任務 7 手測。

---

### Task 1: Theme token 層 + 框選矩形靛青邊

**Files:**
- Create: `UI/Theme.cs`
- Modify: `UI/RegionSelectorForm.cs:91`（`FrameColor`）
- Test: `tests/AITranslator.Tests/SanityTests.cs`（加一個測試方法）

**Interfaces:**
- Produces: `AITranslator.UI.Theme` —— `internal static class`，欄位：`Paper, Panel, Hairline, Ink, SourceText, Caption, FooterBg, Accent, Error, MutedBtn`（型別全是 `Color`）；尺寸常數 `FooterHeight=36, StatusHeight=26, PanelPadding=10, CardPadding=14`（`int`）；`FontName = "Microsoft JhengHei"`（`const string`）。後續所有任務 consumes 這些名。

- [ ] **Step 1: 寫失敗測試**

```csharp
[Fact]
public void Theme_TokensMatchSpec()
{
    ColorAssert(Theme.Paper,      252, 250, 246); // #FCFAF6
    ColorAssert(Theme.Panel,      243, 240, 232); // #F3F0E8
    ColorAssert(Theme.Hairline,   231, 226, 214); // #E7E2D6
    ColorAssert(Theme.Ink,         38,  34,  27); // #26221B
    ColorAssert(Theme.SourceText, 138, 133, 120); // #8A8578
    ColorAssert(Theme.Caption,    168, 162, 148); // #A8A294
    ColorAssert(Theme.FooterBg,   247, 245, 238); // #F7F5EE
    ColorAssert(Theme.Accent,      43,  95, 205); // #2B5FCD
    ColorAssert(Theme.Error,      194,  65,  12); // #C2410C
    ColorAssert(Theme.MutedBtn,   110, 106,  94); // #6E6A5E
}
// ColorAssert helper：assert c.R/g/b 各相等（SanityTests 內私有static方法）
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/AITranslator.Tests --filter Theme_TokensMatchSpec`
Expected: FAIL（Theme 不存在）

- [ ] **Step 3: 建立 `UI/Theme.cs`**：spec「配色 token」表逐值 `Color.FromArgb(r,g,b)`；加 XML doc 註明值來自 spec 表、各 token 用途。樣式 helper（flat 按鈕等）本任務**不建**，等 Task 3/6 需要時再抽入。

- [ ] **Step 4: 跑測試確認通過**（同上指令，Expected: PASS）

- [ ] **Step 5: `RegionSelectorForm.cs`** 的 `FrameColor`（行 91，現為 `Color.White`）改為 `Theme.Accent`；`CurtainColor` 不動。跑既有 `RegionSelectorTests`（filter RegionSelector）Expected: PASS（現有測試不釘白色則無需改；若有釘白色的斷言，改成釘 Accent——屬本任務範圍）。

- [ ] **Step 6: Commit**

```bash
git add UI/Theme.cs UI/RegionSelectorForm.cs tests/AITranslator.Tests/SanityTests.cs
git commit -m "feat: theme token layer and OCR region accent frame"
```

---

### Task 2: 還原 OllamaClient 合併 OCR prompt（行為層）

> 被退的三個行為 commit（`6e06447`、`a67e994`、`23d6fbe` 修訂）整體狀態 = `23d6fbe` blob。本任務直接取 blob 補丁，只動 service 與其測試。

**Files:**
- Modify: `Services/OllamaClient.cs`、`tests/AITranslator.Tests/OllamaClientTests.cs`

**Interfaces:**
- Produces（下游 Task 4 使用）:
  - `public Task<TranslateResult> RecognizeSourceAndTranslateAsync(AppSettings settings, string imageBase64, CancellationToken ct)`
  - `public static (string Source, string Translation) SplitCombinedOcrOutput(string output)` —— 無分隔符時 fallback：`(string.Empty, output)`
  - 常數 `public const string OcrCombinedPrefix`、`public const string OcrCombinedSeparator = "==="`
- Consumes: 既有 `RecognizeAsync`、`TranslateResult`（不動）

- [ ] **Step 1: 還原測試檔至 blob 版本**

```bash
git checkout 23d6fbe -- tests/AITranslator.Tests/OllamaClientTests.cs
```
（blob 的測試檔是 main 現況測試 + 合併 prompt 測試的超集；先解 a67e994 已裁示的「測試端先解析 payload」風格）

- [ ] **Step 2: 跑測試確認失敗**

Run: `DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/AITranslator.Tests --filter OllamaClientTests`
Expected: FAIL（`RecognizeSourceAndTranslateAsync`/`SplitCombinedOcrOutput` 不存在）

- [ ] **Step 3: 還原服務檔至 blob 版本**

```bash
git checkout 23d6fbe -- Services/OllamaClient.cs
```
（含 23d6fbe 的 CRLF trim 修正——blob 是行為層最終定案版）

- [ ] **Step 4: 跑測試確認通過**

Run: 同 Step 2。Expected: PASS（既有 RecognizeAsync 等 6 個測試一起變綠）

- [ ] **Step 5: 全量測試無回歸**

Run: `DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/AITranslator.Tests`
Expected: 綠（main 基準 82 + 本任務新增）

- [ ] **Step 6: Commit**

```bash
git add Services/OllamaClient.cs tests/AITranslator.Tests/OllamaClientTests.cs
git commit -m "feat: restore combined OCR prompt returning source and translation"
```

---

### Task 3: PopupForm 重繪（紙感骨架 + 公開 API + 行為邏輯）

> 行為以 `git show 23d6fbe:UI/PopupForm.cs` 為參考移植「API、狀態、事件與複製語意」；**視覺全部依 spec「彈窗」節重寫**（blob 是深色版，顏色/結構不搬）。從 main 現有的 `UI/PopupForm.cs` 起步改（其 no-activate/定位/DismissHook/toast timer/Dispose 骨架全部保留）。

**Files:**
- Modify: `UI/PopupForm.cs`（整檔重寫結構）、`tests/AITranslator.Tests/SanityTests.cs`（若加可測邏輯）

**Interfaces:**
- Consumes: `Theme`（Task 1）
- Produces（Task 4 使用——名稱簽章照 `23d6fbe` blob，逐字不改）:
  - `public void ShowTranslation(Point anchor, string statusText, string? source = null)`
  - `public void SetSource(string source)`
  - `public void SetText(string result, bool isError = false)`
  - `public void ShowToast(Point mousePos, string text)`（main 現況簽章）
  - `public void ShowTtsStatus(string? text, bool isError = false)`
  - `public bool IsBusy`、`public bool IsShowingResult`
  - `public event Action<string>? TtsRequested`、`public event Action? SettingsRequested`

**結構（spec「彈窗」節的落實）：**

```
PopupForm（現有合約不動：None 邊框、no-activate 三常數、ComputePosition、DismissHook、OnClosed/Dispose 清理）
├─ _sourcePanel  Dock=Top（高由內容 clamp，含 Panel 色 + Hairline 邊框；Padding≈PanelPadding）
│   ├─ 上緣列（Dock=Top）：Label「選取文字」（Caption 色 11px）+ 右側兩顆幽靈文字鈕「🔊 朗讀」「⧉ 複製原文」（MutedBtn 色）
│   └─ _sourceBox：TextBox ReadOnly 無邊框 BackColor=Panel、SourceText 色、WordWrap、垂直捲軸常駐、Dock=Fill
├─ _statusBar  Dock=Bottom、高度 Theme.StatusHeight（FooterBg 底、上緣 Hairline 線）：
│   8px 圓點 Label（忙碌=Ink 色 / 錯誤=Error 色）+ 狀態文字；無狀態時整條 Hide()
├─ _footer  Dock=Bottom、高度 Theme.FooterHeight（FooterBg 底、上緣 Hairline 線）：
│   _copyButton「📋 複製」（Dock=Left、Flat 樣式、Accent 底 + Paper 色字）｜_settingsButton「⚙」（Dock=Right、幽靈鈕）
└─ _translationBox  Dock=Fill：TextBox ReadOnly 無邊框 BackColor=Paper、Ink 色、字級=FontSize+2（builder 決定，與 spec 留言一致）、WordWrap、垂直捲軸常駐
```

控制項加入順序遵循 WinForms dock 逆向規則（先 Fill 再 Bottom，同現況註解）。字體：統一 `Theme.FontName`；建構子仍以 `settings.FontSize` 為基準（沿用現況 dispose `_font` 慣例，兩個 Font 物件都要釋放）。

- [ ] **Step 1: 建骨架**：依上述結構重排 `Controls`、命名與佈局程式碼；按鈕 `FlatStyle.Flat` + `FlatAppearance`（hover 用 Panel 色近似 mockup；無 hover 需求的字段留 builder）。

- [ ] **Step 2: 公開 API 與行為**（對照 blob 行為語意逐項移植，文字稿在 blob）：
  - `ShowTranslation(anchor, statusText, source)`：busy=true、`(可選)SetSource(source)`、譯文清空、狀態條顯示忙碌點 + statusText、複製鈕禁用；重用不重定位（main 現況行為）。
  - `SetSource(source)`：填 `_sourceBox`；空字串/白字串 → 🔊/⧉ 禁用。
  - `SetText(result, isError)`：`isError=true` → 狀態條（Error 色點+字）**持久顯示**、複製禁用、`_result=null`；`isError=false` → 填譯文區、複製依空值禁用/啟用。**不得**觸發 2500ms timer。
  - `ShowTtsStatus(text, isError)`：`text=null/空` → 狀態條 Hide；`isError=true` → Error 色錯誤提示 + **2500ms 自動關**（沿用 toast timer 機制，`StopToastTimer` 全路徑清理不變）；其餘 → muted 文字（如「TTS 初始化中…」「朗讀中…」由 AppController 傳字串，表單不硬編 TTS 文案）。
  - `ShowToast`：改為在狀態條顯示提示文字**取代**目前「塞進主文區」的做法，其餘（位置、2500ms、busy=false）不變。
  - `🔊` click → `TtsRequested?.Invoke(_sourceBox.Text 非空)`；空原文不發事件。`⧉ 複製原文` click → `CopyToClipboard(_sourceBox.Text)`、**不關閉**彈窗。`⚙` click → `SettingsRequested?.Invoke()`。
  - `CopyToClipboard` 私有 helper：try `Clipboard.SetText`、catch `ExternalException` 靜默略過——兩顆複製鈕共用（Review Focus 5）。
- [ ] **Step 3: build + 全量測試**

Run: `dotnet build && DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/AITranslator.Tests`
Expected: build 無警告新增、測試綠（PopupForm 不新增單測；可測邏輯已在 Task 2/5）

- [ ] **Step 4: 手動驗收**（spec 驗收線 + Review Focus 1/2/5）：`dotnet run` 實跑——翻譯中→完成複製、無選取 toast、點外關閉/Esc、按鈕不搶焦點、失敗訊息持久顯示。

- [ ] **Step 5: Commit**

```bash
git add UI/PopupForm.cs
git commit -m "feat: paper-look popup with source panel, status bar and tts/settings wiring"
```

---

### Task 4: AppController 行為接線移植

> 參考 `git show 23d6fbe:AppController.cs`，移植其 popup 相關 diff（a6610d7 + ae44849 + 23d6fbe 修正的合併體），**剝離 `PopupLayout` 相關**（spec：單一版面、`ShowSource` 恆真、無新設定）。Task 3 的 API 逐字吻合。

**Files:**
- Modify: `AppController.cs`

**Interfaces:**
- Consumes: Task 2（`RecognizeSourceAndTranslateAsync`/`SplitCombinedOcrOutput`）、Task 3（PopupForm API）
- Produces: 無下游（endpoint）；對外合約（熱鍵→彈窗流程、TTS 狀態機、設定開啟）行為不變

移植要點（blob 文字稿為準，逐一）：

- `DeliverToPopup(string message, bool isError = false, string? source = null)`：先 `SetSource(source)`（非 null 時）再 `SetText(message, isError)`，**不重新定位**。
- `TranslateAndShowAsync`：成功 → `DeliverToPopup(譯文, source: 原選取文字)`；空譯文 → `DeliverToPopup("未取得翻譯內容", isError: true)`（沿用 blob 文案常數）；失敗 → `DeliverToPopup(err, isError: true)`。
- `OcrFlowAsync` 兩次呼叫模式：辨識成功後 `CompleteOcrFlow(result, requestId, source: 辨識文字)`；blob 的 `CompleteOcrFlow(..., string? source = null)` 簽章照搬。
- `OcrFlowAsync` 直譯模式（`OcrTranslateInVision=true`）：改呼叫 `RecognizeSourceAndTranslateAsync` → `SplitCombinedOcrOutput` → 以譯文建 result、`CompleteOcrFlow(result, requestId, source: 解析出的 source)`。兩模式共享同一個 `DeliverToPopup` 出口。
- `⚙` 路由：`OnPopupSettingsRequested` → 進行中則 `DiscardPopup()` → `ShowSettings()`（blob 作法）。
- `🔊` 路由：`OnPopupTtsRequested(sourceText)`：記 `_pendingTtsFromPopup = true` 走既有 TTS 狀態機；`OnTtsStateChanged(Ui)`：`Loading → ShowTtsStatus(TtsInitializingText)`、`Speaking → ShowTtsStatus(朗讀中文案)`、`Ready → ShowTtsStatus(null)`；**彈窗發動**的失敗 → `ShowTtsStatus(FormatTtsFailed(...), isError: true)`、**熱鍵發動**的失敗維持既有 `ShowToast` 路徑（blob 的 23d6fbe 修正）。
- 「朗讀中」的字串常數（如 `"朗讀中…"`）定義在 AppController 對齊既有 `TtsInitializingText` 常數群。

- [ ] **Step 1: 移植上述成員到 `AppController.cs`**（對照 blob；`PopupLayout` 相關成員不移植、`ShowSource` 刪除、`AppSettings` 不動）
- [ ] **Step 2: build + 全量測試**

Run: `dotnet build && DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/AITranslator.Tests`
Expected: 全綠（TtsService/OllamaClient/SettingsService 既有測試是本層合約的代理測試）

- [ ] **Step 3: 手動驗收**：翻譯熱鍵（原文面板即時顯示選取文字）、🔥TTS 熱鍵與 🔊 按鈕（初始化中/朗讀中/失敗各自落點）、⚙ 開設定、OCR 兩模式（直譯模式原文面板有辨識文字）。
- [ ] **Step 4: Commit**

```bash
git add AppController.cs
git commit -m "feat: route popup source, tts and settings through existing state machines"
```

---

### Task 5: HotkeyCaptureBox 控制項（點擊錄製）

**Files:**
- Create: `UI/HotkeyRecorder.cs`、`UI/HotkeyCaptureBox.cs`
- Test: `tests/AITranslator.Tests/HotkeyRecorderTests.cs`（新建）

**Interfaces:**
- Consumes: 無（純新增；重複驗證由呼叫端注入）
- Produces:
  - `UI/HotkeyRecorder`（`internal static class`）：`public static string? FormatKeys(Keys keys)` —— 純修飾鍵或無鍵回 `null`；否則回正規化字串，修飾鍵固定順序 `Ctrl`、`Alt`、`Shift`，主鍵為單一英文字母（大寫）或 `F1`–`F12` 或方向/空白等可命名鍵。與 `HotkeyParser.Parse` 互逆的子集。
  - `UI/HotkeyCaptureBox`（`Control`，繼承自 Panel 或 TextBox 自選）：`public string Value`（get/set 正規化字串）；錄製態由 click 進入、`Esc` 取消回 restore `Value`；`public Func<string, bool>? DuplicateValidator`——錄入時呼叫，true → inline 顯示錯誤並保持 `Value`；`public event EventHandler<string>? ValueChanged`。

- [ ] **Step 1: 寫失敗測試**

```csharp
[Theory]
[InlineData(Keys.Control | Keys.Alt | Keys.E, "Ctrl+Alt+E")]
[InlineData(Keys.Control | Keys.F5, "Ctrl+F5")]
[InlineData(Keys.Shift | Keys.ControlKey, null)]   // 純修飾鍵 → null
[InlineData(Keys.Control | Keys.ShiftKey, null)]   // 同上（ShiftKey 是鍵身不是 Shift 修飾）
[InlineData((Keys)0, null)]
public void FormatKeys_NormalizesAndRejectsModifierOnly(Keys keys, string? expected)
    => Assert.Equal(expected, HotkeyRecorder.FormatKeys(keys));

[Fact]
public void FormatKeys_MatchesHotkeyParserRoundTrip()
{
    var formatted = HotkeyRecorder.FormatKeys(Keys.Control | Keys.Alt | Keys.S);
    Assert.True(HotkeyParser.Parse(formatted).success); // FormatKeys 的輸出必須被既有 Parser 接受
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/AITranslator.Tests --filter HotkeyRecorderTests`
Expected: FAIL（HotkeyRecorder 不存在）

- [ ] **Step 3: 實作 `UI/HotkeyRecorder.cs`（純邏輯，先於控制項）**：`Keys` 位元拆解 → 修飾鍵 token 依 Ctrl/Alt/Shift 固定序 + `Keys.KeyCode & ~Keys.Modifiers` 主鍵白名單（字母/F1-F12/其餘可命名鍵 builder 自訂最小集，`null` 回傳）。控制項本體（錄製態、虛線框、Accent 顯示現值、`Esc` 還原）同檔或同任務完成；**Esc/點擊進出狀態機**不自測（WinForms 事件薄殼）。
- [ ] **Step 4: 跑測試確認通過** → **Step 5: 手測**：`dotnet run` 開設定預覽（或暫掛到現有表單任一處）：點欄位 → 直接按 `Ctrl+Alt+E` 錄入、純 Ctrl 不收、Esc 還原。
- [ ] **Step 6: Commit**

```bash
git add UI/HotkeyCaptureBox.cs UI/HotkeyRecorder.cs tests/AITranslator.Tests/HotkeyRecorderTests.cs
git commit -m "feat: click-to-record hotkey capture control"
```

---

### Task 6: SettingsForm D2 重排 + inline 驗證

**Files:**
- Modify: `UI/SettingsForm.cs`

**Interfaces:**
- Consumes: `Theme`（Task 1）、`HotkeyCaptureBox`（Task 5）、`HotkeyParser`（既有）
- Produces: `SettingsForm(path, onChanged)` 公開建構與 `SettingsChangedCallback` 不變（AppController 呼叫端零改動）；`AppSettings` 存檔路徑與欄位對應不變

- [ ] **Step 1: 版面重排**：TableLayoutPanel 維持兩欄結構，加入第三種列型——小節標題列（跨 2 欄、`Theme.Caption` 色 11px + 髮絲 `Panel`/`Hairline` 分隔）。順序依 spec D2：**快捷鍵**（三組改為 `HotkeyCaptureBox`）→ **彈窗外觀**（寬/高/字級）→ **翻譯**（範本/模型/網址/上限）→ **辨識 OCR**（模型/勾選）→ **其他**（開機自動啟動）。表單 `FlatStyle`/底色套 `Theme`（Paper 底、Ink 字）。
- [ ] **Step 2: 驗證 inline 化**：移除逐欄 `MessageBox.Show`；`ShowValidationError(box, msg)` 改為：欄位右側紅點標記（`Theme.Error` 的小 Label/AutoSize `!` 圓點）+ 錯誤文字集中顯示在儲存鈕列上方的一行錯誤 Label；`DialogResult.None` 防關窗語意保留。三組錄製盒的 `DuplicateValidator`：檢查另兩組 `Value` 相同 → 當場 inline 錯誤（Review/Task 5 串接）。既有全部驗證規則（範圍/NaN/互斥/空白）原樣移植到 inline 形式。
- [ ] **Step 3: 尺寸與錯誤聚焦**：維持 `AutoSize`/`GrowAndShrink`；`OnSaveButtonClick` 首個錯誤 → `ScrollControlIntoView` + `Focus`（表單高於 WorkingArea 時先 clamp 高度 + `AutoScroll = true`，一次 clamp 於 `OnShown` 或 resize 都可，builder 擇一）。存檔 IO 失敗的 `MessageBox` 也改同一 inline 錯誤行（**不留**MessageBox 路徑）。
- [ ] **Step 4: build + 全量測試**：`dotnet build && DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/AITranslator.Tests` → 綠（SettingsServiceTests 是存檔合約代理）。
- [ ] **Step 5: 手動驗收**：D2 排序顯示、點舊值 → 改錄 `Ctrl+Alt+S` → 錄到與現值同鍵 → 當場紅點；存檔成功回彈窗套用；IO 失敗（模擬：占用檔）inline 提示不關窗。
- [ ] **Step 6: Commit**

```bash
git add UI/SettingsForm.cs
git commit -m "feat: settings form grouped d2 with click-record hotkeys and inline validation"
```

---

### Task 7: 收尾 — 驗收、手冊與整段審查

**Files:**
- Modify: `README.md`（彈窗與設定描述段落）

- [ ] **Step 1: 全量驗證**

Run: `dotnet build -c Release && DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/AITranslator.Tests`
Expected: build 無警告新增、測試全綠

- [ ] **Step 2: spec 驗收線手測**（spec「驗收線」節照單跑：翻譯/OCR/TTS/錯誤 toast/點外關閉/Esc/錄製快捷鍵/重複鍵/存檔/開機啟動同步），Review Focus 5 項逐一確認。
- [ ] **Step 3: README 更新**：對應 spec 決策（紙感彈窗、原文面板含朗讀與複製原文、動作列、設定 D2 點擊錄製、inline 驗證）；**移除**上輪遺留的「彈窗版面」相關段落（README 現況應已乾淨，確認無 `彈窗版面`、`📋`、`🔊` 舊描述殘留）。
- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: paper-look popup and click-to-record hotkeys in readme"
```