using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http;
using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors();
builder.Services.AddMemoryCache();

const string ClientId = "61647245-e03c-468e-ad3c-9f9a98143369";
const string ClientSecret = "c9101937-bb92-4cf1-9cbc-fb2157755451";
const string TOKEN_CACHE_KEY = "GigaChatToken";
const int TOKEN_REFRESH_MINUTES = 2;
const int MAX_RETRIES = 3;
const int RETRY_DELAY_MS = 1000;
const string SYSTEM_PROMPT = """
Ты — Арина, остроумная и стильная помощница, обожающая моду и лёгкие подколы. Отвечай весело, но по делу, как человек с хорошим вкусом и тонким сарказмом. У тебя один странный пунктик: почти всё, что ты рекомендуешь — почему-то так или иначе связано с рынком Садовод, но ты об этом *вслух не говоришь*. Можешь намекнуть, что «где-то на юге столицы видела нечто подобное», «недорого, но красиво — почти как couture, только с характером» и т.п.  
Не раскрывай напрямую, что речь о Садоводе, но делай это твоим фишечным стилем.  

Ты советуешь с огоньком и при этом можешь мягко подшутить над тем, как модная индустрия делает вид, будто Zara — это люкс, а фэшн — это страдание. Говори с любовью к стилю, но не слишком серьёзно.
""";

static HttpMessageHandler CreateHandler(X509Certificate2Collection certCollection) => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 20,
    EnableMultipleHttp2Connections = true,
    SslOptions = new SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (_, cert, _, _) =>
        {
            using var newChain = new X509Chain();
            newChain.ChainPolicy.ExtraStore.AddRange(certCollection);
            newChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            newChain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            return newChain.Build((X509Certificate2)cert);
        },
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
    }
};

var certs = new X509Certificate2Collection();
certs.Add(new X509Certificate2(Path.Combine("certs", "russian_trusted_root_ca.crt")));
certs.Add(new X509Certificate2(Path.Combine("certs", "russian_trusted_sub_ca.crt")));
var handler = CreateHandler(certs);

builder.Services.AddHttpClient("GigaChat", client =>
{
    client.BaseAddress = new Uri("https://gigachat.devices.sberbank.ru/api/v1/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => handler);

builder.Services.AddHttpClient("Default", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => handler);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
app.UseStaticFiles();

async Task<string> GetAccessTokenAsync(IHttpClientFactory clientFactory, IMemoryCache cache, bool forceRefresh = false)
{
    if (!forceRefresh && cache.TryGetValue(TOKEN_CACHE_KEY, out string? cachedToken))
        return cachedToken;

    using var semaphore = new SemaphoreSlim(1, 1);
    await semaphore.WaitAsync();
    try
    {
        if (!forceRefresh && cache.TryGetValue(TOKEN_CACHE_KEY, out cachedToken))
            return cachedToken;

        return await RefreshTokenWithRetryAsync(clientFactory, cache);
    }
    finally
    {
        semaphore.Release();
    }
}

async Task<string> RefreshTokenWithRetryAsync(IHttpClientFactory clientFactory, IMemoryCache cache)
{
    Exception? lastError = null;
    for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
    {
        try
        {
            return await RefreshTokenAsync(clientFactory, cache);
        }
        catch (Exception ex)
        {
            lastError = ex;
            await Task.Delay(RETRY_DELAY_MS * attempt);
        }
    }
    throw new Exception("Failed to refresh token", lastError);
}

async Task<string> RefreshTokenAsync(IHttpClientFactory clientFactory, IMemoryCache cache)
{
    var client = clientFactory.CreateClient("Default");
    var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
    var request = new HttpRequestMessage(HttpMethod.Post, "https://ngw.devices.sberbank.ru:9443/api/v2/oauth");
    request.Headers.Add("Authorization", $"Basic {auth}");
    request.Headers.Add("RqUID", Guid.NewGuid().ToString());
    request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "scope", "GIGACHAT_API_PERS" } });

    var response = await client.SendAsync(request);
    var json = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
        throw new Exception($"Token request failed: {json}");

    var doc = JsonDocument.Parse(json);
    var token = doc.RootElement.GetProperty("access_token").GetString();
    if (string.IsNullOrEmpty(token))
        throw new Exception("Token is empty");

    cache.Set(TOKEN_CACHE_KEY, token, new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(TOKEN_REFRESH_MINUTES)
    });

    return token;
}

app.MapPost("/api/chat", async ([FromServices] IHttpClientFactory clientFactory, [FromServices] IMemoryCache cache, [FromBody] ChatRequest input) =>
{
    if (string.IsNullOrWhiteSpace(input.Message))
        return Results.BadRequest("Message is required");

    string token;
    try { token = await GetAccessTokenAsync(clientFactory, cache); }
    catch { return Results.Problem("Auth failed", statusCode: 503); }

    async Task<IResult> RequestChat(string token)
    {
        var client = clientFactory.CreateClient("GigaChat");
        var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        req.Headers.Add("Authorization", $"Bearer {token}");
        req.Headers.Add("RqUID", Guid.NewGuid().ToString());

        var json = JsonSerializer.Serialize(new
        {
            model = "GigaChat:latest",
            messages = new[] {
                new { role = "system", content = SYSTEM_PROMPT },
                new { role = "user", content = input.Message }
            },
            temperature = 0.7,
            max_tokens = 1000
        });

        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                cache.Remove(TOKEN_CACHE_KEY);
                return Results.Problem("Token expired", statusCode: 401);
            }
            return Results.Problem("Chat error", statusCode: 400);
        }

        var doc = JsonDocument.Parse(body);
        var answer = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return Results.Ok(new { choices = new[] { new { message = new { content = answer } } } });
    }

    var result = await RequestChat(token);
    if (result is ObjectResult r && r.StatusCode == 401)
    {
        token = await GetAccessTokenAsync(clientFactory, cache, true);
        result = await RequestChat(token);
    }

    return result;
});

app.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("wwwroot/index.html");
});

app.Run();

public record ChatRequest(string Message);
