using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Sam.AiAssistant;

/// <summary>
/// تمام سوال/جواب‌های فعال و تمام Sectionهای فعال را در هر فراخوانی از نو می‌خواند، همه Queryها را با
/// پارامترهای داده‌شده اجرا می‌کند و یک String نهایی می‌سازد. هیچ انتخاب یا Cache ای وجود ندارد.
/// </summary>
public sealed partial class AiContextBuilder(
    AiAssistantStore store,
    AiAssistantOptions options,
    ILogger<AiContextBuilder> logger)
{
    private static readonly string[] SensitiveColumnMarkers =
        ["password", "token", "otp", "secret", "hash", "securitystamp", "apikey", "fcm"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|MERGE|EXEC|EXECUTE|GRANT|REVOKE|DENY|INTO|OPENROWSET|OPENQUERY|BACKUP|RESTORE|SHUTDOWN)\b|\bxp_", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenSql();

    private sealed record SectionResult(AiSection Section, string Status, string Json);

    public async Task<string> BuildContextAsync(
        IReadOnlyDictionary<string, object?> parameters, AiSettings settings, CancellationToken ct = default)
    {
        var questions = await store.ListQuestionsAsync(activeOnly: true, ct);
        var sections = await store.ListSectionsAsync(activeOnly: true, ct);

        var knowledge = new StringBuilder();
        foreach (var q in questions)
        {
            knowledge.AppendLine(Render(settings.Get("KnowledgeItemTemplate"), new()
            {
                ["Category"] = q.Category,
                ["Question"] = q.Question,
                ["Answer"] = q.Answer,
                ["Alternatives"] = q.AlternativeQuestions.Count == 0 ? "-" : string.Join(" | ", q.AlternativeQuestions)
            }));
        }

        using var gate = new SemaphoreSlim(Math.Max(1, settings.MaxParallelQueries));
        var results = await Task.WhenAll(sections.Select(s => RunSectionAsync(s, parameters, settings, gate, ct)));

        var template = settings.Get("ContextTemplate");
        var fixedLength = template.Length + knowledge.Length;
        var budget = Math.Max(0, settings.MaxContextChars - fixedLength);

        var sectionsText = new StringBuilder();
        foreach (var result in results)
        {
            var block = BuildSectionBlock(result, settings, budget);
            budget = Math.Max(0, budget - block.Length);
            sectionsText.AppendLine(block);
        }

        var values = new Dictionary<string, string>
        {
            ["KnowledgeBase"] = knowledge.ToString(),
            ["Sections"] = sectionsText.ToString()
        };
        foreach (var (name, value) in parameters)
            values["Param." + name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        return Render(template, values);
    }

    public sealed record SectionTestResult(bool Ok, string Message, string? Json, int RowCount, long ElapsedMs);

    /// <summary>اجرای آزمایشی یک Section (ذخیره‌نشده) برای صفحه مدیریت؛ خطای SQL فقط به ادمین نمایش داده می‌شود.</summary>
    public async Task<SectionTestResult> TestSectionAsync(
        AiSection section, IReadOnlyDictionary<string, object?> parameters, AiSettings settings, CancellationToken ct = default)
    {
        if (!IsQueryAllowed(section))
            return new(false, "Query مجاز نیست: فقط SELECT/WITH تک‌دستوری، بدون دستور تغییر، «;» و کامنت" +
                (options.RequiredParameter is null || section.IsPublic ? "." : $"؛ و باید @{options.RequiredParameter} داشته باشد."), null, 0, 0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.QueryTimeoutSeconds + 5));
            var rows = await ExecuteAsync(section.Query, options.ResolveConnection(section), parameters, settings, timeout.Token);
            var cut = rows.Count > settings.MaxRowsPerSection;
            if (cut) rows = rows.Take(settings.MaxRowsPerSection).ToList();
            object? payload = section.SingleRow ? rows.FirstOrDefault() : rows;
            return new(true, rows.Count == 0 ? "اجرا موفق بود ولی نتیجه‌ای برنگشت." : cut ? "موفق (ردیف‌ها کوتاه شد)" : "موفق",
                JsonSerializer.Serialize(payload, JsonOptions), rows.Count, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(false, ex.Message, null, 0, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>جایگزینی {{Key}} با مقدار؛ کلیدهای ناشناخته دست‌نخورده می‌مانند.</summary>
    public static string Render(string template, Dictionary<string, string> values)
        => PlaceholderRegex().Replace(template, m => values.TryGetValue(m.Groups[1].Value.Trim(), out var v) ? v : m.Value);

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    private static string BuildSectionBlock(SectionResult result, AiSettings settings, int budget)
    {
        var data = result.Json;
        var limit = Math.Min(settings.MaxSectionChars, budget);
        var truncated = data.Length > limit;
        if (truncated) data = data[..Math.Max(0, limit)];

        var text = Render(settings.Get("SectionTemplate"), new()
        {
            ["Title"] = result.Section.Title,
            ["Description"] = result.Section.Value,
            ["Status"] = result.Status,
            ["Data"] = data
        });
        return truncated ? text + "\n" + settings.Get("TruncatedNote") : text;
    }

    private async Task<SectionResult> RunSectionAsync(
        AiSection section, IReadOnlyDictionary<string, object?> parameters, AiSettings settings,
        SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!IsQueryAllowed(section))
            {
                logger.LogError("AI section '{Section}' rejected by query safety check", section.Title);
                return new SectionResult(section, settings.Get("StatusError"), "null");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.QueryTimeoutSeconds + 5));

            var rows = await ExecuteAsync(section.Query, options.ResolveConnection(section), parameters, settings, timeout.Token);
            var cut = rows.Count > settings.MaxRowsPerSection;
            if (cut) rows = rows.Take(settings.MaxRowsPerSection).ToList();

            object? payload = section.SingleRow ? rows.FirstOrDefault() : rows;
            var status = rows.Count == 0 ? settings.Get("StatusEmpty") : settings.Get("StatusOk") + (cut ? " (truncated rows)" : "");
            return new SectionResult(section, status, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // متن SQL و مقدار پارامترها نه لاگ می‌شود و نه به مدل می‌رسد.
            logger.LogError(ex, "AI section '{Section}' failed", section.Title);
            return new SectionResult(section, settings.Get("StatusError"), "null");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>فقط SELECT/WITH تک‌دستوری و بدون دستور تغییر؛ Query غیرعمومی باید پارامتر الزامی را داشته باشد.</summary>
    public bool IsQueryAllowed(AiSection section)
    {
        var q = section.Query.Trim();
        if (!(q.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || q.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (q.Contains(';') || q.Contains("--") || q.Contains("/*")) return false;
        if (ForbiddenSql().IsMatch(q)) return false;
        if (section.IsPublic || options.RequiredParameter is null) return true;
        return Regex.IsMatch(q, $@"@{Regex.Escape(options.RequiredParameter)}\b", RegexOptions.IgnoreCase);
    }

    private async Task<List<Dictionary<string, object?>>> ExecuteAsync(
        string query, string connectionString, IReadOnlyDictionary<string, object?> parameters, AiSettings settings, CancellationToken ct)
    {
        // اتصال مستقل برای هر Query تا اجرای هم‌زمان امن باشد.
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = settings.QueryTimeoutSeconds;
        foreach (var (name, value) in parameters)
            command.Parameters.Add(new SqlParameter("@" + name, value ?? DBNull.Value));

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var skip = names.Select(n => SensitiveColumnMarkers.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase))).ToArray();

        while (rows.Count <= settings.MaxRowsPerSection && await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(names.Length);
            for (var i = 0; i < names.Length; i++)
            {
                if (skip[i]) continue;
                row[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }
}
