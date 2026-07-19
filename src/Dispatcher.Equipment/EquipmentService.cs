using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Equipment;

public sealed class EquipmentService
{
    private readonly EquipmentStore store;
    private readonly FacilityStore facilities;
    private readonly IWallClock clock;

    public EquipmentService(EquipmentStore store, FacilityStore facilities, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(facilities);
        ArgumentNullException.ThrowIfNull(clock);
        this.store = store;
        this.facilities = facilities;
        this.clock = clock;
    }

    public async Task<Result<EquipmentRegistry>> ReadRegistryAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Read(scopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<EquipmentRegistry>(authorization.Error!)
            : Result.Success(await store.ReadRegistryAsync(scopeId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<EquipmentMutation>> CreateEquipmentAsync(
        SessionSnapshot? session,
        CreateEquipment request,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session,
            EquipmentPermissions.Write(request.ScopeId),
            clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<EquipmentMutation>(authorization.Error!);
        }

        if (!await LocationBelongsToScopeAsync(request.LocationId, request.ScopeId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Failure("equipment.location_scope", "Equipment location must exist in the same scope.");
        }

        return await store.CreateEquipmentAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<EquipmentMutation>> EnsureEquipmentAsync(
        SessionSnapshot? session,
        CreateEquipment request,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session,
            EquipmentPermissions.Write(request.ScopeId),
            clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<EquipmentMutation>(authorization.Error!);
        }

        if (!await LocationBelongsToScopeAsync(request.LocationId, request.ScopeId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Failure("equipment.location_scope", "Equipment location must exist in the same scope.");
        }

        var existing = await store.ReadEquipmentAsync(request.EquipmentId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Matches(existing, request)
                ? Result.Success(new EquipmentMutation(existing.EquipmentId, existing.Version))
                : Failure("equipment.identity_conflict", "Equipment identity is already used by different metadata.");
        }

        var created = await store.CreateEquipmentAsync(authorization.Value, request, cancellationToken)
            .ConfigureAwait(false);
        if (created.IsSuccess || created.Error?.Code.Value != "equipment.identity_conflict")
        {
            return created;
        }

        existing = await store.ReadEquipmentAsync(request.EquipmentId, cancellationToken).ConfigureAwait(false);
        return existing is not null && Matches(existing, request)
            ? Result.Success(new EquipmentMutation(existing.EquipmentId, existing.Version))
            : created;
    }

    public async Task<Result<EquipmentMutation>> MoveEquipmentAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        MoveEquipment request,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session,
            EquipmentPermissions.Write(scopeId),
            clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<EquipmentMutation>(authorization.Error!);
        }

        if (!await LocationBelongsToScopeAsync(request.LocationId, scopeId, cancellationToken).ConfigureAwait(false))
        {
            return Failure("equipment.location_scope", "Equipment location must exist in the same scope.");
        }

        return await store.MoveEquipmentAsync(authorization.Value, scopeId, request, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<EquipmentMutation>> AddPointAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        AddPointDefinition request,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session,
            EquipmentPermissions.Write(scopeId),
            clock);
        return authorization.IsFailure
            ? Result.Failure<EquipmentMutation>(authorization.Error!)
            : await store.AddPointAsync(authorization.Value, scopeId, request, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<bool> LocationBelongsToScopeAsync(
        LocationId locationId,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken)
    {
        var location = await facilities.ReadLocationAsync(locationId, cancellationToken).ConfigureAwait(false);
        return location is not null && location.ScopeId == scopeId;
    }

    private static Result<EquipmentMutation> Failure(string code, string message) =>
        Result.Failure<EquipmentMutation>(new OperationError(ErrorCode.From(code), message));

    private static bool Matches(EquipmentSnapshot existing, CreateEquipment request) =>
        existing.ScopeId == request.ScopeId &&
        existing.LocationId == request.LocationId &&
        string.Equals(existing.Code, request.Code.Trim(), StringComparison.Ordinal) &&
        string.Equals(existing.Name, request.Name.Trim(), StringComparison.Ordinal);
}
