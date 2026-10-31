#Requires -Version 7
<#
.SYNOPSIS
    取得 TTS 嵌入資產：以 Rust 建置 ptts_ffi.dll，並下載鎖定修訂的 phonon-31m-en 模型檔到 assets/tts/。

.DESCRIPTION
    產出（皆為 gitignored，供 AITranslator.csproj 以 EmbeddedResource 嵌入）：
      assets/tts/ptts_ffi.dll
      assets/tts/model/{config.json, tokenizer.json, model.q8.gguf, default-voice.safetensors}

    流程：暫存目錄 git clone xn-ptts（--depth 1 --branch <PttsTag>）→
    RUSTFLAGS=-C target-cpu=x86-64-v3 下 cargo build --release -p ptts-ffi →
    複製 target/release/ptts_ffi.dll → 逐一下載 4 個模型檔 → 印出建議 version stamp。

    冪等：重跑前清除暫存 clone 但保留 assets/；任何一步（clone、cargo、任一下載）
    失敗即中止並以非零結束碼離開，不靜默跳過。
#>
[CmdletBinding()]
param(
    # xn-ptts 的 git tag；FFI 鎖定此修訂（全域約束）。
    [string]$PttsTag = 'v0.6.0',

    # phonon-31m-en 模型修訂（huggingface.co/api/models/gradium/phonon-31m-en 回應的 sha 欄位；
    # 2026-10-31 查得，寫死為此 script 的預設值）。
    [string]$ModelCommit = '92741c5b07d085ff840e26fea8ce43110b87852f'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assetsDir = Join-Path $repoRoot 'assets\tts'
$modelDir = Join-Path $assetsDir 'model'
$cloneDir = Join-Path ([System.IO.Path]::GetTempPath()) 'xn-ptts-fetch-assets'

$modelFiles = @('config.json', 'tokenizer.json', 'model.q8.gguf', 'default-voice.safetensors')
$modelBase = "https://huggingface.co/gradium/phonon-31m-en/resolve/$ModelCommit"

# 前一步驟（git / cargo）非零結束即 fail loudly。
function Assert-LastExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step 失敗（exit code $LASTEXITCODE）"
    }
}

# 冪等：清除暫存 clone（assets/ 保留不動）。
if (Test-Path $cloneDir) {
    Remove-Item $cloneDir -Recurse -Force
}

try {
    New-Item -ItemType Directory -Path $modelDir -Force | Out-Null

    Write-Host "==> git clone --depth 1 --branch $PttsTag https://github.com/gradium-ai/xn-ptts"
    git clone --depth 1 --branch $PttsTag https://github.com/gradium-ai/xn-ptts $cloneDir
    Assert-LastExitCode 'git clone xn-ptts'

    $env:RUSTFLAGS = '-C target-cpu=x86-64-v3'
    Write-Host "==> cargo build --release -p ptts-ffi (RUSTFLAGS=$env:RUSTFLAGS)"
    Push-Location $cloneDir
    try {
        cargo build --release -p ptts-ffi
        Assert-LastExitCode 'cargo build --release -p ptts-ffi'
    }
    finally {
        Pop-Location
    }

    # Windows 上 cargo 的輸出是 target\release\。
    $dllSource = Join-Path $cloneDir 'target\release\ptts_ffi.dll'
    $dllTarget = Join-Path $assetsDir 'ptts_ffi.dll'
    if (-not (Test-Path $dllSource)) {
        throw "找不到 cargo 建置產物：$dllSource"
    }
    Copy-Item $dllSource $dllTarget -Force
    Write-Host "==> 已複製 $dllSource -> $dllTarget"

    foreach ($file in $modelFiles) {
        $url = "$modelBase/$file"
        $out = Join-Path $modelDir $file
        Write-Host "==> 下載 $url"
        try {
            # 非 2xx（含 HF 的 404/redirect 失敗）會擲出例外——不靜默續行。
            Invoke-WebRequest -Uri $url -OutFile $out
        }
        catch {
            throw "下載 $url 失敗：$($_.Exception.Message)"
        }
        $downloaded = Get-Item -LiteralPath $out -ErrorAction SilentlyContinue
        if ($null -eq $downloaded -or $downloaded.Length -eq 0) {
            throw "下載 $url 後檔案不存在或為 0 byte：$out"
        }
    }
}
finally {
    if (Test-Path $cloneDir) {
        Remove-Item $cloneDir -Recurse -Force
    }
}

$suggestedStamp = "0.6.0+$ModelCommit"
Write-Host ''
Write-Host "== 完成。建議 version stamp（Services/TtsAssetStore.InternalStamp）："
Write-Host "    $suggestedStamp"
Write-Host ''
Write-Host '資產檔大小：'
$assetNames = @('ptts_ffi.dll')
foreach ($file in $modelFiles) {
    $assetNames += "model/$file"
}
foreach ($asset in $assetNames) {
    $path = Join-Path $assetsDir $asset
    $item = Get-Item -LiteralPath $path
    Write-Host ("    {0,-40} {1:N0} bytes" -f $asset, $item.Length)
}