# AI Translator — 設計文件

日期：2026-10-29
狀態：待批准

## 目的與背景

使用者需要一個 Windows 常駐翻譯工具：在任何程式選取文字後按快捷鍵，
滑鼠位置旁彈出小視窗顯示 Ollama 本地模型翻出的繁體中文翻譯。
目標是「選字 → 看到翻譯」這條路快到不打斷閱讀流程。

## 決定（使用者已確認）

| 決定 | 選擇 |
|------|------|
| 技術棧 | C# WinForms（.NET 8+，單一 .exe） |
| GitHub repo | 私有 `ramwong/AITranslator`（remote 已設好，repo 為空） |
| 選字擷取 | 模擬 Ctrl+C 讀剪貼簿（先備份、後還原） |
| 彈窗關閉 | 點視窗外 / Esc 關閉 |
| 翻譯顯示 | 等完整結果一次顯示（Ollama `stream: false`） |
| 預設 prompt | 由 Claude 撰寫，目標語言繁體中文 |
| 預設 model | `beam:cloud`（使用者自行在 Ollama 官網找可用名稱） |
| 預設 Ollama URL | `http://localhost:11434` |
| 快捷鍵 | `Ctrl+Alt+E` |
| Build | 本機 .NET 10 SDK 可先驗證；GitHub Actions workflow build .exe artifact |

## 需求

### 核心
1. 背景常駐（NotifyIcon 系統匣圖示；右鍵選單：設定 / 離開）。
2. 全域快捷鍵 `Ctrl+Alt+E`（Win32 `RegisterHotKey`）；設定介面可改。
3. 觸發流程：
   - 記下滑鼠位置 → 備份剪貼簿 → 送 `Ctrl+C` → 讀文字 → 還原剪貼簿。
   - 選不到文字（空 / 與備份相同需再驗證）→ 顯示「未選取文字」提示後自動關閉。
4. 在滑鼠附近彈出無邊框視窗（不出現在工作列、top-most）：
   - 顯示「翻譯中…」→ 完成後顯示譯文。
   - 點視窗外 / 按 Esc → 關閉。
   - Ollama 錯誤（連不上、model 不存在等）顯示易讀的錯誤訊息。
5. Ollama 呼叫：`POST {url}/api/generate`，`stream:false`，prompt 由
   設定範本 + 選取文字組成。

### 設定介面（單一設定視窗）
- 彈窗大小（寬 × 高）
- 字型大小
- Prompt 範本（附預設值說明，`{text}` 佔位符）
- Model 名稱
- Ollama URL
- 快捷鍵
- 儲存至 `%APPDATA%\AITranslator\settings.json`（無檔案時用預設值）

### 預設 prompt（Claude 撰寫、可被使用者改）
「請將以下內容翻譯成繁體中文（台灣用語），只輸出譯文，不要解釋或加註:\n{text}」

### 非目標（不做）
- 不做串流逐字顯示
- 不做多語言目標切換介面（改 prompt 即可換目標語言）
- 不做自動更新、不安裝服務

### 反目標（做了就算成功也是失敗）
- 快捷鍵觸發後剪貼簿沒還原（破壞使用者複製的內容）
- 彈窗干擾鍵盤焦點（不打斷打字中的視窗）

## Build / Release

- `.github/workflows/build.yml`：push 或手動觸發 → `dotnet publish`
  win-x64 self-contained 單檔 → 上傳為 artifact（tag 時附到 GitHub Release）。

## 設計裁量（builder check 後由 Claude 決定）

1. **Esc / 點外部關閉 vs 不搶焦點**：彈窗顯示期間啟用低層級
   mouse/keyboard hook（WH_MOUSE_LL / WH_KEYBOARD_LL），**只觀察、不攔截**，
   不會吃掉使用者打字中的 Esc。彈窗內提供「複製」按鈕複製譯文
   （不用可選取文字框，避免啟動化需求）。
2. **Ctrl+C 抓不到文字**：不做 UIA 後備，顯示「未選取文字」提示。
3. **彈窗已開時再按快捷鍵**：取消進行中的請求、擷取新選字、重用同一彈窗。
4. **剪貼簿驗證**：以 `GetClipboardSequenceNumber` 前後比對判斷
   Ctrl+C 是否真的寫入剪貼簿；序號未變 → 視為無選字。
5. **選取長度上限**：預設 5000 字，超出顯示提示；上限可在設定調整。
6. **開機自動啟動**：設定介面加開關（登錄 Run key），預設關閉。

## 交給實作者的自由

- 檔名/命名空間、專案結構細節
- 剪貼簿還原的具體容錯做法（部分程式會鎖剪貼簿，需 retry）
- 彈窗避免搶焦點的具體 WinForms 做法（如 WS_EX_NOACTIVATE）
- 螢幕邊緣防溢出的定位細節

## 驗收

1. 本機 `dotnet build` 通過。
2. push 後 GitHub Actions 綠燈，artifact 有 .exe。
3. （使用者手動驗證）選字 → Ctrl+Alt+E → 滑鼠旁彈窗顯示繁中翻譯。