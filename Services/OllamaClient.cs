using System.Text.Json;
using System.Text.RegularExpressions;
using AITranslator.Models;

namespace AITranslator.Services;

/// <summary>翻譯呼叫結果。失敗時 Text 為空字串，Error 為可顯示訊息；使用者取消時 Error 為空字串。</summary>
public sealed record TranslateResult(bool Success, string Text, string Error);

/// <summary>
/// 透過 Ollama /api/generate 進行翻譯。請求 body 為
/// <c>{"model":...,"prompt":...,"stream":false}</c>，成功時讀回
/// <c>response</c> 欄位作為譯文；各種失敗映射為繁體中文可讀訊息。
/// </summary>
public sealed class OllamaClient
{
    private static readonly Regex ModelNotFoundPattern =
        new("model.*not.*found", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private const string UnparseableResponseError = "Ollama 回應錯誤 (HTTP 200，內容無法解析)";

    private readonly HttpClient _http;

    public OllamaClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<TranslateResult> TranslateAsync(AppSettings settings, string text, CancellationToken ct)
    {
        var url = settings.OllamaUrl.TrimEnd('/') + "/api/generate";
        var payload = JsonSerializer.Serialize(new
        {
            model = settings.Model,
            prompt = PromptBuilder.Build(settings.PromptTemplate, text),
            stream = false,
        });

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
                return new TranslateResult(false, string.Empty, $"找不到模型 \"{settings.Model}\"，請確認名稱是否正確");
            }

            return new TranslateResult(false, string.Empty, $"Ollama 回應錯誤 (HTTP {(int)response.StatusCode})");
        }
        catch (HttpRequestException)
        {
            return new TranslateResult(false, string.Empty, $"無法連線到 Ollama ({settings.OllamaUrl})，請確認 Ollama 已啟動");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 外部取消（非逾時）：視為使用者取消，無錯誤訊息。
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