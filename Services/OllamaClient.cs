using System.Text.Json;
using System.Text.RegularExpressions;
using AITranslator.Models;

namespace AITranslator.Services;

/// <summary>翻譯呼叫結果。失敗時 Text 為空字串，Error 為可顯示訊息；使用者取消時 Error 為空字串。</summary>
public sealed record TranslateResult(bool Success, string Text, string Error);

/// <summary>
/// 透過 Ollama /api/generate 進行翻譯與圖片辨識。請求 body 為
/// <c>{"model":...,"prompt":...,"stream":false}</c>（辨識時另附 <c>images</c>），
/// 成功時讀回 <c>response</c> 欄位作為譯文；各種失敗映射為繁體中文可讀訊息。
/// </summary>
public sealed class OllamaClient
{
    private static readonly Regex ModelNotFoundPattern =
        new("model.*not.*found", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private const string UnparseableResponseError = "Ollama 回應錯誤 (HTTP 200，內容無法解析)";

    /// <summary>OCR 辨識提示詞：要求模型原樣輸出圖片中的文字。</summary>
    public const string OcrPrompt = "請原樣輸出圖片中的所有文字，保留原始段落與分行，不要翻譯、不要解釋、不要加註。";

    /// <summary>單次呼叫模式（辨識即翻譯）套入提示詞模板時使用的佔位文字。</summary>
    public const string OcrImagePlaceholderText = "這張圖片中的文字";

    private readonly HttpClient _http;

    public OllamaClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<TranslateResult> TranslateAsync(AppSettings settings, string text, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = settings.Model,
            prompt = PromptBuilder.Build(settings.PromptTemplate, text),
            stream = false,
        });

        return await SendGenerateAsync(settings.OllamaUrl, payload, settings.Model, ct);
    }

    /// <summary>圖片辨識核心呼叫：以指定提示詞向 <see cref="AppSettings.OcrModel"/> 送出帶影像的生成請求。</summary>
    public Task<TranslateResult> RecognizeAsync(AppSettings settings, string imageBase64, string prompt, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = settings.OcrModel,
            prompt,
            stream = false,
            images = new[] { imageBase64 },
        });

        return SendGenerateAsync(settings.OllamaUrl, payload, settings.OcrModel, ct);
    }

    /// <summary>兩次呼叫模式第一步：提示詞固定為 <see cref="OcrPrompt"/>，只辨識不翻譯。</summary>
    public Task<TranslateResult> RecognizeOcrTextAsync(AppSettings settings, string imageBase64, CancellationToken ct)
        => RecognizeAsync(settings, imageBase64, OcrPrompt, ct);

    /// <summary>單次呼叫模式：把 <see cref="OcrImagePlaceholderText"/> 套入翻譯模板，辨識後由同一模型直接輸出譯文。</summary>
    public Task<TranslateResult> RecognizeAndTranslateAsync(AppSettings settings, string imageBase64, CancellationToken ct)
        => RecognizeAsync(settings, imageBase64, PromptBuilder.Build(settings.PromptTemplate, OcrImagePlaceholderText), ct);

    private async Task<TranslateResult> SendGenerateAsync(string ollamaUrl, string payload, string model, CancellationToken ct)
    {
        var url = ollamaUrl.TrimEnd('/') + "/api/generate";

        try
        {
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var text2 = ParseResponseField(body);
                return new TranslateResult(true, text2, string.Empty);
            }

            if ((int)response.StatusCode == 400 && ModelNotFoundPattern.IsMatch(body))
            {
                return new TranslateResult(false, string.Empty, $"找不到模型 \"{model}\"，請確認名稱是否正確");
            }

            return new TranslateResult(false, string.Empty, $"Ollama 回應錯誤 (HTTP {(int)response.StatusCode})");
        }
        catch (HttpRequestException)
        {
            return new TranslateResult(false, string.Empty, $"無法連線到 Ollama ({ollamaUrl})，請確認 Ollama 已啟動");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient 逾時（外部 ct 仍未取消）。
            return new TranslateResult(false, string.Empty, "Ollama 回應逾時，請確認模型已載入後再試");
        }
        catch (OperationCanceledException)
        {
            // 外部取消：視為使用者取消，無錯誤訊息。
            return new TranslateResult(false, string.Empty, string.Empty);
        }
        catch (JsonException)
        {
            return new TranslateResult(false, string.Empty, UnparseableResponseError);
        }
    }

    private static string ParseResponseField(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("response", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}