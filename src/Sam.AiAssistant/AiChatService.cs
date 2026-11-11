using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Sam.AiAssistant;

/// <summary>
/// جریان هر پیام: خواندن تنظیمات → ساخت Context کامل (بدون Cache) → ارسال به Ollama (/api/chat) → پاسخ.
/// مدل فقط پاسخ نهایی می‌سازد؛ Tool Calling و Agent Loop وجود ندارد.
/// </summary>
public sealed class AiChatService(
    AiAssistantStore store,
    AiAssistantOptions options,
    AiContextBuilder contextBuilder,
    AiConversationMemory memory,
    HttpClient httpClient,
    ILogger<AiChatService> logger)
{
    public async Task<string> SendMessageAsync(
        IReadOnlyDictionary<string, object?> parameters, string question, string? conversationId = null,
        CancellationToken ct = default)
    {
        var settings = await store.GetSettingsAsync(ct);
        var userKey = options.RequiredParameter is not null && parameters.TryGetValue(options.RequiredParameter, out var u)
            ? Convert.ToString(u) : null;

        var sw = Stopwatch.StartNew();
        string? answer = null, error = null;
        try
        {
            if (question.Length > settings.MaxQuestionLength)
                throw new AiAssistantException($"طول پیام نباید بیشتر از {settings.MaxQuestionLength} کاراکتر باشد.");

            var context = await contextBuilder.BuildContextAsync(parameters, settings, ct);
            var userMessage = AiContextBuilder.Render(settings.Get("UserMessageTemplate"),
                new() { ["Context"] = context, ["Question"] = question });

            // حافظه: فقط وقتی شناسه گفتگوی معتبر داریم و MemoryTurns > 0
            var memoryKey = IsValidConversationId(conversationId) ? userKey ?? "anonymous" : null;
            var turns = memoryKey is null ? [] : memory.Get(memoryKey, conversationId!, settings.MemoryTurns);

            answer = await CallOllamaAsync(settings, settings.Get("SystemMessage"), userMessage, ct, turns);

            if (memoryKey is not null && answer != settings.Get("NotFoundMessage"))
            {
                var cap = settings.MemoryMaxCharsPerMessage;
                memory.Add(memoryKey, conversationId!, Clip(question, cap), Clip(answer, cap), settings.MemoryTurns);
            }
            return answer;
        }
        catch (AiAssistantException ex)
        {
            error = ex.Message;
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI assistant failure");
            error = settings.Get("GenericErrorMessage");
            throw new AiAssistantException(error, ex);
        }
        finally
        {
            if (options.LogConversations)
            {
                try
                {
                    await store.AddLogAsync(new AiChatLog
                    {
                        UserKey = userKey, Question = question, Answer = answer, Error = error,
                        DurationMs = (int)sw.ElapsedMilliseconds
                    }, CancellationToken.None);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Could not write AI chat log"); }
            }
        }
    }

    /// <summary>تست اتصال به Ollama و وجود مدل با یک پیام کوتاه (بدون Context).</summary>
    public async Task<(bool Ok, string Message, long ElapsedMs, string Model)> TestModelAsync(CancellationToken ct = default)
    {
        var settings = await store.GetSettingsAsync(ct);
        var label = $"Ollama: {settings.ModelName} @ {settings.OllamaBaseUrl}";
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await CallOllamaAsync(settings, "Reply with a single short word.", "Say hello.", ct);
            return (true, reply, sw.ElapsedMilliseconds, label);
        }
        catch (AiAssistantException ex)
        {
            return (false, ex.Message, sw.ElapsedMilliseconds, label);
        }
    }

    public static bool IsValidConversationId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private async Task<string> CallOllamaAsync(
        AiSettings settings, string system, string user, CancellationToken ct,
        IReadOnlyList<AiConversationMemory.Turn>? history = null)
    {
        // ترتیب: system → (سؤال/جواب‌های قبلی) → پیام فعلی همراه با Context تازه
        var messages = new List<OllamaMessage> { new("system", system) };
        foreach (var t in history ?? [])
        {
            messages.Add(new("user", t.Question));
            messages.Add(new("assistant", t.Answer));
        }
        messages.Add(new("user", user));

        var body = new OllamaRequest
        {
            Model = settings.ModelName,
            Stream = false,
            Messages = messages,
            Options = new OllamaOptions { Temperature = settings.Temperature, NumCtx = settings.ContextWindow }
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.OllamaTimeoutSeconds));
        var url = settings.OllamaBaseUrl.TrimEnd('/') + "/api/chat";

        try
        {
            using var response = await httpClient.PostAsJsonAsync(url, body, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Ollama returned HTTP {StatusCode}", (int)response.StatusCode);
                throw new AiAssistantException(response.StatusCode == HttpStatusCode.NotFound
                    ? settings.Get("ModelMissingMessage") : settings.Get("GenericErrorMessage"));
            }

            var parsed = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: timeout.Token);
            var text = parsed?.Message?.Content?.Trim();
            return string.IsNullOrWhiteSpace(text) ? settings.Get("NotFoundMessage") : text;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Ollama request timed out");
            throw new AiAssistantException(settings.Get("OllamaTimeoutMessage"), ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Ollama is unreachable");
            throw new AiAssistantException(settings.Get("OllamaUnreachableMessage"), ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Ollama returned invalid JSON");
            throw new AiAssistantException(settings.Get("GenericErrorMessage"), ex);
        }
    }

    private sealed class OllamaRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("messages")] public List<OllamaMessage> Messages { get; set; } = [];
        [JsonPropertyName("options")] public OllamaOptions Options { get; set; } = new();
    }

    private sealed class OllamaOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("num_ctx")] public int NumCtx { get; set; }
    }

    private sealed record OllamaMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class OllamaResponse
    {
        [JsonPropertyName("message")] public OllamaMessage? Message { get; set; }
    }
}
