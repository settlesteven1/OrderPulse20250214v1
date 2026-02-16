using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace OrderPulse.Web.Services;

/// <summary>
/// Attaches the MSAL access token to outgoing API requests.
/// </summary>
public class ApiAuthorizationMessageHandler : AuthorizationMessageHandler
{
    public ApiAuthorizationMessageHandler(
        IAccessTokenProvider provider,
        NavigationManager navigation,
        IConfiguration configuration)
        : base(provider, navigation)
    {
        var apiBase = configuration["ApiBaseUrl"] ?? navigation.BaseUri;
        ConfigureHandler(authorizedUrls: new[] { apiBase });
    }
}
