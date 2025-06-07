using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http;
using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors();
builder.Services.AddMemoryCache();

// Константы для GigaChat API
const string ClientId = "61647245-e03c-468e-ad3c-9f9a98143369";
const string ClientSecret = "c9101937-bb92-4cf1-9cbc-fb2157755451";
const string TOKEN_CACHE_KEY = "GigaChatToken";
const int TOKEN_REFRESH_MINUTES = 20; // Refresh token 5 minutes before expiration
const int TOKEN_CACHE_MINUTES = 25;   // Standard token lifetime
const int MAX_RETRIES = 3;           // Максимальное количество попыток получения токена
const int RETRY_DELAY_MS = 1000;     // Задержка между попытками в миллисекундах

// Загружаем сертификаты
var rootCertPath = Path.Combine(Directory.GetCurrentDirectory(), "certs", "russian_trusted_root_ca.crt");
var subCertPath = Path.Combine(Directory.GetCurrentDirectory(), "certs", "russian_trusted_sub_ca.crt");

X509Certificate2Collection certCollection = new X509Certificate2Collection();
try
{
    certCollection.Add(new X509Certificate2(rootCertPath));
    certCollection.Add(new X509Certificate2(subCertPath));
    Console.WriteLine("Certificates loaded successfully");
}
catch (Exception ex)
{
    Console.WriteLine($"Error loading certificates: {ex.Message}");
}

// Создаем HttpClientHandler с сертификатами и настройками соединения
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
    {
        // Создаем новую цепочку сертификатов
        using var newChain = new X509Chain();
        newChain.ChainPolicy.ExtraStore.AddRange(certCollection);
        newChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        newChain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

        // Проверяем сертификат
        bool isValid = newChain.Build((X509Certificate2)cert);
        Console.WriteLine($"Certificate validation result: {isValid}");
        return isValid;
    },
    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    CheckCertificateRevocationList = false,
    MaxConnectionsPerServer = 20
};

// Add HttpClient for GigaChat API with proper handler lifecycle
builder.Services.AddHttpClient("GigaChat", client =>
{
    client.BaseAddress = new Uri("https://gigachat.devices.sberbank.ru/api/v1/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => handler)
.SetHandlerLifetime(Timeout.InfiniteTimeSpan); // Prevent handler disposal

// Add default HttpClient with proper handler lifecycle
builder.Services.AddHttpClient("Default", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => handler)
.SetHandlerLifetime(Timeout.InfiniteTimeSpan); // Prevent handler disposal

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// Configure CORS
app.UseCors(builder => builder
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

// Serve static files
app.UseStaticFiles();

// Функция для получения токена с учетом кэширования и повторных попыток
async Task<string> GetAccessTokenAsync(IHttpClientFactory clientFactory, IMemoryCache cache, bool forceRefresh = false)
{
    if (!forceRefresh && cache.TryGetValue(TOKEN_CACHE_KEY, out string? cachedToken))
    {
        return cachedToken;
    }

    // Используем семафор для предотвращения одновременного обновления токена
    using var semaphore = new SemaphoreSlim(1, 1);
    await semaphore.WaitAsync();

    try
    {
        // Повторная проверка кэша после получения блокировки
        if (!forceRefresh && cache.TryGetValue(TOKEN_CACHE_KEY, out cachedToken))
        {
            return cachedToken;
        }

        return await RefreshTokenWithRetryAsync(clientFactory, cache);
    }
    finally
    {
        semaphore.Release();
    }
}

// Функция для обновления токена с повторными попытками
async Task<string> RefreshTokenWithRetryAsync(IHttpClientFactory clientFactory, IMemoryCache cache)
{
    Exception? lastException = null;

    for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
    {
        try
        {
            return await RefreshTokenAsync(clientFactory, cache);
        }
        catch (Exception ex)
        {
            lastException = ex;
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Token refresh attempt {attempt} failed: {ex.Message}");

            if (attempt < MAX_RETRIES)
            {
                await Task.Delay(RETRY_DELAY_MS * attempt); // Увеличиваем задержку с каждой попыткой
            }
        }
    }

    throw new Exception($"Failed to refresh token after {MAX_RETRIES} attempts", lastException);
}

// Выделяем логику обновления токена в отдельную функцию
async Task<string> RefreshTokenAsync(IHttpClientFactory clientFactory, IMemoryCache cache)
{
    var client = clientFactory.CreateClient("Default");
    var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
    
    var request = new HttpRequestMessage(HttpMethod.Post, "https://ngw.devices.sberbank.ru:9443/api/v2/oauth");
    request.Headers.Add("Authorization", $"Basic {auth}");
    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Add("RqUID", Guid.NewGuid().ToString());
    
    var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        {"scope", "GIGACHAT_API_PERS"}
    });
    request.Content = content;

    try 
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Requesting new token...");
        var response = await client.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();
        
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Token request failed: {responseContent}");
            throw new Exception($"Failed to get access token: {response.StatusCode} - {responseContent}");
        }

        var tokenData = JsonSerializer.Deserialize<JsonDocument>(responseContent);
        var accessToken = tokenData?.RootElement.GetProperty("access_token").GetString();
        var expiresAt = tokenData?.RootElement.GetProperty("expires_at").GetInt64();

        if (string.IsNullOrEmpty(accessToken))
        {
            throw new Exception("Invalid token response: access_token is missing");
        }

        // Кэшируем токен с проактивным обновлением
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(TOKEN_REFRESH_MINUTES))
            .RegisterPostEvictionCallback((key, value, reason, state) =>
            {
                if (reason == EvictionReason.Expired)
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Token cache expired, triggering refresh");
                    // Асинхронно запускаем обновление токена
                    Task.Run(async () =>
                    {
                        try
                        {
                            await RefreshTokenWithRetryAsync(clientFactory, cache);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Failed to refresh token: {ex.Message}");
                        }
                    });
                }
            });
        
        cache.Set(TOKEN_CACHE_KEY, accessToken, cacheOptions);
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] New token cached successfully");

        return accessToken;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error getting token: {ex}");
        // Удаляем старый токен из кэша при ошибке
        cache.Remove(TOKEN_CACHE_KEY);
        throw;
    }
}

// Chat completion endpoint
app.MapPost("/api/chat", async ([FromServices] IHttpClientFactory clientFactory, [FromServices] IMemoryCache cache, [FromBody] ChatRequest userRequest) =>
{
    try 
    {
        if (string.IsNullOrEmpty(userRequest.Message))
        {
            return Results.BadRequest("Message is required");
        }

        // Получаем токен с автоматическим обновлением
        string accessToken;
        try 
        {
            accessToken = await GetAccessTokenAsync(clientFactory, cache);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Failed to get access token: {ex.Message}");
            return Results.Problem("Authentication service unavailable", statusCode: 503);
        }

        // Делаем запрос к чату с автоматической повторной попыткой при истечении токена
        async Task<IResult> MakeCompletionRequest(string token, HttpClient client)
        {
            var completionRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            
            completionRequest.Headers.Add("Authorization", $"Bearer {token}");
            completionRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            completionRequest.Headers.Add("RqUID", Guid.NewGuid().ToString());

            var payload = new
            {
                model = "GigaChat:latest",
                messages = new[]
                {
                    new { role = "user", content = userRequest.Message }
                },
                temperature = 0.7,
                max_tokens = 1000,
                n = 1,
                stream = false,
                repetition_penalty = 1,
                update_interval = 0
            };

            var jsonContent = JsonSerializer.Serialize(payload);
            completionRequest.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                var completionResponse = await client.SendAsync(completionRequest);
                var completionContent = await completionResponse.Content.ReadAsStringAsync();

                if (!completionResponse.IsSuccessStatusCode)
                {
                    if (completionResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Token unauthorized, removing from cache");
                        cache.Remove(TOKEN_CACHE_KEY);
                        return Results.Problem("Token expired, please retry the request", statusCode: 401);
                    }
                    return Results.Problem($"Chat API error: {completionContent}", statusCode: 400);
                }

                var responseData = JsonSerializer.Deserialize<JsonDocument>(completionContent);
                var messageContent = responseData?.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrEmpty(messageContent))
                {
                    return Results.Problem("Empty response from chat API", statusCode: 400);
                }

                return Results.Ok(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content = messageContent
                            }
                        }
                    }
                });
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Request failed: {ex.Message}");
                return Results.Problem("Service temporarily unavailable", statusCode: 503);
            }
        }

        // Get HttpClient instance
        var gigaChatClient = clientFactory.CreateClient("GigaChat");

        // Первая попытка с текущим токеном
        var result = await MakeCompletionRequest(accessToken, gigaChatClient);
        
        // Если токен истек, пробуем получить новый и повторить запрос
        if (result is ObjectResult objResult && objResult.StatusCode == 401)
        {
            try
            {
                accessToken = await GetAccessTokenAsync(clientFactory, cache, forceRefresh: true);
                result = await MakeCompletionRequest(accessToken, gigaChatClient);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Failed to refresh token and retry request: {ex.Message}");
                return Results.Problem("Failed to refresh authentication", statusCode: 503);
            }
        }

        return result;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex}");
        return Results.Problem($"Error processing request: {ex.Message}", statusCode: 500);
    }
});

// Default route to serve index.html
app.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("wwwroot/index.html");
});

app.Run();

public record ChatRequest(string Message); 