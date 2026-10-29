using System.Net;
using System.Text;
using System.Text.Json;
using AITranslator.Models;
using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

/// <summary>
/// 測試用 stub handler：攔截請求（記錄 URL 與 body），回傳罐頭回應。
/// </summary>
internal sealed class StubOllamaHandler : HttpMessageHandler
{
    /// <summary>攔截到的最後一個請求。</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>攔截到的最後一個請求 body（字串形式）。</summary>
    public string? LastBody { get; private set; }

    /// <summary>罐頭回應；null 時回 200。</summary>
    public HttpResponseMessage? Response { get; set; }

    /// <summary>改為丟出例外（模擬連線失敗等）。優先於 <see cref="Response"/>。</summary>
    public Exception? Thrown { get; set; }

    /// <summary>讓請求懸掛直到 token 取消（模擬取消情境）。</summary>
    public bool HangUntilCancelled { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        if (Thrown is not null)
        {
            throw Thrown;
        }

        if (HangUntilCancelled)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return Response ?? new HttpResponseMessage(HttpStatusCode.OK);
    }
}

public class OllamaClientTests
{
    private static AppSettings CreateSettings(string? ollamaUrl = null, string? template = null) =>
        new()
        {
            OllamaUrl = ollamaUrl ?? "http://localhost:11434",
            Model = "beam:cloud",
            PromptTemplate = template ?? "翻譯成繁體中文:\n{text}",
        };

    private static (OllamaClient Client, StubOllamaHandler Handler) CreateClient()
    {
        var handler = new StubOllamaHandler();
        return (new OllamaClient(new HttpClient(handler)), handler);
    }

    private static string LastBodyOrThrow(StubOllamaHandler handler)
    {
        var body = handler.LastBody;
        Assert.NotNull(body);
        return body!;
    }

    // ---------- 成功路徑 ----------

    [Fact]
    public async Task SuccessJson_ParsesResponseFieldIntoText()
    {
        var (client, handler) = CreateClient();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"response":"譯文"}""", Encoding.UTF8, "application/json"),
        };

        var result = await client.TranslateAsync(CreateSettings(), "Hello", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("譯文", result.Text);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task Request_UsesGenerateEndpointOnTrimmedUrl()
    {
        var (client, handler) = CreateClient();
        var requestTask = client.TranslateAsync(CreateSettings("http://localhost:11434/"), "Hello", CancellationToken.None);
        var result = await requestTask;

        _ = result;
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("http://localhost:11434/api/generate", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task RequestBody_ContainsStreamFalse()
    {
        var (client, handler) = CreateClient();

        await client.TranslateAsync(CreateSettings(), "Hello", CancellationToken.None);

        Assert.Contains("\"stream\":false", LastBodyOrThrow(handler));
    }

    [Fact]
    public async Task RequestBody_ContainsModelName()
    {
        var (client, handler) = CreateClient();

        await client.TranslateAsync(CreateSettings(), "Hello", CancellationToken.None);

        using var doc = JsonDocument.Parse(LastBodyOrThrow(handler));
        Assert.Equal("beam:cloud", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task RequestBody_PromptIsTemplateComposedWithText()
    {
        var (client, handler) = CreateClient();
        const string template = "翻譯成繁體中文:\n{text}";
        const string text = "abc";

        await client.TranslateAsync(CreateSettings(template: template), text, CancellationToken.None);

        using var doc = JsonDocument.Parse(LastBodyOrThrow(handler));
        Assert.Equal("翻譯成繁體中文:\nabc", doc.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task RequestBody_PromptWithoutPlaceholderIsComposedOnNewLine()
    {
        var (client, handler) = CreateClient();

        await client.TranslateAsync(
            CreateSettings(template: "請翻譯以下內容"), "abc", CancellationToken.None);

        using var doc = JsonDocument.Parse(LastBodyOrThrow(handler));
        Assert.Equal("請翻譯以下內容\nabc", doc.RootElement.GetProperty("prompt").GetString());
    }

    // ---------- 錯誤映射 ----------

    [Fact]
    public async Task ModelNotFound400_ReturnsReadableErrorWithModelName()
    {
        var (client, handler) = CreateClient();
        handler.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":"model \"beam:wrong\" not found, try pulling it first"}""",
                Encoding.UTF8,
                "application/json"),
        };

        var result = await client.TranslateAsync(CreateSettings(), "Hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
        Assert.Contains("找不到模型 \"beam:cloud\"", result.Error);
        Assert.Contains("請確認名稱是否正確", result.Error);
    }

    [Fact]
    public async Task Http500_ReturnsHttpStatusMessage()
    {
        var (client, handler) = CreateClient();
        handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
        };

        var result = await client.TranslateAsync(CreateSettings(), "Hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal("Ollama 回應錯誤 (HTTP 500)", result.Error);
    }

    [Fact]
    public async Task Http400NotModelRelated_ReturnsHttpStatusMessage()
    {
        var (client, handler) = CreateClient();
        handler.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"malformed"}""", Encoding.UTF8, "application/json"),
        };

        var result = await client.TranslateAsync(CreateSettings(), "Hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Ollama 回應錯誤 (HTTP 400)", result.Error);
    }

    [Fact]
    public async Task ConnectionFailure_ReturnsCantConnectMessage()
    {
        var (client, handler) = CreateClient();
        handler.Thrown = new HttpRequestException("socket error");

        var result = await client.TranslateAsync(CreateSettings("http://localhost:11434"), "Hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(
            "無法連線到 Ollama (http://localhost:11434)，請確認 Ollama 已啟動",
            result.Error);
    }

    [Fact]
    public async Task ExternalCancellation_ReturnsEmptyErrorSuccessFalse()
    {
        var (client, handler) = CreateClient();
        handler.HangUntilCancelled = true;
        using var cts = new CancellationTokenSource(200);

        var result = await client.TranslateAsync(CreateSettings(), "Hello", cts.Token);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task HttpClientTimeout_ReturnsReadableTimeoutError()
    {
        var (client, handler) = CreateClient();
        // 模擬 HttpClient 逾時：handler 丟出 TaskCanceledException，但外部 ct 未取消。
        handler.Thrown = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        var result = await client.TranslateAsync(CreateSettings(), "Hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal("Ollama 回應逾時，請確認模型已載入後再試", result.Error);
    }
}