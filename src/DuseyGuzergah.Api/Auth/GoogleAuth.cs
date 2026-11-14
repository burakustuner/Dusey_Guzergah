using System.Collections.Concurrent;
using System.Text.Json;
using DuseyGuzergah.Api.Contracts;
using Microsoft.Extensions.Options;

namespace DuseyGuzergah.Api.Auth;

public sealed class GoogleAuthOptions
{
    public const string SectionName = "GoogleAuth";

    public string ClientId { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

public sealed record GoogleUser(string Subject, string Email, string Name, string Picture);

public sealed class GoogleIdTokenMiddleware
{
    public const string UserItemKey = "GoogleUser";

    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _http;
    private readonly GoogleAuthOptions _options;
    private readonly ConcurrentDictionary<string, CachedUser> _cache = new();

    public GoogleIdTokenMiddleware(RequestDelegate next, IHttpClientFactory http, IOptions<GoogleAuthOptions> options)
    {
        _next = next;
        _http = http;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.IsConfigured || !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals("/api/auth/config", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorized(context, "Giriş gerekli.");
            return;
        }

        var user = await VerifyAsync(header[prefix.Length..].Trim(), context.RequestAborted);
        if (user is null)
        {
            await WriteUnauthorized(context, "Google oturumu geçersiz.");
            return;
        }

        context.Items[UserItemKey] = user;
        await _next(context);
    }

    private async Task<GoogleUser?> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (_cache.TryGetValue(token, out var cached))
        {
            if (cached.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                return cached.User;
            }

            _cache.TryRemove(token, out _);
        }

        var client = _http.CreateClient("google");
        using var response = await client.GetAsync(
            "https://oauth2.googleapis.com/tokeninfo?id_token=" + Uri.EscapeDataString(token),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var info = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var aud = info.TryGetProperty("aud", out var audNode) ? audNode.ToString() : "";
        var verified = info.TryGetProperty("email_verified", out var verifiedNode) ? verifiedNode.ToString() : "";
        if (!string.Equals(aud, _options.ClientId, StringComparison.Ordinal)
            || !string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(info.TryGetProperty("exp", out var expNode) ? expNode.ToString() : "", out var expSeconds))
        {
            return null;
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
        if (expires <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var user = new GoogleUser(
            Text(info, "sub"),
            Text(info, "email"),
            Text(info, "name"),
            Text(info, "picture"));
        _cache[token] = new CachedUser(user, expires);
        return user;
    }

    private static string Text(JsonElement info, string name)
        => info.TryGetProperty(name, out var node) ? node.ToString() : "";

    private static async Task WriteUnauthorized(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiMessage(message));
    }

    private sealed record CachedUser(GoogleUser User, DateTimeOffset ExpiresUtc);
}
