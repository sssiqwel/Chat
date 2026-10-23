using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls("http://127.0.0.1:5090");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var app = builder.Build();
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
const string Account = "http://127.0.0.1:5091";
const string Room = "http://127.0.0.1:5092";
const string Message = "http://127.0.0.1:5093";

app.UseExceptionHandler(handler =>
{
    handler.Run(async ctx =>
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("""{"error":"Внутренняя ошибка."}""");
    });
});

app.MapGet("/health", () => Results.Json(new { status = "ok", service = "gateway" }, json));

app.MapGet("/", ctx => WritePage(ctx, "index.html"));
app.MapGet("/room", ctx => WritePage(ctx, "room.html"));

app.MapPost("/api/register", ctx => Proxy(ctx, http, HttpMethod.Post, Account + "/api/register", "учётных записей"));
app.MapPost("/api/login", ctx => Proxy(ctx, http, HttpMethod.Post, Account + "/api/login", "учётных записей"));
app.MapGet("/api/me", ctx => Proxy(ctx, http, HttpMethod.Get, Account + "/api/me", "учётных записей"));
app.MapMethods("/api/me", new[] { "PATCH" }, ctx => Proxy(ctx, http, HttpMethod.Patch, Account + "/api/me", "учётных записей"));
app.MapPost("/api/me/avatar", ctx => Proxy(ctx, http, HttpMethod.Post, Account + "/api/me/avatar", "учётных записей"));
app.MapGet("/api/avatars/{name}", (HttpContext ctx, string name) =>
    Proxy(ctx, http, HttpMethod.Get, Account + "/api/avatars/" + Uri.EscapeDataString(name), "учётных записей"));

app.MapPost("/api/rooms", async (HttpContext ctx) =>
{
    var user = await ResolveUser(ctx, http, json);
    if (user is null)
        return;
    var body = await Read<TitleBody>(ctx.Request, json);
    if (body is null)
    {
        await WriteError(ctx, 400, "Пустое или нечитаемое тело запроса.");
        return;
    }
    var payload = JsonSerializer.Serialize(new { title = body.Title, ownerId = user.UserId }, json);
    await SendJson(ctx, http, HttpMethod.Post, Room + "/api/rooms", payload, "комнат");
});

app.MapPost("/api/rooms/{id}/join", async (HttpContext ctx, string id) =>
{
    var user = await ResolveUser(ctx, http, json);
    if (user is null)
        return;
    var payload = JsonSerializer.Serialize(new { userId = user.UserId }, json);
    await SendJson(ctx, http, HttpMethod.Post, Room + "/api/rooms/" + Uri.EscapeDataString(id) + "/join", payload, "комнат");
});

app.MapGet("/api/rooms", async (HttpContext ctx) =>
{
    var user = await ResolveUser(ctx, http, json);
    if (user is null)
        return;
    await Proxy(ctx, http, HttpMethod.Get, Room + "/api/rooms?userId=" + Uri.EscapeDataString(user.UserId!), "комнат");
});

app.MapGet("/api/rooms/{id}", async (HttpContext ctx, string id) =>
{
    var user = await ResolveUser(ctx, http, json);
    if (user is null)
        return;

    HttpResponseMessage roomResponse;
    try
    {
        roomResponse = await http.GetAsync(Room + "/api/rooms/" + Uri.EscapeDataString(id), ctx.RequestAborted);
    }
    catch (HttpRequestException)
    {
        await WriteError(ctx, 502, "Сервис комнат недоступен.");
        return;
    }
    using (roomResponse)
    {
        var text = await roomResponse.Content.ReadAsStringAsync(ctx.RequestAborted);
        if (!roomResponse.IsSuccessStatusCode)
        {
            await ForwardBytes(ctx, (int)roomResponse.StatusCode, roomResponse.Content.Headers.ContentType?.ToString(), Encoding.UTF8.GetBytes(text));
            return;
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var ids = new List<string>();
        if (root.TryGetProperty("members", out var members))
        {
            foreach (var member in members.EnumerateArray())
            {
                if (member.TryGetProperty("userId", out var userId) && userId.GetString() is string value)
                    ids.Add(value);
            }
        }

        Dictionary<string, (string Name, string? Avatar)> profiles;
        try
        {
            profiles = await Lookup(http, json, ids, ctx.RequestAborted);
        }
        catch (HttpRequestException)
        {
            await WriteError(ctx, 502, "Сервис учётных записей недоступен.");
            return;
        }

        var enrichedMembers = new List<object>();
        if (root.TryGetProperty("members", out var again))
        {
            foreach (var member in again.EnumerateArray())
            {
                var userId = member.GetProperty("userId").GetString() ?? "";
                var joined = member.TryGetProperty("joinedUtc", out var joinedUtc) ? joinedUtc.GetString() : null;
                profiles.TryGetValue(userId, out var profile);
                enrichedMembers.Add(new
                {
                    userId,
                    joinedUtc = joined,
                    displayName = string.IsNullOrEmpty(profile.Name) ? "Неизвестный" : profile.Name,
                    avatarUrl = profile.Avatar
                });
            }
        }

        await ctx.Response.WriteAsJsonAsync(new
        {
            id = root.GetProperty("id").GetString(),
            title = root.GetProperty("title").GetString(),
            ownerId = root.GetProperty("ownerId").GetString(),
            createdUtc = root.GetProperty("createdUtc").GetString(),
            members = enrichedMembers
        }, json, ctx.RequestAborted);
    }
});

app.MapPost("/api/rooms/{id}/messages", (HttpContext ctx, string id) =>
    Proxy(ctx, http, HttpMethod.Post, Message + "/api/rooms/" + Uri.EscapeDataString(id) + "/messages", "сообщений"));
app.MapGet("/api/rooms/{id}/messages", (HttpContext ctx, string id) =>
    Proxy(ctx, http, HttpMethod.Get, Message + "/api/rooms/" + Uri.EscapeDataString(id) + "/messages", "сообщений"));

app.MapPost("/internal/session", async (HttpContext ctx) =>
{
    if (!IsLoopback(ctx))
    {
        await WriteError(ctx, 403, "Закрыто.");
        return;
    }
    await Proxy(ctx, http, HttpMethod.Post, Account + "/api/sessions/resolve", "учётных записей");
});

app.MapPost("/internal/membership", async (HttpContext ctx) =>
{
    if (!IsLoopback(ctx))
    {
        await WriteError(ctx, 403, "Закрыто.");
        return;
    }
    var body = await Read<MembershipBody>(ctx.Request, json);
    if (body is null || string.IsNullOrWhiteSpace(body.RoomId) || string.IsNullOrWhiteSpace(body.UserId))
    {
        await WriteError(ctx, 400, "Пустое или нечитаемое тело запроса.");
        return;
    }
    var url = Room + "/api/rooms/" + Uri.EscapeDataString(body.RoomId) + "/members/" + Uri.EscapeDataString(body.UserId);
    await Proxy(ctx, http, HttpMethod.Get, url, "комнат");
});

app.MapPost("/internal/profiles", async (HttpContext ctx) =>
{
    if (!IsLoopback(ctx))
    {
        await WriteError(ctx, 403, "Закрыто.");
        return;
    }
    var body = await Read<IdsBody>(ctx.Request, json);
    if (body is null)
    {
        await WriteError(ctx, 400, "Пустое или нечитаемое тело запроса.");
        return;
    }
    Dictionary<string, (string Name, string? Avatar)> profiles;
    try
    {
        profiles = await Lookup(http, json, body.UserIds ?? [], ctx.RequestAborted);
    }
    catch (HttpRequestException)
    {
        await WriteError(ctx, 502, "Сервис учётных записей недоступен.");
        return;
    }
    var users = (body.UserIds ?? [])
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id.Trim())
        .Distinct(StringComparer.Ordinal)
        .Select(id =>
        {
            profiles.TryGetValue(id, out var profile);
            return new
            {
                id,
                displayName = string.IsNullOrEmpty(profile.Name) ? "Неизвестный" : profile.Name,
                avatarUrl = profile.Avatar
            };
        });
    await ctx.Response.WriteAsJsonAsync(new { users }, json, ctx.RequestAborted);
});

app.MapFallback(async ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/internal"))
    {
        await WriteError(ctx, 404, "Маршрут не найден.");
        return;
    }
    ctx.Response.StatusCode = 404;
    ctx.Response.ContentType = "text/plain; charset=utf-8";
    await ctx.Response.WriteAsync("Страница не найдена.");
});

Console.WriteLine("Шлюз слушает http://127.0.0.1:5090");
Console.WriteLine("Своей базы нет.");
app.Run();

static async Task WritePage(HttpContext ctx, string fileName)
{
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-store";
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var name = asm.GetManifestResourceNames().First(item => item.EndsWith(fileName, StringComparison.Ordinal));
    await using var stream = asm.GetManifestResourceStream(name)!;
    await stream.CopyToAsync(ctx.Response.Body);
}

static async Task<ResolvedUser?> ResolveUser(HttpContext ctx, HttpClient http, JsonSerializerOptions json)
{
    var token = ReadBearer(ctx.Request);
    if (string.IsNullOrEmpty(token))
    {
        await WriteError(ctx, 401, "Нужно войти.");
        return null;
    }
    HttpResponseMessage response;
    try
    {
        response = await http.PostAsJsonAsync(
            "http://127.0.0.1:5091/api/sessions/resolve",
            new { token },
            json,
            ctx.RequestAborted);
    }
    catch (HttpRequestException)
    {
        await WriteError(ctx, 502, "Сервис учётных записей недоступен.");
        return null;
    }
    using (response)
    {
        var text = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            await WriteError(ctx, 401, "Сеанс не найден.");
            return null;
        }
        var body = JsonSerializer.Deserialize<ResolvedUser>(text, json);
        if (body is null || string.IsNullOrEmpty(body.UserId))
        {
            await WriteError(ctx, 401, "Сеанс не найден.");
            return null;
        }
        return body;
    }
}

static async Task<Dictionary<string, (string Name, string? Avatar)>> Lookup(
    HttpClient http, JsonSerializerOptions json, IEnumerable<string> ids, CancellationToken ct)
{
    var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    var map = new Dictionary<string, (string Name, string? Avatar)>(StringComparer.Ordinal);
    if (list.Length == 0)
        return map;
    using var response = await http.PostAsJsonAsync("http://127.0.0.1:5091/api/users/lookup", new { userIds = list }, json, ct);
    response.EnsureSuccessStatusCode();
    var text = await response.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(text);
    if (!doc.RootElement.TryGetProperty("users", out var users))
        return map;
    foreach (var user in users.EnumerateArray())
    {
        var id = user.GetProperty("id").GetString() ?? "";
        var name = user.TryGetProperty("displayName", out var display) ? display.GetString() ?? "" : "";
        string? avatar = null;
        if (user.TryGetProperty("avatarFile", out var file) && file.ValueKind == JsonValueKind.String)
        {
            var stored = file.GetString();
            if (!string.IsNullOrEmpty(stored))
                avatar = "/api/avatars/" + stored;
        }
        if (!string.IsNullOrEmpty(id))
            map[id] = (name, avatar);
    }
    return map;
}

static async Task Proxy(HttpContext ctx, HttpClient http, HttpMethod method, string url, string serviceName)
{
    try
    {
        using var request = new HttpRequestMessage(method, url);
        CopyBearer(ctx.Request, request);
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            var stream = new StreamContent(ctx.Request.Body);
            if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                stream.Headers.ContentType = MediaTypeHeaderValue.Parse(ctx.Request.ContentType);
            if (ctx.Request.ContentLength is long length)
                stream.Headers.ContentLength = length;
            request.Content = stream;
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        var bytes = await response.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
        var type = response.Content.Headers.ContentType?.ToString();
        await ForwardBytes(ctx, (int)response.StatusCode, type, bytes);
    }
    catch (HttpRequestException)
    {
        await WriteError(ctx, 502, "Сервис " + serviceName + " недоступен.");
    }
    catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        await WriteError(ctx, 502, "Сервис " + serviceName + " недоступен.");
    }
}

static async Task SendJson(HttpContext ctx, HttpClient http, HttpMethod method, string url, string payload, string serviceName)
{
    try
    {
        using var request = new HttpRequestMessage(method, url);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ctx.RequestAborted);
        var text = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
        await ForwardBytes(ctx, (int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), Encoding.UTF8.GetBytes(text));
    }
    catch (HttpRequestException)
    {
        await WriteError(ctx, 502, "Сервис " + serviceName + " недоступен.");
    }
    catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        await WriteError(ctx, 502, "Сервис " + serviceName + " недоступен.");
    }
}

static async Task ForwardBytes(HttpContext ctx, int status, string? contentType, byte[] bytes)
{
    if (status >= 400 && bytes.Length == 0)
    {
        await WriteError(ctx, status, "Пустой ответ сервиса.");
        return;
    }
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = string.IsNullOrEmpty(contentType) ? "application/json; charset=utf-8" : contentType;
    if (bytes.Length > 0)
        await ctx.Response.Body.WriteAsync(bytes.AsMemory());
}

static async Task WriteError(HttpContext ctx, int status, string error)
{
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await ctx.Response.WriteAsync("{\"error\":" + JsonSerializer.Serialize(error, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "}");
}

static void CopyBearer(HttpRequest from, HttpRequestMessage to)
{
    if (from.Headers.TryGetValue("Authorization", out var value))
        to.Headers.TryAddWithoutValidation("Authorization", value.ToString());
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

static bool IsLoopback(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress;
    if (ip is null)
        return true;
    if (ip.IsIPv4MappedToIPv6)
        ip = ip.MapToIPv4();
    return IPAddress.IsLoopback(ip);
}

sealed record ResolvedUser(string? UserId, string? DisplayName, string? Email);
sealed record TitleBody(string? Title);
sealed record MembershipBody(string? RoomId, string? UserId);
sealed record IdsBody(string[]? UserIds);
