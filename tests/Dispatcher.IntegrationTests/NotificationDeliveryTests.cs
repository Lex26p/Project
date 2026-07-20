using System.Net;
using System.Net.Sockets;
using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Notifications;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class NotificationDeliveryTests
{
    private const string RawSecret = "controlled-smtp-password";
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("fb000000-0000-0000-0000-000000000001"));
    private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
        Guid.Parse("fb000000-0000-0000-0000-000000000002"));
    private static readonly PersonId Original = PersonId.From(
        Guid.Parse("fb000000-0000-0000-0000-000000000003"));
    private static readonly PersonId RecipientA = PersonId.From(
        Guid.Parse("fb000000-0000-0000-0000-000000000004"));
    private static readonly PersonId RecipientB = PersonId.From(
        Guid.Parse("fb000000-0000-0000-0000-000000000005"));
    private static readonly PersonId RecipientC = PersonId.From(
        Guid.Parse("fb000000-0000-0000-0000-000000000006"));
    private readonly PostgreSqlClusterFixture cluster;

    public NotificationDeliveryTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task OutageBacklogCrashRestartDuplicateAndTerminalOutcomesAreDurable()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            NotificationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new MutableClock();
        var store = new NotificationDeliveryStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var candidate = Candidate(OperationalEventId.New());
        var routes = new[]
        {
            new NotificationRoute(RecipientA, NotificationChannel.Email, true, Original),
            new NotificationRoute(RecipientB, NotificationChannel.Email, true, null),
            new NotificationRoute(RecipientC, NotificationChannel.Email, false, null),
        };
        var settings = Settings(RecipientA, RecipientB, RecipientC);
        var accepted = await store.EnsureObligationsAsync(candidate, routes, settings);
        var duplicate = await store.EnsureObligationsAsync(candidate, routes, settings);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(3, accepted.Value.Count);
        Assert.Contains(accepted.Value, item => item.RecipientId == RecipientA && item.CoveredPersonId == Original);
        Assert.Equal(
            accepted.Value.OrderBy(item => item.RecipientId.Value).Select(item => item.DeliveryId),
            duplicate.Value.OrderBy(item => item.RecipientId.Value).Select(item => item.DeliveryId));

        var crashedClaim = (await store.ClaimAsync("worker-crashed", TimeSpan.FromSeconds(30))).Value.Claim!;
        clock.Advance(TimeSpan.FromSeconds(31));
        var client = new QueueSmtpClient(new IOException("provider outage"));
        var restartedStore = new NotificationDeliveryStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var restartedWorker = Worker(restartedStore, client, maxAttempts: 2);
        var outage = await restartedWorker.RunOnceAsync("worker-restarted", TimeSpan.FromSeconds(30));
        Assert.Equal(NotificationDeliveryStatus.PendingRetry, outage.Value.Obligation!.Status);
        Assert.Equal(crashedClaim.Attempt.AttemptId, Assert.Single(client.AttemptIds));

        var delivered = new List<NotificationDeliveryObligation>();
        for (var index = 0; index < 8; index++)
        {
            var result = await restartedWorker.RunOnceAsync("worker-restarted", TimeSpan.FromSeconds(30));
            if (result.Value.Obligation is null)
            {
                break;
            }

            if (result.Value.Obligation.Status == NotificationDeliveryStatus.Delivered)
            {
                delivered.Add(result.Value.Obligation);
            }
        }

        Assert.Equal(3, delivered.Count);
        var recoveredAttempts = await restartedStore.ReadAttemptsAsync(crashedClaim.Obligation.DeliveryId);
        Assert.Equal(2, recoveredAttempts.Count);
        Assert.Equal(crashedClaim.Attempt.AttemptId, recoveredAttempts[0].AttemptId);
        Assert.All(recoveredAttempts, attempt => Assert.DoesNotContain(RawSecret, attempt.ProviderReceipt ?? string.Empty));

        var mandatoryFailure = await TerminalOutcomeAsync(
            restartedStore,
            Settings(RecipientA),
            RecipientA,
            mandatory: true,
            clock);
        var personalFailure = await TerminalOutcomeAsync(
            restartedStore,
            Settings(RecipientB),
            RecipientB,
            mandatory: false,
            clock);
        Assert.Equal(NotificationDeliveryStatus.EscalationRequired, mandatoryFailure.Status);
        Assert.Equal(NotificationDeliveryStatus.TerminalFailure, personalFailure.Status);
    }

    [Fact]
    public async Task ControlledSmtpChannelAndRealtimeCounterProjectionAreQualifiedWithoutDocker()
    {
        await using var smtp = await ControlledSmtpServer.StartAsync();
        var provider = new SmtpNotificationProvider(
            new SmtpProviderConfiguration(
                SmtpProviderProfile.ControlledTest,
                IPAddress.Loopback.ToString(),
                smtp.Port,
                false,
                "dispatcher@example.test",
                null,
                null,
                TimeSpan.FromSeconds(5)),
            new RejectingSecretResolver(),
            new SystemSmtpClientAdapter());

        var channel = await provider.TestChannelAsync("operator@example.test");

        Assert.Equal(NotificationAttemptOutcome.Delivered, channel.Outcome);
        var transcript = await smtp.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Subject: Dispatcher SMTP channel test", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RawSecret, transcript, StringComparison.Ordinal);

        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            NotificationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new MutableClock();
        var store = new NotificationStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var candidate = Candidate(OperationalEventId.New());
        var acceptance = await store.AcceptAsync(
            candidate,
            [new NotificationRoute(RecipientA, NotificationChannel.Inbox, true, null)]);
        var created = Assert.Single(acceptance.Value.InboxItems);
        Assert.Equal(new NotificationInboxCounterSnapshot(1, 1, 1), await store.ReadCounterSnapshotAsync(RecipientA));
        Assert.True((await store.MarkReadAsync(RecipientA, created.ItemId, created.Version)).IsSuccess);
        var feed = await store.ReadCounterFeedAsync(RecipientA, 0);
        Assert.Equal(NotificationCounterFeedKind.Delta, feed.Kind);
        Assert.Equal(2, feed.Changes.Count);
        Assert.Equal(new NotificationInboxCounterSnapshot(2, 1, 0), feed.Changes[^1]);
    }

    private static async Task<NotificationDeliveryObligation> TerminalOutcomeAsync(
        NotificationDeliveryStore store,
        IReadOnlyDictionary<PersonId, PersonalNotificationSettings> settings,
        PersonId recipient,
        bool mandatory,
        MutableClock clock)
    {
        var candidate = Candidate(OperationalEventId.New());
        var accepted = await store.EnsureObligationsAsync(
            candidate,
            [new NotificationRoute(recipient, NotificationChannel.Email, mandatory, null)],
            settings);
        Assert.True(accepted.IsSuccess);
        var worker = Worker(store, new QueueSmtpClient(new IOException("terminal outage")), maxAttempts: 1);
        var result = await worker.RunOnceAsync($"terminal-{recipient.Value:N}", TimeSpan.FromSeconds(30));
        Assert.NotNull(result.Value.Obligation);
        return result.Value.Obligation;
    }

    private static NotificationDeliveryWorker Worker(
        NotificationDeliveryStore store,
        ISmtpClientAdapter client,
        int maxAttempts) => new(
        store,
        new SmtpNotificationProvider(
            new SmtpProviderConfiguration(
                SmtpProviderProfile.Production,
                "smtp.example.test",
                587,
                true,
                "dispatcher@example.test",
                "dispatcher",
                NotificationSecretReference.From("env:SMTP_PASSWORD"),
                TimeSpan.FromSeconds(5)),
            new StaticSecretResolver(),
            client),
        new NotificationDeliveryPolicy(
            maxAttempts,
            Enumerable.Repeat(TimeSpan.Zero, maxAttempts - 1).ToArray()));

    private static Dictionary<PersonId, PersonalNotificationSettings> Settings(params PersonId[] persons) =>
        persons.ToDictionary(
            person => person,
            person => new PersonalNotificationSettings(
                person,
                StateVersion.Initial,
                null,
                [],
                null,
                [new NotificationChannelPreference(NotificationChannel.Email, true)],
                $"{person.Value:N}@example.test"));

    private static NotificationCandidate Candidate(OperationalEventId eventId) =>
        NotificationCandidate.FromOperationalEvent(
            new OperationalEventRecord(
                eventId,
                new EventJournalPosition(1),
                ScopeId,
                PointId,
                AlarmOccurrenceId.New(),
                StateVersion.Initial,
                AlarmPriority.High,
                OperationalEventKind.AlarmRaised,
                MutableClock.Start,
                MutableClock.Start),
            "High value alarm");

    private sealed class MutableClock : IWallClock
    {
        public static DateTimeOffset Start { get; } = new(2026, 7, 20, 20, 0, 0, TimeSpan.Zero);
        private DateTimeOffset now = Start;
        public DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class StaticSecretResolver : INotificationSecretResolver
    {
        public ValueTask<NotificationSecretLease> ResolveAsync(
            NotificationSecretReference reference,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(NotificationSecretLease.Create(RawSecret));
    }

    private sealed class RejectingSecretResolver : INotificationSecretResolver
    {
        public ValueTask<NotificationSecretLease> ResolveAsync(
            NotificationSecretReference reference,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<NotificationSecretLease>(new InvalidOperationException("Secret must not be resolved."));
    }

    private sealed class QueueSmtpClient : ISmtpClientAdapter
    {
        private readonly Queue<Exception> failures;
        public QueueSmtpClient(params Exception[] failures) => this.failures = new(failures);
        public List<NotificationAttemptId> AttemptIds { get; } = [];

        public Task<string> SendAsync(
            SmtpProviderConfiguration configuration,
            SmtpDeliveryMessage message,
            ReadOnlyMemory<char>? credential,
            CancellationToken cancellationToken)
        {
            AttemptIds.Add(message.AttemptId);
            if (failures.TryDequeue(out var failure))
            {
                return Task.FromException<string>(failure);
            }

            return Task.FromResult($"receipt-{message.AttemptId.Value:N}");
        }
    }

    private sealed class ControlledSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellation = new();

        private ControlledSmtpServer(TcpListener listener, Task<string> completion)
        {
            this.listener = listener;
            Completion = completion;
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Task<string> Completion { get; }

        public static Task<ControlledSmtpServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var completion = RunAsync(listener);
            return Task.FromResult(new ControlledSmtpServer(listener, completion));
        }

        private static async Task<string> RunAsync(TcpListener listener)
        {
            using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            var transcript = new List<string>();
            await writer.WriteLineAsync("220 controlled-smtp ESMTP").ConfigureAwait(false);
            var data = false;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                transcript.Add(line);
                if (data)
                {
                    if (line == ".")
                    {
                        data = false;
                        await writer.WriteLineAsync("250 queued").ConfigureAwait(false);
                    }

                    continue;
                }

                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-controlled-smtp").ConfigureAwait(false);
                    await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                }
                else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                }
                else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    data = true;
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>").ConfigureAwait(false);
                }
                else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
                    break;
                }
            }

            return string.Join('\n', transcript);
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
            }

            cancellation.Dispose();
        }
    }
}
