namespace Sam.AiAssistant;

public sealed class AiQuestion
{
    public int Id { get; set; }
    public string Category { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public List<string> AlternativeQuestions { get; set; } = new();
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class AiSection
{
    public int Id { get; set; }
    public string Title { get; set; } = "";

    /// <summary>توضیح مفهوم داده‌ای که Query برمی‌گرداند.</summary>
    public string Value { get; set; } = "";

    /// <summary>Query فقط‌خواندنی و پارامتری (@UserId و هر پارامتر ثبت‌شده در Options).</summary>
    public string Query { get; set; } = "";

    /// <summary>نتیجه به‌صورت یک آبجکت (نه آرایه).</summary>
    public bool SingleRow { get; set; }

    /// <summary>داده عمومی؛ بررسی پارامتر الزامی (UserId) برای آن انجام نمی‌شود.</summary>
    public bool IsPublic { get; set; }

    /// <summary>
    /// دیتابیس اجرای این Query: یک Connection String کامل، یا نام یک اتصال ثبت‌شده با AddQueryConnection.
    /// خالی = QueryConnectionString پیش‌فرض.
    /// </summary>
    public string? ConnectionString { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class AiChatLog
{
    public long Id { get; set; }
    public string? UserKey { get; set; }
    public string Question { get; set; } = "";
    public string? Answer { get; set; }
    public string? Error { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>خطایی که پیامش برای نمایش مستقیم به کاربر امن است.</summary>
public sealed class AiAssistantException(string userMessage, Exception? inner = null) : Exception(userMessage, inner);
