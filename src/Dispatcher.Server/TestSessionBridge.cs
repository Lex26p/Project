using System.Collections.Concurrent;
using Dispatcher.Platform;

namespace Dispatcher.Server;

public sealed class TestSessionBridgeOptions
{
    public const string SectionName = "Dispatcher:TestSessionBridge";

    public bool Enabled { get; set; }
}

public sealed class SessionDirectory
{
    private readonly ConcurrentDictionary<SessionId, SessionSnapshot> sessions = new();

    public void Set(SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        sessions[session.Id] = session;
    }

    public bool TryGet(SessionId sessionId, out SessionSnapshot? session) =>
        sessions.TryGetValue(sessionId, out session);
}

public sealed class RequestSessionResolver
{
    public const string HeaderName = "X-Dispatcher-Test-Session";

    private readonly IWebHostEnvironment environment;
    private readonly TestSessionBridgeOptions options;
    private readonly SessionDirectory sessions;

    public RequestSessionResolver(
        IWebHostEnvironment environment,
        Microsoft.Extensions.Options.IOptions<TestSessionBridgeOptions> options,
        SessionDirectory sessions)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessions);
        this.environment = environment;
        this.options = options.Value;
        this.sessions = sessions;
    }

    public SessionSnapshot? Resolve(HttpContext? context)
    {
        if (context?.Items.TryGetValue(ProductionSessionMiddleware.SessionItemKey, out var production) == true &&
            production is SessionSnapshot productionSession)
        {
            return productionSession;
        }

        var allowedEnvironment = environment.IsDevelopment() || environment.IsEnvironment("Test");
        if (!options.Enabled || !allowedEnvironment || context is null)
        {
            return null;
        }

        var raw = context.Request.Headers[HeaderName].ToString();
        return Guid.TryParse(raw, out var value) &&
               sessions.TryGet(SessionId.From(value), out var session)
            ? session
            : null;
    }
}
