using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Configuration;

public sealed class ConfigurationService
{
    private readonly ConfigurationStore store;
    private readonly IWallClock clock;

    public ConfigurationService(ConfigurationStore store, IWallClock clock)
    {
        this.store = store;
        this.clock = clock;
    }

    public Task<Result<ConfigurationScopeSnapshot>> ReadScopeAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndReadAsync(
            session,
            ConfigurationPermissions.Read(scopeId),
            () => store.ReadScopeAsync(scopeId, cancellationToken));

    public Task<Result<ConfigurationRevisionSnapshot>> ReadDesiredReleaseAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndReadAsync(
            session,
            ConfigurationPermissions.Read(scopeId),
            () => store.ReadDesiredReleaseAsync(scopeId, cancellationToken));

    public Task<Result<ConfigurationRevisionSnapshot>> SaveAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        SaveConfigurationRequest request,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndExecuteAsync(
            session,
            ConfigurationPermissions.Save(scopeId),
            authorization => store.SaveAsync(authorization, scopeId, request, cancellationToken));

    public Task<Result<ConfigurationRevisionSnapshot>> ValidateAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndExecuteAsync(
            session,
            ConfigurationPermissions.Validate(scopeId),
            authorization => store.ValidateAsync(
                authorization,
                scopeId,
                revisionId,
                expectedVersion,
                cancellationToken));

    public Task<Result<ConfigurationRevisionSnapshot>> PublishAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        PublishConfigurationRequest request,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndExecuteAsync(
            session,
            ConfigurationPermissions.Publish(scopeId),
            authorization => store.PublishAsync(authorization, scopeId, request, cancellationToken));

    public Task<Result<ConfigurationRevisionSnapshot>> RollbackAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        ConfigurationRevisionId sourceRevisionId,
        long expectedScopeVersion,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndExecuteAsync(
            session,
            ConfigurationPermissions.Save(scopeId),
            authorization => store.RollbackAsync(
                authorization,
                scopeId,
                sourceRevisionId,
                expectedScopeVersion,
                cancellationToken));

    public Task<Result<DistributionJobSnapshot>> ClaimDistributionAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndExecuteAsync(
            session,
            ConfigurationPermissions.Distribute(scopeId),
            authorization => store.ClaimDistributionAsync(
                authorization,
                scopeId,
                workerId,
                leaseDuration,
                cancellationToken));

    public Task<Result<ConfigurationRevisionSnapshot>> CompleteDistributionAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        DistributionJobId jobId,
        string workerId,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndExecuteAsync(
            session,
            ConfigurationPermissions.Distribute(scopeId),
            authorization => store.CompleteDistributionAsync(
                authorization,
                scopeId,
                jobId,
                workerId,
                cancellationToken));

    public Task<Result<ConfigurationRevisionSnapshot>> AcknowledgeActivationAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndExecuteAsync(
            session,
            ConfigurationPermissions.Activate(scopeId),
            authorization => store.AcknowledgeActivationAsync(
                authorization,
                scopeId,
                revisionId,
                expectedVersion,
                cancellationToken));

    private async Task<Result<TValue>> AuthorizeAndReadAsync<TValue>(
        SessionSnapshot? session,
        PermissionCode permission,
        Func<Task<Result<TValue>>> execute)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
        return authorization.IsFailure
            ? Result.Failure<TValue>(authorization.Error!)
            : await execute().ConfigureAwait(false);
    }

    private async Task<Result<TValue>> AuthorizeAndExecuteAsync<TValue>(
        SessionSnapshot? session,
        PermissionCode permission,
        Func<AuthorizedAccess, Task<Result<TValue>>> execute)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
        return authorization.IsFailure
            ? Result.Failure<TValue>(authorization.Error!)
            : await execute(authorization.Value).ConfigureAwait(false);
    }
}
