# AITranslator

選字即譯工具：在任何程式選取文字，按 **Ctrl+Alt+E**，滑鼠位置旁就會彈出小視窗，
顯示由本地 [Ollama](https://ollama.com) 模型翻出的**繁體中文**翻譯。

## 特色

- 全域快捷鍵 `Ctrl+Alt+E`（可在設定改）
- 在滑鼠位置旁彈出翻譯視窗，不打斷你打字的視窗（視窗不搶焦點）
- 點視窗外其他地方、或按 `Esc` 即關閉；視窗內有「複製」按鈕可複製譯文
- 使用 **Ollama 本地模型**，不送資料上雲端
- 擷取選字前會先備份你的剪貼簿、事後還原（含圖片、檔案等非文字內容）

## 下載

到 [Releases](https://github.com/ramwong/AITranslator/releases) 頁面下載最新版的
`AITranslator-win-x64`（exe + 原生 DLL，解壓即可執行，**不用安裝**）。

也可以到 [Actions](https://github.com/ramwong/AITranslator/actions) 下載最新
master build 的 artifact（名字叫 `AITranslator-win-x64`）。

## 系統需求

- Windows 10 / 11（x64）
- [Ollama](https://ollama.com/download) 已安裝並執行中（`ollama serve` 或桌面版）
- 已拉一個翻譯模型，例如：`ollama pull <model-name>`

## 快速開始

1. 啟動 `AITranslator.exe`（系統匣會出現圖示）
2. 系統匣圖示右鍵 → **設定**
3. 填入 Ollama 的 **模型名稱**（到 [Ollama Library](https://ollama.com/library)
   找，例如 `qwen2.5:7b`；預設值 `beam:cloud` 僅為範例，多半要改成你有的模型）
4. Ollama 位址維持 `http://localhost:11434`（本機預設埠）即可
5. 在任何程式選取文字 → 按 `Ctrl+Alt+E` → 等待翻譯彈窗

## 設定說明

| 設定 | 預設值 | 說明 |
|------|--------|------|
| 彈窗寬度 / 高度 | 420 / 260 | 翻譯視窗大小（像素） |
| 彈窗字型大小 | 12 | 翻譯文字的字級 |
| Prompt 翻譯範本 | 繁體中文直譯 | `{text}` 是選取文字的佔位符；想翻成簡體、日文等改這裡即可 |
| Ollama 模型名稱 | `beam:cloud` | 必須與 `ollama list` 顯示的名稱一致 |
| Ollama API 網址 | `http://localhost:11434` | 遠端跑 Ollama 時改成對方網址 |
| 快捷鍵 | `Ctrl+Alt+E` | 格式 `修飾鍵+按鍵`，如 `Ctrl+Shift+T`、`Alt+F3` |
| 選取文字上限字數 | 5000 | 超過上限會提示而不翻譯 |
| 開機自動啟動 | 關 | 寫入登錄 Run key |

設定存於 `%APPDATA%\AITranslator\settings.json`。檔案損壞或被刪會自動回復預設值。

## 常見問題

**顯示「未選取文字」？**
先確認目標程式裡有選取文字、且焦點還在那個窗。有些程式（受保護的 PDF、終端機）
不允許模擬 Ctrl+C，此時擷取不到。

**顯示「無法連線到 Ollama」？**
Ollama 沒在跑，或網址/埠不對。終端機執行 `ollama serve` 或啟動桌面版再試。

**顯示「找不到模型”？**
設定中的模型名稱與 `ollama list` 不符；模型名稱要在 Ollama 官網或
`ollama pull` 時記下的完整名稱（含大小）。

**按住快捷鍵不動會連續觸發？**
放開再按；重按會取消上一次還在進行的翻譯。

## 從原始碼建置

需要 .NET 8 SDK（或更新）：

```bash
dotnet build
dotnet test          # 單元測試
dotnet publish AITranslator.csproj -c Release -r win-x64 -o publish
```

push 到 GitHub 後，`.github/workflows/build.yml` 會自動 build 並上傳 exe artifact；
打 `v*` tag 時會自動建立 GitHub Release。

## 自行修改

- **「關於」對話框文案**：`UI/AboutForm.cs` 頂部的 `Constants` 區塊（名稱、簡介、連結）
- **翻譯 prompt / 目標語言**：設定介面的 Prompt 範本，或 `Models/AppSettings.cs` 的預設值
- **預設值（彈窗大小、快捷鍵等）**：`Models/AppSettings.cs`
- **翻譯錯誤提示文案**：`Services/OllamaClient.cs` 的錯誤映射

## License

私人專案，未附授權條款；自由取用於個人用途。