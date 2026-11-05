using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Platform.Storage;

namespace StreamVODTracker;

internal sealed record GoogleOAuthSession(string AccessToken, string RefreshToken, string Email, int ExpiresIn);

internal static class GoogleDriveOAuthClient
{
    private const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

    public static async Task<GoogleDriveCredentials> ConnectAsync(string clientId, string? clientSecret, string? loginHint, ILauncher launcher)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Enter the Google OAuth desktop client ID.");
        var port = GetAvailablePort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        var state = CreateRandomToken(32);
        var verifier = CreateRandomToken(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(), ["redirect_uri"] = redirectUri, ["response_type"] = "code",
            ["scope"] = $"openid email {DriveFileScope}", ["access_type"] = "offline", ["prompt"] = "consent select_account",
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) parameters["login_hint"] = loginHint.Trim();
        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var callbackTask = listener.GetContextAsync().WaitAsync(timeout.Token);
        if (!await launcher.LaunchUriAsync(new Uri(authUrl))) throw new InvalidOperationException("Could not open the browser for Google sign-in.");
        var context = await callbackTask;
        var query = context.Request.QueryString;
        var html = Encoding.UTF8.GetBytes("<!doctype html><html><meta charset='utf-8'><body>Google sign-in finished. You can close this tab.</body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = html.Length;
        await context.Response.OutputStream.WriteAsync(html);
        context.Response.Close();
        if (!string.IsNullOrWhiteSpace(query["error"])) throw new InvalidOperationException($"Google sign-in failed: {query["error"]}");
        if (!string.Equals(query["state"], state, StringComparison.Ordinal)) throw new SecurityException("Google sign-in state did not match.");
        var code = query["code"];
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Google did not return an authorization code.");

        using var http = new HttpClient();
        var tokenParameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(), ["code"] = code, ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code", ["redirect_uri"] = redirectUri
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) tokenParameters["client_secret"] = clientSecret.Trim();
        using var tokenResponse = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(tokenParameters));
        using var tokenDocument = await ReadResponseAsync(tokenResponse, "Google did not issue an OAuth token.");
        var root = tokenDocument.RootElement;
        var access = GetString(root, "access_token");
        var refresh = GetString(root, "refresh_token");
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
            throw new InvalidOperationException("Google did not return a refresh token. Reconnect and grant access again.");
        using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        userRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        using var userResponse = await http.SendAsync(userRequest);
        using var userDocument = await ReadResponseAsync(userResponse, "Could not read the Google account email.");
        var email = GetString(userDocument.RootElement, "email");
        if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("Google did not return an account email.");
        return new GoogleDriveCredentials(email, refresh);
    }

    public static async Task<GoogleOAuthSession> RefreshAsync(string clientId, string? clientSecret, GoogleDriveCredentials credentials, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient();
        var tokenParameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(), ["refresh_token"] = credentials.RefreshToken, ["grant_type"] = "refresh_token"
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) tokenParameters["client_secret"] = clientSecret.Trim();
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(tokenParameters), cancellationToken);
        using var document = await ReadResponseAsync(response, "Google Drive access token could not be renewed.");
        var root = document.RootElement;
        var access = GetString(root, "access_token");
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidOperationException("Google did not return a Drive access token.");
        var refresh = GetString(root, "refresh_token");
        var nextCredentials = refresh is null ? credentials : credentials with { RefreshToken = refresh };
        if (OperatingSystem.IsWindows()) WindowsCredentialVault.SaveGoogleDrive(nextCredentials);
        var expiry = int.TryParse(GetString(root, "expires_in"), out var seconds) ? seconds : 3600;
        return new GoogleOAuthSession(access, nextCredentials.RefreshToken, nextCredentials.Email, expiry);
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateRandomToken(int byteCount) => Base64Url(RandomNumberGenerator.GetBytes(byteCount));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, string fallback)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var message = fallback;
            try
            {
                using var errorDocument = JsonDocument.Parse(body);
                if (errorDocument.RootElement.TryGetProperty("error_description", out var description)) message = description.GetString() ?? message;
                else if (errorDocument.RootElement.TryGetProperty("error", out var error)) message += $" ({error.GetString()})";
            }
            catch (JsonException) { }
            throw new InvalidOperationException(message);
        }
        return JsonDocument.Parse(body);
    }
}
