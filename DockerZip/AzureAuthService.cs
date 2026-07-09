using Microsoft.Identity.Client;
using System.Net.Http;
using System.Text.Json;

namespace DockerZip;

/// <summary>
/// Manages Azure AD (Microsoft Entra ID) interactive login via MSAL and
/// performs the two-step token exchange required by Azure Container Registry (ACR).
///
/// Token exchange flow:
///   1. Interactive browser login  →  Azure AD access token
///   2. POST /oauth2/exchange      →  ACR refresh token
///   3. POST /oauth2/token         →  ACR scoped access token  (used as Bearer in registry API calls)
/// </summary>
public sealed class AzureAuthService : IDisposable
{
    // ── Constants ──────────────────────────────────────────────────────────────
    // Well-known Microsoft Azure CLI public client ID; registered by Microsoft
    // for native / desktop interactive auth flows — no client secret needed.
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    // Scope that grants the management.azure.com audience — required for ACR exchange.
    private static readonly string[] AzureManagementScopes =
        ["https://management.azure.com/.default"];

    // ── State ──────────────────────────────────────────────────────────────────
    private readonly IPublicClientApplication _pca;
    private readonly HttpClient _http = new();
    private AuthenticationResult? _authResult;

    // ── Properties ────────────────────────────────────────────────────────────
    /// <summary>Returns true when we hold a non-expired Azure AD token.</summary>
    public bool IsLoggedIn =>
        _authResult is not null &&
        _authResult.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2);

    public string? SignedInUser    => _authResult?.Account?.Username;
    public string? SignedInTenant  => _authResult?.TenantId;

    // ── Construction ──────────────────────────────────────────────────────────
    /// <param name="clientId">
    ///   Azure AD App Registration client ID.
    ///   Leave as default to use the Azure CLI public client (no registration needed).
    ///   For your own app: https://learn.microsoft.com/entra/identity-platform/quickstart-register-app
    /// </param>
    /// <param name="tenantId">
    ///   Use "organizations" for work/school accounts (required for ACR),
    ///   or a specific tenant GUID to restrict to a single directory.
    /// </param>
    public AzureAuthService(
        string clientId = AzureCliClientId,
        string tenantId = "organizations")
    {
        _pca = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithDefaultRedirectUri()   // http://localhost — works for browser-based interactive flow
            .Build();
    }

    // ── Authentication ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts a silent token refresh from the MSAL in-memory cache.
    /// Returns <c>true</c> if a valid token was found without user interaction.
    /// </summary>
    public async Task<bool> TrySilentRefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var accounts = await _pca.GetAccountsAsync();
            _authResult = await _pca
                .AcquireTokenSilent(AzureManagementScopes, accounts.FirstOrDefault())
                .ExecuteAsync(ct);
            return true;
        }
        catch (MsalException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a browser window for interactive Microsoft login.
    /// </summary>
    /// <param name="parentWindowHandle">Handle of the owner window (for proper centering).</param>
    public async Task LoginInteractiveAsync(IntPtr parentWindowHandle, CancellationToken ct = default)
    {
        _authResult = await _pca
            .AcquireTokenInteractive(AzureManagementScopes)
            .WithParentActivityOrWindow(parentWindowHandle)
            .ExecuteAsync(ct);
    }

    /// <summary>Clears the token cache and signs the user out.</summary>
    public async Task LogoutAsync()
    {
        foreach (var account in (await _pca.GetAccountsAsync()).ToList())
            await _pca.RemoveAsync(account);
        _authResult = null;
    }

    // ── ACR Token Exchange ────────────────────────────────────────────────────

    /// <summary>
    /// Exchanges the cached Azure AD access token for an ACR-scoped Bearer token
    /// that can be used directly in Docker Registry API calls.
    /// </summary>
    /// <param name="registryHost">e.g. <c>myregistry.azurecr.io</c></param>
    /// <param name="scope">Registry scope from the 401 challenge, e.g. <c>repository:myimage:pull</c></param>
    public async Task<string> GetAcrAccessTokenAsync(
        string registryHost,
        string scope,
        CancellationToken ct = default)
    {
        if (!IsLoggedIn)
            throw new InvalidOperationException(
                "Not signed in to Azure. Click 'Sign in with Azure' before downloading.");

        var aadToken = _authResult!.AccessToken;
        var tenantId = _authResult!.TenantId;

        // ── Step 1: AAD access token  →  ACR refresh token ───────────────────
        var exchangeResp = await _http.PostAsync(
            $"https://{registryHost}/oauth2/exchange",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]   = "access_token",
                ["service"]      = registryHost,
                ["tenant"]       = tenantId,
                ["access_token"] = aadToken,
            }),
            ct);

        if (!exchangeResp.IsSuccessStatusCode)
        {
            var body = await exchangeResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"ACR token exchange failed ({(int)exchangeResp.StatusCode}): {body}");
        }

        var refreshToken = ParseTokenField(
            await exchangeResp.Content.ReadAsStringAsync(ct), "refresh_token");

        // ── Step 2: ACR refresh token  →  scoped access token ────────────────
        var tokenResp = await _http.PostAsync(
            $"https://{registryHost}/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["service"]       = registryHost,
                ["scope"]         = scope,
                ["refresh_token"] = refreshToken,
            }),
            ct);

        if (!tokenResp.IsSuccessStatusCode)
        {
            var body = await tokenResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"ACR access token request failed ({(int)tokenResp.StatusCode}): {body}");
        }

        return ParseTokenField(await tokenResp.Content.ReadAsStringAsync(ct), "access_token");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ParseTokenField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty(fieldName, out var prop) && prop.GetString() is string val)
            return val;
        throw new InvalidDataException($"Token response missing '{fieldName}' field.");
    }

    public void Dispose() => _http.Dispose();
}
