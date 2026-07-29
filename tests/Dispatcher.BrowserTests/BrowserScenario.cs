using System.Collections.Concurrent;
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
    private static readonly string[]
        TextTransferFormats =
        [
            "Text",
        ];
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
    private static readonly Guid TrendWidgetId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000018");
    private static readonly Guid EventWidgetId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000019");
    private static readonly Guid CurrentBindingId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000020");
    private static readonly Guid HistoryBindingId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000021");
    private static readonly Guid AlarmBindingId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000022");
    private static readonly Guid MimicBindingId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000023");
    private static readonly Guid MimicId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000024");
    private static readonly Guid MimicRevisionId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000025");
    private static readonly Guid SecondRevisionId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000026");
    private static readonly Guid CommissioningLocationId =
        Guid.Parse(
            "81000000-0000-0000-0000-000000000027");
    private const string AccessToken =
        "browser-access-token";
    private const string RefreshToken =
        "browser-refresh-token";
    private readonly IBrowserContext context;
    private bool sessionActive;
    private bool sessionExpired;
    private bool c09Enabled;
    private bool c10Enabled;
    private bool c14Enabled;
    private bool eventGap;
    private bool additionalOccurrence;
    private ulong occurrenceCursor = 1;
    private ulong projectionVersion = 1;
    private string conditionState = "Active";
    private string acknowledgementState =
        "Unacknowledged";
    private Guid? assignedTo;
    private DateTimeOffset? shelvedUntil;
    private readonly ConcurrentDictionary<
        string,
        ConcurrentQueue<string>>
        hubMessages = [];
    private readonly ConcurrentDictionary<
        string,
        Guid[]> hubPoints = [];
    private long currentValue = 42;
    private ulong runtimeCursor = 1;
    private ulong dashboardRevision = 1;
    private ulong subscriptionRevision = 1;
    private bool kioskOffline;
    private bool kioskRevoked;
    private ulong kioskProfileVersion = 1;
    private long editorVersion;
    private long hubConnectionSequence;
    private readonly List<C14Draft> c14Drafts = [];
    private Guid? c14DiagnosticRowId;
    private Guid c14DiagnosticJobId;
    private int c14DiagnosticStatus = 3;
    private string? c14DiagnosticOutcomeCode = "diagnostic.succeeded";
    private string? c14DiagnosticOutcomeMessage = "Diagnostic completed.";
    private long c14ConfigurationVersion;
    private bool c14ConfigurationValidated;
    private bool c14ConfigurationPublished;

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

    public static Guid RuntimeDashboardId =>
        DashboardId;

    public static Guid RuntimeWindowId =>
        WindowId;

    public static Guid RuntimeMimicId =>
        MimicId;

    public static Guid RuntimeMimicBindingId =>
        MimicBindingId;

    public static Guid CommissioningScopeId =>
        ScopeId;

    public static Guid CommissioningDefaultLocationId =>
        CommissioningLocationId;

    public Guid PublishedDashboardRevisionId =>
        dashboardRevision == 1
            ? RevisionId
            : SecondRevisionId;

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
        await context.RouteAsync(
                "**/hubs/runtime**",
                scenario.HandleRuntimeHubAsync)
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

    public void EnableC10()
    {
        c10Enabled = true;
    }

    public void EnableC14()
    {
        c14Enabled = true;
    }

    public void SetC14DiagnosticOutcome(
        int status,
        string? outcomeCode,
        string? outcomeMessage)
    {
        c14DiagnosticStatus = status;
        c14DiagnosticOutcomeCode = outcomeCode;
        c14DiagnosticOutcomeMessage = outcomeMessage;
    }

    public void UpdateDashboardCurrent(long value)
    {
        currentValue = value;
        runtimeCursor =
            checked(runtimeCursor + 1);
    }

    public void PublishDashboardRevision()
    {
        dashboardRevision =
            checked(dashboardRevision + 1);
    }

    public void SetKioskOffline(bool value)
    {
        kioskOffline = value;
    }

    public void RevokeKiosk()
    {
        kioskRevoked = true;
    }

    public void RestoreKiosk()
    {
        kioskRevoked = false;
        kioskOffline = false;
        kioskProfileVersion =
            checked(kioskProfileVersion + 1);
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

    private async Task HandleRuntimeHubAsync(
        IRoute route)
    {
        var request = route.Request;
        var uri = new Uri(request.Url);
        if (uri.AbsolutePath.EndsWith(
                "/negotiate",
                StringComparison.OrdinalIgnoreCase))
        {
            var token =
                $"browser-hub-{Interlocked.Increment(ref hubConnectionSequence)}";
            hubMessages[token] =
                new ConcurrentQueue<string>();
            await JsonAsync(
                    route,
                    new
                    {
                        negotiateVersion = 1,
                        connectionId = token,
                        connectionToken = token,
                        availableTransports =
                            new[]
                            {
                                new
                                {
                                    transport =
                                        "LongPolling",
                                    transferFormats =
                                        TextTransferFormats,
                                },
                            },
                    })
                .ConfigureAwait(false);
            return;
        }

        var connectionToken =
            ReadQueryParameter(
                uri.Query,
                "id") ??
            "browser-hub-unknown";
        var messages =
            hubMessages.GetOrAdd(
                connectionToken,
                _ =>
                    new ConcurrentQueue<
                        string>());
        if (request.Method == "DELETE")
        {
            await StatusAsync(route, 202)
                .ConfigureAwait(false);
            return;
        }

        if (request.Method == "POST")
        {
            foreach (var frame in
                     (request.PostData ??
                      string.Empty).Split(
                         '\u001e',
                         StringSplitOptions
                             .RemoveEmptyEntries))
            {
                using var document =
                    JsonDocument.Parse(frame);
                var root =
                    document.RootElement;
                if (root.TryGetProperty(
                        "protocol",
                        out _))
                {
                    messages.Enqueue(
                        "{}\u001e");
                    continue;
                }

                if (!root.TryGetProperty(
                        "type",
                        out var type) ||
                    type.GetInt32() != 1)
                {
                    continue;
                }

                var invocationId =
                    root.GetProperty(
                            "invocationId")
                        .GetString()!;
                var target =
                    root.GetProperty("target")
                        .GetString();
                var arguments =
                    root.GetProperty(
                        "arguments");
                object result;
                if (target ==
                    "BootstrapPoints")
                {
                    var scopeId =
                        arguments[0].GetGuid();
                    var points =
                        arguments[1]
                            .EnumerateArray()
                            .Select(item =>
                                item.GetGuid())
                            .ToArray();
                    hubPoints[connectionToken] =
                        points;
                    result =
                        RuntimeSnapshotPayload(
                            scopeId,
                            points);
                }
                else if (target == "Poll")
                {
                    var scopeId =
                        arguments[0].GetGuid();
                    var cursor =
                        arguments[1].GetUInt64();
                    result =
                        RuntimePollPayload(
                            scopeId,
                            cursor,
                            hubPoints.GetValueOrDefault(
                                connectionToken) ??
                            []);
                }
                else
                {
                    result = new { };
                }

                messages.Enqueue(
                    JsonSerializer.Serialize(
                        new
                        {
                            type = 3,
                            invocationId,
                            result,
                        },
                        SerializerOptions) +
                    "\u001e");
            }

            await StatusAsync(route, 200)
                .ConfigureAwait(false);
            return;
        }

        if (!messages.TryDequeue(
                out var payload))
        {
            await Task.Delay(50)
                .ConfigureAwait(false);
            payload =
                messages.TryDequeue(
                    out var delayed)
                    ? delayed
                    : "{\"type\":6}\u001e";
        }

        await route.FulfillAsync(
                new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType =
                        "application/octet-stream",
                    Body = payload,
                })
            .ConfigureAwait(false);
    }

    private object RuntimeSnapshotPayload(
        Guid scopeId,
        IReadOnlyCollection<Guid> points) =>
        new
        {
            scopeId,
            cursor = runtimeCursor,
            points =
                points.Select(
                        RuntimePointPayload)
                    .ToArray(),
        };

    private object RuntimePollPayload(
        Guid scopeId,
        ulong cursor,
        IReadOnlyCollection<Guid> points) =>
        cursor < runtimeCursor
            ? new
            {
                kind = 1,
                delta = new
                {
                    scopeId,
                    from = cursor,
                    to = runtimeCursor,
                    changes =
                        points.Select(
                                RuntimePointPayload)
                            .ToArray(),
                },
            }
            : new
            {
                kind = 2,
                delta = (object?)null,
            };

    private object RuntimePointPayload(
        Guid pointId) =>
        new
        {
            pointId,
            value = currentValue,
            unit = "°C",
            quality = "Good",
            freshness = "Fresh",
            sourceTimestamp =
                DateTimeOffset.UtcNow,
            receiveTimestamp =
                DateTimeOffset.UtcNow,
            processedTimestamp =
                DateTimeOffset.UtcNow,
        };

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

        if (c10Enabled &&
            (path.StartsWith(
                 "/api/dashboards",
                 StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(
                 "/api/dashboard-editor",
                 StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(
                 "/api/mimic-editor",
                 StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(
                 "/api/terminal/runtime",
                 StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(
                 "/api/history/",
                 StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(
                 "/occurrences/snapshot",
                 StringComparison.OrdinalIgnoreCase)))
        {
            await HandleC10Async(
                    route,
                    request,
                    uri)
                .ConfigureAwait(false);
            return;
        }

        if (c14Enabled &&
            (path.StartsWith(
                 "/api/equipment-staging",
                 StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(
                 "/api/equipment-configuration",
                 StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(
                 "/api/registry/",
                 StringComparison.OrdinalIgnoreCase)))
        {
            await HandleC14Async(route, request, uri)
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

    private async Task HandleC14Async(
        IRoute route,
        IRequest request,
        Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.Equals(
                path,
                "/api/registry/scopes",
                StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(
                    route,
                    new[]
                    {
                        new
                        {
                            scopeId = ScopeId,
                            label = "C14 scope",
                            locationCount = 1,
                            equipmentCount = 0,
                        },
                    })
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/registry/equipment",
                StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(
                    route,
                    new
                    {
                        scopeId = ScopeId,
                        total = 0,
                        items = Array.Empty<object>(),
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(
                "/api/equipment-configuration",
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await AuthorizedJsonAsync(route, new
                    {
                        scopeId = new { value = ScopeId },
                        draftRevisionId = new { value = RevisionId },
                        publishedRevisionId = c14ConfigurationPublished
                            ? new { value = RevisionId }
                            : null,
                        distributedRevisionId = c14ConfigurationPublished
                            ? new { value = RevisionId }
                            : null,
                        activatedRevisionId = c14ConfigurationPublished
                            ? new { value = RevisionId }
                            : null,
                        version = Math.Max(1, c14ConfigurationVersion),
                        revisions = new[] { C14ConfigurationPayload() },
                    })
                    .ConfigureAwait(false);
                return;
            }

            if (path.EndsWith("/validate", StringComparison.OrdinalIgnoreCase))
            {
                c14ConfigurationValidated = true;
                c14ConfigurationVersion++;
            }
            else if (path.EndsWith("/publish", StringComparison.OrdinalIgnoreCase))
            {
                c14ConfigurationPublished = true;
                c14ConfigurationVersion++;
            }
            else if (path.EndsWith("/save-staging", StringComparison.OrdinalIgnoreCase))
            {
                c14ConfigurationVersion = Math.Max(1, c14ConfigurationVersion + 1);
                c14ConfigurationValidated = false;
                c14ConfigurationPublished = false;
            }

            await AuthorizedJsonAsync(route, C14ConfigurationPayload())
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/api/equipment-staging", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(route, c14Drafts.Select(C14DraftPayload).ToArray())
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith("/diagnostics/latest", StringComparison.OrdinalIgnoreCase))
        {
            var rowId = Guid.Parse(path.Split('/')[3]);
            if (c14DiagnosticRowId != rowId)
            {
                await AuthorizedStatusAsync(route, 204).ConfigureAwait(false);
                return;
            }

            await AuthorizedJsonAsync(route, C14DiagnosticPayload(rowId))
                .ConfigureAwait(false);
            return;
        }

        if (path.Contains("/diagnostics/", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(
                    route,
                    C14DiagnosticPayload(c14DiagnosticRowId ?? Guid.Empty))
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith("/diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            c14DiagnosticRowId = Guid.Parse(path.Split('/')[3]);
            c14DiagnosticJobId = Guid.CreateVersion7();
            await AuthorizedJsonAsync(route, C14DiagnosticPayload(c14DiagnosticRowId.Value))
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith("/authorize-update", StringComparison.OrdinalIgnoreCase))
        {
            var rowId = Guid.Parse(path.Split('/')[3]);
            var draft = c14Drafts.Single(item => item.RowId == rowId);
            draft.UpdateAuthorized = true;
            draft.Version++;
            await AuthorizedJsonAsync(route, C14DraftPayload(draft))
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith("/copy", StringComparison.OrdinalIgnoreCase))
        {
            var rowId = Guid.Parse(path.Split('/')[3]);
            var source = c14Drafts.Single(item => item.RowId == rowId);
            var copy = source.Copy();
            c14Drafts.Add(copy);
            await AuthorizedJsonAsync(route, new[] { C14DraftPayload(copy) })
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith("/templates", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(route, C14TemplatePayload())
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/api/equipment-staging/templates", StringComparison.OrdinalIgnoreCase))
        {
            await AuthorizedJsonAsync(route, new[] { C14TemplatePayload() })
                .ConfigureAwait(false);
            return;
        }

        if (path.Contains("/templates/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/apply", StringComparison.OrdinalIgnoreCase))
        {
            var body = JsonDocument.Parse(request.PostData ?? "{}").RootElement;
            var rowId = body.TryGetProperty("rowId", out var row) &&
                        row.ValueKind != JsonValueKind.Null
                ? row.GetGuid()
                : Guid.CreateVersion7();
            var draft = c14Drafts.FirstOrDefault(item => item.RowId == rowId) ??
                        C14Draft.New(rowId, "modbus_tcp");
            draft.Port = 502;
            if (!c14Drafts.Contains(draft))
            {
                c14Drafts.Add(draft);
            }

            await AuthorizedJsonAsync(route, C14DraftPayload(draft))
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/api/equipment-staging/apply", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var draft in c14Drafts)
            {
                draft.Applied = draft.Errors.Count == 0 &&
                                (draft.Action != 2 || draft.UpdateAuthorized);
            }

            await AuthorizedJsonAsync(
                    route,
                    c14Drafts.Select(item => new
                    {
                        rowId = item.RowId,
                        action = item.Action,
                        succeeded = item.Applied,
                        errors = item.Applied ? Array.Empty<object>() : item.Errors.ToArray(),
                    }).ToArray())
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/api/equipment-staging/csv", StringComparison.OrdinalIgnoreCase))
        {
            var imported = C14Draft.New(Guid.CreateVersion7(), "modbus_tcp");
            imported.Code = "CSV-PLC";
            imported.Name = "CSV PLC";
            imported.Host = "127.0.0.1";
            c14Drafts.Add(imported);
            await AuthorizedJsonAsync(route, new
                {
                    rows = new[] { C14DraftPayload(imported) },
                    errors = new[]
                    {
                        new
                        {
                            rowId = Guid.Empty,
                            equipmentId = new { value = Guid.Empty },
                            state = (int?)null,
                            errors = new[]
                            {
                                new { field = "port", code = "staging.range", message = "Port is invalid." },
                            },
                        },
                    },
                })
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.Method, "PUT", StringComparison.OrdinalIgnoreCase))
        {
            var rowId = Guid.Parse(path.Split('/')[3]);
            using var document = JsonDocument.Parse(request.PostData ?? "{}");
            var root = document.RootElement;
            var draft = c14Drafts.FirstOrDefault(item => item.RowId == rowId) ??
                        C14Draft.New(rowId, root.GetProperty("protocol").GetString() ?? "modbus_tcp");
            draft.Code = root.GetProperty("code").GetString() ?? string.Empty;
            draft.Name = root.GetProperty("name").GetString() ?? string.Empty;
            draft.Host = root.GetProperty("host").GetString() ?? string.Empty;
            draft.Port = root.GetProperty("port").GetInt32();
            draft.LocationId = root.GetProperty("locationId").GetGuid();
            draft.Protocol = root.GetProperty("protocol").GetString() ?? "modbus_tcp";
            draft.Action = root.GetProperty("action").GetString() switch
            {
                "Update" => 2,
                "Skip" => 3,
                _ => 1,
            };
            draft.HasSecret = draft.HasSecret ||
                              root.TryGetProperty("secret", out var secret) &&
                              secret.ValueKind == JsonValueKind.String &&
                              !string.IsNullOrEmpty(secret.GetString());
            draft.Version++;
            draft.Errors.Clear();
            if (string.IsNullOrWhiteSpace(draft.Code))
            {
                draft.Errors.Add(new { field = "code", code = "staging.required", message = "Equipment code is required." });
            }

            if (string.IsNullOrWhiteSpace(draft.Host))
            {
                draft.Errors.Add(new { field = "host", code = "staging.required", message = "Host is required." });
            }

            if (!c14Drafts.Contains(draft))
            {
                c14Drafts.Add(draft);
            }

            await AuthorizedJsonAsync(route, C14DraftPayload(draft))
                .ConfigureAwait(false);
            return;
        }

        await StatusAsync(route, 404).ConfigureAwait(false);
    }

    private object C14DraftPayload(C14Draft draft) => new
    {
        rowId = draft.RowId,
        equipmentId = new { value = draft.EquipmentId },
        scopeId = new { value = ScopeId },
        locationId = new { value = draft.LocationId },
        draft.Code,
        draft.Name,
        protocol = draft.Protocol == "modbus_tcp" ? 1 : 2,
        draft.Host,
        draft.Port,
        modbusUnitId = draft.Protocol == "modbus_tcp" ? 1 : (int?)null,
        modbusTable = draft.Protocol == "modbus_tcp" ? "holding" : null,
        modbusAddress = draft.Protocol == "modbus_tcp" ? 0 : (int?)null,
        modbusValueType = draft.Protocol == "modbus_tcp" ? "signed16" : null,
        modbusByteOrder = draft.Protocol == "modbus_tcp" ? "big" : null,
        modbusWordOrder = draft.Protocol == "modbus_tcp" ? "high_first" : null,
        modbusScale = draft.Protocol == "modbus_tcp" ? 1m : (decimal?)null,
        snmpVersion = draft.Protocol == "snmp_v2c" ? "v2c" : null,
        snmpOid = draft.Protocol == "snmp_v2c" ? "1.3.6.1.2.1.1.3.0" : null,
        snmpValueType = draft.Protocol == "snmp_v2c" ? "timeticks" : null,
        unit = "-",
        draft.HasSecret,
        action = draft.Action,
        draft.UpdateAuthorized,
        existingDeviceMatch = draft.Action == 2,
        fingerprint = $"C14-{draft.Version}",
        draft.Version,
        appliedAt = draft.Applied ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
        errors = draft.Errors.ToArray(),
    };

    private object C14DiagnosticPayload(Guid rowId) => new
    {
        jobId = c14DiagnosticJobId,
        rowId,
        scopeId = new { value = ScopeId },
        mode = 2,
        status = c14DiagnosticStatus,
        fingerprint = "C14-DIAGNOSTIC",
        isStale = c14Drafts.FirstOrDefault(item => item.RowId == rowId)?.Version > 1,
        attempts = 1,
        createdAt = DateTimeOffset.UtcNow,
        startedAt = DateTimeOffset.UtcNow,
        completedAt = c14DiagnosticStatus > 2 ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
        outcomeCode = c14DiagnosticOutcomeCode,
        outcomeMessage = c14DiagnosticOutcomeMessage,
        samples = c14DiagnosticStatus == 3
            ? new object[]
            {
                new
                {
                    name = "1.3.6.1.2.1.1.3.0",
                    value = (long?)42,
                    unit = "s",
                    quality = "Good",
                    observedAt = DateTimeOffset.UtcNow,
                    errorCode = (string?)null,
                },
            }
            : Array.Empty<object>(),
    };

    private static object C14TemplatePayload() => new
    {
        templateId = Guid.Parse("81000000-0000-0000-0000-000000000028"),
        scopeId = new { value = ScopeId },
        name = "Browser template",
        protocol = 1,
        port = 502,
        modbusTable = "holding",
        modbusAddress = 0,
        modbusValueType = "signed16",
        modbusByteOrder = "big",
        modbusWordOrder = "high_first",
        modbusScale = 1m,
        snmpVersion = (string?)null,
        snmpOid = (string?)null,
        snmpValueType = (string?)null,
        unit = "-",
        version = 1,
    };

    private object C14ConfigurationPayload() => new
    {
        revisionId = new { value = RevisionId },
        scopeId = new { value = ScopeId },
        revisionNumber = 1UL,
        sourceRevisionId = (object?)null,
        manifestJson = "{}",
        manifestFingerprint = "C14",
        dependencies = Array.Empty<object>(),
        dependencyFingerprint = "C14",
        version = Math.Max(1, c14ConfigurationVersion),
        savedAt = DateTimeOffset.UtcNow,
        validatedAt = c14ConfigurationValidated ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
        publishedAt = c14ConfigurationPublished ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
        distributedAt = c14ConfigurationPublished ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
        activatedAt = c14ConfigurationPublished ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
    };

    private async Task HandleC10Async(
        IRoute route,
        IRequest request,
        Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.StartsWith(
                "/api/terminal/runtime",
                StringComparison.OrdinalIgnoreCase))
        {
            if (kioskOffline)
            {
                await route.AbortAsync()
                    .ConfigureAwait(false);
                return;
            }

            if (kioskRevoked)
            {
                await StatusAsync(route, 403)
                    .ConfigureAwait(false);
                return;
            }

            if (path.EndsWith(
                    "/heartbeat",
                    StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(
                        route,
                        new
                        {
                            acceptedAt =
                                DateTimeOffset.UtcNow,
                            profileVersion =
                                kioskProfileVersion,
                            resyncRequired =
                                dashboardRevision >
                                1,
                        })
                    .ConfigureAwait(false);
                return;
            }

            await JsonAsync(
                    route,
                    new
                    {
                        terminalId = TerminalId,
                        deviceIdentityId =
                            DeviceIdentityId,
                        profileId = ProfileId,
                        profileVersion =
                            kioskProfileVersion,
                        experience = "Wallboard",
                        offlineMode =
                            "ReadOnlyLastSynchronized",
                        employeeReauthenticationRequired =
                            false,
                        dashboard =
                            DashboardManifestPayload(),
                        synchronizedAt =
                            DateTimeOffset.UtcNow,
                    })
                .ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(request))
        {
            await StatusAsync(route, 401)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(
                path,
                "/api/dashboards",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    new[]
                    {
                        new
                        {
                            dashboardId = DashboardId,
                            name = "Operations overview",
                            description =
                                "Published current, History and events",
                            isFavorite = true,
                            lastOpenedAt =
                                DateTimeOffset.UtcNow
                                    .AddMinutes(-5),
                            canEdit = true,
                        },
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(
                "/opened",
                StringComparison.OrdinalIgnoreCase))
        {
            await StatusAsync(route, 204)
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(
                "/status",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    new
                    {
                        isCurrent =
                            subscriptionRevision ==
                            dashboardRevision,
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(
                "/subscriptions",
                StringComparison.OrdinalIgnoreCase))
        {
            subscriptionRevision =
                dashboardRevision;
            await JsonAsync(
                    route,
                    DashboardSubscriptionPayload())
                .ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(
                $"/api/dashboards/{DashboardId:D}",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    DashboardManifestPayload())
                .ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(
                "/api/dashboard-editor/",
                StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith(
                    "/impact",
                    StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(
                        route,
                        EditorImpactPayload())
                    .ConfigureAwait(false);
                return;
            }

            if (request.Method == "GET")
            {
                await StatusAsync(route, 204)
                    .ConfigureAwait(false);
                return;
            }

            editorVersion =
                checked(editorVersion + 1);
            await JsonAsync(
                    route,
                    EditorRevisionPayload())
                .ConfigureAwait(false);
            return;
        }

        if (path.StartsWith(
                "/api/mimic-editor/",
                StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith(
                    "/impact",
                    StringComparison.OrdinalIgnoreCase))
            {
                await JsonAsync(
                        route,
                        EditorImpactPayload())
                    .ConfigureAwait(false);
                return;
            }

            if (request.Method == "GET")
            {
                await StatusAsync(route, 204)
                    .ConfigureAwait(false);
                return;
            }

            if (path.EndsWith(
                    "/preview",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (request.PostData?.Contains(
                        "script",
                        StringComparison.OrdinalIgnoreCase) ==
                    true)
                {
                    await route.FulfillAsync(
                            new RouteFulfillOptions
                            {
                                Status = 400,
                                ContentType =
                                    "application/problem+json",
                                Body =
                                    """
                                    {"detail":"SVG script elements are not allowed."}
                                    """,
                            })
                        .ConfigureAwait(false);
                    return;
                }

                await JsonAsync(
                        route,
                        new
                        {
                            sanitizedSvg =
                                SafeMimicSvg(),
                        })
                    .ConfigureAwait(false);
                return;
            }

            editorVersion =
                checked(editorVersion + 1);
            await JsonAsync(
                    route,
                    EditorRevisionPayload())
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
                                    now.AddMinutes(-1),
                                toExclusive = now,
                                count = 2L,
                                average =
                                    (double)currentValue,
                                minimum =
                                    currentValue - 1,
                                maximum =
                                    currentValue + 1,
                                quality = "Good",
                                freshness = "Fresh",
                                hasGap = false,
                            },
                        },
                    })
                .ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(
                "/occurrences/snapshot",
                StringComparison.OrdinalIgnoreCase))
        {
            await JsonAsync(
                    route,
                    new
                    {
                        cursor = occurrenceCursor,
                        occurrences =
                            OccurrencesPayload(),
                    })
                .ConfigureAwait(false);
            return;
        }

        await StatusAsync(route, 404)
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

    private object BootstrapPayload()
    {
        var permissions =
            new List<string>
            {
                "workspace.home.read",
                "runtime.current.read",
            };
        if (c09Enabled || c10Enabled)
        {
            permissions.Add(
                "history.range.read");
            permissions.Add(
                "events.dispatcher.read");
        }

        if (c09Enabled)
        {
            permissions.Add(
                "alarm.occurrence.acknowledge");
            permissions.Add(
                "alarm.occurrence.assign");
            permissions.Add(
                "alarm.occurrence.shelve");
        }

        if (c10Enabled)
        {
            permissions.Add(
                "dashboards.catalog.read");
            permissions.Add(
                $"dashboards.d{DashboardId:N}.read");
            permissions.Add(
                $"dashboards.d{DashboardId:N}.editor.read");
            permissions.Add(
                $"dashboards.d{DashboardId:N}.editor.save");
            permissions.Add(
                $"dashboards.d{DashboardId:N}.editor.validate");
            permissions.Add(
                $"dashboards.d{DashboardId:N}.editor.publish");
            permissions.Add(
                $"mimics.m{MimicId:N}.editor.read");
            permissions.Add(
                $"mimics.m{MimicId:N}.editor.save");
            permissions.Add(
                $"mimics.m{MimicId:N}.editor.validate");
            permissions.Add(
                $"mimics.m{MimicId:N}.editor.publish");
            permissions.Add(
                $"runtime.point.p{EventPointId:N}.read");
        }

        if (c14Enabled)
        {
            permissions.Add(
                $"equipment.scope.s{ScopeId:N}.read");
            permissions.Add(
                $"equipment.scope.s{ScopeId:N}.write");
            permissions.Add(
                $"equipment.scope.s{ScopeId:N}.administer");
            permissions.Add(
                $"configuration.scope.s{ScopeId:N}.read");
            permissions.Add(
                $"configuration.scope.s{ScopeId:N}.save");
            permissions.Add(
                $"configuration.scope.s{ScopeId:N}.validate");
            permissions.Add(
                $"configuration.scope.s{ScopeId:N}.publish");
        }

        return new
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
                permissions.ToArray(),
        };
    }

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
        if (c10Enabled)
        {
            items.Add(
                new
                {
                    label = "Dashboards",
                    route = "/dashboards",
                });
        }
        if (c14Enabled)
        {
            items.Add(
                new
                {
                    label = "Equipment",
                    route = "/equipment",
                });
        }

        return items.ToArray();
    }

    private object DashboardManifestPayload() =>
        new
        {
            dashboardId = DashboardId,
            revisionId =
                dashboardRevision == 1
                    ? RevisionId
                    : SecondRevisionId,
            revisionNumber =
                dashboardRevision,
            name = "Operations overview",
            description =
                "Published current, History and events",
            windows = new[]
            {
                new
                {
                    windowId = WindowId,
                    title = "Main process",
                    widgets = new[]
                    {
                        new
                        {
                            widgetId = WidgetId,
                            kind = "value",
                            title = "Process value",
                            bindingIds =
                                new[]
                                {
                                    CurrentBindingId,
                                },
                        },
                        new
                        {
                            widgetId = TrendWidgetId,
                            kind = "trend:L",
                            title = "Process trend",
                            bindingIds =
                                new[]
                                {
                                    HistoryBindingId,
                                },
                        },
                        new
                        {
                            widgetId = EventWidgetId,
                            kind = "events",
                            title = "Active events",
                            bindingIds =
                                new[]
                                {
                                    AlarmBindingId,
                                },
                        },
                    },
                    bindings =
                        DashboardBindings(),
                    layout = "Combined",
                    mimicId = MimicId,
                    mimicRevisionId =
                        MimicRevisionId,
                },
            },
            dependencies =
                Array.Empty<object>(),
            publishedAt =
                DateTimeOffset.UtcNow,
            selectedWindowId = WindowId,
            mimics = new[]
            {
                new
                {
                    windowId = WindowId,
                    mimicId = MimicId,
                    revisionId =
                        MimicRevisionId,
                    revisionNumber = 1UL,
                    name = "Process Mimic",
                    sanitizedSvg =
                        SafeMimicSvg(),
                    bindings = new[]
                    {
                        new
                        {
                            bindingId =
                                MimicBindingId,
                            source = "Current",
                            scopeId = ScopeId,
                            pointId =
                                EventPointId,
                            historySourceId =
                                (Guid?)null,
                        },
                    },
                    canEdit = true,
                },
            },
        };

    private static object[] DashboardBindings() =>
        [
            new
            {
                bindingId =
                    CurrentBindingId,
                source = "Current",
                scopeId = ScopeId,
                pointId = EventPointId,
                historySourceId =
                    (Guid?)null,
            },
            new
            {
                bindingId =
                    HistoryBindingId,
                source = "History",
                scopeId = ScopeId,
                pointId = EventPointId,
                historySourceId =
                    (Guid?)HistorySourceId,
            },
            new
            {
                bindingId = AlarmBindingId,
                source = "Alarm",
                scopeId = ScopeId,
                pointId = EventPointId,
                historySourceId =
                    (Guid?)null,
            },
        ];

    private object DashboardSubscriptionPayload() =>
        new
        {
            subscriptionId =
                Guid.Parse(
                    "81000000-0000-0000-0000-000000000027"),
            dashboardId = DashboardId,
            revisionId =
                dashboardRevision == 1
                    ? RevisionId
                    : SecondRevisionId,
            windows = new[]
            {
                new
                {
                    windowId = WindowId,
                    widgets = new[]
                    {
                        new
                        {
                            widgetId = WidgetId,
                            bindingIds =
                                new[]
                                {
                                    CurrentBindingId,
                                },
                        },
                        new
                        {
                            widgetId = TrendWidgetId,
                            bindingIds =
                                new[]
                                {
                                    HistoryBindingId,
                                },
                        },
                        new
                        {
                            widgetId = EventWidgetId,
                            bindingIds =
                                new[]
                                {
                                    AlarmBindingId,
                                },
                        },
                    },
                    mimicBindingIds =
                        new[]
                        {
                            MimicBindingId,
                        },
                },
            },
            links = new[]
            {
                new
                {
                    bindingId =
                        CurrentBindingId,
                    source = "Current",
                    scopeId = ScopeId,
                    pointId = EventPointId,
                    endpoint =
                        "/hubs/runtime",
                },
                new
                {
                    bindingId =
                        HistoryBindingId,
                    source = "History",
                    scopeId = ScopeId,
                    pointId = EventPointId,
                    endpoint =
                        "/api/history/aggregate",
                },
                new
                {
                    bindingId = AlarmBindingId,
                    source = "Alarm",
                    scopeId = ScopeId,
                    pointId = EventPointId,
                    endpoint = "/hubs/events",
                },
                new
                {
                    bindingId = MimicBindingId,
                    source = "Current",
                    scopeId = ScopeId,
                    pointId = EventPointId,
                    endpoint =
                        "/hubs/runtime",
                },
            },
        };

    private object EditorRevisionPayload() =>
        new
        {
            revisionId = RevisionId,
            revisionNumber = 1UL,
            version = editorVersion,
            validatedAt =
                editorVersion >= 2
                    ? DateTimeOffset.UtcNow
                    : (DateTimeOffset?)null,
            publishedAt =
                editorVersion >= 3
                    ? DateTimeOffset.UtcNow
                    : (DateTimeOffset?)null,
        };

    private static object EditorImpactPayload() =>
        new
        {
            revisionId = RevisionId,
            replacesRevisionId =
                (Guid?)null,
            windowIds =
                new[]
                {
                    WindowId,
                },
            bindingCount = 1,
            mimicCount = 0,
            requiresRuntimeResnapshot = true,
        };

    private static string SafeMimicSvg() =>
        $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 40">
          <rect id="pump" x="5" y="5" width="90" height="30" fill="none" stroke="currentColor" data-binding-id="{MimicBindingId:D}" />
        </svg>
        """;

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

    private sealed class C14Draft
    {
        public Guid RowId { get; init; }
        public Guid EquipmentId { get; init; }
        public Guid LocationId { get; set; } = CommissioningLocationId;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Protocol { get; set; } = "modbus_tcp";
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 502;
        public bool HasSecret { get; set; }
        public int Action { get; set; } = 1;
        public bool UpdateAuthorized { get; set; }
        public bool Applied { get; set; }
        public long Version { get; set; } = 1;
        public List<object> Errors { get; } = [];

        public static C14Draft New(Guid rowId, string protocol) => new()
        {
            RowId = rowId,
            EquipmentId = Guid.CreateVersion7(),
            Protocol = protocol,
            Port = protocol == "snmp_v2c" ? 161 : 502,
        };

        public C14Draft Copy() => new()
        {
            RowId = Guid.CreateVersion7(),
            EquipmentId = Guid.CreateVersion7(),
            LocationId = LocationId,
            Code = $"{Code}-1",
            Name = Name,
            Protocol = Protocol,
            Host = Host,
            Port = Port,
            HasSecret = false,
            Action = 1,
        };
    }

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
