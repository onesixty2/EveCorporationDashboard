namespace EveCorporationDashboard.Services;

/// <summary>
/// The app's own EVE SSO registration (developers.eveonline.com), shared by every install.
///
/// This is a "public client" PKCE flow (see EsiAuthService) - there is no client secret
/// involved anywhere, so the Client ID below is safe to ship in source/binaries. Never
/// pair it with the app registration's Secret Key; that key must stay out of this repo.
///
/// End users no longer register their own EVE application or configure a callback URL -
/// they just click "Log in with EVE". If you fork this project, register your own
/// application at https://developers.eveonline.com/applications ("Authentication & API
/// Access") with the scopes below and the redirect URIs below (trailing slashes included),
/// then replace ClientId here.
/// </summary>
public static class EsiConfig
{
    public const string ClientId = "261eaefcd69c4701b17ab338316a5cbd";

    public const string Scopes =
        "esi-corporations.track_members.v1 esi-universe.read_structures.v1 esi-industry.read_corporation_mining.v1 " +
        "esi-assets.read_corporation_assets.v1 esi-corporations.read_structures.v1 " +
        "esi-corporations.read_starbases.v1";

    /// <summary>Must exactly match the callback URL registered on the EVE application.</summary>
    public static readonly IReadOnlyList<string> RedirectUris = new[]
    {
        "http://localhost:8635/callback/",
    };
}
