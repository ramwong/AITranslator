# OCR 螢幕框選翻譯 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ctrl+Alt+I 進入螢幕框選模式，框內畫面送 Ollama vision 模型辨識文字，再（依設定）同模型直譯或交給現有翻譯模型，結果以現有彈窗呈現。

**Architecture:** 三組熱鍵共用 `AppController` 的 `_requestId`/`_cts` 過期防護與 `PopupForm`。新增 `ScreenCaptureService`（GDI 擷取）、`RegionSelectorForm`（全螢幕暗幕 overlay）、`OllamaClient.RecognizeAsync`（`/api/generate` + `images`）。框選以 modal `ShowDialog` 進入；期間送達的 `WM_HOTKEY` 會重入分派——後到熱鍵取消框選並接管（見 Task 5）。

**Tech Stack:** .NET 8 WinForms（`PerMonitorV2`）、`RegisterHotKey`、GDI `Graphics.CopyFromScreen`、Ollama `/api/generate`。

**Spec:** `docs/superpowers/specs/2026-10-31-ocr-translation-design.md`

## Global Constraints

- 文案一律繁體中文（台灣用語），比照既有字串風格。
- 預設值：`OcrModel = "gemma4:31b-cloud"`、`OcrHotkey = "Ctrl+Alt+I"`、`OcrTranslateInVision = false`。
- OCR 影像最長邊 > 2048px 才等比縮到 2048px（`ScreenCaptureService.MaxImageSide = 2048`）。
- 框選最小尺寸 8×8（`RegionSelectorForm.MinRectSize = 8`）。
- OCR 模型可用 PromptTemplate 的 `{text}` 佔位符機制替換字面值 `這張圖片中的文字`。
- 現有翻譯與 TTS 流程行為不得改變（只允許共享既有取消/序號機制）。
- Toast 文案（依 spec §6）：`框選範圍太小`、`未辨識到文字`、`辨識中…`、`辨識文字超過 {MaxTextLength} 字上限`。
- 單元測試指令：`dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj`（當前 62/62 綠）。

## Review Focus

1. **多螢幕不同 DPI 縮放**：座標以實體像素為準（PerMonitorV2）；彈窗錨點為框中心、clamp 進所在螢幕。→ Task 4 手動測試 A4；純函式測試釕 Normalize（Task 4 Step 1）。
2. **框選期間送達的 WM_HOTKEY**（重入）：後到熱鍵必須取消框選並完整執行自身流程，且外層 OCR 不得以其過期序號繼續。→ Task 5 的重入檢查 + 手動測試 B1。
3. **抓圖順序**（先關 overlay 再 CopyFromScreen，否則抓到暗幕本身）。→ Task 5 手動測試 B2（譯文非全黑/正常文字）。
4. **過期回應丟棄**：OCR 與翻譯共用 `_requestId`，await 後序號不符必須丟棄。→ Task 5 程式碼步驟明列 + 手動測試 B3。
5. **極小框/無文字**：8×8 下限與空辨識結果分別 toast。→ Task 4 的 `Decide` 測試 + Task 5 的空字串分支。

---

### Task 1: 設定欄位（AppSettings + SettingsForm）

**Files:**
- Modify: `Models/AppSettings.cs`
- Modify: `UI/SettingsForm.cs`
- Test: `tests/AITranslator.Tests/SettingsServiceTests.cs`

**Interfaces:**
- Produces: `AppSettings.OcrModel` / `AppSettings.OcrHotkey` / `AppSettings.OcrTranslateInVision`（string / string / bool 屬性，預設值如 Global Constraints）

- [ ] **Step 1: 寫失敗測試**（加進 `SettingsServiceTests`）

```csharp
[Fact]
public void Load_MissingFile_DefaultsIncludeOcrFields()
{
    var settings = SettingsService.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
    Assert.Equal("gemma4:31b-cloud", settings.OcrModel);
    Assert.Equal("Ctrl+Alt+I", settings.OcrHotkey);
    Assert.False(settings.OcrTranslateInVision);
}

[Fact]
public void SaveAndLoad_RoundTripsOcrFields()
{
    // 在臨時目錄 Save 一份 OcrModel="m:vision"、OcrHotkey="Ctrl+Alt+K"、
    // OcrTranslateInVision=true 的設定，Load 後三者逐一 Assert 相等。
}
```

（既有 `SettingsServiceTests` 有臨時檔案的 Save/Load 慣例，比照其樣式。）

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj --filter "FullyQualifiedName~SettingsService"`
Expected: FAIL（`OcrModel` 等成員不存在 → 編譯錯誤）

- [ ] **Step 3: 實作**

`AppSettings` 加三個屬性（屬性初始化器即預設值，註解風格比照 `TtsHotkey` 的 XML doc）。

`SettingsForm`：
- 新增欄位 `_ocrModelBox`、`_ocrHotkeyBox`（`CreateTextBox()`）與 `_ocrTranslateInVisionBox`（`CheckBox`，Text=`"辨識模型直接翻譯"`，tooltip 無、Label 欄空白，比照 `_startWithWindowsBox` 列的擺法）。
- `PopulateFields`：`_ocrModelBox.Text = _settings.OcrModel;` 等。
- `BuildLayout`：在「TTS 快捷鍵」列之後依序加 `AddFieldRow(table, row++, "辨識 (OCR) 模型名稱", _ocrModelBox)`、`AddFieldRow(table, row++, "辨識 (OCR) 快捷鍵", _ocrHotkeyBox)`、CheckBox 列（`AddFieldRow(table, row++, string.Empty, _ocrTranslateInVisionBox)`）。
- `OnSaveButtonClick` 驗證（TTS 熱鍵驗證之後）：
  - `_ocrModelBox.Text` 非空白（訊息：`"OCR 模型名稱不可為空白。"`）。
  - `_ocrHotkeyBox.Text.Trim()` 過 `HotkeyParser.Parse`（訊息文案同 TTS：`"快捷鍵格式不正確。需含 Ctrl、Alt 或 Shift 與單一英文字母（或 F1–F12），例如 Ctrl+Alt+E。"`）。
  - `ocrHotkey` 與 `hotkey`、`ttsHotkey` 皆不分大小寫互斥（訊息：`"OCR 快捷鍵不可與其他快捷鍵相同。"`，各觸發一次 MessageBox、指到 `_ocrHotkeyBox`）。
- 存檔區塊：`_settings.OcrModel = _ocrModelBox.Text.Trim(); _settings.OcrHotkey = ocrHotkey; _settings.OcrTranslateInVision = _ocrTranslateInVisionBox.Checked;`

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj --filter "FullyQualifiedName~SettingsService"`
Expected: PASS（含未動到的既有測試）

- [ ] **Step 5: Commit**

```bash
git add Models/AppSettings.cs UI/SettingsForm.cs tests/AITranslator.Tests/SettingsServiceTests.cs
git commit -m "feat: add OCR settings (model, hotkey, translate-in-vision)"
```

### Task 2: OllamaClient 辨識呼叫

**Files:**
- Modify: `Services/OllamaClient.cs`
- Test: `tests/AITranslator.Tests/OllamaClientTests.cs`

**Interfaces:**
- Consumes: `AppSettings.OcrModel`、`AppSettings.OcrTranslateInVision`、`AppSettings.PromptTemplate`（Task 1）、`PromptBuilder.Build`
- Produces:
  - `public const string OcrPrompt`（= `"請原樣輸出圖片中的所有文字，保留原始段落與分行，不要翻譯、不要解釋、不要加註。"`）
  - `public const string OcrImagePlaceholderText`（= `"這張圖片中的文字"`）
  - `public Task<TranslateResult> RecognizeAsync(AppSettings settings, string imageBase64, string prompt, CancellationToken ct)` — core 呼叫
  - `public Task<TranslateResult> RecognizeOcrTextAsync(AppSettings settings, string imageBase64, CancellationToken ct)` — 兩次呼叫模式第一步，prompt 用 `OcrPrompt`
  - `public Task<TranslateResult> RecognizeAndTranslateAsync(AppSettings settings, string imageBase64, CancellationToken ct)` — 單次呼叫模式，prompt 用 `PromptBuilder.Build(settings.PromptTemplate, OcrImagePlaceholderText)`
  - 回傳型別沿用既有 `TranslateResult`；`Error` 為空字串 = 使用者取消

- [ ] **Step 1: 寫失敗測試**（沿用 `StubOllamaHandler` 樣式）

```csharp
[Fact]
public async Task RecognizeAsync_SendModelPromptImagesToGenerate()
{
    var (client, handler) = CreateClient();
    var settings = CreateSettings();
    settings.OcrModel = "gemma4:31b-cloud";
    handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"response\":\"你好\"}", Encoding.UTF8, "application/json"),
    };
    var result = await client.RecognizeAsync(settings, "aGVsbG8=", "打字", CancellationToken.None);
    Assert.Equal("http://localhost:11434/api/generate", handler.LastRequest!.RequestUri!.ToString());
    // body 含 "model":"gemma4:31b-cloud"、"images":["aGVsbG8="]、「打字」，且 stream:false
    Assert.True(result.Success);
    Assert.Equal("你好", result.Text);
}

// 錯誤映射各一（比照既有翻譯測試名稱/樣式）：
// 1. 400 model-not-found → Error 含 "gemma4:31b-cloud"（模型名，不是翻譯模型名）
// 2. HttpRequestException → "無法連線到 Ollama (...)"
// 3. HangUntilCancelled + 外部取消 → Success=false, Error=""
[Fact]
public void PromptBuilder_BuildImagePrompt_ReplacesPlaceholderLiteral()
{
    Assert.Contains("這張圖片中的文字", PromptBuilder.Build("翻譯:\n{text}", OllamaClient.OcrImagePlaceholderText));
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj --filter "FullyQualifiedName~OllamaClient"`
Expected: FAIL（`RecognizeAsync` 不存在）

- [ ] **Step 3: 實作**

把 `TranslateAsync` 的「組 URL/POST/讀 body/ParseResponseField/錯誤映射」抽成私有骨架（參數化：url 結尾同為 `/api/generate`、model、prompt、body 額外欄位），`TranslateAsync` 與 `RecognizeAsync` 共用；錯誤映射文案中模型名取自傳入 model。`images` 欄位：`images = new[] { imageBase64 }`（JsonSerializer 序列化、UTF8、`stream:false`——與既有 body 同構）。兩個便利方法各一行呼叫 core。

- [ ] **Step 4: 跑測試確認通過（全測試套件）**

Run: `dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj`
Expected: PASS（既有 OllamaClient 翻譯測試不得行為改變）

- [ ] **Step 5: Commit**

```bash
git add Services/OllamaClient.cs tests/AITranslator.Tests/OllamaClientTests.cs
git commit -m "feat: add OllamaClient vision recognition call with shared error mapping"
```

### Task 3: 框內畫面擷取（ScreenCaptureService）

**Files:**
- Create: `Services/ScreenCaptureService.cs`
- Test: `tests/AITranslator.Tests/ScreenCaptureServiceTests.cs`

**Interfaces:**
- Produces:
  - `public static class ScreenCaptureService`
  - `public const int MaxImageSide = 2048`
  - `public static double? ComputeScale(int width, int height)` — 最長邊 ≤ 2048 → `null`；否則回傳等比縮小係數（正數、≤1，使縮後最長邊恰為 2048）
  - `public static string CaptureToBase64(System.Drawing.Rectangle rect)` — `CopyFromScreen`（物理像素）→（依 `ComputeScale` 縮放）PNG → base64 string；不落地暫存檔

- [ ] **Step 1: 寫失敗測試**（純函式，僅測 `ComputeScale`；`CaptureToBase64` 屬 GDI 互動，不單測）

```csharp
[Theory]
[InlineData(1000, 800, null)]          // 不縮
[InlineData(2048, 1024, null)]         // 恰在界線不縮
[InlineData(4096, 2048, 0.5)]          // 長邊縮到 2048、等比
[InlineData(2049, 10, 2048.0 / 2049)]  // 稍超過也縮
public void ComputeScale_EnforcesMaxSide(int w, int h, double? expected)
{
    var scale = ScreenCaptureService.ComputeScale(w, h);
    Assert.Equal(expected, scale, precision: 6);
}
```

（`0.5` 與 `2048.0/2049` 以 `precision: 6` 比較；若專案測試另有斷言慣例，比照。）

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj --filter "FullyQualifiedName~ScreenCaptureService"`
Expected: FAIL（類型不存在）

- [ ] **Step 3: 實作 `ScreenCaptureService`**

`ComputeScale`：`Math.Max(w, h) <= MaxImageSide` → null；否則 `MaxImageSide / (double)Math.Max(w, h)`。
`CaptureToBase64`：`new Rectangle` → `Bitmap` from `Graphics.FromHwnd(IntPtr.Zero).CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size)`（物理像素，無 DPI 邊修——呼叫端保證 rect 為物理像素）→ scale 非 null 時以 `Graphics.DrawImage` 縮到新尺寸 → PNG `MemoryStream` → `Convert.ToBase64String`。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj --filter "FullyQualifiedName~ScreenCaptureService"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Services/ScreenCaptureService.cs tests/AITranslator.Tests/ScreenCaptureServiceTests.cs
git commit -m "feat: add screen region capture with 2048px downscale"
```

### Task 4: 全螢幕框選 overlay（RegionSelectorForm）

**Files:**
- Create: `UI/RegionSelectorForm.cs`

**Interfaces:**
- Consumes: 無（UI 元件）
- Produces（供 Task 5）:
  - `public enum RegionSelectionStatus { Selected, Cancelled, TooSmall }`
  - `public sealed record RegionSelectionResult(Rectangle Rect, RegionSelectionStatus Status)` — `Rect` 為**虛擬螢幕物理像素座標**
  - `public sealed class RegionSelectorForm : Form`，建構子無參數，`public RegionSelectionResult SelectRegion()` — 為 `Screen.AllScreens` 每台螢幕建立子 overlay、進訊息迴圈到選取結束；`FormBorderStyle.None`、`TopMost`、`ShowInTaskbar=false`、`WS_EX_NOACTIVATE`、`Cursor.Cross`、暗幕 `Opacity 0.4`、選區內重繪原畫面（`CopyFromScreen` 補畫）+ 白色 1px 邊框
  - `public static Rectangle Normalize(Point start, Point end)` — 純函式，兩點 → 規範化矩形（含兩點、寬高 ≥ 0）
  - `public const int MinRectSize = 8`
  - `public static RegionSelectionStatus Decide(Rectangle rect)` — `NormalizedRect` 寬或高 < 8 → `TooSmall`；否則 `Selected`
  - 取消途徑：ESC 或按下滑鼠右鍵 → 所有 overlay 關閉、`SelectRegion` 回 `Cancelled`
  - `public void CancelSelection()` — 供 `AppController` 在熱鍵重入時取消框選（等同 ESC；冪等）

- [ ] **Step 1: 寫失敗測試**（只測純決策函式；overlay/多螢幕 GDI 為手動清單項，見 Step 6）

```csharp
[Fact]
public void Normalize_ReordersOppositeCorners()
{
    var r = RegionSelectorForm.Normalize(new Point(100, 200), new Point(40, 80));
    Assert.Equal(40, r.X); Assert.Equal(80, r.Y); Assert.Equal(60, r.Width); Assert.Equal(120, r.Height);
}

[Theory]
[InlineData(8, 9, RegionSelectionStatus.Selected)]   // 恰在下限
[InlineData(7, 100, RegionSelectionStatus.TooSmall)]
[InlineData(100, 7, RegionSelectionStatus.TooSmall)]
public void Decide_EnforcesMinRectSize(int w, int h, RegionSelectionStatus expected)
    => Assert.Equal(expected, RegionSelectorForm.Decide(new Rectangle(0, 0, w, h)));
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj --filter "FullyQualifiedName~RegionSelector"`
Expected: FAIL（成員不存在）

- [ ] **Step 3: 實作 `RegionSelectorForm`**

- `Normalize`/`Decide`：純函式如上。
- Overlay 行為（跨螢幕）：主表單蓋「滑鼠所在螢幕」，其餘螢幕各建一個同款 shadow overlay（`Opacity 0.4` 純暗、不接受拖曳）；拖曳只由滑鼠所在那台的表單處理 `_MouseDown/_MouseMove/_MouseUp`。所有表單 `StartPosition=Manual`、蓋滿各自 `Screen.Bounds`（物理像素 `Screen.Bounds` 在 PerMonitorV2 下即實體座標）。
- 繪製：`Paint` 中以 `Region`（排除選取矩形）填半透明黑；選取矩形內部 `CopyFromScreen` 補畫原畫面 + 白色 1px 邊框。
- 事件：`ESCAPE`（`KeyDown`）/ `WM_MOUSE_RBUTTONDOWN`（可覆寫 `WndProc` 或 `MouseClick`）→ `CancelSelection()`；`MouseUp` 左鍵 → `Decide(Normalize(...))` → `TooSmall`/`Selected` 各自結束流程（`SelectRegion` 回傳對應結果）。
- `SelectRegion()`：建立所有 overlay → `Application.Run(this)` 或 `ShowDialog()` 進訊息迴圈 → 結束時關掉其餘 overlay、回傳結果。
- `CancelSelection()`：`InvokeRequired` 皆安全；設 `_cancelled = true` 後關閉所有 overlay。

- [ ] **Step 4: 編譯**

Run: `dotnet build D:\AITranslator\AITranslator.csproj`
Expected: Build succeeded（此任務無 UI 測試；正確性由下步手動清單擔保）

- [ ] **Step 5: Commit**

```bash
git add UI/RegionSelectorForm.cs tests/AITranslator.Tests/RegionSelectorTests.cs
git commit -m "feat: add fullscreen region selection overlay"
```

- [ ] **Step 6: 手動驗證清單（執行者與 user 一起跑，或以 `dotnet run` 自驗後回報）**

- A1 `dotnet run` 後按 Ctrl+Alt+I：畫面變暗、游標十字，拉框見原畫面亮起、白框跟隨。
- A2 ESC 取消：暗幕消失、無 toast。
- A3 右鍵取消：同 A2。
- A4 在 100% 與 150% 縮放的雙螢幕各自拉框，且跨螢幕拖曳：框對齊無位移。
- A5 按 Ctrl+Alt+I 後只拉不到 8px 的框：出現「框選範圍太小」toast。

### Task 5: AppController OCR 流程串接

**Files:**
- Modify: `AppController.cs`

**Interfaces:**
- Consumes: Task 1（`OcrModel`/`OcrHotkey`/`OcrTranslateInVision`）、Task 2（`RecognizeOcrTextAsync`/`RecognizeAndTranslateAsync`/`OcrPrompt`）、Task 3（`ScreenCaptureService.CaptureToBase64`）、Task 4（`RegionSelectorForm.SelectRegion`/`CancelSelection`/`RegionSelectionResult`）、既有 `PopupForm.ShowTranslation/ShowToast/SetText`、`_requestId/_cts`
- Produces: `HotkeyIdOcr = 3`；`OnOcrHotkey()` 掛進 `OnHotkeyDispatch`；`OnSettingsChanged` 對 OCR 熱鍵做值變更才 `ReRegister`（比照 TTS）

- [ ] **Step 1: 實作 controller 流程**（先測試後實作不可行——本任務為 WinForms 整合，以「建置+手動清單」擔保；純決策已分別在 Task 4/2 測試）

`AppController`：
- `private const int HotkeyIdOcr = 3;` 註解比照現有兩個 id。
- `OnHotkeyDispatch` 加 `case HotkeyIdOcr: OnOcrHotkey(); break;`
- 熱鍵重入取消框選：加 `private RegionSelectorForm? _activeRegionSelector;`；`OnOcrHotkey` 於 `SelectRegion()` 前指派、返回後清 null；既有 `OnHotkey`/`OnTtsHotkey` 一開始（取消 `_cts` 的同一處）加 `_activeRegionSelector?.CancelSelection();` —— 後到熱鍵先行取消框選（等同 ESC），外層 `SelectRegion`因而先返回。
- `OnOcrHotkey()`（UI 執行緒）：
  1. 取消進行中請求（`_cts` cancel/dispose/null）、`_requestId++`、`_popup.IsDisposed` 清理 —— 與 `OnHotkey` 開頭三步相同。
  2. `var requestId = _requestId;` 記下本請求序號。
  3. `_activeRegionSelector = new RegionSelectorForm(); var selection = _activeRegionSelector.SelectRegion(); _activeRegionSelector = null;`
  4. `selection.Status == Cancelled` → return（無 toast）。
  5. `TooSmall` → `EnsurePopup().ShowToast(Cursor.Position, "框選範圍太小")`。
  6. 重入檢查：`requestId != _requestId`（框選期間有後到熱鍵接管）→ return，不抓圖不發請求。
  7. `var anchor = new Point(selection.Rect.X + selection.Rect.Width / 2, selection.Rect.Y + selection.Rect.Height / 2);`
  8. `var image = ScreenCaptureService.CaptureToBase64(selection.Rect);`（同步，UI 執行緒；順序：overlay 已關才抓）
  9. `EnsurePopup().ShowTranslation(anchor, "辨識中…"); var cts = NewCts();` → `OcrFlowAsync(anchor, image, requestId, cts.Token)`。

- `private async void OcrFlowAsync(Point anchor, string imageBase64, int requestId, CancellationToken ct)`：
  ```csharp
  try
  {
      if (_settings.OcrTranslateInVision)
      {
          // 單次呼叫：辨識+翻譯一次完成（不套用辨識文字上限——無中間結果可比對）
          var direct = await _ollamaClient.RecognizeAndTranslateAsync(_settings, imageBase64, ct);
          CompleteOcrFlow(direct, requestId);
          return;
      }

      // 兩次呼叫：先辨識
      var ocr = await _ollamaClient.RecognizeOcrTextAsync(_settings, imageBase64, ct);
      if (requestId != _requestId) { return; }   // 過期防護（同 TranslateAndShowAsync）

      if (!ocr.Success)
      {
          CompleteOcrFlow(ocr, requestId); // LLM/HTTP 層失敗：比照翻譯錯誤路徑
          return;
      }

      // 辨識文字預檢（spec §6）：空字串與超上限都用 toast（短暫提示，不佔彈窗）
      if (ocr.Text.Length == 0)
      {
          EnsurePopup().ShowToast(anchor, "未辨識到文字");
          return;
      }

      if (ocr.Text.Length > _settings.MaxTextLength)
      {
          EnsurePopup().ShowToast(anchor, $"辨識文字超過 {_settings.MaxTextLength} 字上限");
          return;
      }

      EnsurePopup().ShowTranslation(anchor, TranslatingText); //「翻譯中…」（既有常數）
      var translated = await _ollamaClient.TranslateAsync(_settings, ocr.Text, ct);
      CompleteOcrFlow(translated, requestId);
  }
  catch (Exception ex)
  {
      Console.Error.WriteLine($"AITranslator: OCR 流程未預期例外：{ex}");
  }
  ```

  ```csharp
  /// <summary>把結果送上彈窗：過期丟棄、成功/取消/錯誤三分，語義同 TranslateAndShowAsync。</summary>
  private void CompleteOcrFlow(TranslateResult result, int requestId)
  {
      if (requestId != _requestId) { return; }

      if (result.Success)
      {
          DeliverToPopup(string.IsNullOrEmpty(result.Text) ? EmptyTranslationText : result.Text);
      }
      else if (result.Error.Length == 0)
      {
          DiscardPopup(); // 使用者取消
      }
      else
      {
          DeliverToPopup(result.Error);
      }
  }
  ```

- `OnSettingsChanged`：仿 TTS，記舊 `OcrHotkey`，值變更才 `_hotkeyService.ReRegister(HotkeyIdOcr, newSettings.OcrHotkey)`（失敗共用同一則氣泡通知）。
- `Exit()` 與 `Dispose()`：補 `_hotkeyService.Unregister(HotkeyIdOcr);`（其餘清理不動）。

- [ ] **Step 2: 建置**

Run: `dotnet build D:\AITranslator\AITranslator.csproj`
Expected: Build succeeded.

- [ ] **Step 3: 全測試套件**

Run: `dotnet test D:\AITranslator\tests\AITranslator.Tests\AITranslator.Tests.csproj`
Expected: PASS（全部）

- [ ] **Step 4: Commit**

```bash
git add AppController.cs
git commit -m "feat: wire Ctrl+Alt+I hotkey to region OCR translation flow"
```

### Task 6: 手動驗證清單（與 user 共同執行，不可省）

**Files:**
- 無新增檔案；執行 `dotnet run` 於實機

**Interfaces:**
- Consumes: Task 1–5 全部成果

- [ ] **B1 重入接管**：按 Ctrl+Alt+I 框到一半按 Ctrl+Alt+E：框選立即取消、正常走「選取文字→翻譯中→譯文」流程。
- [ ] **B2 正常翻譯**：框選含文字區域 →「辨識中…」→ 數秒後彈窗出譯文，位置在框中心附近、clamp 進螢幕。
- [ ] **B3 錯誤呈現**：把設定改成不存在的 OCR 模型名 → toast/彈窗出現含模型名的錯誤訊息；改回後恢復。
- [ ] **B4 單次呼叫模式**：勾「辨識模型直接翻譯」→ 框選後譯文仍正確出現；取消勾選恢復兩次呼叫。
- [ ] **B5 與既有熱鍵並存**：Ctrl+Alt+E（翻譯）與 Ctrl+Alt+S（朗讀）在 OCR 加入後行為不變。
- [ ] **B6 設定介面**：新欄位顯示、驗證錯誤提示正確、存檔後重啟仍保留。
- [ ] 全數通過後：

```bash
git status   # 確認工作區乾淨（本計畫無新增檔案要 commit）
```