using System.Collections.Concurrent;

namespace Sam.AiAssistant;

/// <summary>
/// حافظه‌ی گفتگو (در RAM سرور): برای هر (کاربر + شناسه گفتگو) آخرین N نوبت «سؤال کاربر / پاسخ دستیار» نگه‌داری می‌شود.
/// تاریخچه فقط سمت سرور ساخته می‌شود، پس کلاینت نمی‌تواند پاسخ جعلی به‌جای دستیار تزریق کند.
/// فقط سؤال خام کاربر ذخیره می‌شود (نه Context دیتابیس) تا حجم و داده‌ی قدیمی به مدل نرسد.
/// </summary>
public sealed class AiConversationMemory
{
    public sealed record Turn(string Question, string Answer);

    private sealed class Entry
    {
        public readonly object Lock = new();
        public readonly List<Turn> Turns = new();
        public DateTime LastUsed = DateTime.UtcNow;
    }

    private static readonly TimeSpan IdleTtl = TimeSpan.FromHours(1);
    private const int MaxConversations = 5000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private DateTime _lastSweep = DateTime.UtcNow;

    private static string Key(string userKey, string conversationId) => userKey + "\u001f" + conversationId;

    /// <summary>آخرین <paramref name="maxTurns"/> نوبت؛ با maxTurns=0 حافظه خاموش است و چیزی برنمی‌گردد.</summary>
    public IReadOnlyList<Turn> Get(string userKey, string conversationId, int maxTurns)
    {
        if (maxTurns <= 0 || !_entries.TryGetValue(Key(userKey, conversationId), out var e)) return [];
        lock (e.Lock)
        {
            e.LastUsed = DateTime.UtcNow;
            return e.Turns.TakeLast(maxTurns).ToList();
        }
    }

    public void Add(string userKey, string conversationId, string question, string answer, int maxTurns)
    {
        if (maxTurns <= 0) return;
        Sweep();
        var e = _entries.GetOrAdd(Key(userKey, conversationId), _ => new Entry());
        lock (e.Lock)
        {
            e.Turns.Add(new Turn(question, answer));
            if (e.Turns.Count > maxTurns) e.Turns.RemoveRange(0, e.Turns.Count - maxTurns);
            e.LastUsed = DateTime.UtcNow;
        }
    }

    public void Clear(string userKey, string conversationId) => _entries.TryRemove(Key(userKey, conversationId), out _);

    /// <summary>پاک‌سازی گفتگوهای بی‌استفاده تا حافظه سرور بی‌نهایت رشد نکند.</summary>
    private void Sweep()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweep < TimeSpan.FromMinutes(5) && _entries.Count < MaxConversations) return;
        _lastSweep = now;
        foreach (var (k, e) in _entries)
            if (now - e.LastUsed > IdleTtl) _entries.TryRemove(k, out _);
        if (_entries.Count >= MaxConversations)
            foreach (var k in _entries.OrderBy(p => p.Value.LastUsed).Take(_entries.Count - MaxConversations / 2).Select(p => p.Key))
                _entries.TryRemove(k, out _);
    }
}
