using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls("http://127.0.0.1:5093");
builder.Services.AddTransient<OnlyGatewayHandler>();
builder.Services.AddHttpClient("gateway", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:5090");
    client.Timeout = TimeSpan.FromSeconds(10);
}).AddHttpMessageHandler<OnlyGatewayHandler>();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var app = builder.Build();
var root = DataRoot();
var dbPath = Path.Combine(root, "data", "messages", "messages.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
InitDb(dbPath);
var gateway = new GatewayClient(app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("gateway"), json);

app.UseExceptionHandler(handler =>
{
    handler.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("""{"error":"Внутренняя ошибка."}""");
    });
});

app.MapGet("/health", () => Results.Json(new { status = "ok", service = "message" }, json));

app.MapPost("/api/rooms/{roomId}/messages", async (HttpContext ctx, string roomId, CancellationToken ct) =>
{
    var gate = await Authorize(ctx, gateway, roomId, ct);
    if (gate.Error is not null)
        return gate.Error;

    var body = await Read<MessageBody>(ctx.Request, json);
    if (body is null)
        return Fail(400, "Пустое или нечитаемое тело запроса.");
    var text = (body.Body ?? "").Trim();
    if (string.IsNullOrEmpty(text))
        return Fail(400, "Текст сообщения пуст.");
    if (text.Length > 2000)
        return Fail(400, "Сообщение слишком длинное.");

    var id = Guid.NewGuid().ToString("N");
    var created = Now();
    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO messages (id, room_id, author_id, body, created_utc)
        VALUES ($id, $room, $author, $body, $created)
        """;
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$room", roomId);
    cmd.Parameters.AddWithValue("$author", gate.UserId);
    cmd.Parameters.AddWithValue("$body", text);
    cmd.Parameters.AddWithValue("$created", created);
    cmd.ExecuteNonQuery();

    return Results.Json(new
    {
        id,
        roomId,
        body = text,
        createdUtc = created,
        authorId = gate.UserId
    }, json, statusCode: 201);
});

app.MapGet("/api/rooms/{roomId}/messages", async (HttpContext ctx, string roomId, CancellationToken ct) =>
{
    var gate = await Authorize(ctx, gateway, roomId, ct);
    if (gate.Error is not null)
        return gate.Error;

    var rows = new List<StoredMessage>();
    using (var conn = Open(dbPath))
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = """
            SELECT id, room_id, author_id, body, created_utc
            FROM messages
            WHERE room_id = $room
            ORDER BY created_utc, id
            """;
        cmd.Parameters.AddWithValue("$room", roomId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
    }

    var ids = rows.Select(row => row.AuthorId).Distinct(StringComparer.Ordinal).ToArray();
    ProfileMap profiles;
    try
    {
        profiles = await gateway.Profiles(ids, ct);
    }
    catch (HttpRequestException)
    {
        return Fail(502, "Шлюз недоступен, история без имени не отдаётся.");
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return Fail(502, "Шлюз недоступен, история без имени не отдаётся.");
    }
    if (!profiles.Ok)
        return Fail(502, "Шлюз недоступен, история без имени не отдаётся.");

    var history = rows.Select(row =>
    {
        profiles.ById.TryGetValue(row.AuthorId, out var profile);
        return new
        {
            id = row.Id,
            roomId = row.RoomId,
            body = row.Body,
            createdUtc = row.CreatedUtc,
            author = new
            {
                id = row.AuthorId,
                displayName = profile?.DisplayName ?? "Неизвестный",
                avatarUrl = profile?.AvatarUrl
            }
        };
    });
    return Results.Json(history, json);
});

Console.WriteLine("Message слушает http://127.0.0.1:5093");
Console.WriteLine("База: data/messages/messages.db");
app.Run();

static async Task<(IResult? Error, string UserId)> Authorize(
    HttpContext ctx, GatewayClient gateway, string roomId, CancellationToken ct)
{
    var token = ReadBearer(ctx.Request);
    if (string.IsNullOrEmpty(token))
        return (Fail(401, "Нужно войти."), "");

    ResolvedSession session;
    Membership membership;
    try
    {
        session = await gateway.Resolve(token, ct);
        if (session.Status == 401)
            return (Fail(401, string.IsNullOrEmpty(session.Error) ? "Сеанс не найден." : session.Error), "");
        if (session.Status != 200 || string.IsNullOrEmpty(session.UserId))
            return (Fail(502, "Шлюз не подтвердил сеанс."), "");

        membership = await gateway.Membership(roomId, session.UserId, ct);
    }
    catch (HttpRequestException)
    {
        return (Fail(502, "Шлюз недоступен."), "");
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return (Fail(502, "Шлюз недоступен."), "");
    }

    if (membership.Status == 404)
        return (Fail(404, "Чат не найден."), "");
    if (membership.Status == 403 || !membership.IsMember)
        return (Fail(403, "Вы не участник этого чата."), "");
    if (membership.Status != 200)
        return (Fail(502, "Шлюз не подтвердил участие."), "");
    return (null, session.UserId!);
}

static string DataRoot()
{
    var env = Environment.GetEnvironmentVariable("CHAT_ROOT");
    if (!string.IsNullOrWhiteSpace(env))
        return Path.GetFullPath(env);
    return Directory.GetCurrentDirectory();
}

static void InitDb(string dbPath)
{
    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS messages (
            id TEXT PRIMARY KEY,
            room_id TEXT NOT NULL,
            author_id TEXT NOT NULL,
            body TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_messages_room ON messages(room_id, created_utc);
        """;
    cmd.ExecuteNonQuery();
}

static SqliteConnection Open(string dbPath)
{
    var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
    cmd.ExecuteNonQuery();
    return conn;
}

static string? ReadBearer(HttpRequest req)
{
    var header = req.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return header[prefix.Length..].Trim();
    return null;
}

static async Task<T?> Read<T>(HttpRequest req, JsonSerializerOptions json) where T : class
{
    if (req.ContentLength == 0)
        return null;
    try
    {
        return await req.ReadFromJsonAsync<T>(json);
    }
    catch (JsonException)
    {
        return null;
    }
}

static IResult Fail(int status, string error) =>
    Results.Json(new { error }, statusCode: status);

static string Now() => DateTime.UtcNow.ToString("O");

sealed class OnlyGatewayHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Host != "127.0.0.1" || uri.Port != 5090)
            throw new InvalidOperationException("Разрешён только шлюз на 127.0.0.1:5090.");
        return base.SendAsync(request, cancellationToken);
    }
}

sealed class GatewayClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public GatewayClient(HttpClient http, JsonSerializerOptions json)
    {
        _http = http;
        _json = json;
    }

    public async Task<ResolvedSession> Resolve(string token, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("/internal/session", new { token }, _json, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var body = Parse<SessionBody>(text);
        return new ResolvedSession((int)response.StatusCode, body?.UserId, body?.DisplayName, body?.Error);
    }

    public async Task<Membership> Membership(string roomId, string userId, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("/internal/membership", new { roomId, userId }, _json, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var body = Parse<MemberBody>(text);
        return new Membership((int)response.StatusCode, body?.Member == true);
    }

    public async Task<ProfileMap> Profiles(IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("/internal/profiles", new { userIds }, _json, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return new ProfileMap(false, new Dictionary<string, Profile>(StringComparer.Ordinal));
        var body = Parse<ProfilesBody>(text);
        var map = new Dictionary<string, Profile>(StringComparer.Ordinal);
        if (body?.Users is not null)
        {
            foreach (var user in body.Users)
            {
                if (!string.IsNullOrEmpty(user.Id))
                    map[user.Id] = new Profile(user.DisplayName ?? "Неизвестный", user.AvatarUrl);
            }
        }
        return new ProfileMap(true, map);
    }

    private T? Parse<T>(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(text, _json);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

sealed record ResolvedSession(int Status, string? UserId, string? DisplayName, string? Error);
sealed record Membership(int Status, bool IsMember);
sealed record Profile(string DisplayName, string? AvatarUrl);
sealed record ProfileMap(bool Ok, Dictionary<string, Profile> ById);
sealed record StoredMessage(string Id, string RoomId, string AuthorId, string Body, string CreatedUtc);
sealed record MessageBody(string? Body);
sealed record SessionBody(string? UserId, string? DisplayName, string? Error);
sealed record MemberBody(bool? Member, string? Error);
sealed record ProfilesBody(ProfileBody[]? Users);
sealed record ProfileBody(string? Id, string? DisplayName, string? AvatarUrl);
