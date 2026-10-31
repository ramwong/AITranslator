# 第三方元件聲明（Third-Party Notices）

AITranslator 內嵌下列第三方元件，用以實作選取文字朗讀（TTS）功能。本專案本身的
授權另見 [README.md](README.md) 的 License 一節。

## Phonon 31M（語音模型，`phonon-31m-en`）

- **提供者**：[gradium-ai](https://github.com/gradium-ai)
- **來源**：<https://huggingface.co/gradium/phonon-31m-en>
- **授權**：[Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)
- **鎖定修訂**：pinned commit `92741c5b07d085ff840e26fea8ce43110b87852f`（2026-10-31 查得）
- **內嵌內容**：English 語音模型的 4 個檔案——`config.json`、`tokenizer.json`、
  `model.q8.gguf`、`default-voice.safetensors`（供 `ptts_ffi.dll` 推論使用）

## xn-ptts（TTS 推論引擎，`ptts_ffi.dll`）

- **提供者**：[gradium-ai](https://github.com/gradium-ai)
- **來源**：<https://github.com/gradium-ai/xn-ptts>
- **授權**：SPDX `MIT OR Apache-2.0`（雙授權，取其一即可）
  - [MIT License](https://opensource.org/license/mit)
  - [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)
- **鎖定版本**：tag `v0.6.0`
- **內嵌內容**：由該修訂以 Rust 建置的 `ptts_ffi.dll`（Windows x64）

## 版本鎖定與內部快取

為確保使用者端行為一致，上述兩項均以固定修訂建置；exe 首次執行 TTS 時會將內嵌
資產解壓到使用者快取目錄，並以 stamp `0.6.0+92741c5b07d085ff840e26fea8ce43110b87852f`
標記版本，stamp 不同時重新解壓。重建這些資產請見 [README.md](README.md) 的
「從原始碼建置」一節（`scripts/fetch-assets.ps1`）。