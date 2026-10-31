# OCR 螢幕框選翻譯功能 — 設計文件

日期：2026-10-31
狀態：待使用者核可

## 意圖與背景

AITranslator 目前的兩個入口（Ctrl+Alt+E 翻譯選取文字、Ctrl+Alt+S 朗讀）都需要目標程式
「可選取文字」。很多場景做不到：遊戲、圖片內嵌字幕、PDF 掃描頁、自繪 UI、影片等。

本功能新增第三個入口：**Ctrl+Alt+I 進入螢幕框選模式，使用者拉一個矩形，框內畫面交給
Ollama vision 模型辨識文字，再翻譯**，結果以現有彈窗呈現。辨識（OCR）與翻譯是兩個
獨立設定，使用者可依模型能力選擇一次或兩次 LLM 呼叫。

## 使用者拍板的決策（依對話整理）

1. **框選方式**：像截圖工具那樣按住滑鼠拉矩形框。
2. **後續流程**：放開滑鼠後靜默連續完成——OCR → 翻譯 → 彈窗，中間不需使用者操作。
3. **單次/兩次呼叫可選**：設定加一個 checkbox「用辨識模型直接翻譯」。
   - 勾選：vision 模型一次呼叫同時辨識+翻譯（適合能力強的模型）。
   - 不勾：vision 模型只輸出辨識文字，翻譯交給現有翻譯模型（適合不擅翻譯的辨識模型）。
   - **預設不勾**（兩次呼叫，較穩）。
4. **彈窗內容**：只顯示翻譯結果，不顯示辨識原文。
5. **預設值**：OCR 模型 `gemma4:31b-cloud`（已確認存在於使用者 Ollama）、快捷鍵 `Ctrl+Alt+I`。

## 目標 / 非目標 / 反目標

**目標**
- 三組熱鍵並存互不干擾；OCR 熱鍵可在設定修改，格式驗證與現有兩組相同且不得與其重複。
- 選取體驗對齊常見截圖工具：全螢幕（跨多螢幕）變暗、十字準星、拉框亮起，放開即完成。
- 過程有回饋：處理期間顯示「辨識中…」toast；失敗走現有 toast 管道。
- 支援 DPI 縮放與多螢幕（含不同縮放比例的螢幕）。

**非目標**
- 不做即時取詞、滑鼠懸停偵測、滾輪縮放、放大鏡、取色器等截圖工具的進階功能。
- 不做辨識原文的顯示/編輯 UI。
- 不做非 Ollama 的 vision 後端。
- 不改動現有翻譯與 TTS 流程的任何行為。

**反目標**
- 彈窗或 toast 語系混用簡體/英文。
- 因框選 overlay 造成的輸入焦點遺失或其他視窗閃爍、工作列閃現。
- 靜默流程中卡住無回饋（逾時必須落到可讀錯誤訊息）。

## 既有架構（本設計要嵌入的接點）

- `AppController`（UI 執行緒）：`MessageWindow` 收 `WM_HOTKEY` → 依 id 分派；翻譯流程
  `_requestId` 序號防過期回應、`_cts` 取消、`PopupForm.ShowTranslation/ShowToast`。
- `OllamaClient.TranslateAsync`：POST `/api/generate`，body `{model, prompt, stream:false}`。
- `SettingsService` / `AppSettings`：JSON 設定檔，屬性初始化器即預設值。
- `SettingsForm`：TableLayoutPanel 欄位 + 逐欄驗證 + 存檔回呼。
- `HotkeyService`：以 id 註冊/重註冊（目前 id 1=翻譯、2=TTS，OCR 用 id 3）。
- `PopupForm`：no-activate 彈窗，`ShowToast`（2500ms 自動關）與 `SetText`。

## 功能設計

### 1. 設定（AppSettings + SettingsForm）

新增三個屬性（預設值即屬性初始化器）：

| 屬性 | 預設值 | 說明 |
|------|--------|------|
| `OcrModel` | `gemma4:31b-cloud` | vision 辨識模型名稱，驗證非空白 |
| `OcrHotkey` | `Ctrl+Alt+I` | 與現有兩組熱鍵格式規則相同；**不得與任一重複**（驗證訊息比照 TTS 熱鍵規則） |
| `OcrTranslateInVision` | `false` | 勾選 = 辨識模型直接輸出譯文（單次呼叫） |

SettingsForm 版面：在「TTS 快捷鍵」列附近加入「辨識 (OCR) 快捷鍵」「辨識 (OCR) 模型名稱」
兩個 TextBox 列，以及一個 CheckBox 列（「辨識模型直接翻譯」）。驗證流程比照現有欄位。
設定變更後 `AppController.OnSettingsChanged` 對 OCR 熱鍵做**值變更才 ReRegister**（同 TTS 模式）。

### 2. 螢幕框選模式（新元件 `UI/RegionSelectorForm`）

全螢幕 overlay 表單（每台螢幕一個，`Screen.AllScreens` 逐一建立），特徵：

- 邊框 `None`、`TopMost`、`ShowInTaskbar = false`、不啟動焦點搶奪（`WS_EX_NOACTIVATE` + 必要時 `ShowWithoutActivation`）；蓋滿虛擬螢幕範圍。
- 半透明暗色背景（`Opacity ≈ 0.4` 黑），拖曳中在選取矩形外側維持暗色、**矩形內部以不透明重繪原畫面**（先 `CopyFromScreen` 補畫被暗幕蓋掉的選區），邊框白色 1px。
- 游標 `Cursors.Cross`；**ESC 或按下滑鼠右鍵取消**整個選取模式（所有 overlay 關閉，無 toast）。
- 放開左鍵：若矩形 < 最小尺寸（如 8×8）→ 關閉並 toast「框選範圍太小」；否則關閉所有 overlay，回傳矩形（螢幕實體像素座標，自行處理 DPI 縮放）。
- 熱鍵觸發時以滑鼠所在螢幕為主要 overlay，跨螢幕拖曳要正常（各螢幕各自的表單 + 虛擬螢幕座標）。

框選為**同步模式對話**（UI 執行緒在選取期間進入 overlay 的訊息迴圈，`ShowDialog` 慣例即可），
結束時回傳 `RegionSelection { Rectangle rect, bool cancelled }`。

> 已確認：`Program.cs` 使用 `HighDpiMode.PerMonitorV2`，`CopyFromScreen` 在實體像素座標
> 下進行、座標不失真即可，無額外 DPI 對策。

### 3. 擷取框內畫面（新方法，建議放 `Services/ScreenCaptureService`）

`CopyFromScreen` 擷取矩形 → `Bitmap` → PNG → base64（`MemoryStream`，不落地暫存檔）。
**尺寸策略**（拍板者：Claude）：影像最長邊超過 2048px 時等比縮小到 2048px，否則原樣送出。
理由：vision 模型內部原本就會將影像縮到可比對的解析度，>2048 只徒增 payload 與延遲；
≤2048 的框不縮，避免小字因縮圖降到不可辨識。
擷取必須同步於 UI（STA）執行緒、在 overlay 關閉**後**執行——注意順序：先關 overlay
（暗幕消失）再抓圖，否則抓到暗幕本身。

### 4. Ollama 呼叫（`OllamaClient` 擴充）

- 新增 `RecognizeAsync(AppSettings, string imageBase64, CancellationToken)`：POST `/api/generate`，
  body `{model: OcrModel, prompt: <OCR 提示詞>, images: [base64], stream:false}`。
  OCR 提示詞（寫死為常數）：要求「原樣輸出圖中全部文字，保留段落，不要翻譯、不要解釋、
  不要加註」。錯誤映射（model not found、HTTP 非 200、無法連線、逾時、JSON 失败）比照
  `TranslateAsync` 既有文案風格，模型名稱改為 `OcrModel`。
- 單次呼叫模式（`OcrTranslateInVision = true`）：`RecognizeTextAsync` 改為帶翻譯提示——
  **直接沿用 `PromptTemplate`**（把 `{text}` 佔位符換成「這張圖片中的文字」字面值），
  讓 vision 模型輸出譯文。使用者自訂的翻譯範本在兩種模式下都生效。
- 兩次呼叫模式：`RecognizeAsync` → 成功文字再走現有 `TranslateAsync`。辨識文字套用現有
  `MaxTextLength` 上限（超過 → toast「辨識文字超過上限」），與選取文字流程一致。

### 5. 控制器流程（`AppController.OnOcrHotkey`）

```
WM_HOTKEY(id=3) → OnOcrHotkey()（UI 執行緒）
  取消進行中翻譯請求（沿用 _cts）與 _requestId++（序號防過期，與翻譯共用機制）
  開啟 RegionSelectorForm（modal 訊息迴圈，滑鼠落點螢幕）
  取消/框太小 → return（太小時 toast）
  CopyFromScreen 抓 base64（同步）
  EnsurePopup().ShowTranslation(mouseOfSelectionCentre, "辨識中…")
  await 流程：
    OcrTranslateInVision? 一次呼叫 RecognizeAsync(with translation prompt)
                          : await RecognizeAsync → MaxTextLength 檢查 → await TranslateAsync
  requestId 过期 → 丟棄；成功 → DeliverToPopup(譯文)；失敗 → DeliverToPopup(錯誤訊息)
  彈窗位置：以框選矩形中心（而非游標）為錨，避免游標在框遠端時彈窗出現突兀
```

既有翻譯（Ctrl+Alt+E）在 OCR 流程進行中被按下時：先取消 OCR 的 ct，並以 `_requestId`
機制丟棄後到的 OCR 回應。

**選取期間的熱鍵**（拍板者：Claude，取代原「modal 收不到熱鍵」的錯誤推測——`ShowDialog`
的訊息迴圈會分派 `MessageWindow` 的 `WM_HOTKEY`，三組熱鍵在框選期間都會送達）：
任何一組熱鍵（含 OCR 自身以外的翻譯與 TTS，乃至重按的 OCR）在框選期間送達時，一律
**先取消當前框選（等同 ESC、無 toast）、關閉 overlay，再執行該熱鍵自己的完整流程**
（含各自對進行中請求的取消/接管語義）。即「後到的熱鍵接管」，類似按 ESC 後立即按該鍵。
TTS 流程不需為此特別調整——它本就獨立於 `_requestId`，取消框選後照常擷取即可。

### 6. 失敗與邊界

| 情境 | 行為 |
|------|------|
| ESC / 右鍵取消 | 靜默關閉，無 toast |
| 框 < 8×8 | 關閉，toast「框選範圍太小」 |
| 辨識結果為空字串 | toast「未辨識到文字」 |
| 辨識文字 > MaxTextLength | toast 上限提示 |
| 模型找不到/連線失敗/逾時 | `TranslateResult.Error` 文案（含 `OcrModel` 名稱），DeliverToPopup 或 toast，比照翻譯流程 |
| 單次呼叫模式的長度上限 | 不預檢（無中間辨識文字可比對），譯文全額收下；不誡斷譯文（拍板者：Claude） |
| vision 呼叫逾時 | 沿用 `HttpClient` 預設 100s（與現有翻譯一致）；逾時文案照 `TranslateResult.Error`（拍板者：Claude） |
| 選取期間送達的任何熱鍵 | 取消框選（靜默）→ 執行該熱鍵流程（拍板者：Claude） |

## 留給 builder 的決定（非使用者拍板）

- RegionSelectorForm 的 GDI 細節：暗幕繪法（分層表單 vs `Region` 挖洞 vs 全畫布重繪）、
  跨螢幕表單管理方式、DPI 對策的具體實作。
- `RecognizeAsync` 與 `TranslateAsync` 的共用提取方式（共用請求/解析/錯誤映射骨架，
  差異參數化；避免複製整段）。
- 提示詞常數的確切措辭（要求格式嚴謹、防 model 加註）。
- 單次呼叫模式 `{text}` 佔位符替換的確切字面值。
- 測試切分：hotkey 衝突驗證、OcrClient 各錯誤映射、AppSettings 預設值等可單測的部分；
  overlay/GDI 屬手動驗證（手動測試清單文件化，供使用者核）。