# 選字 TTS 功能設計

日期：2026-10-31
狀態：待實作

## 意圖與緣由

在任何程式選取文字，按全域快捷鍵即可用**本地 TTS 引擎把選取的原文唸出來**，
不打斷手邊工作、不上雲。與現有翻譯功能（Ctrl+Alt+E）同構，但目標是「聽原文」，
不是「看譯文」。

## 已定的需求

- 快捷鍵：**Ctrl+Alt+S**（預設值，照現有翻譯快捷鍵模式進設定，可供修改）
- 觸發一次：擷取選取文字 → 以 phonon TTS 唸出
- 播放中或產生中再按同一次快捷鍵：**停止**（停止播放並終止產生程序）
  - 這是切換（toggle）行為：每次按鍵要嘛開始要嘛停止
- TTS 的對象是**選取的原文**，不需要語言判斷，也不唸譯文
- 畫面上**不彈窗**：成功時純出聲，無 UI

## TTS 引擎：Phonon 31M（串流式 C FFI）

- 模型：[`gradium/phonon-31m-en`](https://huggingface.co/gradium/phonon-31m-en)，
  **Apache 2.0** 授權，可嵌入 exe 再散佈（須附授權聲明）
- 推理程式：[`gradium-ai/xn-ptts`](https://github.com/gradium-ai/xn-ptts) 的
  **C FFI 函式庫 `libptts_ffi`**（MIT OR Apache-2.0），以 Rust 從原始碼編譯出
  `ptts_ffi.dll`（`RUSTFLAGS="-C target-cpu=x86-64-v3" cargo build --release -p ptts-ffi`；
  釋出資產中**沒有**現成 Windows FFI dll，必須自行編譯）
- 模型採 **q8 量化權重**（`model.q8.gguf`，約 65MB）
- **模型與 ptts_ffi.dll 直接嵌入 AITranslator.exe**，維持專案「單一檔案、免安裝」的傳統
- 不使用 `ptts` CLI、不產生任何檔案、不呼叫 HTTP、不下載任何東西

> **為何必須串流**：實測（`2026-10-31-tts-cpu-benchmark.md`，i7-6700K）顯示
> 整段生成 50 字需約 4.9 秒才可播放；串流首音僅 **0.106 秒**。整段再播的延遲
> 不可接受，故採幀級串流。

### C FFI 介面要點（節錄官方文件）

- `ptts_new(dir, PTTS_UNIT_CPU, "en")` 載入模型資料夾（慢呼叫，載一次常駐）
- `ptts_speak(h, text, callback, user, &result)` 串流 `text` 的音訊：
  callback 在呼叫執行緒上分批收到 **24 kHz 單聲道 f32**（[-1,1]），
  回傳 `false` 即提前停止；callback 等待播放時生成自動暫停（有界緩衝）
- `ptts_last_error(h)` 回報失敗原因；`ptts_free(h)` 釋放
- 資料夾需要：`config.json`、`tokenizer.json`、`model.q8.gguf`、
  `default-voice.safetensors`；本地資料夾不需任何 Hub 存取
- 呼叫必須阻擋式、離開 UI 執行緒；同一時間至多一個 speak
- CPU 執行緒數以 `RAYON_NUM_THREADS` 環境變數控制（僅在首次 `ptts_new` 前有效）

## 行為細節

- 文字擷取重用現有 `CaptureService`（模擬 Ctrl+C、剪貼簿備份與還原）
- 文字上限：沿用現有「選取文字上限字數」設定（預設 5000），超過時提示而不唸；
  實作時須**實測** phonon 對 5000 字長文的行為（31M 小模型可能有輸入序列上限，
  串流不會移除此風險）。若長文會失敗或爆音，改為依句子邊界分段、連續多次
  `ptts_speak`（callback 層面聽感連續）；此取捨由實作者依實測結果決定
- 未選取文字或擷取失敗：沿用現有「未選取文字」提示彈窗，不另外設計
- 不產出任何臨時音訊檔：音訊以串流直接進 waveOut，全程在記憶體與聲卡
- **TTS 與翻譯兩條流程完全獨立**：互不取消對方、各自有自己的進行中狀態與
  臨時檔；TTS 的 toggle 停止只作用於 TTS 自身。翻譯 `Ctrl+Alt+E` 與
  TTS `Ctrl+Alt+S` 可同時在進行中
- 程式離開：取消進行中 speak（callback 回 false）、`ptts_free`、`waveOutClose`
- 失敗（ptts_ffi 回傳 false、解壓失敗等）：以 `ptts_last_error` 的訊息提示彈窗，
  不靜默失敗

## 嵌入與解壓策略

- `ptts_ffi.dll`、`model.q8.gguf`、`config.json`、`tokenizer.json`、
  `default-voice.safetensors` 全部以 **embedded resource** 內嵌到組件
- 首次按 TTS 快捷鍵時才解壓到 `%LOCALAPPDATA%\AITranslator\tts\`（快取目錄），
  不是啟動時解壓——避免 65MB 解壓拖慢每次開機
- 解壓一次即永久快取；快取目錄寫入**版本戳檔**（ptts_ffi 版本 + 資產雜湊），
  與本次 exe 內嵌版本不符即整個重新解壓；使用快取失敗時自動清掉重解一次，
  仍失敗才提示
- ptts 產生音訊時以 `--model <快取資料夾> --lang en --quant q8` 使用本地資料夾，不需網路

## 播放方案

- 以 WinMM `waveOut` 播放（24 kHz 單聲道、IEEE float，Windows 10 內建支援，
  不引入 NAudio 等外部音訊套件）；callback 線上填入 waveOut 緩衝佇列，
  佇列滿了就等——生成端會自動因等待而暫停（官方有界緩衝設計）
- `ptts_speak` 在專用背景執行緒執行（阻擋式呼叫）；UI 執行緒只做 toggle
- **模型常駐**：第一次按鍵於背景載入 `ptts_new`（此期間按鍵提示「TTS 初始化中…」
  且不重複觸發載入），載入完成後保持 handle 至程式離開時 `ptts_free`
- **停止** = callback 回傳 `false`（提前結束生成）+ `waveOutReset`
  清除已排隊緩衝；停止後可立即按鍵再次播放

## 建構與授權

- 倉庫內放置 `NOTICE`（或於 README 加註）：列出 Phonon 模型（Apache 2.0）
  與 xn-ptts 推理程式（MIT OR Apache-2.0），含來源連結
- **CI 在建置時取得鎖定版本的資產**：build.yml 增加步驟——安裝 Rust 工具鏈、
  checkout xn-ptts 於指定 tag、以 `x86-64-v3` 編出 `ptts_ffi.dll`；下載指定
  HF commit 的 `gradium/phonon-31m-en` 檔案集；兩者放進 gitignored 資產目錄
  後 `dotnet publish` 嵌進 exe。倉庫本身保持無二進位檔。本機開發提供等價
  script（建 dll + 下載模型到同一 gitignored 目錄），無資產時 publish 警示
- 只上傳 `AITranslator.exe` 的 release 流程不變（嵌入資源會自動包含）
- 版本控管：鎖定所使用的 xn-ptts release tag 與 HF 模型 commit（在 README 與
  script 中註明實際版本），更新時手動替換並同步快取版本戳

## 非目標（本次不做）

- 不唸中文或翻譯結果（Phonon 31M 只支援英文；多語是 40M 版但授權不可商用）
- 語音選擇 UI（用模型預設語音；`ptts_set_voice` 未來可擴充）
- 說話速率調整（官方無此 API 於 C FFI；未來可擴充）
- 防止熱鍵衝突偵測（沿用現有 RegisterHotKey 失敗提示機制）

## 留給實作者決定

- 進行測試與驗證的粒度（現有 tests/ 資料夾、`InternalsVisibleTo`，建議新增 TTS 服務的單元測試）
- `%LOCALAPPDATA%` 快取目錄失敗的 fallback 是否需要
- Settings 資料模型欄位命名與遷移（照 `AppSettings` 現有慣例）