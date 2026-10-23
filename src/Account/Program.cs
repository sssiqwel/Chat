using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls("http://127.0.0.1:5091");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

var app = builder.Build();
var root = DataRoot();
var dbPath = Path.Combine(root, "data", "accounts", "accounts.db");
var avatarDir = Path.Combine(root, "data", "accounts", "avatars");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
Directory.CreateDirectory(avatarDir);
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

app.MapGet("/health", () => Results.Json(new { status = "ok", service = "account" }, json));

app.MapPost("/api/register", async (HttpContext ctx) =>
{
    var body = await Read<RegisterBody>(ctx.Request, json);
    if (body is null)
        return Fail(400, "Пустое или нечитаемое тело запроса.");

    var email = NormalizeEmail(body.Email);
    var name = (body.DisplayName ?? "").Trim();
    var password = body.Password ?? "";
    if (string.IsNullOrEmpty(email))
        return Fail(400, "Укажите почту.");
    if (!IsEmail(email))
        return Fail(400, "Почта выглядит неверно.");
    if (string.IsNullOrEmpty(password))
        return Fail(400, "Укажите пароль.");
    if (password.Length < 4)
        return Fail(400, "Пароль короче 4 символов.");
    if (password.Length > 200)
        return Fail(400, "Пароль слишком длинный.");
    if (string.IsNullOrEmpty(name))
        return Fail(400, "Укажите имя.");
    if (name.Length > 80)
        return Fail(400, "Имя слишком длинное.");

    var (hash, salt) = HashPassword(password);
    var id = NewId();
    try
    {
        using var conn = Open(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (id, email, display_name, password_hash, password_salt, avatar_file, created_utc)
            VALUES ($id, $email, $name, $hash, $salt, NULL, $created)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$email", email);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$salt", salt);
        cmd.Parameters.AddWithValue("$created", Now());
        cmd.ExecuteNonQuery();
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        return Fail(409, "Такая почта уже зарегистрирована.");
    }

    return Results.Json(new { id, email, displayName = name }, json, statusCode: 201);
});

app.MapPost("/api/login", async (HttpContext ctx) =>
{
    var body = await Read<LoginBody>(ctx.Request, json);
    if (body is null)
        return Fail(400, "Пустое или нечитаемое тело запроса.");

    var email = NormalizeEmail(body.Email);
    var password = body.Password ?? "";
    if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        return Fail(400, "Укажите почту и пароль.");

    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT id, display_name, password_hash, password_salt
        FROM users WHERE email = $email
        """;
    cmd.Parameters.AddWithValue("$email", email);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read() || !VerifyPassword(password, reader.GetString(2), reader.GetString(3)))
        return Fail(401, "Неверная почта или пароль.");

    var userId = reader.GetString(0);
    var displayName = reader.GetString(1);
    reader.Close();

    var token = NewToken();
    using var insert = conn.CreateCommand();
    insert.CommandText = """
        INSERT INTO sessions (token, user_id, created_utc) VALUES ($token, $user, $created)
        """;
    insert.Parameters.AddWithValue("$token", token);
    insert.Parameters.AddWithValue("$user", userId);
    insert.Parameters.AddWithValue("$created", Now());
    insert.ExecuteNonQuery();

    return Results.Json(new { token, userId, displayName, email }, json);
});

app.MapPost("/api/sessions/resolve", async (HttpContext ctx) =>
{
    var body = await Read<TokenBody>(ctx.Request, json);
    var token = body?.Token?.Trim();
    if (string.IsNullOrEmpty(token))
        return Fail(401, "Сеанс не найден.");
    var user = FindByToken(dbPath, token);
    if (user is null)
        return Fail(401, "Сеанс не найден.");
    return Results.Json(new { userId = user.Id, displayName = user.DisplayName, email = user.Email }, json);
});

app.MapGet("/api/me", (HttpRequest req) =>
{
    var user = FindByToken(dbPath, ReadBearer(req));
    if (user is null)
        return Fail(401, "Нужно войти.");
    return Results.Json(PublicUser(user), json);
});

app.MapPatch("/api/me", async (HttpContext ctx) =>
{
    var user = FindByToken(dbPath, ReadBearer(ctx.Request));
    if (user is null)
        return Fail(401, "Нужно войти.");
    var body = await Read<ProfileBody>(ctx.Request, json);
    if (body is null)
        return Fail(400, "Пустое или нечитаемое тело запроса.");
    var name = (body.DisplayName ?? "").Trim();
    if (string.IsNullOrEmpty(name))
        return Fail(400, "Укажите имя.");
    if (name.Length > 80)
        return Fail(400, "Имя слишком длинное.");

    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE users SET display_name = $name WHERE id = $id";
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$id", user.Id);
    cmd.ExecuteNonQuery();
    user = user with { DisplayName = name };
    return Results.Json(PublicUser(user), json);
});

app.MapPost("/api/me/avatar", async (HttpContext ctx) =>
{
    var user = FindByToken(dbPath, ReadBearer(ctx.Request));
    if (user is null)
        return Fail(401, "Нужно войти.");
    if (!ctx.Request.HasFormContentType)
        return Fail(400, "Файл аватара не передан.");

    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
    if (file is null || file.Length == 0)
        return Fail(400, "Файл аватара не передан.");
    if (file.Length > 2 * 1024 * 1024)
        return Fail(400, "Файл аватара больше 2 МБ.");

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"))
        return Fail(400, "Нужен файл png, jpg, gif или webp.");

    var stored = user.Id + ext;
    var dest = Path.Combine(avatarDir, stored);
    await using (var stream = File.Create(dest))
        await file.CopyToAsync(stream);

    if (!string.IsNullOrEmpty(user.AvatarFile) && user.AvatarFile != stored)
    {
        var previous = Path.Combine(avatarDir, user.AvatarFile);
        if (File.Exists(previous))
            File.Delete(previous);
    }

    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE users SET avatar_file = $file WHERE id = $id";
    cmd.Parameters.AddWithValue("$file", stored);
    cmd.Parameters.AddWithValue("$id", user.Id);
    cmd.ExecuteNonQuery();

    return Results.Json(new { avatarUrl = "/api/avatars/" + stored }, json);
}).DisableAntiforgery();

app.MapGet("/api/avatars/{name}", (string name) =>
{
    var safe = Path.GetFileName(name);
    if (!string.Equals(safe, name, StringComparison.Ordinal))
        return Fail(400, "Некорректное имя файла.");
    var path = Path.GetFullPath(Path.Combine(avatarDir, safe));
    var rootFull = Path.GetFullPath(avatarDir + Path.DirectorySeparatorChar);
    if (!path.StartsWith(rootFull, StringComparison.Ordinal) || !File.Exists(path))
        return Fail(404, "Аватар не найден.");
    return Results.File(path, ContentType(safe));
});

app.MapPost("/api/users/lookup", async (HttpContext ctx) =>
{
    var body = await Read<LookupBody>(ctx.Request, json);
    var ids = (body?.UserIds ?? [])
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id.Trim())
        .Distinct(StringComparer.Ordinal)
        .Take(100)
        .ToArray();
    var users = new List<object>();
    if (ids.Length == 0)
        return Results.Json(new { users }, json);

    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    var names = new string[ids.Length];
    for (var i = 0; i < ids.Length; i++)
    {
        names[i] = "$p" + i;
        cmd.Parameters.AddWithValue(names[i], ids[i]);
    }
    cmd.CommandText = $"""
        SELECT id, display_name, avatar_file
        FROM users
        WHERE id IN ({string.Join(", ", names)})
        """;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        users.Add(new
        {
            id = reader.GetString(0),
            displayName = reader.GetString(1),
            avatarFile = reader.IsDBNull(2) ? null : reader.GetString(2)
        });
    }
    return Results.Json(new { users }, json);
});

Console.WriteLine("Account слушает http://127.0.0.1:5091");
Console.WriteLine("База: data/accounts/accounts.db");
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
        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY,
            email TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            password_hash TEXT NOT NULL,
            password_salt TEXT NOT NULL,
            avatar_file TEXT NULL,
            created_utc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS sessions (
            token TEXT PRIMARY KEY,
            user_id TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );
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

static SessionUser? FindByToken(string dbPath, string? token)
{
    if (string.IsNullOrWhiteSpace(token))
        return null;
    using var conn = Open(dbPath);
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT u.id, u.email, u.display_name, u.avatar_file
        FROM sessions s
        JOIN users u ON u.id = s.user_id
        WHERE s.token = $token
        """;
    cmd.Parameters.AddWithValue("$token", token.Trim());
    using var reader = cmd.ExecuteReader();
    if (!reader.Read())
        return null;
    return new SessionUser(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3));
}

static object PublicUser(SessionUser user) => new
{
    id = user.Id,
    email = user.Email,
    displayName = user.DisplayName,
    avatarUrl = string.IsNullOrEmpty(user.AvatarFile) ? null : "/api/avatars/" + user.AvatarFile
};

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

static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

static bool IsEmail(string email) =>
    email.Length <= 200 && System.Text.RegularExpressions.Regex.IsMatch(
        email, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$");

static (string Hash, string Salt) HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
    return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
}

static bool VerifyPassword(string password, string hashB64, string saltB64)
{
    var salt = Convert.FromBase64String(saltB64);
    var expected = Convert.FromBase64String(hashB64);
    var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
    return CryptographicOperations.FixedTimeEquals(actual, expected);
}

static string NewId() => Guid.NewGuid().ToString("N");

static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

static string Now() => DateTime.UtcNow.ToString("O");

static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
{
    ".jpg" or ".jpeg" => "image/jpeg",
    ".gif" => "image/gif",
    ".webp" => "image/webp",
    _ => "image/png"
};

sealed record SessionUser(string Id, string Email, string DisplayName, string? AvatarFile);
sealed record RegisterBody(string? Email, string? Password, string? DisplayName);
sealed record LoginBody(string? Email, string? Password);
sealed record TokenBody(string? Token);
sealed record ProfileBody(string? DisplayName);
sealed record LookupBody(string[]? UserIds);
