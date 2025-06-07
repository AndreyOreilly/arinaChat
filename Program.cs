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

// Создаем HttpClientHandler с сертификатами
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
    {
        // Создаем новую цепочку сертификатов
        var newChain = new X509Chain();
        newChain.ChainPolicy.ExtraStore.AddRange(certCollection);
        newChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        newChain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

        // Проверяем сертификат
        bool isValid = newChain.Build((X509Certificate2)cert);
        newChain.Dispose();

        Console.WriteLine($"Certificate validation result: {isValid}");
        return isValid;
    },
    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    CheckCertificateRevocationList = false
};

// Add HttpClient for GigaChat API
builder.Services.AddHttpClient("GigaChat", client =>
{
    client.BaseAddress = new Uri("https://gigachat.devices.sberbank.ru/api/v1/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).ConfigurePrimaryHttpMessageHandler(() => handler);

// Add default HttpClient
builder.Services.AddHttpClient("Default").ConfigurePrimaryHttpMessageHandler(() => handler);

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

// Функция для получения токена с учетом кэширования
async Task<string> GetAccessTokenAsync(IHttpClientFactory clientFactory, IMemoryCache cache)
{
    // Проверяем кэш
    if (cache.TryGetValue(TOKEN_CACHE_KEY, out string? cachedToken))
    {
        return cachedToken;
    }

    // Получаем новый токен
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
        Console.WriteLine("Sending token request...");
        var response = await client.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();
        
        Console.WriteLine($"Token Response: {responseContent}");

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to get access token: {responseContent}");
        }

        var tokenData = JsonSerializer.Deserialize<JsonDocument>(responseContent);
        var accessToken = tokenData?.RootElement.GetProperty("access_token").GetString();
        var expiresAt = tokenData?.RootElement.GetProperty("expires_at").GetInt64();

        if (string.IsNullOrEmpty(accessToken))
        {
            throw new Exception("Invalid token response");
        }

        // Кэшируем токен на 25 минут
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(25));
        
        cache.Set(TOKEN_CACHE_KEY, accessToken, cacheOptions);

        return accessToken;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error getting token: {ex}");
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
        var accessToken = await GetAccessTokenAsync(clientFactory, cache);

        // Делаем запрос к чату
        var gigaChatClient = clientFactory.CreateClient("GigaChat");
        var completionRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        
        // Правильная установка заголовков
        completionRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
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

        var completionResponse = await gigaChatClient.SendAsync(completionRequest);
        var completionContent = await completionResponse.Content.ReadAsStringAsync();
        
        Console.WriteLine($"Completion Response: {completionContent}");

        if (!completionResponse.IsSuccessStatusCode)
        {
            // Если токен истек, удаляем его из кэша и пробуем еще раз
            if (completionResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                cache.Remove(TOKEN_CACHE_KEY);
                return Results.BadRequest("Token expired, please try again");
            }
            return Results.BadRequest($"Chat API error: {completionContent}");
        }

        // Преобразуем ответ в нужный формат
        var responseData = JsonSerializer.Deserialize<JsonDocument>(completionContent);
        var messageContent = responseData?.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrEmpty(messageContent))
        {
            return Results.BadRequest("Empty response from chat API");
        }

        var response = new
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
        };

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex}");
        return Results.BadRequest($"Error processing request: {ex.Message}");
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