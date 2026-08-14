using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveCorporationDashboard.Services;

public class AuthResult
{
    public string AccessToken = "";
    public string RefreshToken = "";
    public DateTime ExpiresAtUtc;
    public long CharacterId;
    public string CharacterName = "";
}

/// <summary>
/// EVE SSO OAuth2 for native apps: PKCE, system browser, and a loopback TCP listener
/// for the callback (no admin rights or URL ACLs needed).
/// </summary>
public class EsiAuthService
{
    private const string AuthorizeUrl = "https://login.eveonline.com/v2/oauth/authorize/";
    private const string TokenUrl = "https://login.eveonline.com/v2/oauth/token";

    private static readonly HttpClient Http = new();

    /// <summary>
    /// Tries each candidate redirect URI in order, using the first whose loopback port is
    /// free. All candidates must be registered on the EVE app so this degrades gracefully
    /// when a user already has something bound to the primary port.
    /// </summary>
    public async Task<AuthResult> LoginAsync(string clientId, IReadOnlyList<string> redirectUris, string scopes,
        CancellationToken ct)
    {
        for (int i = 0; i < redirectUris.Count; i++)
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, new Uri(redirectUris[i]).Port);
                listener.Start();
            }
            catch (SocketException) when (i < redirectUris.Count - 1)
            {
                continue; // port already in use - fall back to the next registered redirect URI
            }

            try
            {
                return await LoginWithListenerAsync(clientId, redirectUris[i], scopes, listener, ct);
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("No loopback port was available for the EVE SSO callback.");
    }

    private static async Task<AuthResult> LoginWithListenerAsync(string clientId, string redirectUri, string scopes,
        TcpListener listener, CancellationToken ct)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        // The redirect URI is sent verbatim - it must byte-for-byte match the EVE app registration.
        string callbackPath = new Uri(redirectUri).AbsolutePath.TrimEnd('/');

        string url = AuthorizeUrl +
            "?response_type=code" +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&client_id=" + Uri.EscapeDataString(clientId) +
            "&scope=" + Uri.EscapeDataString(scopes) +
            "&code_challenge=" + challenge +
            "&code_challenge_method=S256" +
            "&state=" + state;

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        string code = await WaitForCodeAsync(listener, state, callbackPath, ct);
        return await ExchangeAsync(clientId, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        });
    }

    public Task<AuthResult> RefreshAsync(string clientId, string refreshToken) =>
        ExchangeAsync(clientId, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        });

    private static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState,
        string callbackPath, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            string? requestLine = await reader.ReadLineAsync(ct);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { /* drain headers */ }
            if (requestLine == null) continue;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) continue;
            var uri = new Uri("http://localhost" + parts[1]);

            if (!uri.AbsolutePath.TrimEnd('/').Equals(callbackPath, StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync(stream, "404 Not Found", "Not found.");
                continue;
            }

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string? code = query["code"];
            string? gotState = query["state"];
            string? error = query["error"];

            if (error != null)
            {
                await RespondAsync(stream, "200 OK", $"Login failed: {WebUtility.HtmlEncode(error)}. You can close this window.");
                throw new InvalidOperationException($"EVE SSO returned an error: {error}");
            }
            if (code == null || gotState != expectedState)
            {
                await RespondAsync(stream, "200 OK", "Invalid callback. You can close this window and retry.");
                continue;
            }

            await RespondAsync(stream, "200 OK",
                "<h2>Login successful</h2><p>You can close this window and return to EVE Member Tracker.</p>");
            return code;
        }
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string bodyHtml)
    {
        string body = $"<html><body style=\"font-family:sans-serif\">{bodyHtml}</body></html>";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    private static async Task<AuthResult> ExchangeAsync(string clientId, Dictionary<string, string> form)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Host = "login.eveonline.com";
        using var resp = await Http.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"EVE SSO token request failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string accessToken = root.GetProperty("access_token").GetString()!;
        var result = new AuthResult
        {
            AccessToken = accessToken,
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 30),
        };
        ParseJwtIdentity(accessToken, result);
        return result;
    }

    private static void ParseJwtIdentity(string jwt, AuthResult result)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return;
        string payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var root = doc.RootElement;
        if (root.TryGetProperty("sub", out var sub))
        {
            // sub is "CHARACTER:EVE:<id>"
            var idPart = sub.GetString()?.Split(':').LastOrDefault();
            if (long.TryParse(idPart, out long id)) result.CharacterId = id;
        }
        if (root.TryGetProperty("name", out var name)) result.CharacterName = name.GetString() ?? "";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
