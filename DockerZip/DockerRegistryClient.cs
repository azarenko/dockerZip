using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DockerZip;

/// <summary>
/// Speaks Docker Registry HTTP API v2.
/// Handles Bearer token auth (Docker Hub, GCR, etc.) and HTTP Basic auth.
/// </summary>
public sealed class DockerRegistryClient : IDisposable
{
    private readonly string _registryBase;   // e.g. "https://registry-1.docker.io"
    private readonly string? _username;
    private readonly string? _password;
    private readonly HttpClient _http;
    private readonly AzureAuthService? _azureAuth;

    // Token cache keyed by scope string
    private readonly Dictionary<string, string> _tokenCache = new(StringComparer.Ordinal);

    private static readonly string[] ManifestAcceptTypes =
    [
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
    ];

    public DockerRegistryClient(
        string registryBase,
        string? username,
        string? password,
        AzureAuthService? azureAuth = null)
    {
        _registryBase = registryBase.TrimEnd('/');
        _username = username;
        _password = password;
        _azureAuth = azureAuth;

        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DockerZip/1.0");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the raw manifest JSON and its content-type media type.</summary>
    public async Task<(string Json, string MediaType)> GetManifestAsync(
        string image, string reference, CancellationToken ct)
    {
        var url = $"{_registryBase}/v2/{image}/manifests/{reference}";
        using var resp = await SendAsync(url, ManifestAcceptTypes, image, "pull", ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        return (json, mediaType);
    }

    /// <summary>Streams a blob directly to <paramref name="destination"/>.</summary>
    public async Task DownloadBlobAsync(
        string image,
        string digest,
        Stream destination,
        IProgress<(long Done, long Total)>? progress,
        CancellationToken ct)
    {
        var url = $"{_registryBase}/v2/{image}/blobs/{digest}";
        using var resp = await SendAsync(url, [], image, "pull", ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);

        var buf = new byte[65536];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await destination.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            progress?.Report((done, total));
        }
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        string url,
        string[] acceptTypes,
        string image,
        string actionScope,
        CancellationToken ct)
    {
        var cacheKey = $"{image}:{actionScope}";
        _tokenCache.TryGetValue(cacheKey, out var token);

        HttpResponseMessage resp = await ExecuteRequest(url, acceptTypes, token, ct);

        if (resp.StatusCode != HttpStatusCode.Unauthorized)
            return resp;

        // Parse WWW-Authenticate
        var wwwAuth = resp.Headers.WwwAuthenticate.FirstOrDefault()?.ToString();
        resp.Dispose();

        if (string.IsNullOrEmpty(wwwAuth))
            throw new InvalidOperationException("Registry returned 401 without WWW-Authenticate header.");

        token = await AcquireTokenAsync(wwwAuth, ct);
        if (token != null)
            _tokenCache[cacheKey] = token;

        return await ExecuteRequest(url, acceptTypes, token, ct);
    }

    private async Task<HttpResponseMessage> ExecuteRequest(
        string url, string[] acceptTypes, string? token, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);

        foreach (var mt in acceptTypes)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mt));

        if (token != null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else if (!string.IsNullOrEmpty(_username))
        {
            var cred = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", cred);
        }

        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<string?> AcquireTokenAsync(string wwwAuthenticate, CancellationToken ct)
    {
        // Parse: Bearer realm="https://...",service="...",scope="..."
        var attrs = ParseChallenge(wwwAuthenticate);
        if (!attrs.TryGetValue("realm", out var realm)) return null;

        attrs.TryGetValue("service", out var service);
        attrs.TryGetValue("scope", out var scope);

        // ── Azure Container Registry path ─────────────────────────────────────
        // ACR realms look like: https://myregistry.azurecr.io/oauth2/token
        if (_azureAuth != null && IsAcrRealm(realm))
        {
            if (!_azureAuth.IsLoggedIn)
                throw new InvalidOperationException(
                    "Azure SSO is enabled but you are not signed in. " +
                    "Click 'Sign in with Azure' in the Authentication panel first.");

            // service = "myregistry.azurecr.io"
            var registryHost = !string.IsNullOrEmpty(service)
                ? service
                : new Uri(realm).Host;

            return await _azureAuth.GetAcrAccessTokenAsync(
                registryHost,
                scope ?? "registry:catalog:*",
                ct);
        }

        // ── Standard Bearer token flow (Docker Hub, GCR, etc.) ───────────────
        var qb = new List<string>();
        if (service != null) qb.Add($"service={Uri.EscapeDataString(service)}");
        if (scope != null) qb.Add($"scope={Uri.EscapeDataString(scope)}");

        var tokenUrl = qb.Count > 0 ? $"{realm}?{string.Join("&", qb)}" : realm;

        var req = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
        if (!string.IsNullOrEmpty(_username))
        {
            var cred = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", cred);
        }

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("token", out var t)) return t.GetString();
        if (doc.RootElement.TryGetProperty("access_token", out var at)) return at.GetString();
        return null;
    }

    // ACR registry hosts end with one of these suffixes
    private static bool IsAcrRealm(string realm) =>
        realm.Contains(".azurecr.io",  StringComparison.OrdinalIgnoreCase) ||
        realm.Contains(".azurecr.cn",  StringComparison.OrdinalIgnoreCase) ||
        realm.Contains(".azurecr.us",  StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseChallenge(string header)
    {
        // Strip scheme prefix ("Bearer ")
        var idx = header.IndexOf(' ');
        if (idx >= 0) header = header[(idx + 1)..];

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Simple key="value" parser
        int pos = 0;
        while (pos < header.Length)
        {
            // Skip whitespace / commas
            while (pos < header.Length && (header[pos] == ',' || header[pos] == ' ')) pos++;
            if (pos >= header.Length) break;

            int eqPos = header.IndexOf('=', pos);
            if (eqPos < 0) break;

            var key = header[pos..eqPos].Trim();
            pos = eqPos + 1;

            string value;
            if (pos < header.Length && header[pos] == '"')
            {
                pos++; // skip opening "
                int end = header.IndexOf('"', pos);
                value = end >= 0 ? header[pos..end] : header[pos..];
                pos = end >= 0 ? end + 1 : header.Length;
            }
            else
            {
                int commaPos = header.IndexOf(',', pos);
                value = commaPos >= 0 ? header[pos..commaPos] : header[pos..];
                pos = commaPos >= 0 ? commaPos : header.Length;
            }

            result[key] = value;
        }

        return result;
    }

    public void Dispose() => _http.Dispose();
}
