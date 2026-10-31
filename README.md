# AITranslator

選字即譯與框選 OCR 翻譯工具：在任何程式**選取文字**按 **Ctrl+Alt+E**，或按 **Ctrl+Alt+I**
**拉框圈選螢幕上的文字**（遊戲、影片字幕、掃描 PDF 等選不到字的畫面也行），
滑鼠位置旁就會彈出小視窗，顯示由本地 [Ollama](https://ollama.com) 模型翻出的**繁體中文**翻譯。

## 特色

- 全域快捷鍵 `Ctrl+Alt+E`（可在設定改）
- 螢幕框選 OCR 翻譯：`Ctrl+Alt+I` 進入拉框模式（畫面變暗、十字準星），放開滑鼠即
  送 Ollama **vision 模型**辨識文字並翻譯；`Esc` 或滑鼠右鍵取消（可在設定改）
- 在滑鼠位置旁彈出翻譯視窗，不打斷你打字的視窗（視窗不搶焦點）
- 點視窗外其他地方、或按 `Esc` 即關閉；視窗內有「複製」按鈕可複製譯文
- 使用 **Ollama 本地模型**，不送資料上雲端
- 擷取選字前會先備份你的剪貼簿、事後還原（含圖片、檔案等非文字內容）
- 選取文字按 `Ctrl+Alt+S`（可在設定改）即以本地 [Phonon 31M](https://huggingface.co/gradium/phonon-31m-en)
  模型**朗讀原文**（幀級串流，邊合成邊播）；再按一次即停，同樣全程離線

## 下載

到 [Releases](https://github.com/ramwong/AITranslator/releases) 頁面下載最新版的
`AITranslator.exe`（單一檔案即可執行，原生元件已封裝在其中，**不用安裝**）。

也可以到 [Actions](https://github.com/ramwong/AITranslator/actions) 下載最新
master build 的 artifact（名字叫 `AITranslator-win-x64`）。

## 系統需求

- Windows 10 / 11（x64）
- [Ollama](https://ollama.com/download) 已安裝並執行中（`ollama serve` 或桌面版）
- 已拉一個翻譯模型，例如：`ollama pull <model-name>`
- 想用 OCR 框選翻譯：再拉一個 **vision 模型**（能讀圖的模型，如 `gemma4:31b-cloud`），
  `ollama pull <model-name>` 即可
- TTS 朗讀**全程離線、無額外相依**：語音引擎與模型檔已內嵌於 exe（Rust 工具鏈僅
  建置時需要，使用者端不用安裝）

## 快速開始

1. 啟動 `AITranslator.exe`（系統匣會出現圖示）
2. 系統匣圖示右鍵 → **設定**
3. 填入 Ollama 的 **模型名稱**（到 [Ollama Library](https://ollama.com/library)
   找，例如 `qwen2.5:7b`；預設值 `beam:cloud` 僅為範例，多半要改成你有的模型）
4. Ollama 位址維持 `http://localhost:11434`（本機預設埠）即可
5. 在任何程式選取文字 → 按 `Ctrl+Alt+E` → 等待翻譯彈窗
6. （選用）選不到字的畫面：按 `Ctrl+Alt+I` → 畫面變暗 → 拉框圈住文字 → 放開滑鼠
   即自動辨識並翻譯成彈窗（`Esc`/滑鼠右鍵取消；框太小或框內無字會有提示）
7. （選用）想聽原文：選取文字 → 按 `Ctrl+Alt+S` 朗讀；再按一次即停。
   第一次按需先載入語音模型，會顯示「TTS 初始化中…」，載好就自動開始唸

## 設定說明

| 設定 | 預設值 | 說明 |
|------|--------|------|
| 彈窗寬度 / 高度 | 420 / 260 | 翻譯視窗大小（像素） |
| 彈窗字型大小 | 12 | 翻譯文字的字級 |
| Prompt 翻譯範本 | 繁體中文直譯 | `{text}` 是選取文字的佔位符；想翻成簡體、日文等改這裡即可 |
| Ollama 模型名稱 | `beam:cloud` | 必須與 `ollama list` 顯示的名稱一致 |
| 辨識 (OCR) 模型名稱 | `gemma4:31b-cloud` | 框選辨識用的 **vision 模型**；一樣要與 `ollama list` 一致 |
| Ollama API 網址 | `http://localhost:11434` | 遠端跑 Ollama 時改成對方網址 |
| 快捷鍵 | `Ctrl+Alt+E` | 格式 `修飾鍵+按鍵`，如 `Ctrl+Shift+T`、`Alt+F3` |
| TTS 快捷鍵 | `Ctrl+Alt+S` | 朗讀選取文字的快捷鍵，格式同「快捷鍵」；不可與翻譯快捷鍵相同（存檔時驗證擋下） |
| 辨識 (OCR) 快捷鍵 | `Ctrl+Alt+I` | 螢幕框選的快捷鍵，格式同上；不可與其他兩組相同（存檔時驗證擋下） |
| 辨識模型直接翻譯 | 關 | 勾＝vision 模型一次呼叫完成辨識+翻譯（模型需夠強）；不勾＝辨識模型只出文字、翻譯交上面翻譯模型（穩） |
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

**OCR 框選後顯示「未辨識到文字」？**
框內可能真的沒有文字，或字太小。拉框緊貼文字、字級不要太小再試；
大於 2048px 的框會自動等比縮小，極小的字縮圖後可能辨識不出來。

**OCR 顯示「找不到模型 "…"」？**
「辨識 (OCR) 模型名稱」需為 `ollama list` 裡存在且**能讀圖**的 vision 模型
（非 vision 模型收不到圖片請求）。辨識與翻譯是兩個獨立設定，可各用不同模型。

**OCR 要兩次呼叫還是一次？**
預設兩次：辨識模型先打出框內文字，再交翻譯模型翻譯（品質最穩）。
若你的 vision 模型翻譯也夠強，勾「辨識模型直接翻譯」改成一次呼叫，較快。

**TTS 朗讀是怎麼運作的？需要連網嗎？**
選取文字按 TTS 快捷鍵（預設 `Ctrl+Alt+S`）即朗讀原文，再按一次即停。全程**離線**：
語音引擎 `ptts_ffi.dll` 與 Phonon 31M 模型檔都內嵌在 exe 裡，不送任何資料上雲端。
注意 TTS 只唸**英文**原文（模型為英文語音），且朗讀的是你選取的文字，與翻譯結果無關。

**第一次按 TTS 快捷鍵沒聲音？**
首次使用需先載入語音模型，當時游標旁會顯示「TTS 初始化中…」，載好後自動開始唸；
之後同一個執行階段不需要再等。若尚未選取文字，會顯示「未選取文字」而不出聲。

**顯示「TTS 失敗：…」？**
朗讀引擎載入或合成失敗。可再按一次 TTS 快捷鍵重試；若持續失敗，訊息中會附上原因。
音訊走 Windows 的 waveOut 裝置，也請確認系統音量與輸出裝置正常。

## 從原始碼建置

需要 .NET 8 SDK（或更新）：

```bash
dotnet build
DOTNET_ROLL_FORWARD=Major dotnet test   # 單元測試（本機僅新 SDK 時）
dotnet publish AITranslator.csproj -c Release -r win-x64 -o publish
```

TTS 資產（`ptts_ffi.dll` 與模型檔）以 gitignored 的 `assets/tts/` 為來源，缺了它一樣能
build，只是沒有朗讀功能；要完整建置請先跑 PowerShell 7 + Rust 工具鏈：

```powershell
./scripts/fetch-assets.ps1
```

push 到 GitHub 後，`.github/workflows/build.yml` 會自動 build 並上傳 exe artifact；
打 `v*` tag 時會自動建立 GitHub Release。

## 自行修改

- **翻譯 prompt / 目標語言**：設定介面的 Prompt 範本，或 `Models/AppSettings.cs` 的預設值
- **OCR 辨識提示詞**：`Services/OllamaClient.cs` 的 `OcrPrompt` 常數
- **預設值（彈窗大小、快捷鍵等）**：`Models/AppSettings.cs`
- **翻譯錯誤提示文案**：`Services/OllamaClient.cs` 的錯誤映射

## TTS 版本鎖定

朗讀功能的引擎與模型皆以鎖定版本建置、內嵌於 exe：

- 語音引擎：[xn-ptts](https://github.com/gradium-ai/xn-ptts) tag `v0.6.0` 建出的 `ptts_ffi.dll`
- 語音模型：[Phonon 31M（英文）](https://huggingface.co/gradium/phonon-31m-en)，
  pinned commit `92741c5b07d085ff840e26fea8ce43110b87852f`
- 內部快取 stamp：`0.6.0+92741c5b07d085ff840e26fea8ce43110b87852f`

## License

AITranslator 本身以 [The Unlicense](https://unlicense.org)（公有領域貢獻）提供 — 免署名、
免標來源，任意使用、修改、散佈、商業用途皆可。第三方元件（TTS 引擎與語音模型）的
來源與授權請見 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。