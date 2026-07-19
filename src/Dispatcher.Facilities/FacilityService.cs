using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Facilities;

public sealed class FacilityService
{
    private readonly FacilityStore store;
    private readonly IWallClock clock;

    public FacilityService(FacilityStore store, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        this.store = store;
        this.clock = clock;
    }

    public async Task<Result<LocationSnapshot>> ReadLocationAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        LocationId locationId,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, FacilityPermissions.Read(scopeId), clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<LocationSnapshot>(authorization.Error!);
        }

        var location = await store.ReadLocationAsync(locationId, cancellationToken).ConfigureAwait(false);
        return location is null || location.ScopeId != scopeId
            ? Failure<LocationSnapshot>("facility.location_not_found", "The location is not visible in this scope.")
            : Result.Success(location);
    }

    public async Task<Result<FacilityGraph>> ReadGraphAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, FacilityPermissions.Read(scopeId), clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<FacilityGraph>(authorization.Error!);
        }

        return Result.Success(await store.ReadGraphAsync(scopeId, cancellationToken).ConfigureAwait(false));
    }

    public Task<Result<FacilityMutation>> CreateLocationAsync(
        SessionSnapshot? session,
        CreateLocation request,
        CancellationToken cancellationToken = default) =>
        AuthorizeAndExecuteAsync(
            session,
            request.ScopeId,
            authorization => store.CreateLocationAsync(authorization, request, cancellationToken));

    public async Task<Result<FacilityMutation>> MoveLocationAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        MoveLocation request,
        CancellationToken cancellationToken = default) =>
        await AuthorizeAndExecuteAsync(
            session,
            scopeId,
            authorization => store.MoveLocationAsync(authorization, scopeId, request, cancellationToken))
            .ConfigureAwait(false);

    public async Task<Result<FacilityMutation>> AddFunctionalRelationAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        AddFunctionalLocationRelation request,
        CancellationToken cancellationToken = default) =>
        await AuthorizeAndExecuteAsync(
            session,
            scopeId,
            authorization => store.AddFunctionalRelationAsync(authorization, scopeId, request, cancellationToken))
            .ConfigureAwait(false);

    private async Task<Result<FacilityMutation>> AuthorizeAndExecuteAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Func<AuthorizedAccess, Task<Result<FacilityMutation>>> execute)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, FacilityPermissions.Write(scopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<FacilityMutation>(authorization.Error!)
            : await execute(authorization.Value).ConfigureAwait(false);
    }

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
