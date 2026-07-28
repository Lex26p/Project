using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace Dispatcher.BrowserTests;

public sealed class BrowserScenario :
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions
        SerializerOptions =
            new(JsonSerializerDefaults.Web);
    private static readonly Guid AccountId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000001");
    private static readonly Guid SessionId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000002");
    private static readonly Guid SubjectId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000003");
    private static readonly Guid PersonId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000004");
    private static readonly Guid ScopeId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000005");
    private static readonly Guid HomeItemId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000006");
    private static readonly Guid TerminalId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000007");
    private static readonly Guid DeviceIdentityId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000008");
    private static readonly Guid ProfileId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000009");
    private static readonly Guid DashboardId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000010");
    private static readonly Guid RevisionId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000011");
    private static readonly Guid WindowId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000012");
    private static readonly Guid WidgetId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000013");
    private const string AccessToken =
        "browser-access-token";
    private const string RefreshToken =
        "browser-refresh-token";
    private readonly IBrowserContext context;
    private bool sessionActive;
    private bool sessionExpired;

    private BrowserScenario(
        IBrowserContext context,
        IPage page,
        Uri serverAddress)
    {
        this.context = context;
        Page = page;
        ServerAddress = serverAddress;
    }

    public IPage Page { get; }

    public Uri ServerAddress { get; }

    public static async Task<BrowserScenario>
        CreateAsync(
            IBrowser browser,
            Uri serverAddress)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(
            serverAddress);
        var context =
            await browser.NewContextAsync(
                    new BrowserNewContextOptions
                    {
                        ViewportSize =
                            new ViewportSize
                            {
                                Width = 1440,
                                Height = 900,
                            },
                    })
                .ConfigureAwait(false);
        var page =
            await context.NewPageAsync()
                .ConfigureAwait(false);
        page.SetDefaultTimeout(10_000);
        var scenario =
            new BrowserScenario(
                context,
                page,
                serverAddress);
        await context.RouteAsync(
                "**/api/**",
                scenario.HandleApiAsync)
            .ConfigureAwait(false);
        return scenario;
    }

    public string Url(string relativePath) =>
        new Uri(
            ServerAddress,
            relativePath.TrimStart('/'))
        .ToString();

    public void ExpireSession()
    {
        sessionExpired = true;
    }

    public async ValueTask DisposeAsync()
    {
        await context.CloseAsync()
            .ConfigureAwait(false);
    }

    private async Task HandleApiAsync(
        IRoute route)
    {
        var request = route.Request;
        var uri = new Uri(request.Url);
        var path = uri.AbsolutePath;

        if (string.Equals(
                path,
                "/api/auth/login",
                StringComparison.OrdinalIgnoreCase))
        {
            sessionActive = true;
            sessionExpired = false;
            await JsonAsync(
                    route,
                    SessionPayload())
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/auth/bootstrap",
                StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(
                    route,
                    BootstrapPayload())
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/auth/refresh",
                StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(
                    route,
                    SessionPayload())
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/auth/revoke",
                StringComparison.OrdinalIgnoreCase))
        {
            sessionActive = false;
            sessionExpired = false;
            await route.FulfillAsync(
                    new RouteFulfillOptions
                    {
                        Status = 204,
                    })
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/workspace/navigation",
                StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(
                    route,
                    new[]
                    {
                        new
                        {
                            label = "Home",
                            route = "/home",
                        },
                        new
                        {
                            label = "Current",
                            route = "/current",
                        },
                    })
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/workspace/access",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAuthorized(request))
            {
                await StatusAsync(
                        route,
                        401)
                    .ConfigureAwait(false);
                return;
            }

            var routeValue =
                ReadQueryParameter(
                    uri.Query,
                    "route");
            await StatusAsync(
                    route,
                    string.Equals(
                        routeValue,
                        "/history",
                        StringComparison.OrdinalIgnoreCase)
                            ? 403
                            : 204)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/workspace/home",
                StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(
                    route,
                    HomePayload())
                .ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(
                "/api/workspace/recent/",
                StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedStatusAsync(
                    route,
                    204)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/terminal/runtime/sync",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    KioskPayload())
                .ConfigureAwait(false);
            return;
        }

        await StatusAsync(
                route,
                404)
            .ConfigureAwait(false);
    }

    private bool IsAuthorized(
        IRequest request)
    {
        if (!sessionActive ||
            sessionExpired)
        {
            return false;
        }

        var authorization =
            request.Headers
                .FirstOrDefault(
                    pair =>
                        string.Equals(
                            pair.Key,
                            "authorization",
                            StringComparison
                                .OrdinalIgnoreCase))
                .Value;
        return string.Equals(
            authorization,
            $"Dispatcher-Session {AccessToken}",
            StringComparison.Ordinal);
    }

    private async Task AuthorizedJsonAsync(
        IRoute route,
        object payload)
    {
        if (!IsAuthorized(route.Request))
        {
            await StatusAsync(
                    route,
                    401)
                .ConfigureAwait(false);
            return;
        }

        await JsonAsync(
                route,
                payload)
            .ConfigureAwait(false);
    }

    private async Task AuthorizedStatusAsync(
        IRoute route,
        int status)
    {
        await StatusAsync(
                route,
                IsAuthorized(route.Request)
                    ? status
                    : 401)
            .ConfigureAwait(false);
    }

    private static Task JsonAsync(
        IRoute route,
        object payload) =>
        route.FulfillAsync(
            new RouteFulfillOptions
            {
                Status = 200,
                ContentType =
                    "application/json; charset=utf-8",
                Body = JsonSerializer.Serialize(
                    payload,
                    SerializerOptions),
            });

    private static Task StatusAsync(
        IRoute route,
        int status) =>
        route.FulfillAsync(
            new RouteFulfillOptions
            {
                Status = status,
                ContentType =
                    "application/problem+json; charset=utf-8",
                Body = status >= 400
                    ? JsonSerializer.Serialize(
                        new
                        {
                            status,
                            title =
                                status == 401
                                    ? "session.expired"
                                    : status == 403
                                        ? "permission.denied"
                                        : "not_found",
                        },
                        SerializerOptions)
                    : string.Empty,
            });

    private static object SessionPayload()
    {
        var now =
            DateTimeOffset.UtcNow;
        return new
        {
            accountId = AccountId,
            sessionId = SessionId,
            accessToken = AccessToken,
            refreshToken = RefreshToken,
            expiresAt =
                now.AddMinutes(30),
            refreshExpiresAt =
                now.AddHours(2),
        };
    }

    private static object BootstrapPayload() =>
        new
        {
            accountId = AccountId,
            sessionId = SessionId,
            subjectId = SubjectId,
            expiresAt =
                DateTimeOffset.UtcNow
                    .AddMinutes(30),
            allowedScopeIds =
                new[]
                {
                    ScopeId,
                },
            defaultScopeId =
                ScopeId,
            permissions =
                new[]
                {
                    "workspace.home.read",
                    "runtime.current.read",
                },
        };

    private static object HomePayload() =>
        new
        {
            profile =
                new
                {
                    userId = AccountId,
                    personId = PersonId,
                    displayName =
                        "Browser Operator",
                    title =
                        "Control room",
                    availability =
                        "Available",
                    visibility =
                        "Organization",
                },
            preferences =
                new
                {
                    compactNavigation =
                        false,
                    startRoute =
                        "/home",
                },
            items =
                new[]
                {
                    new
                    {
                        itemId =
                            HomeItemId,
                        label =
                            "Restricted history",
                        route =
                            "/history",
                        order = 10,
                    },
                },
            favorites =
                Array.Empty<object>(),
            recent =
                Array.Empty<object>(),
        };

    private static object KioskPayload() =>
        new
        {
            terminalId =
                TerminalId,
            deviceIdentityId =
                DeviceIdentityId,
            profileId =
                ProfileId,
            profileVersion =
                1UL,
            experience =
                "Dashboard",
            offlineMode =
                "ReadOnlyLastSynchronized",
            employeeReauthenticationRequired =
                false,
            dashboard =
                new
                {
                    dashboardId =
                        DashboardId,
                    revisionId =
                        RevisionId,
                    revisionNumber =
                        1UL,
                    name =
                        "Plant overview",
                    description =
                        "Browser kiosk fixture",
                    windows =
                        new[]
                            {
                                new
                                {
                                    windowId =
                                        WindowId,
                                    title =
                                        "Overview",
                                    widgets =
                                        new[]
                                            {
                                                new
                                                {
                                                    widgetId =
                                                        WidgetId,
                                                    kind =
                                                        "Value",
                                                    title =
                                                        "Production",
                                                    bindingIds =
                                                        Array.Empty<Guid>(),
                                                },
                                            },
                                    bindings =
                                        Array.Empty<object>(),
                                },
                            },
                    dependencies =
                        Array.Empty<object>(),
                    publishedAt =
                        DateTimeOffset.UtcNow,
                },
            synchronizedAt =
                DateTimeOffset.UtcNow,
        };

    private static string? ReadQueryParameter(
        string query,
        string name)
    {
        foreach (var pair in query
                     .TrimStart('?')
                     .Split(
                         '&',
                         StringSplitOptions
                             .RemoveEmptyEntries))
        {
            var parts =
                pair.Split('=', 2);
            if (parts.Length == 2 &&
                string.Equals(
                    Uri.UnescapeDataString(
                        parts[0]),
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(
                    parts[1]
                        .Replace(
                            '+',
                            ' '));
            }
        }

        return null;
    }
}
