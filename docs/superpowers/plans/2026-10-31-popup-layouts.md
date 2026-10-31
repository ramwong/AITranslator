# 彈窗三版面＋設定按鈕＋OCR 省原文 token 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 彈窗依設定以三種版面（左右分欄／上下分段／純譯文）呈現原文＋譯文，內建「⚙ 設定」「🔊 朗讀」按鈕，深色配色，並讓 OCR 在顯示原文版面下以最少請求拿回原文。

**Architecture:** 新增 `PopupLayout` enum 與設定欄位（JSON 隨 `AppSettings` 一併存取）；`PopupForm` 重構為依版面建構的三種佈局＋集中配色常數；`OllamaClient` 新增合併提示詞（翻譯半段仍由使用者範本組成）與輸出解析器；`AppController` 把「原文帶入彈窗」「彈窗 TTS 按鈕／⚙ 接到既有狀態機與設定流程」接上。彈窗視窗層性質（no-activate、DismissHook、toast、定位）保持不變。

**Tech Stack:** .NET 8 / C# / WinForms、xUnit；測試僅覆蓋純決策函式（與 repo 現行慣例一致，表單視覺以手動驗證清單擔保）。

**Spec:** `docs/superpowers/specs/2026-10-31-popup-layouts-design.md`（含使用者於 mockup 確認的按鈕擺法與配色授權）

## Global Constraints

- 彈窗深色配色（verbatim）：底色 `#1E1F24`、分隔線 `#3A3D45`、按鈕底 `#2A2C33`、按鈕 hover `#343841`、原文文字 `#9BA7B8`、主要文字（譯文）`#F2F4F8`、錯誤文字 `#D07A6A`。
- 按鈕文案含 emoji，統一大小：`📋 複製`、`🔊 朗讀`、`⚙ 設定`。
- 版面① 左欄按鈕列：`📋 複製` 最左、`🔊 朗讀` 最右（左右撐開）；右欄（譯文）按鈕列：`📋 複製` 最左、`⚙ 設定` 最右。
- 版面② 上列：`📋 複製`、`🔊 朗讀` 成組右對齊；下列（譯文）：`📋 複製`、`⚙ 設定` 成組右對齊（⚙ 仍最右）。
- 版面③ 底列：`📋 複製` 最左、`⚙ 設定` 最右。
- `⚙ 設定` 點下＝關閉彈窗＋開設定視窗；`🔊 朗讀` 點下朗讀「原文」且**不關彈窗**。
- TTS 按鈕路徑**不套** `MaxTextLength` 預檢；朗讀期間／失敗的狀態顯示於彈窗「按鈕區狀態列」，**不得**覆蓋譯文/原文、不得觸發 toast、不得自動關窗（toast 路徑僅保留給熱鍵觸發的 TTS）。
- 版面③（`TranslationOnly`）的 OCR 單次呼叫路徑**維持現有提示詞與二次呼叫結構**，不得多發影像請求；顯示原文版面＋單次呼叫＝一次請求內輸出原文＋譯文，翻譯半段由 `settings.PromptTemplate` 組成。
- `ShowTranslation`/`SetText` 改為選讀參數（預設值維持舊呼叫點可編譯）：原文一拿到就顯示。
- 錯誤訊息（LLM/HTTP 錯誤、「未取得翻譯內容」）經 `SetText` 送達時用錯誤色。
- popup 的 `WS_EX_NOACTIVATE`／`ShowWithoutActivation`／DismissHook／定位 clamp 行為不變。
- 測試為純邏輯（不建表單 handle、不驗 WinForms 視覺）；視覺驗證走手動清單。
- 彈窗尺寸沿用 `PopupWidth`/`PopupHeight`（整個視窗，不隨版面另立尺寸）。

## Review Focus

1. **深色底 ReadOnly TextBox 文字變灰**：WinForms ReadOnly 控制項須在設 `ReadOnly=true` **之後**才設 `BackColor`/`ForeColor`（repo 已知模式，`PopupForm.cs:88`），譯文不得變不可讀的灰。→ Task 3 手動清單項。
2. **長文折行擠壓**：長原文＋長譯文下，文字區須各有捲軸、按鈕列不變形、狀態列出現時文字區縮短但不覆蓋內容。→ Task 3 手動清單項。
3. **合併輸出解析降級**：模型不吐分隔符時整段視為譯文、原文留空，流程不得中斷。→ Task 2 測試 `SplitCombinedOcrOutput_NoSeparator_FallsBackToTranslation`（分隔符缺席即翻譯回退，不論原文內容為何）。Task 2 測 `SplitCombinedOcrOutput` 分隔符缺席、多分隔符、含縮排分隔行。
4. **版面③ 不得多發請求**：`TranslationOnly` 下單次呼叫做完一次呼叫不發第二次影像請求。→ Task 4 測：`OcrFlowAsync` 於 `TranslationOnly` 走 `RecognizeAndTranslateAsync`（既有方法、單一 HTTP 請求）。
5. **⚙ 點擊不觸發 DismissHook 誤關**：按鈕在彈窗界內，點擊不得被當成外部點擊關窗。→ Task 3 手動清單項（點 ⚙/朗讀/複製時彈窗行為符合預期：⚙ 應關窗開設定，其餘不關）。Task 3 手動清單第 5 項釘住：點 ⚙ 關窗開設定、點朗讀/複製彈窗不因 DismissHook 提前關閉。

---

## File Structure

- Create: `Models/PopupLayout.cs` — enum（Split/Stack/TranslationOnly）。
- Modify: `Models/AppSettings.cs` — 新增 `PopupLayout` 屬性（預設 Split）。
- Modify: `UI/SettingsForm.cs` — 新增「彈窗版面」ComboBox 列。
- Create: `UI/PopupTheme.cs` — 集中配色常數（internal static）。
- Modify: `UI/PopupForm.cs` — 三版面建構、原文區、三種按鈕、TTS 狀態列、事件。
- Modify: `Services/OllamaClient.cs` — 合併提示詞常數、`RecognizeSourceAndTranslateAsync`、`SplitCombinedOcrOutput`。
- Modify: `AppController.cs` — 顯示原文接線、合併 OCR 路徑、彈窗事件接線、`_pendingTtsFromPopup`。
- Test: `tests/AITranslator.Tests/SettingsServiceTests.cs`、`tests/AITranslator.Tests/OllamaClientTests.cs`（沿用既有 StubOllamaHandler）。

---

### Task 1: 彈窗版面設定（enum + AppSettings + SettingsForm 下拉選單）

**Files:**
- Create: `Models/PopupLayout.cs`
- Modify: `Models/AppSettings.cs`（`OcrTranslateInVision` 之後加屬性）
- Modify: `UI/SettingsForm.cs`（欄位宣告、PopulateFields、BuildLayout、OnSaveButtonClick）
- Test: `tests/AITranslator.Tests/SettingsServiceTests.cs`

**Interfaces:**
- Consumes: 無（首個任務）。
- Produces: `AITranslator.Models.PopupLayout { Split, Stack, TranslationOnly }`；
  `AppSettings.PopupLayout : PopupLayout`（預設 `Split`）。Task 3、4、5 依賴此 enum 與屬性名。

- [ ] **Step 1: 寫失敗測試**（`SettingsServiceTests.cs`）

在 `Defaults_MatchSpec` 加一行：

```csharp
Assert.Equal(PopupLayout.Split, s.PopupLayout);
```

在 `RoundTrip_PreservesValues` 的設定初始化器與對應 Load 後斷言加：

```csharp
PopupLayout = PopupLayout.Stack,
// ...
Assert.Equal(PopupLayout.Stack, loaded.PopupLayout); // 讀回後的實際欄位名以該測試既有寫法為準
```

新增獨立測試（避免耦合既有 roundtrip 的其他欄位）：

```csharp
[Fact]
public void RoundTrip_PopupLayoutDefaultsToSplit_WhenFileLacksField()
{
    // 存一份 PopupLayout=Split 的設定後，於 JSON 中移除 "PopupLayout" 欄位字樣，Load 仍為 Split
    var original = new AppSettings { PopupLayout = PopupLayout.Split };
    SettingsService.Save(_settingsPath, original);
    var json = File.ReadAllText(_settingsPath).Replace("\"PopupLayout\"", "\"Removed\"");
    File.WriteAllText(_settingsPath, json);
    Assert.Equal(PopupLayout.Split, SettingsService.Load(_settingsPath).PopupLayout);
}
```

- [ ] **Step 2: 執行測試確認失敗**

Run: `dotnet test tests/AITranslator.Tests`
Expected: 編譯失敗（`PopupLayout`/`AppSettings.PopupLayout` 不存在）。

- [ ] **Step 3: 實作**

`Models/PopupLayout.cs`：

```csharp
namespace AITranslator.Models;

/// <summary>彈窗版面模式：Split=左右分欄、Stack=上下分段、TranslationOnly=純譯文。</summary>
public enum PopupLayout { Split, Stack, TranslationOnly }
```

`AppSettings.cs` 於 `OcrTranslateInVision` 之後：

```csharp
/// <summary>彈窗呈現版面：顯示原文的版面（Split/Stack）會讓 OCR 與剪貼簿流程把原文一併送進彈窗。</summary>
public PopupLayout PopupLayout { get; set; } = PopupLayout.Split;
```

`SettingsForm.cs`：新增 `private readonly ComboBox _popupLayoutBox;`——建構區 `DropDownList = true`、
加入項目（依序）`"左右分欄"`、`"上下分段"`、`"純譯文"`；`PopulateFields` 設
`_popupLayoutBox.SelectedIndex = (int)_settings.PopupLayout;`；`BuildLayout` 於「彈窗字型大小」
後加 `AddFieldRow(table, row++, "彈窗版面", _popupLayoutBox);`；`OnSaveButtonClick` 於
`_settings.FontSize = fontSize` 一組賦值附近加
`_settings.PopupLayout = (PopupLayout)_popupLayoutBox.SelectedIndex;`（SelectedIndex 必為 0–2，DropDownList 型別無空值情境）。

- [ ] **Step 4: 執行測試確認通過**

Run: `dotnet test tests/AITranslator.Tests`
Expected: PASS（82+ 全綠）。

- [ ] **Step 5: Commit**

```bash
git add Models/PopupLayout.cs Models/AppSettings.cs UI/SettingsForm.cs tests/AITranslator.Tests/SettingsServiceTests.cs
git commit -m "feat: add popup layout setting (split/stack/translation-only)"
```

---

### Task 2: OCR 合併提示詞與輸出解析器

**Files:**
- Modify: `Services/OllamaClient.cs`
- Test: `tests/AITranslator.Tests/OllamaClientTests.cs`

**Interfaces:**
- Consumes: 既有 `RecognizeAsync(settings, imageBase64, prompt, ct)`、`PromptBuilder.Build`、`OcrImagePlaceholderText`。
- Produces（Task 4 依賴）：
  - `OllamaClient.OcrCombinedPrefix : string`（常數，含「兩段輸出＋單獨一行 `===` 分隔」的指示）
  - `OllamaClient.OcrCombinedSeparator : string`（常數＝`"==="`）
  - `OllamaClient.RecognizeSourceAndTranslateAsync(AppSettings settings, string imageBase64, CancellationToken ct) : Task<TranslateResult>`——prompt 為 `OcrCombinedPrefix + "\n\n" + PromptBuilder.Build(settings.PromptTemplate, OcrImagePlaceholderText)`（翻譯半段尊重使用者範本）
  - `OllamaClient.SplitCombinedOcrOutput(string output) : (string Source, string Translation)`——找到第一個「Trim 後等於 `OcrCombinedSeparator`」的行；Source=其前所有行去掉一個行尾換行、Translation=其後所有行去掉一個行首換行；找不到分隔行則 Source=`""`、Translation=原始輸出（降級：整段當譯文）

- [ ] **Step 1: 寫失敗測試**（`OllamaClientTests.cs` 尾端新增區段）

```csharp
[Fact]
public async Task RecognizeSourceAndTranslate_PromptContainsPrefixAndTemplate()
{
    var (client, handler) = CreateClient();
    var settings = CreateSettings(template: "CustomTemplate {text}");
    await client.RecognizeSourceAndTranslateAsync(settings, "IMGDATA", CancellationToken.None);

    var body = LastBodyOrThrow(handler);
    Assert.Contains(OllamaClient.OcrCombinedPrefix, body);
    Assert.Contains("CustomTemplate", body);
    Assert.Contains("IMGDATA", body);
    Assert.Contains(OllamaClient.OcrCombinedSeparator, body); // 分隔指示在提示詞中
}

[Theory]
[InlineData("原文A\n===\n譯文B", "原文A", "譯文B")]
[InlineData("沒分隔的整段", "", "沒分隔的整段")]
[InlineData("第一\n===\n中\n===\n二", "第一", "中\n===\n二")] // 首個分隔行生效
[InlineData("原文\n  ===  \n譯文", "原文", "譯文")]           // 分隔行允許兩側縮排
public void SplitCombinedOcrOutput_ParsesAndFallsBack(string output, string source, string translation)
{
    var (src, tr) = OllamaClient.SplitCombinedOcrOutput(output);
    Assert.Equal(source, src);
    Assert.Equal(translation, tr);
}
```

- [ ] **Step 2: 執行測試確認失敗**

Run: `dotnet test tests/AITranslator.Tests`
Expected: FAIL（方法不存在）。

- [ ] **Step 3: 實作**（`OllamaClient.cs`）

新增兩個常數（值如 Interfaces 所定，Prefix 完整繁中指示以測試常數引用為準；文案：

```csharp
public const string OcrCombinedPrefix =
    "請分兩段輸出：第一段原樣輸出圖片中的所有文字（保留原始段落與分行，不要翻譯、不要解釋、不要加註）；第二段為翻譯。兩段之間以單獨一行輸出三個等號（===）分隔。";
public const string OcrCombinedSeparator = "===";
```

`SplitCombinedOcrOutput`：逐行掃描，取第一個 `Trim == OcrCombinedSeparator` 的行；前段去掉一個行尾換行、後段去掉一個行首換行；找不到即（`""`，原輸出）。`RecognizeSourceAndTranslateAsync` 依 Interfaces 公式組 prompt 呼叫既有 `RecognizeAsync`。

- [ ] **Step 4: 執行測試確認通過**

Run: `dotnet test tests/AITranslator.Tests`
Expected: PASS（82+ 全綠）。

- [ ] **Step 5: Commit**

```bash
git add Services/OllamaClient.cs tests/AITranslator.Tests/OllamaClientTests.cs
git commit -m "feat: combined OCR prompt returns source and translation in one call"
```

---

### Task 3: PopupForm 深色主題＋三版面＋按鈕與事件（純 UI，無單元測試）

**Files:**
- Create: `UI/PopupTheme.cs`
- Modify: `UI/PopupForm.cs`（幾乎整檔重構；`CreateParams`/`ShowWithoutActivation`/`ComputePosition`/toast 計時器/DismissHook 生命週期邏輯原樣保留）

**Interfaces:**
- Consumes: Task 1 的 `models.PopupLayout`、`settings.PopupLayout`。
- Produces（Task 4、5 依賴）：
  - `ShowTranslation(Point anchor, string statusText, string? source = null)`——`source=null` 時**清空**原文區（新請求語意）；版面③ `source` 恒為 null。
  - `SetSource(string source)`——就地更新原文區（不清譯文/不重定位；合併 OCR 的原文與譯文同時到位時由 `SetText` 之前呼叫）。
  - `SetText(string result, bool isError = false)`——保留既有 `source`；`isError=true` 時譯文區文字用 `PopupTheme.ErrorText`，否則 `PopupTheme.MainText`（busy/status 期間譯文區文字用 `PopupTheme.SourceText` 次級色）。
  - `ShowToast(Point mousePos, string text)`——語義不變：覆蓋全部內容、2400→既有 2500ms 自動關閉、停用全部文字類按鈕。
  - `ShowTtsStatus(string? text, bool isError = false)`——`null` 隱藏狀態列。
  - `event Action<string>? TtsRequested;`（參數為當前原文，可能為空字串）
  - `event Action? SettingsRequested;`
  - `bool IsShowingResult { get; }`——彈窗正呈現翻譯結果/錯誤內容（非 toast、非 busy）。
- 狀態列：`Label`（高約 24px，`AutoSize=false`、Dock 對齊、字級同彈窗）置於譯文**按鈕列上方**（Split 的右欄、Stack 的下區、TranslationOnly 的底列），出現時擠壓文字區高度而不覆蓋文字。

- [ ] **Step 1: 建 `UI/PopupTheme.cs`**

```csharp
using System.Drawing;

namespace AITranslator.UI;

/// <summary>彈窗深色配色常數（spec：2026-10-31-popup-layouts-design.md）。</summary>
internal static class PopupTheme
{
    public static readonly Color Background = Color.FromArgb(30, 31, 36);     // #1E1F24
    public static readonly Color Divider = Color.FromArgb(58, 61, 69);        // #3A3D45
    public static readonly Color ButtonBack = Color.FromArgb(42, 44, 51);     // #2A2C33
    public static readonly Color ButtonHover = Color.FromArgb(52, 56, 65);    // #343841
    public static readonly Color SourceText = Color.FromArgb(155, 167, 184);  // #9BA7B8
    public static readonly Color MainText = Color.FromArgb(242, 244, 248);    // #F2F4F8
    public static readonly Color ErrorText = Color.FromArgb(208, 122, 106);   // #D07A6A
}
```

- [ ] **Step 2: 重構 `PopupForm.cs`**

保留（原樣搬移、不改語義）：`CreateParams`、`ShowWithoutActivation`、`ComputePosition`、toast 計時器全套、`OnShown`/`OnClosed`/`Dispose` 的 DismissHook 生命週期、`Clipboard.SetText` 的 `ExternalException` 靜默 catch。其餘按規格：

- 欄位：`_translationBox`（原 `_textBox` 更名）、`TextBox? _sourceBox`、`Button _translationCopyButton`、`Button? _sourceCopyButton`、`Button? _ttsButton`、`Label _ttsStatusLabel`、`string? _source`；`_result`、`_busy`、`_dismissHooked` 不變。
- 建構子依 `settings.PopupLayout` 呼叫 `BuildSplit()`/`BuildStack()`/`BuildTranslationOnly()`；三者共用：
  - 文字區＝多行唯讀 `TextBox`（`Multiline/ReadOnly/WordWrap/ScrollBars=Vertical/BorderStyle=None/TabStop=false/Cursor=Arrow`，`Dock=Fill`），`ReadOnly=true` 後才設 `BackColor=PopupTheme.Background`、`ForeColor`（譯文區 `MainText`、原文區 `SourceText`）。
  - 按鈕：`FlatStyle=Flat`、`BackColor=ButtonBack`、`ForeColor=MainText`、`FlatAppearance.BorderColor=Divider`、`MouseOverBackColor=ButtonHover`、文字如 `📋 複製`/`🔊 朗讀`/`⚙ 設定`、高度 36（沿用既有按鈕列高）、文字類按鈕初值 `Enabled=false`（⚙ 永遠 enabled）。
  - 分隔：Split 用 1px 寬 Panel（`BackColor=Divider`）；Stack 用 1px 高。
  - 按鈕列「左右撐開」用 2 格 `TableLayoutPanel`（50/50）：左格 `Anchor=Left`（複製）、右格 `Anchor=Right`（朗讀或設定）；「成組靠右」用 `FlowLayoutPanel`（`FlowDirection=LeftToRight`、`Anchor=Right`、`WrapContents=false`）。
  - 版面①：外層 `TableLayoutPanel` 單列三欄（Percent 50 / Absolute 1 / Percent 50），每欄內 `Panel(Dock=Fill)`：`TextBox(Fill)`＋`ttsStatusLabel(Bottom 約24px，隱藏)`＋`Button bar(Bottom 36px)`。左欄 bar=複製左/朗讀右；右欄 bar=複製左/設定右。
  - 版面②：外層上下兩列（Percent 50 / Absolute 1 / Percent 50），上列=原文 TextBox＋bar（`FlowLayoutPanel` 靠右：複製、朗讀）；下列=譯文 TextBox＋狀態列＋bar（靠右：複製、設定）。
  - 版面③：`_translationBox(Dock=Fill)`＋`_ttsStatusLabel`＋bar（複製左、設定右，2 格 TableLayoutPanel 同版面①）。
- `ShowTranslation(anchor, statusText, source = null)`：`_source = (sourceBox != null) ? source : null`——注意原 `ShowTranslation` 的 `_result=null`、`_busy=true`、複製禁用、`StopToastTimer()` 全保留；原文區文字同步設為 `_source ?? ""`、來源複製/朗讀鈕 `Enabled=!string.IsNullOrEmpty(_source)`；譯文區 ForeColor 設 `SourceText`（status 次級色）。譯文/原文區之外的既有定位、重用邏輯不變。
- `SetText(result, isError = false)`：語義同舊版（busy=false、複製 enabled、toast 計時器不受影響）＋`ForeColor=isError ? ErrorText : MainText`；`_translationCopyButton.Enabled=!string.IsNullOrEmpty(result)`；原文相關按鈕依 `_source` 不變。
- `ShowToast`：現行語義＋**清空原文區**、全部文字類按鈕禁用（`_source=null`）。
- `ShowTtsStatus(text, isError)`：見 Interfaces；另外 `ShowTranslation`/`SetText`/`ShowToast`/`Close`（任何重設內容的入口）都把狀態列 `Visible=false`。
- `OnCopyButtonClick` 拆兩個 handler（譯文複製沿用 `_result`；原文複製用 `_source`），兩者都複製後 `Close()`（譯文複製=現行行為；原文複製同構）。
- `_ttsButton.Click => TtsRequested?.Invoke(_source ?? string.Empty)`；`⚙ 設定.Click => SettingsRequested?.Invoke()`。
- `IsShowingResult`：`SetText` 設 true；`ShowTranslation`/`ShowToast` 設 false；`OnClosed` 重設 false。
- 文件註解（class summary）更新為三版面描述，保留既有重點（no-activate、TextBox 選擇理由、DismissHook）。

- [ ] **Step 3: 建置確認**

Run: `dotnet build`
Expected: 0 error（Task 4 之前的 `AppController` 舊呼叫點因選讀參數預設值仍可編譯）。

- [ ] **Step 4: 手動驗證清單**（啟動 `dotnet run`，逐一項）：

1. 三種版面於設定頁切換存檔後，重新觸發熱鍵彈窗即以新版面出現，全部深色、譯文/原文文字顏色正確（Review Focus 1）。
2. 貼入長原文＋長譯文：兩區各有捲軸、按鈕列不變形；TTS 狀態列出現時只縮短文字區不覆蓋內容（Review Focus 2）。
3. `📋 複製`（譯文/原文）寫剪貼簿後關彈窗；無內容時禁用。
4. `🔊 朗讀` 點下彈窗**不關閉**、不清內容（TTS 引擎未載入時按鈕列出現「TTS 初始化中…」狀態列；若 TTS 已載入則直接出聲）。
5. `⚙ 設定`：彈窗關閉、設定視窗彈出；彈窗還在時點各按鈕都不會被 DismissHook 誤關（Review Focus 5）。
6. toast（如「未選取文字」）仍 2500ms 自動關閉。

- [ ] **Step 5: Commit**

```bash
git add UI/PopupTheme.cs UI/PopupForm.cs
git commit -m "feat: dark-themed popup with split/stack/translation-only layouts and settings/TTS buttons"
```

---

### Task 4: AppController 顯示原文接線（含合併 OCR 路徑）

**Files:**
- Modify: `AppController.cs`（`OnHotkey`、`TranslateAndShowAsync`、`OcrFlowAsync`、`CompleteOcrFlow`、`DeliverToPopup`）
- Test: `tests/AITranslator.Tests/`（AppController 流程不寫單元測試——既有慣例；本任務以 Task 2 解析器測試＋Step 5 手動清單擔保）

**Interfaces:**
- Consumes: Task 1 `settings.PopupLayout`、Task 2 `RecognizeSourceAndTranslateAsync`/`SplitCombinedOcrOutput`、Task 3 `ShowTranslation(source:)`/`SetSource`/`SetText(isError:)`/`IsShowingResult`。
- Produces: `private bool ShowSource => _settings.PopupLayout != PopupLayout.TranslationOnly;`；
  `DeliverToPopup(string message, bool isError = false, string? source = null)`；
  `CompleteOcrFlow(TranslateResult result, int requestId, string? source = null)`。

- [ ] **Step 1: 接線剪貼簿翻譯路徑**

`OnHotkey` 的長度檢查通過後（原 `EnsurePopup().ShowTranslation(mouse, TranslatingText)`）：

```csharp
EnsurePopup().ShowTranslation(mouse, TranslatingText, ShowSource ? text : null);
```

- [ ] **Step 2: 接線 OCR 兩次呼叫路徑**

`OcrFlowAsync` 兩次呼叫分支：辨識後預檢通過、原「改成翻譯中」的 `ShowTranslation` 改帶原文：

```csharp
EnsurePopup().ShowTranslation(anchor, TranslatingText, ShowSource ? ocr.Text : null);
```

（單次呼叫分支改為：`ShowSource` 時呼叫 `RecognizeSourceAndTranslateAsync` 並以
`SplitCombinedOcrOutput` 拆 `(src, tr)`，再以
`new TranslateResult(combined.Success, tr, combined.Error)` 與 `source: src` 交給
`CompleteOcrFlow`（空譯文→「未取得翻譯內容」邏輯由 CompleteOcrFlow 既有分支處理；
分隔符缺席時 `src` 為空＝自然降級）；非 `ShowSource` 維持 `RecognizeAndTranslateAsync`
**完全不變**（Review Focus 4：不得多發請求）。）

- [ ] **Step 3: 接線結果送達**

`TranslateAndShowAsync` 與 `CompleteOcrFlow`：成功但譯文空 → `DeliverToPopup(EmptyTranslationText, isError: true)`；譯文 → `DeliverToPopup(result.Text)`（彈窗保留既有原文，不必重傳）；`result.Error` 非空 → `DeliverToPopup(result.Error, isError: true)`。`DeliverToPopup` 簽名擴充為 `(string message, bool isError = false, string? source = null)`：`source` 非 null 時先 `_popup.SetSource(source)` 再 `SetText`（僅合併 OCR 路徑傳入；`DeliverToPopup` 不重定位）。

- [ ] **Step 4: 執行測試與建置**

Run: `dotnet test tests/AITranslator.Tests && dotnet build`
Expected: PASS、0 error。

- [ ] **Step 5: 手動驗證清單**

1. 版面①② 剪貼簿翻譯：按 Ctrl+Alt+E，「翻譯中…」階段左/上區即出現原文，譯文就緒後填充；版面③ 無原文區。
2. 版面①② ＋兩次呼叫模式 OCR：框選後「辨識中…」（無原文）→ 辨識到文字即先顯示原文＋「翻譯中…」→ 完成顯示譯文。
3. 版面①② ＋單次呼叫模式 OCR：一次請求同時出原文＋譯文；版面③ 單次呼叫僅譯文（可於 Ollama log 確認單一請求）。
4. 錯誤情境（停掉 Ollama 再翻譯）：彈窗錯誤色文字。

- [ ] **Step 6: Commit**

```bash
git add AppController.cs
git commit -m "feat: populate popup source region across translate and OCR flows"
```

---

### Task 5: 彈窗 TTS／⚙ 設定事件接線＋TTS 狀態列

**Files:**
- Modify: `AppController.cs`（`EnsurePopup`、`OnTtsHotkey`、`OnTtsStateChangedUi`、新增 `OnPopupTtsRequested`、`OnPopupSettingsRequested`、`FormatTtsFailed`）
- Test: 無新增單元測試（既有 TtsServiceTests 覆蓋狀態機；本任務走 Step 4 手動清單）

**Interfaces:**
- Consumes: Task 3 `TtsRequested`/`SettingsRequested`/`ShowTtsStatus`/`IsShowingResult`；既有 `_ttsService` 狀態機（`EnsureLoaded`/`Toggle`/`State`/`LastError`/`StateChanged`）。
- Produces: 彈窗按鈕完成串接；熱鍵 TTS 流程行為不變。

- [ ] **Step 1: 訂閱彈窗事件**

`EnsurePopup` 建立實例後：

```csharp
_popup.SettingsRequested += OnPopupSettingsRequested;
_popup.TtsRequested += OnPopupTtsRequested;
```

`OnPopupSettingsRequested()`：`DiscardPopup(); ShowSettings();`。
（設計意圖：⚙=關彈窗、以現行設定開強制回設定視窗；重複訂閱無虞——彈窗單次建立後重用，事件僅在 EnsurePopup 建新實例時掛一次。）

- [ ] **Step 2: TTS 按鈕流程**

新增欄位 `private bool _pendingTtsFromPopup;`（標記 `_pendingTtsText` 的來源）。新增：

```csharp
private void OnPopupTtsRequested(string sourceText)
{
    // 按鈕路徑不套 MaxTextLength（spec）；不使用 toast——狀態一律顯示於彈窗按鈕區狀態列（不覆蓋內容、不自動關窗）
    if (_popup is null || _popup.IsDisposed) return;
    var popup = _popup!;
    switch (_ttsService.State)
    {
        case TtsState.Loading:
        case TtsState.Stopping:
            popup.ShowTtsStatus(TtsInitializingText); return;
        case TtsState.NotLoaded:
            _pendingTtsText = sourceText;
            _pendingTtsFromPopup = true;
            _ttsService.EnsureLoaded();
            popup.ShowTtsStatus(TtsInitializingText); return;
        case TtsState.Failed:
            var lastError = _ttsService.LastError;
            _pendingTtsText = sourceText;
            _pendingTtsFromPopup = true;
            _ttsService.EnsureLoaded();
            popup.ShowTtsStatus(FormatTtsFailed(lastError), isError: true); return;
        case TtsState.Ready:
        case TtsState.Speaking:
            _ttsService.Toggle(sourceText); return;
    }
}
```

同時抽出 `private static string FormatTtsFailed(string? lastError)`（把 `OnTtsHotkey` 與 `OnTtsStateChangedUi` 中現行 `string.Format(TtsFailedText, ...)` 兩處收斂於此，失敗原因空值落到 `TtsFailedUnknownReason`），`OnTtsHotkey` 的 NotLoaded 與 Failed 分支補 `_pendingTtsFromPopup = false;`（來源旗標跟著 `_pendingTtsText` 一起被覆寫），toast 行為與文案不變。

- [ ] **Step 3: StateChanged 分流**

`OnTtsStateChangedUi` 兩處調整：

```csharp
case TtsState.Failed:
    var error = _ttsService.LastError;
    var popup = _popup;
    if (_pendingTtsFromPopup && popup is not null && !popup.IsDisposed
        && popup.Visible && popup.IsShowingResult)
    {
        popup.ShowTtsStatus(FormatTtsFailed(error), isError: true); // 不覆蓋內容、不自動關窗
    }
    else
    {
        EnsurePopup().ShowToast(Cursor.Position, FormatTtsFailed(error)); // 既有行為
    }
    _pendingTtsText = null;
    _pendingTtsFromPopup = false; // 來源已消費（無論走狀態列或 toast）
    return;

case TtsState.Ready:
    var pending = _pendingTtsText;
    _pendingTtsText = null;
    if (pending is not null && _ttsService.State == TtsState.Ready)
    {
        _ttsService.Toggle(pending);
    }
    if (_pendingTtsFromPopup) // 開始出聲：清掉狀態列（Ready 後靜默，無提示）
    {
        var popup2 = _popup;
        if (popup2 is not null && !popup2.IsDisposed) popup2.ShowTtsStatus(null);
        _pendingTtsFromPopup = false;
    }
    return;
```

（變數命名以該檔既有風格為準——兩個 case 不得互踩既有規則：Failed 分支原先「讀 LastError 在 EnsureLoaded 前」的語義保持在 `OnPopupTtsRequested`；此處只消費。）

- [ ] **Step 4: 手動驗證清單**

1. 版面① 彈窗開著點 `🔊 朗讀`：未載入時按鈕列出現「TTS 初始化中…」與此同時**譯文/原文完整可讀**；Ready 即出聲、狀態列消失（Review Focus 3）。
2. TTS 失敗情境（如資產缺失）：狀態列錯誤色失敗原因、彈窗不自動關。
3. 熱鍵 TTS（Ctrl+Alt+S）行為與文案與現狀完全一致（初始化中 toast、「未選取文字」toast 等）。
4. `⚙ 設定` 在設定視窗改版面＋尺寸存檔後，彈窗以新版面重建。

- [ ] **Step 5: Commit**

```bash
git add AppController.cs
git commit -m "feat: wire popup TTS and settings buttons to existing state machines"
```

---

### Task 6: README＋最終驗證

**Files:**
- Modify: `README.md`（彈窗功能段落補三版面＋⚙ 設定＋🔊 朗讀，並提及「純譯文版面下 OCR 不多花 token」）

**Interfaces:** Consumes: 全部前置任務。Produces: 文件與驗證收尾。

- [ ] **Step 1: 更新 README**（沿用既有中文語氣；一小段即可，不重寫整篇）
- [ ] **Step 2: 全量測試**

Run: `dotnet test tests/AITranslator.Tests`
Expected: 全綠（0 fail）。

- [ ] **Step 3: Release 建置**

Run: `dotnet build -c Release`
Expected: 0 error。

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: popup layouts, in-popup settings and TTS buttons"
```