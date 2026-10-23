using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

[CollectionDefinition("chat")]
public sealed class ChatCollection : ICollectionFixture<ServicesFixture>;

[Collection("chat")]
public sealed class ChatFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ServicesFixture _fx;
    private readonly HttpClient _http;

    public ChatFlowTests(ServicesFixture fx)
    {
        _fx = fx;
        _http = fx.Client;
    }

    [Fact]
    public async Task RegisterAndLogin_StoresHashAndRandomToken()
    {
        var email = UniqueEmail("anna");
        const string password = "секрет-анны";
        var created = await Send(HttpMethod.Post, "/api/register", null, new
        {
            email,
            password,
            displayName = "Анна"
        });
        Assert.Equal(201, created.Status);
        Assert.False(string.IsNullOrWhiteSpace(created.Text));

        var duplicate = await Send(HttpMethod.Post, "/api/register", null, new
        {
            email,
            password,
            displayName = "Анна"
        });
        Assert.Equal(409, duplicate.Status);
        Assert.Contains("уже зарегистрирована", duplicate.Text);

        var wrong = await Send(HttpMethod.Post, "/api/login", null, new { email, password = "не-тот-пароль" });
        Assert.Equal(401, wrong.Status);
        Assert.Contains("Неверная почта", wrong.Text);

        var first = await Login(email, password);
        var second = await Login(email, password);
        Assert.NotEqual(first.Token, second.Token);
        Assert.True(first.Token.Length >= 32);

        var row = ReadRow(_fx.AccountsDb, "SELECT password_hash, password_salt, display_name FROM users WHERE email = $email",
            ("$email", email));
        Assert.NotEqual(password, row["password_hash"]);
        Assert.DoesNotContain(password, row["password_hash"]);
        Assert.False(string.IsNullOrWhiteSpace(row["password_salt"]));
        Assert.Equal("Анна", row["display_name"]);

        var me = await Send(HttpMethod.Get, "/api/me", first.Token, null);
        Assert.Equal(200, me.Status);
        Assert.Equal("Анна", me.Json.GetProperty("displayName").GetString());
        Assert.Equal(email, me.Json.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Avatar_IsStoredOnlyByAccount()
    {
        var email = UniqueEmail("face");
        await Send(HttpMethod.Post, "/api/register", null, new { email, password = "пароль-лица", displayName = "Лицо" });
        var login = await Login(email, "пароль-лица");
        var png = TinyPng();

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "portrait.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/avatar") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(200, (int)response.StatusCode);
        using var uploaded = JsonDocument.Parse(text);
        var avatarUrl = uploaded.RootElement.GetProperty("avatarUrl").GetString();
        Assert.StartsWith("/api/avatars/", avatarUrl);

        using var fetched = await _http.GetAsync(avatarUrl);
        Assert.Equal(200, (int)fetched.StatusCode);
        var fetchedBytes = await fetched.Content.ReadAsByteArrayAsync();
        Assert.Equal(png, fetchedBytes);

        var avatarDir = Path.Combine(_fx.Root, "data", "accounts", "avatars");
        var stored = Directory.GetFiles(avatarDir);
        Assert.Contains(stored, path => File.ReadAllBytes(path).AsSpan().SequenceEqual(png));

        var dataRoot = Path.Combine(_fx.Root, "data");
        foreach (var path in Directory.GetFiles(dataRoot, "*", SearchOption.AllDirectories))
        {
            if (path.StartsWith(avatarDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            if (IsDatabaseSideFile(path))
            {
                var bytes = File.ReadAllBytes(path);
                Assert.False(IndexOf(bytes, png) >= 0, path);
                continue;
            }
            Assert.Fail("Лишний файл вне каталога аватаров: " + path);
        }

        Assert.DoesNotContain("avatars", File.ReadAllText(Path.Combine(FindRepo(), "src", "Room", "Program.cs")));
        Assert.DoesNotContain("avatars", File.ReadAllText(Path.Combine(FindRepo(), "src", "Message", "Program.cs")));
    }

    [Fact]
    public async Task OutsiderCannotSend_MemberSeesHistoryWithName()
    {
        var annaEmail = UniqueEmail("anna");
        var borisEmail = UniqueEmail("boris");
        await Send(HttpMethod.Post, "/api/register", null, new { email = annaEmail, password = "секрет-анны", displayName = "Анна" });
        await Send(HttpMethod.Post, "/api/register", null, new { email = borisEmail, password = "секрет-бориса", displayName = "Борис" });
        var anna = await Login(annaEmail, "секрет-анны");
        var boris = await Login(borisEmail, "секрет-бориса");

        var png = TinyPng();
        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "anna.png");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/avatar") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", anna.Token);
            using var uploaded = await _http.SendAsync(request);
            Assert.Equal(200, (int)uploaded.StatusCode);
        }

        var created = await Send(HttpMethod.Post, "/api/rooms", anna.Token, new { title = "Кухня" });
        Assert.Equal(201, created.Status);
        var roomId = created.Json.GetProperty("id").GetString()!;

        var anonymous = await Send(HttpMethod.Post, "/api/rooms/" + roomId + "/messages", null, new { body = "тихо" });
        Assert.Equal(401, anonymous.Status);
        Assert.Contains("войти", anonymous.Text);

        var outsider = await Send(HttpMethod.Post, "/api/rooms/" + roomId + "/messages", boris.Token, new { body = "меня нет в чате" });
        Assert.Equal(403, outsider.Status);
        Assert.Contains("не участник", outsider.Text);
        Assert.Equal(0, Count(_fx.MessagesDb, "SELECT COUNT(*) FROM messages WHERE room_id = $room", ("$room", roomId)));

        var empty = await Send(HttpMethod.Post, "/api/rooms/" + roomId + "/messages", anna.Token, new { body = "   " });
        Assert.Equal(400, empty.Status);
        Assert.Contains("пуст", empty.Text);

        var missing = await Send(HttpMethod.Post, "/api/rooms/" + Guid.NewGuid().ToString("N") + "/join", boris.Token, new { });
        Assert.Equal(404, missing.Status);
        Assert.Contains("не найден", missing.Text);

        var joined = await Send(HttpMethod.Post, "/api/rooms/" + roomId + "/join", boris.Token, new { });
        Assert.Equal(201, joined.Status);

        var sent = await Send(HttpMethod.Post, "/api/rooms/" + roomId + "/messages", boris.Token, new { body = "я в чате" });
        Assert.Equal(201, sent.Status);

        var renamed = await Send(HttpMethod.Patch, "/api/me", anna.Token, new { displayName = "Анна Петрова" });
        Assert.Equal(200, renamed.Status);
        var annaSent = await Send(HttpMethod.Post, "/api/rooms/" + roomId + "/messages", anna.Token, new { body = "и я тоже" });
        Assert.Equal(201, annaSent.Status);

        var history = await Send(HttpMethod.Get, "/api/rooms/" + roomId + "/messages", boris.Token, null);
        Assert.Equal(200, history.Status);
        var items = history.Json.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("Борис", items[0].GetProperty("author").GetProperty("displayName").GetString());
        Assert.Equal("я в чате", items[0].GetProperty("body").GetString());
        Assert.Equal("Анна Петрова", items[1].GetProperty("author").GetProperty("displayName").GetString());
        Assert.Equal("и я тоже", items[1].GetProperty("body").GetString());
        var avatarUrl = items[1].GetProperty("author").GetProperty("avatarUrl").GetString();
        Assert.StartsWith("/api/avatars/", avatarUrl);
        using var avatar = await _http.GetAsync(avatarUrl);
        Assert.Equal(png, await avatar.Content.ReadAsByteArrayAsync());

        var messageColumns = Columns(_fx.MessagesDb, "messages");
        Assert.DoesNotContain("display_name", messageColumns);
        Assert.DoesNotContain("avatar_file", messageColumns);
        var bodies = ColumnValues(_fx.MessagesDb, "SELECT body FROM messages");
        Assert.Contains("я в чате", bodies);
        Assert.DoesNotContain("Анна Петрова", bodies);
        Assert.DoesNotContain("Борис", bodies);
        var roomColumns = Columns(_fx.RoomsDb, "rooms").Concat(Columns(_fx.RoomsDb, "members"));
        Assert.DoesNotContain("display_name", roomColumns);
        Assert.DoesNotContain("Анна Петрова", ColumnValues(_fx.RoomsDb, "SELECT title FROM rooms"));
    }

    [Fact]
    public void Databases_HaveDifferentTablesAndPaths()
    {
        Assert.True(File.Exists(_fx.AccountsDb));
        Assert.True(File.Exists(_fx.RoomsDb));
        Assert.True(File.Exists(_fx.MessagesDb));
        Assert.NotEqual(_fx.AccountsDb, _fx.RoomsDb);
        Assert.NotEqual(_fx.RoomsDb, _fx.MessagesDb);
        Assert.NotEqual(_fx.AccountsDb, _fx.MessagesDb);
        Assert.EndsWith(Path.Combine("data", "accounts", "accounts.db"), _fx.AccountsDb);
        Assert.EndsWith(Path.Combine("data", "rooms", "rooms.db"), _fx.RoomsDb);
        Assert.EndsWith(Path.Combine("data", "messages", "messages.db"), _fx.MessagesDb);

        Assert.Equal(new[] { "sessions", "users" }, Tables(_fx.AccountsDb));
        Assert.Equal(new[] { "members", "rooms" }, Tables(_fx.RoomsDb));
        Assert.Equal(new[] { "messages" }, Tables(_fx.MessagesDb));

        var repo = FindRepo();
        var messageSource = ReadSources(Path.Combine(repo, "src", "Message"));
        Assert.DoesNotContain("5091", messageSource);
        Assert.DoesNotContain("5092", messageSource);
        Assert.Contains("127.0.0.1:5090", messageSource);
        Assert.DoesNotContain("accounts.db", messageSource);
        Assert.DoesNotContain("rooms.db", messageSource);

        var accountSource = ReadSources(Path.Combine(repo, "src", "Account"));
        Assert.Contains("accounts.db", accountSource);
        Assert.Contains("avatars", accountSource);
        Assert.DoesNotContain("rooms.db", accountSource);
        Assert.DoesNotContain("messages.db", accountSource);

        var gatewaySource = ReadSources(Path.Combine(repo, "src", "Gateway"));
        Assert.Contains("127.0.0.1:5091", gatewaySource);
        Assert.Contains("127.0.0.1:5092", gatewaySource);
        Assert.Contains("127.0.0.1:5093", gatewaySource);
        Assert.DoesNotContain("Sqlite", File.ReadAllText(Path.Combine(repo, "src", "Gateway", "Gateway.csproj")));
        Assert.False(File.Exists(Path.Combine(_fx.Root, "data", "gateway", "gateway.db")));
    }

    [Fact]
    public async Task Page_ShowsRussianFormsAndEmptyResponseText()
    {
        using var response = await _http.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Contains("Регистрация", html);
        Assert.Contains("Войти", html);
        Assert.Contains("Загрузить аватар", html);
        Assert.Contains("Создать чат", html);
        Assert.Contains("Вступить", html);
        Assert.Contains("пустой ответ", html);
        Assert.Contains("sessionStorage", html);
        Assert.Contains("window.open", html);

        using var roomPage = await _http.GetAsync("/room");
        var roomHtml = await roomPage.Content.ReadAsStringAsync();
        Assert.Equal(200, (int)roomPage.StatusCode);
        Assert.Contains("Отправить", roomHtml);
        Assert.Contains("История", roomHtml);
        Assert.Contains("пустой ответ", roomHtml);
        Assert.Contains("sessionStorage", roomHtml);

        var empty = await Send(HttpMethod.Post, "/api/register", null, null);
        Assert.True(empty.Status >= 400);
        Assert.False(string.IsNullOrWhiteSpace(empty.Text));
    }

    private async Task<LoginResult> Login(string email, string password)
    {
        var result = await Send(HttpMethod.Post, "/api/login", null, new { email, password });
        Assert.Equal(200, result.Status);
        return new LoginResult(
            result.Json.GetProperty("token").GetString()!,
            result.Json.GetProperty("userId").GetString()!);
    }

    private async Task<ApiResult> Send(HttpMethod method, string url, string? token, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        else if (method == HttpMethod.Post || method == HttpMethod.Patch)
            request.Content = new StringContent("", Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("["))
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
            json = doc.RootElement.Clone();
        }
        return new ApiResult((int)response.StatusCode, text, json);
    }

    private static string UniqueEmail(string name) => name + "-" + Guid.NewGuid().ToString("N") + "@example.com";

    private static string[] Tables(string db)
    {
        using var conn = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static Dictionary<string, string> ReadRow(string db, string sql, params (string Name, string Value)[] parameters)
    {
        using var conn = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetString(i);
        return row;
    }

    private static string[] Columns(string db, string table)
    {
        using var conn = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('" + table + "') ORDER BY cid";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static string[] ColumnValues(string db, string sql)
    {
        using var conn = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        return values.ToArray();
    }

    private static long Count(string db, string sql, params (string Name, string Value)[] parameters)
    {
        using var conn = new SqliteConnection("Data Source=" + db + ";Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static string ReadSources(string directory) =>
        string.Join("\n", Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText));

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Не найден корень проекта.");
    }

    private static bool IsDatabaseSideFile(string path) =>
        path.EndsWith(".db", StringComparison.Ordinal) ||
        path.EndsWith(".db-wal", StringComparison.Ordinal) ||
        path.EndsWith(".db-shm", StringComparison.Ordinal);

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var found = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }
            if (found)
                return i;
        }
        return -1;
    }

    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAF/gL+pN0xSQAAAABJRU5ErkJggg==");

    private sealed record LoginResult(string Token, string UserId);
    private sealed record ApiResult(int Status, string Text, JsonElement Json);
}

public sealed class ServicesFixture : IAsyncLifetime
{
    private readonly List<Process> _procs = new();
    private readonly List<string> _logs = new();

    public string Root { get; private set; } = "";
    public HttpClient Client { get; private set; } = new();
    public string AccountsDb => Path.Combine(Root, "data", "accounts", "accounts.db");
    public string RoomsDb => Path.Combine(Root, "data", "rooms", "rooms.db");
    public string MessagesDb => Path.Combine(Root, "data", "messages", "messages.db");

    public async Task InitializeAsync()
    {
        foreach (var port in new[] { 5090, 5091, 5092, 5093 })
        {
            if (!PortFree(port))
                throw new InvalidOperationException("Порт " + port + " уже занят.");
        }

        Root = Directory.CreateTempSubdirectory("chat-services-").FullName;
        var repo = FindRepo();
        foreach (var name in new[] { "Account", "Room", "Message", "Gateway" })
        {
            var project = Path.Combine(repo, "src", name, name + ".csproj");
            var build = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { "build", project, "-c", "Debug", "--nologo" },
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Не удалось запустить сборку.");
            var output = await build.StandardOutput.ReadToEndAsync();
            var error = await build.StandardError.ReadToEndAsync();
            await build.WaitForExitAsync();
            if (build.ExitCode != 0)
                throw new InvalidOperationException(output + error);
        }

        Start(repo, "Account", 5091);
        Start(repo, "Room", 5092);
        Start(repo, "Message", 5093);
        Start(repo, "Gateway", 5090);
        Client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5090"), Timeout = TimeSpan.FromSeconds(15) };
        await WaitUntilHealthy();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        foreach (var proc in _procs)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
        foreach (var proc in _procs)
        {
            try
            {
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
        catch
        {
        }
    }

    private void Start(string repo, string name, int port)
    {
        var dll = Directory.GetFiles(Path.Combine(repo, "src", name, "bin"), name + ".dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        proc.StartInfo.ArgumentList.Add("exec");
        proc.StartInfo.ArgumentList.Add(dll);
        proc.StartInfo.Environment["CHAT_ROOT"] = Root;
        proc.StartInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:" + port;
        proc.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        proc.OutputDataReceived += (_, eventArgs) => Remember(name + " " + eventArgs.Data);
        proc.ErrorDataReceived += (_, eventArgs) => Remember(name + " " + eventArgs.Data);
        if (!proc.Start())
            throw new InvalidOperationException("Не стартовал " + name);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _procs.Add(proc);
    }

    private async Task WaitUntilHealthy()
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < until)
        {
            if (_procs.Any(proc => proc.HasExited))
                throw new InvalidOperationException("Процесс завершился раньше времени.\n" + string.Join("\n", _logs));
            try
            {
                var ready = true;
                foreach (var port in new[] { 5091, 5092, 5093, 5090 })
                {
                    using var response = await probe.GetAsync("http://127.0.0.1:" + port + "/health");
                    if (!response.IsSuccessStatusCode)
                        ready = false;
                }
                if (ready)
                    return;
            }
            catch
            {
            }
            await Task.Delay(200);
        }
        throw new InvalidOperationException("Сервисы не поднялись.\n" + string.Join("\n", _logs));
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (_logs)
        {
            _logs.Add(line);
            if (_logs.Count > 200)
                _logs.RemoveAt(0);
        }
    }

    private static bool PortFree(int port)
    {
        try
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Не найден корень проекта.");
    }
}
