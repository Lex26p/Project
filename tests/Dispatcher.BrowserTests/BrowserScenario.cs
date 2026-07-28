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
    private static readonly Guid EventPointId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000014");
    private static readonly Guid EventOccurrenceId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000015");
    private static readonly Guid NewEventOccurrenceId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000016");
    private static readonly Guid HistorySourceId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000017");
    private const string AccessToken =
        "browser-access-token";
    private const string RefreshToken =
        "browser-refresh-token";
    private readonly IBrowserContext context;
    private bool sessionActive;
    private bool sessionExpired;
    private bool c09Enabled;
    private bool eventGap;
    private bool additionalOccurrence;
    private ulong occurrenceCursor = 1;
    private ulong projectionVersion = 1;
    private string conditionState = "Active";
    private string acknowledgementState =
        "Unacknowledged";
    private Guid? assignedTo;
    private DateTimeOffset? shelvedUntil;

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

    public static Guid PrimaryOccurrenceId =>
        EventOccurrenceId;

    public static Guid HistoryPointId =>
        EventPointId;

    public static Guid HistoryStreamSourceId =>
        HistorySourceId;

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

    public void EnableC09()
    {
        c09Enabled = true;
    }

    public void RaiseNewOccurrence()
    {
        additionalOccurrence = true;
        occurrenceCursor =
            checked(occurrenceCursor + 1);
    }

    public void ReturnPrimaryToNormal()
    {
        conditionState = "Normal";
        projectionVersion =
            checked(projectionVersion + 1);
        occurrenceCursor =
            checked(occurrenceCursor + 1);
    }

    public void CauseEventGap()
    {
        eventGap = true;
    }

    public string HistoryUrl() =>
        Url(
            $"/history?scopeId={ScopeId:D}&sourceId={HistorySourceId:D}&pointId={EventPointId:D}");

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
                    NavigationPayload())
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
                        StringComparison.OrdinalIgnoreCase) &&
                    !c09Enabled
                            ? 403
                            : 204)
                .ConfigureAwait(false);
            return;
        }

        if (c09Enabled &&
            (path.StartsWith(
                 "/api/events/",
                 StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(
                 "/api/alarms/",
                 StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(
                 "/api/history/",
                 StringComparison.OrdinalIgnoreCase)))
        {
            await HandleC09Async(
                    route,
                    request,
                    uri)
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

    private async Task HandleC09Async(
        IRoute route,
        IRequest request,
        Uri uri)
    {
        if (!IsAuthorized(request))
        {
            await StatusAsync(route, 401)
                .ConfigureAwait(false);
            return;
        }

        var path = uri.AbsolutePath;
        if (path.EndsWith(
                "/occurrences/snapshot",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    new
                    {
                        cursor =
                            occurrenceCursor,
                        occurrences =
                            OccurrencesPayload(),
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(
                "/occurrences/feed",
                StringComparison.OrdinalIgnoreCase))
        {
            var requested =
                ulong.TryParse(
                    ReadQueryParameter(
                        uri.Query,
                        "cursor"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var cursor)
                        ? cursor
                        : 0;
            if (eventGap)
            {
                eventGap = false;
                await JsonAsync(
                        route,
                        new
                        {
                            kind = "Gap",
                            from = requested,
                            to = requested,
                            changes =
                                Array.Empty<object>(),
                        })
                    .ConfigureAwait(false);
                return;
            }

            await JsonAsync(
                    route,
                    new
                    {
                        kind =
                            requested <
                            occurrenceCursor
                                ? "Delta"
                                : "NoChange",
                        from = requested,
                        to = occurrenceCursor,
                        changes =
                            requested <
                            occurrenceCursor
                                ? OccurrencesPayload()
                                : Array.Empty<object>(),
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.Contains(
                "/api/alarms/",
                StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith(
                    "/acknowledge",
                    StringComparison.OrdinalIgnoreCase))
            {
                acknowledgementState =
                    "Acknowledged";
            }
            else if (path.EndsWith(
                         "/assign",
                         StringComparison.OrdinalIgnoreCase))
            {
                assignedTo = SubjectId;
            }
            else if (path.EndsWith(
                         "/shelve",
                         StringComparison.OrdinalIgnoreCase))
            {
                shelvedUntil =
                    DateTimeOffset.UtcNow
                        .AddMinutes(30);
            }
            else if (path.EndsWith(
                         "/unshelve",
                         StringComparison.OrdinalIgnoreCase))
            {
                shelvedUntil = null;
            }
            else
            {
                await StatusAsync(route, 404)
                    .ConfigureAwait(false);
                return;
            }

            projectionVersion =
                checked(projectionVersion + 1);
            occurrenceCursor =
                checked(occurrenceCursor + 1);
            await JsonAsync(
                    route,
                    new
                    {
                        completion = "Applied",
                        idempotencyKey =
                            "browser-action",
                        occurrenceId =
                            EventOccurrenceId,
                        pointId = EventPointId,
                        priority = "Critical",
                        dashboardBindingKey =
                            $"point:{EventPointId:N}",
                        equipmentHref =
                            $"/equipment?pointId={EventPointId:D}",
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(
                "/counters",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    new
                    {
                        eventCount = 3,
                        activeOccurrenceCount =
                            conditionState ==
                            "Active"
                                ? 1
                                : 0,
                        unacknowledgedOccurrenceCount =
                            acknowledgementState ==
                            "Unacknowledged"
                                ? 1
                                : 0,
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(
                "/api/events/",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    new
                    {
                        events = new[]
                        {
                            new
                            {
                                eventId =
                                    Guid.Parse(
                                        "81000000-0000-0000-0000-000000000018"),
                                position = 1UL,
                                pointId =
                                    EventPointId,
                                occurrenceId =
                                    EventOccurrenceId,
                                sourceConditionVersion =
                                    1UL,
                                priority =
                                    "Critical",
                                kind =
                                    "AlarmRaised",
                                occurredAt =
                                    DateTimeOffset
                                        .UtcNow
                                        .AddMinutes(-10),
                                acceptedAt =
                                    DateTimeOffset
                                        .UtcNow
                                        .AddMinutes(-10),
                            },
                        },
                        upperBound = 1UL,
                        nextAfter =
                            (ulong?)null,
                        nextUpper =
                            (ulong?)null,
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(
                "/aggregate",
                StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.UtcNow;
            await JsonAsync(
                    route,
                    new
                    {
                        policyVersion = 1,
                        resolutionSeconds = 60d,
                        buckets = new[]
                        {
                            new
                            {
                                fromInclusive =
                                    now.AddMinutes(-2),
                                toExclusive =
                                    now.AddMinutes(-1),
                                count = 2L,
                                average = 41.5d,
                                minimum = 40L,
                                maximum = 43L,
                                quality = "Good",
                                freshness = "Fresh",
                                hasGap = false,
                            },
                            new
                            {
                                fromInclusive =
                                    now.AddMinutes(-1),
                                toExclusive = now,
                                count = 1L,
                                average = 39d,
                                minimum = 39L,
                                maximum = 39L,
                                quality = "Bad",
                                freshness = "Stale",
                                hasGap = true,
                            },
                        },
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(
                "/range",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    new
                    {
                        records = new object[]
                        {
                            new
                            {
                                position = 1UL,
                                kind = "sample",
                                value = 42L,
                                unit = "°C",
                                quality = "Good",
                                freshness = "Fresh",
                                sourceTimestamp =
                                    DateTimeOffset.UtcNow
                                        .AddMinutes(-2),
                                isLate = false,
                                isOutOfOrder = false,
                                gapFirstSourcePosition =
                                    (ulong?)null,
                                gapLastSourcePosition =
                                    (ulong?)null,
                                gapReason =
                                    (string?)null,
                            },
                            new
                            {
                                position = 2UL,
                                kind = "gap",
                                value = (long?)null,
                                unit = (string?)null,
                                quality = (string?)null,
                                freshness =
                                    (string?)null,
                                sourceTimestamp =
                                    (DateTimeOffset?)null,
                                isLate = (bool?)null,
                                isOutOfOrder =
                                    (bool?)null,
                                gapFirstSourcePosition =
                                    10UL,
                                gapLastSourcePosition =
                                    12UL,
                                gapReason =
                                    "source unavailable",
                            },
                        },
                        upperBound = 2UL,
                        nextAfter =
                            (ulong?)null,
                        nextUpper =
                            (ulong?)null,
                    })
                .ConfigureAwait(false);
            return;
        }

        await StatusAsync(route, 404)
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

    private object BootstrapPayload() =>
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
            permissions = c09Enabled
                ? new[]
                {
                    "workspace.home.read",
                    "runtime.current.read",
                    "history.range.read",
                    "events.dispatcher.read",
                    "alarm.occurrence.acknowledge",
                    "alarm.occurrence.assign",
                    "alarm.occurrence.shelve",
                }
                : new[]
                {
                    "workspace.home.read",
                    "runtime.current.read",
                },
        };

    private object[] NavigationPayload()
    {
        var items = new List<object>
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
        };
        if (c09Enabled)
        {
            items.Add(
                new
                {
                    label = "Events",
                    route = "/events",
                });
            items.Add(
                new
                {
                    label = "History",
                    route = "/history",
                });
        }

        return items.ToArray();
    }

    private object[] OccurrencesPayload()
    {
        var items = new List<object>
        {
            OccurrencePayload(
                EventOccurrenceId,
                projectionVersion,
                "Critical",
                conditionState,
                acknowledgementState,
                assignedTo,
                shelvedUntil),
        };
        if (additionalOccurrence)
        {
            items.Add(
                OccurrencePayload(
                    NewEventOccurrenceId,
                    projectionVersion: 1,
                    priority: "High",
                    condition: "Active",
                    acknowledgement:
                        "Unacknowledged",
                    assigned: null,
                    shelved: null));
        }

        return items.ToArray();
    }

    private static object OccurrencePayload(
        Guid occurrenceId,
        ulong projectionVersion,
        string priority,
        string condition,
        string acknowledgement,
        Guid? assigned,
        DateTimeOffset? shelved)
    {
        var now = DateTimeOffset.UtcNow;
        return new
        {
            projectionVersion,
            occurrenceId,
            pointId = EventPointId,
            priority,
            openedAt = now.AddMinutes(-10),
            closedAt =
                condition == "Normal"
                    ? now.AddMinutes(-1)
                    : (DateTimeOffset?)null,
            conditionState = condition,
            conditionPendingSince =
                (DateTimeOffset?)null,
            conditionActiveSince =
                now.AddMinutes(-10),
            conditionClearedAt =
                condition == "Normal"
                    ? now.AddMinutes(-1)
                    : (DateTimeOffset?)null,
            conditionVersion =
                projectionVersion,
            acknowledgementState =
                acknowledgement,
            acknowledgedBy =
                acknowledgement ==
                "Acknowledged"
                    ? SubjectId
                    : (Guid?)null,
            acknowledgedAt =
                acknowledgement ==
                "Acknowledged"
                    ? now
                    : (DateTimeOffset?)null,
            acknowledgementVersion =
                acknowledgement ==
                "Acknowledged"
                    ? 2UL
                    : 1UL,
            assignedTo = assigned,
            assignedAt =
                assigned is null
                    ? (DateTimeOffset?)null
                    : now,
            assignmentVersion =
                assigned is null
                    ? 1UL
                    : 2UL,
            shelvedUntil = shelved,
            shelvingReason =
                shelved is null
                    ? null
                    : "Operator review",
            shelvingVersion =
                shelved is null
                    ? 1UL
                    : 2UL,
            isSuppressed = false,
            suppressionReason =
                (string?)null,
            suppressionVersion = 1UL,
            canAcknowledge = true,
            canAssign = true,
            canShelve = true,
        };
    }

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
