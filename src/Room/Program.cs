using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls("http://127.0.0.1:5092");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var app = builder.Build();
var root = DataRoot();
var dbPath = Path.Combine(root, "data", "rooms", "rooms.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
InitDb(dbPath);

app.UseExceptionHandler(handler =>
{
    handler.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("""{"error":"Внутренняя ошибка."}""");
    });
});

app.MapGet("/health", () => Results.Json(new { status = "ok", service = "room" }, json));

app.MapPost("/api/rooms", async (HttpContext ctx) =>
{
    var body = await Read<CreateRoomBody>(ctx.Request, json);
    if (body is null)
        return Fail(400, "Пустое или нечитаемое тело запроса.");
    var title = (body.Title ?? "").Trim();
    var ownerId = (body.OwnerId ?? "").Trim();
    if (string.IsNullOrEmpty(title))
        return Fail(400, "Укажите название чата.");
    if (title.Length > 120)
        return Fail(400, "Название чата слишком длинное.");
    if (string.IsNullOrEmpty(ownerId))
        return Fail(400, "Не указан создатель чата.");

    var id = Guid.NewGuid().ToString("N");
    var created = Now();
    using var conn = Open(dbPath);
    using var tx = conn.BeginTransaction();
    using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO rooms (id, title, owner_id, created_utc)
            VALUES ($id, $title, $owner, $created)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$owner", ownerId);
        cmd.Parameters.AddWithValue("$created", created);
        cmd.ExecuteNonQuery();
    }
    using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO members (room_id, user_id, joined_utc)
            VALUES ($room, $user, $created)
            """;
        cmd.Parameters.AddWithValue("$room", id);
        cmd.Parameters.AddWithValue("$user", ownerId);
        cmd.Parameters.AddWithValue("$created", created);
        cmd.ExecuteNonQuery();
    }
    tx.Commit();

    return Results.Json(new { id, title, ownerId, createdUtc = created }, json, statusCode: 201);
});

app.MapPost("/api/rooms/{id}/join", async (HttpContext ctx, string id) =>
{
    var body = await Read<JoinBody>(ctx.Request, json);
    var userId = (body?.UserId ?? "").Trim();
    if (string.IsNullOrEmpty(userId))
        return Fail(400, "Не указан участник.");
    if (!RoomExists(dbPath, id))
        return Fail(404, "Чат не найден.");
    if (IsMember(dbPath, id, userId))
        return Results.Json(new { roomId = id, userId, alreadyMember = true }, json);

    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO members (room_id, user_id, joined_utc) VALUES ($room, $user, $created)
        """;
    cmd.Parameters.AddWithValue("$room", id);
    cmd.Parameters.AddWithValue("$user", userId);
    cmd.Parameters.AddWithValue("$created", Now());
    cmd.ExecuteNonQuery();
    return Results.Json(new { roomId = id, userId, alreadyMember = false }, json, statusCode: 201);
});

app.MapGet("/api/rooms", (string? userId) =>
{
    userId = (userId ?? "").Trim();
    if (string.IsNullOrEmpty(userId))
        return Fail(400, "Не указан участник.");

    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT r.id, r.title, r.owner_id, r.created_utc,
               (SELECT COUNT(*) FROM members mm WHERE mm.room_id = r.id)
        FROM rooms r
        INNER JOIN members m ON m.room_id = r.id
        WHERE m.user_id = $user
        ORDER BY r.created_utc, r.id
        """;
    cmd.Parameters.AddWithValue("$user", userId);
    using var reader = cmd.ExecuteReader();
    var rooms = new List<object>();
    while (reader.Read())
    {
        rooms.Add(new
        {
            id = reader.GetString(0),
            title = reader.GetString(1),
            ownerId = reader.GetString(2),
            createdUtc = reader.GetString(3),
            memberCount = reader.GetInt32(4)
        });
    }
    return Results.Json(rooms, json);
});

app.MapGet("/api/rooms/{id}", (string id) =>
{
    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, title, owner_id, created_utc FROM rooms WHERE id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read())
        return Fail(404, "Чат не найден.");
    var room = new
    {
        id = reader.GetString(0),
        title = reader.GetString(1),
        ownerId = reader.GetString(2),
        createdUtc = reader.GetString(3)
    };
    reader.Close();

    using var membersCmd = conn.CreateCommand();
    membersCmd.CommandText = """
        SELECT user_id, joined_utc FROM members WHERE room_id = $id ORDER BY joined_utc, user_id
        """;
    membersCmd.Parameters.AddWithValue("$id", id);
    using var membersReader = membersCmd.ExecuteReader();
    var members = new List<object>();
    while (membersReader.Read())
    {
        members.Add(new { userId = membersReader.GetString(0), joinedUtc = membersReader.GetString(1) });
    }
    return Results.Json(new
    {
        room.id,
        room.title,
        room.ownerId,
        room.createdUtc,
        members
    }, json);
});

app.MapGet("/api/rooms/{id}/members/{userId}", (string id, string userId) =>
{
    if (!RoomExists(dbPath, id))
        return Fail(404, "Чат не найден.");
    if (!IsMember(dbPath, id, userId))
        return Results.Json(new { member = false, error = "Вы не участник этого чата." }, json, statusCode: 403);
    return Results.Json(new { member = true, roomId = id, userId }, json);
});

Console.WriteLine("Room слушает http://127.0.0.1:5092");
Console.WriteLine("База: data/rooms/rooms.db");
app.Run();

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
        CREATE TABLE IF NOT EXISTS rooms (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            owner_id TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS members (
            room_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            joined_utc TEXT NOT NULL,
            PRIMARY KEY (room_id, user_id)
        );
        CREATE INDEX IF NOT EXISTS idx_members_user ON members(user_id);
        """;
    cmd.ExecuteNonQuery();
}

static SqliteConnection Open(string dbPath)
{
    var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
    cmd.ExecuteNonQuery();
    return conn;
}

static bool RoomExists(string dbPath, string id)
{
    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM rooms WHERE id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    return cmd.ExecuteScalar() is not null;
}

static bool IsMember(string dbPath, string roomId, string userId)
{
    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM members WHERE room_id = $room AND user_id = $user";
    cmd.Parameters.AddWithValue("$room", roomId);
    cmd.Parameters.AddWithValue("$user", userId);
    return cmd.ExecuteScalar() is not null;
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

sealed record CreateRoomBody(string? Title, string? OwnerId);
sealed record JoinBody(string? UserId);
