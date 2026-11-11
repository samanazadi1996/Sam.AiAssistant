using Microsoft.Data.SqlClient;

namespace Sam.AiAssistant;

/// <summary>دسترسی به جدول‌های خود دستیار (schema: AiAssistant) روی DataConnectionString با ADO.NET.</summary>
public sealed class AiAssistantStore(AiAssistantOptions options)
{
    private const string SeededQuestionsKey = "_SeededQuestions";
    private const string SeededSectionsKey = "_SeededSections";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _ready;

    private const string Ddl = """
        IF SCHEMA_ID('AiAssistant') IS NULL EXEC('CREATE SCHEMA AiAssistant');

        IF OBJECT_ID('AiAssistant.Settings', 'U') IS NULL
            CREATE TABLE AiAssistant.Settings(
                [Key] nvarchar(100) NOT NULL CONSTRAINT PK_AiAssistant_Settings PRIMARY KEY,
                [Value] nvarchar(max) NOT NULL,
                UpdatedAt datetime2 NOT NULL CONSTRAINT DF_AiAssistant_Settings_UpdatedAt DEFAULT SYSDATETIME());

        IF OBJECT_ID('AiAssistant.Questions', 'U') IS NULL
            CREATE TABLE AiAssistant.Questions(
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiAssistant_Questions PRIMARY KEY,
                Category nvarchar(200) NOT NULL,
                Question nvarchar(1000) NOT NULL,
                Answer nvarchar(max) NOT NULL,
                AlternativeQuestions nvarchar(max) NULL,
                SortOrder int NOT NULL CONSTRAINT DF_AiAssistant_Questions_SortOrder DEFAULT 0,
                IsActive bit NOT NULL CONSTRAINT DF_AiAssistant_Questions_IsActive DEFAULT 1,
                UpdatedAt datetime2 NOT NULL CONSTRAINT DF_AiAssistant_Questions_UpdatedAt DEFAULT SYSDATETIME());

        IF OBJECT_ID('AiAssistant.Sections', 'U') IS NULL
            CREATE TABLE AiAssistant.Sections(
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiAssistant_Sections PRIMARY KEY,
                Title nvarchar(200) NOT NULL,
                Value nvarchar(1000) NOT NULL,
                Query nvarchar(max) NOT NULL,
                SingleRow bit NOT NULL CONSTRAINT DF_AiAssistant_Sections_SingleRow DEFAULT 0,
                IsPublic bit NOT NULL CONSTRAINT DF_AiAssistant_Sections_IsPublic DEFAULT 0,
                ConnectionString nvarchar(1000) NULL,
                SortOrder int NOT NULL CONSTRAINT DF_AiAssistant_Sections_SortOrder DEFAULT 0,
                IsActive bit NOT NULL CONSTRAINT DF_AiAssistant_Sections_IsActive DEFAULT 1,
                UpdatedAt datetime2 NOT NULL CONSTRAINT DF_AiAssistant_Sections_UpdatedAt DEFAULT SYSDATETIME());

        IF COL_LENGTH('AiAssistant.Sections', 'ConnectionString') IS NULL
            ALTER TABLE AiAssistant.Sections ADD ConnectionString nvarchar(1000) NULL;

        IF OBJECT_ID('AiAssistant.ChatLogs', 'U') IS NULL
            CREATE TABLE AiAssistant.ChatLogs(
                Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AiAssistant_ChatLogs PRIMARY KEY,
                UserKey nvarchar(100) NULL,
                Question nvarchar(2000) NOT NULL,
                Answer nvarchar(max) NULL,
                Error nvarchar(1000) NULL,
                DurationMs int NOT NULL,
                CreatedAt datetime2 NOT NULL CONSTRAINT DF_AiAssistant_ChatLogs_CreatedAt DEFAULT SYSDATETIME());
        """;

    // ---------- راه‌اندازی ----------

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_ready) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_ready) return;
            if (options.AutoCreateDatabase) await EnsureDatabaseAsync(ct);
            if (options.AutoCreateTables)
            {
                await using var c = await OpenAsync(ct);
                await using (var cmd = Cmd(c, Ddl)) await cmd.ExecuteNonQueryAsync(ct);
                await SeedAsync(c, ct);
            }
            _ready = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>اگر دیتابیس وجود ندارد آن را از طریق master می‌سازد.</summary>
    private async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        var target = new SqlConnectionStringBuilder(options.DataConnectionString);
        var name = target.InitialCatalog;
        if (string.IsNullOrWhiteSpace(name)) return;

        var master = new SqlConnectionStringBuilder(options.DataConnectionString) { InitialCatalog = "master" };
        await using var c = new SqlConnection(master.ConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = Cmd(c, "IF DB_ID(@n) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@n); EXEC(@sql); END", ("@n", name));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SeedAsync(SqlConnection c, CancellationToken ct)
    {
        var stored = await ReadSettingsAsync(c, ct);

        foreach (var def in AiSettings.Definitions)
        {
            if (stored.ContainsKey(def.Key)) continue;
            var value = options.SettingDefaults.TryGetValue(def.Key, out var o) ? o : def.Default;
            await Exec(c, ct, "INSERT INTO AiAssistant.Settings([Key],[Value]) VALUES(@k,@v)", ("@k", def.Key), ("@v", value));
        }

        if (!stored.ContainsKey(SeededQuestionsKey))
        {
            foreach (var q in options.SeedQuestions) await InsertQuestionAsync(c, q, ct);
            await Exec(c, ct, "INSERT INTO AiAssistant.Settings([Key],[Value]) VALUES(@k,'1')", ("@k", SeededQuestionsKey));
        }

        if (!stored.ContainsKey(SeededSectionsKey))
        {
            foreach (var s in options.SeedSections) await InsertSectionAsync(c, s, ct);
            await Exec(c, ct, "INSERT INTO AiAssistant.Settings([Key],[Value]) VALUES(@k,'1')", ("@k", SeededSectionsKey));
        }
    }

    // ---------- تنظیمات ----------

    public async Task<AiSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        return AiSettings.From(await ReadSettingsAsync(c, ct), options.SettingDefaults);
    }

    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        var known = AiSettings.Definitions.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values.Where(p => known.Contains(p.Key)))
        {
            await Exec(c, ct, """
                UPDATE AiAssistant.Settings SET [Value]=@v, UpdatedAt=SYSDATETIME() WHERE [Key]=@k;
                IF @@ROWCOUNT = 0 INSERT INTO AiAssistant.Settings([Key],[Value]) VALUES(@k,@v);
                """, ("@k", key), ("@v", value ?? ""));
        }
    }

    private static async Task<Dictionary<string, string>> ReadSettingsAsync(SqlConnection c, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = Cmd(c, "SELECT [Key],[Value] FROM AiAssistant.Settings");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetString(0)] = r.GetString(1);
        return result;
    }

    // ---------- سوال و جواب ----------

    public async Task<List<AiQuestion>> ListQuestionsAsync(bool activeOnly, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, $"""
            SELECT Id, Category, Question, Answer, AlternativeQuestions, SortOrder, IsActive
            FROM AiAssistant.Questions {(activeOnly ? "WHERE IsActive = 1" : "")}
            ORDER BY Category, SortOrder, Id
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AiQuestion>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new AiQuestion
            {
                Id = r.GetInt32(0),
                Category = r.GetString(1),
                Question = r.GetString(2),
                Answer = r.GetString(3),
                AlternativeQuestions = r.IsDBNull(4)
                    ? new()
                    : r.GetString(4).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                SortOrder = r.GetInt32(5),
                IsActive = r.GetBoolean(6)
            });
        }
        return list;
    }

    public async Task<int> SaveQuestionAsync(AiQuestion q, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        if (q.Id == 0) return await InsertQuestionAsync(c, q, ct);

        await Exec(c, ct, """
            UPDATE AiAssistant.Questions SET Category=@c, Question=@q, Answer=@a, AlternativeQuestions=@alt,
                SortOrder=@s, IsActive=@active, UpdatedAt=SYSDATETIME() WHERE Id=@id
            """, ("@id", q.Id), ("@c", q.Category), ("@q", q.Question), ("@a", q.Answer),
            ("@alt", JoinAlternatives(q)), ("@s", q.SortOrder), ("@active", q.IsActive));
        return q.Id;
    }

    public async Task DeleteQuestionAsync(int id, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        await Exec(c, ct, "DELETE FROM AiAssistant.Questions WHERE Id=@id", ("@id", id));
    }

    private static string JoinAlternatives(AiQuestion q)
        => string.Join('\n', q.AlternativeQuestions.Select(a => a.Trim()).Where(a => a.Length > 0));

    private static async Task<int> InsertQuestionAsync(SqlConnection c, AiQuestion q, CancellationToken ct)
    {
        await using var cmd = Cmd(c, """
            INSERT INTO AiAssistant.Questions(Category, Question, Answer, AlternativeQuestions, SortOrder, IsActive)
            OUTPUT INSERTED.Id VALUES(@c,@q,@a,@alt,@s,@active)
            """, ("@c", q.Category), ("@q", q.Question), ("@a", q.Answer), ("@alt", JoinAlternatives(q)),
            ("@s", q.SortOrder), ("@active", q.IsActive));
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ---------- Sectionها ----------

    public async Task<List<AiSection>> ListSectionsAsync(bool activeOnly, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, $"""
            SELECT Id, Title, Value, Query, SingleRow, IsPublic, SortOrder, IsActive, ConnectionString
            FROM AiAssistant.Sections {(activeOnly ? "WHERE IsActive = 1" : "")}
            ORDER BY SortOrder, Id
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AiSection>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new AiSection
            {
                Id = r.GetInt32(0),
                Title = r.GetString(1),
                Value = r.GetString(2),
                Query = r.GetString(3),
                SingleRow = r.GetBoolean(4),
                IsPublic = r.GetBoolean(5),
                SortOrder = r.GetInt32(6),
                IsActive = r.GetBoolean(7),
                ConnectionString = r.IsDBNull(8) ? null : r.GetString(8)
            });
        }
        return list;
    }

    public async Task<int> SaveSectionAsync(AiSection s, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        if (s.Id == 0) return await InsertSectionAsync(c, s, ct);

        await Exec(c, ct, """
            UPDATE AiAssistant.Sections SET Title=@t, Value=@v, Query=@q, SingleRow=@sr, IsPublic=@p, ConnectionString=@cs,
                SortOrder=@o, IsActive=@a, UpdatedAt=SYSDATETIME() WHERE Id=@id
            """, ("@id", s.Id), ("@t", s.Title), ("@v", s.Value), ("@q", s.Query), ("@sr", s.SingleRow),
            ("@p", s.IsPublic), ("@cs", NullIfEmpty(s.ConnectionString)), ("@o", s.SortOrder), ("@a", s.IsActive));
        return s.Id;
    }

    public async Task DeleteSectionAsync(int id, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        await Exec(c, ct, "DELETE FROM AiAssistant.Sections WHERE Id=@id", ("@id", id));
    }

    private static async Task<int> InsertSectionAsync(SqlConnection c, AiSection s, CancellationToken ct)
    {
        await using var cmd = Cmd(c, """
            INSERT INTO AiAssistant.Sections(Title, Value, Query, SingleRow, IsPublic, ConnectionString, SortOrder, IsActive)
            OUTPUT INSERTED.Id VALUES(@t,@v,@q,@sr,@p,@cs,@o,@a)
            """, ("@t", s.Title), ("@v", s.Value), ("@q", s.Query), ("@sr", s.SingleRow),
            ("@p", s.IsPublic), ("@cs", NullIfEmpty(s.ConnectionString)), ("@o", s.SortOrder), ("@a", s.IsActive));
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ---------- لاگ ----------

    public async Task AddLogAsync(AiChatLog log, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        await Exec(c, ct, """
            INSERT INTO AiAssistant.ChatLogs(UserKey, Question, Answer, Error, DurationMs)
            VALUES(@u, @q, @a, @e, @d)
            """, ("@u", Trunc(log.UserKey, 100)), ("@q", Trunc(log.Question, 2000)!), ("@a", log.Answer),
            ("@e", Trunc(log.Error, 1000)), ("@d", log.DurationMs));
    }

    public async Task<List<AiChatLog>> ListLogsAsync(int top, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = Cmd(c, """
            SELECT TOP (@n) Id, UserKey, Question, Answer, Error, DurationMs, CreatedAt
            FROM AiAssistant.ChatLogs ORDER BY Id DESC
            """, ("@n", top));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AiChatLog>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new AiChatLog
            {
                Id = r.GetInt64(0),
                UserKey = r.IsDBNull(1) ? null : r.GetString(1),
                Question = r.GetString(2),
                Answer = r.IsDBNull(3) ? null : r.GetString(3),
                Error = r.IsDBNull(4) ? null : r.GetString(4),
                DurationMs = r.GetInt32(5),
                CreatedAt = r.GetDateTime(6)
            });
        }
        return list;
    }

    // ---------- ابزار ----------

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Trunc(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqlConnection(options.DataConnectionString);
        await c.OpenAsync(ct);
        return c;
    }

    private static SqlCommand Cmd(SqlConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static async Task Exec(SqlConnection c, CancellationToken ct, string sql, params (string, object?)[] parameters)
    {
        await using var cmd = Cmd(c, sql, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
