using Dispatcher.Core;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Server;

public sealed record RegistryScopePayload(
    Guid ScopeId,
    string Label,
    int LocationCount,
    int EquipmentCount);

public sealed record LocationSummaryPayload(
    Guid LocationId,
    Guid? PhysicalParentId,
    string Code,
    string Name,
    int ChildCount,
    int EquipmentCount);

public sealed record LocationListPayload(
    Guid ScopeId,
    int Total,
    IReadOnlyList<LocationSummaryPayload> Items);

public sealed record LocationRelationPayload(Guid LocationId, string Code, string Name);

public sealed record EquipmentSummaryPayload(
    Guid EquipmentId,
    Guid LocationId,
    string Code,
    string Name,
    int PointCount);

public sealed record LocationDetailPayload(
    Guid ScopeId,
    LocationSummaryPayload Location,
    IReadOnlyList<LocationSummaryPayload> Children,
    IReadOnlyList<LocationRelationPayload> FunctionalIncoming,
    IReadOnlyList<LocationRelationPayload> FunctionalOutgoing,
    IReadOnlyList<EquipmentSummaryPayload> Equipment);

public sealed record LocationPlanContextPayload(
    Guid ScopeId,
    LocationSummaryPayload Location,
    IReadOnlyList<LocationRelationPayload> Ancestors,
    IReadOnlyList<LocationSummaryPayload> Children,
    IReadOnlyList<EquipmentSummaryPayload> Equipment);

public sealed record EquipmentListPayload(
    Guid ScopeId,
    int Total,
    IReadOnlyList<EquipmentSummaryPayload> Items);

public sealed record EquipmentCurrentPayload(
    long Value,
    string Unit,
    string Quality,
    string Freshness,
    DateTimeOffset SourceTimestamp,
    DateTimeOffset ReceiveTimestamp,
    DateTimeOffset ProcessedTimestamp,
    bool IsUsable);

public sealed record EquipmentPointPayload(
    Guid PointId,
    string Code,
    string Name,
    string Unit,
    EquipmentCurrentPayload? Current);

public sealed record EquipmentDetailPayload(
    Guid ScopeId,
    EquipmentSummaryPayload Equipment,
    LocationRelationPayload Location,
    string CurrentStatus,
    string ConnectionStatus,
    string ConnectionStatusReason,
    IReadOnlyList<EquipmentPointPayload> Points);

public static class RegistryRoutes
{
    public const string Locations = "/locations";
    public const string Equipment = "/equipment";

    public static bool IsCanonical(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var path = route.Split('?', 2)[0].TrimEnd('/');
        if (string.Equals(path, Locations, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, Equipment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 &&
               (string.Equals(segments[0], "locations", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[0], "equipment", StringComparison.OrdinalIgnoreCase)) &&
               Guid.TryParse(segments[1], out _);
    }
}

public sealed class RegistryProjectionService
{
    private const string FacilityReadPrefix = "facility.scope.s";
    private const string ReadSuffix = ".read";
    private readonly FacilityService facilities;
    private readonly EquipmentService equipment;
    private readonly AuthorizedRuntimeReader runtime;
    private readonly IWallClock clock;

    public RegistryProjectionService(
        FacilityService facilities,
        EquipmentService equipment,
        AuthorizedRuntimeReader runtime,
        IWallClock clock)
    {
        this.facilities = facilities;
        this.equipment = equipment;
        this.runtime = runtime;
        this.clock = clock;
    }

    public Result<bool> CanAccess(SessionSnapshot? session, string route)
    {
        if (!RegistryRoutes.IsCanonical(route))
        {
            return Failure<bool>("registry.route_not_found", "The route is not a canonical registry route.");
        }

        var scopes = ReadableScopes(session);
        return scopes.IsFailure
            ? Result.Failure<bool>(scopes.Error!)
            : scopes.Value.Count > 0
                ? Result.Success(true)
                : Failure<bool>("permission.denied", "No registry scope is accessible.");
    }

    public Result<IReadOnlyList<FacilityScopeId>> ReadableScopes(SessionSnapshot? session)
    {
        var validation = SessionAuthorization.ValidateSession(session, clock);
        if (validation.IsFailure)
        {
            return Result.Failure<IReadOnlyList<FacilityScopeId>>(validation.Error!);
        }

        var permissions = validation.Value.Permissions;
        var scopes = permissions.Grants
            .Select(permission => TryParseFacilityReadScope(permission.Value))
            .Where(scopeId => scopeId is not null)
            .Select(scopeId => scopeId!.Value)
            .Where(scopeId => permissions.Allows(FacilityPermissions.Read(scopeId)) &&
                              permissions.Allows(EquipmentPermissions.Read(scopeId)))
            .Distinct()
            .OrderBy(scopeId => scopeId.Value)
            .ToArray();
        return Result.Success<IReadOnlyList<FacilityScopeId>>(scopes);
    }

    public async Task<Result<IReadOnlyList<RegistryScopePayload>>> ReadScopesAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken = default)
    {
        var scopes = ReadableScopes(session);
        if (scopes.IsFailure)
        {
            return Result.Failure<IReadOnlyList<RegistryScopePayload>>(scopes.Error!);
        }

        var items = new List<RegistryScopePayload>(scopes.Value.Count);
        foreach (var scopeId in scopes.Value)
        {
            var context = await ReadContextAsync(session, scopeId, cancellationToken).ConfigureAwait(false);
            if (context.IsFailure)
            {
                return Result.Failure<IReadOnlyList<RegistryScopePayload>>(context.Error!);
            }

            var roots = context.Value.Graph.Locations.Where(item => item.PhysicalParentId is null).ToArray();
            var label = roots.Length == 1 ? roots[0].Name : scopeId.Value.ToString("D");
            items.Add(new RegistryScopePayload(
                scopeId.Value,
                label,
                context.Value.Graph.Locations.Count,
                context.Value.Registry.Equipment.Count));
        }

        return Result.Success<IReadOnlyList<RegistryScopePayload>>(items);
    }

    public async Task<Result<LocationListPayload>> ReadLocationsAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        string query,
        CancellationToken cancellationToken = default)
    {
        var context = await ReadContextAsync(session, scopeId, cancellationToken).ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<LocationListPayload>(context.Error!);
        }

        var normalized = query.Trim();
        var summaries = context.Value.Graph.Locations
            .Where(item => Matches(item.Code, item.Name, normalized))
            .Select(item => ToLocationSummary(item, context.Value))
            .OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LocationId)
            .ToArray();
        return Result.Success(new LocationListPayload(scopeId.Value, summaries.Length, summaries));
    }

    public async Task<Result<LocationDetailPayload>> ReadLocationAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        LocationId locationId,
        CancellationToken cancellationToken = default)
    {
        var context = await ReadContextAsync(session, scopeId, cancellationToken).ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<LocationDetailPayload>(context.Error!);
        }

        var location = context.Value.Graph.Locations.SingleOrDefault(item => item.LocationId == locationId);
        if (location is null)
        {
            return Failure<LocationDetailPayload>("registry.location_not_found", "The location was not found in this scope.");
        }

        var byId = context.Value.Graph.Locations.ToDictionary(item => item.LocationId);
        var incoming = context.Value.Graph.FunctionalRelations
            .Where(relation => relation.TargetLocationId == locationId)
            .Select(relation => ToRelation(byId[relation.SourceLocationId]))
            .ToArray();
        var outgoing = context.Value.Graph.FunctionalRelations
            .Where(relation => relation.SourceLocationId == locationId)
            .Select(relation => ToRelation(byId[relation.TargetLocationId]))
            .ToArray();
        return Result.Success(new LocationDetailPayload(
            scopeId.Value,
            ToLocationSummary(location, context.Value),
            context.Value.Graph.Locations
                .Where(item => item.PhysicalParentId == locationId)
                .Select(item => ToLocationSummary(item, context.Value))
                .ToArray(),
            incoming,
            outgoing,
            EquipmentAt(locationId, context.Value)));
    }

    public async Task<Result<LocationPlanContextPayload>> ReadPlanContextAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        LocationId locationId,
        CancellationToken cancellationToken = default)
    {
        var detail = await ReadLocationAsync(session, scopeId, locationId, cancellationToken).ConfigureAwait(false);
        if (detail.IsFailure)
        {
            return Result.Failure<LocationPlanContextPayload>(detail.Error!);
        }

        var graph = await facilities.ReadGraphAsync(session, scopeId, cancellationToken).ConfigureAwait(false);
        if (graph.IsFailure)
        {
            return Result.Failure<LocationPlanContextPayload>(graph.Error!);
        }

        var byId = graph.Value.Locations.ToDictionary(item => item.LocationId);
        var ancestors = new List<LocationRelationPayload>();
        var parentId = byId[locationId].PhysicalParentId;
        while (parentId is not null)
        {
            var parent = byId[parentId.Value];
            ancestors.Add(ToRelation(parent));
            parentId = parent.PhysicalParentId;
        }

        ancestors.Reverse();
        return Result.Success(new LocationPlanContextPayload(
            scopeId.Value,
            detail.Value.Location,
            ancestors,
            detail.Value.Children,
            detail.Value.Equipment));
    }

    public async Task<Result<EquipmentListPayload>> ReadEquipmentAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        string query,
        CancellationToken cancellationToken = default)
    {
        var context = await ReadContextAsync(session, scopeId, cancellationToken).ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<EquipmentListPayload>(context.Error!);
        }

        var normalized = query.Trim();
        var items = context.Value.Registry.Equipment
            .Where(item => Matches(item.Code, item.Name, normalized))
            .Select(item => ToEquipmentSummary(item, context.Value.Registry))
            .OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.EquipmentId)
            .ToArray();
        return Result.Success(new EquipmentListPayload(scopeId.Value, items.Length, items));
    }

    public async Task<Result<EquipmentDetailPayload>> ReadEquipmentAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        EquipmentId equipmentId,
        CancellationToken cancellationToken = default)
    {
        var context = await ReadContextAsync(session, scopeId, cancellationToken).ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<EquipmentDetailPayload>(context.Error!);
        }

        var item = context.Value.Registry.Equipment.SingleOrDefault(value => value.EquipmentId == equipmentId);
        if (item is null)
        {
            return Failure<EquipmentDetailPayload>("registry.equipment_not_found", "The equipment was not found in this scope.");
        }

        var location = context.Value.Graph.Locations.Single(value => value.LocationId == item.LocationId);
        var definitions = context.Value.Registry.Points
            .Where(point => point.EquipmentId == equipmentId)
            .OrderBy(point => point.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requestedPoints = definitions.Select(point => point.PointId.Value).ToHashSet();
        var current = runtime.ReadSnapshot(session, RuntimeScopeId.From(scopeId.Value), requestedPoints);
        var currentByPoint = current.IsSuccess
            ? current.Value.Payload.Points.ToDictionary(point => point.PointId)
            : new Dictionary<Guid, RuntimePointPayload>();
        var points = definitions.Select(definition => new EquipmentPointPayload(
            definition.PointId.Value,
            definition.Code,
            definition.Name,
            definition.Unit.Symbol,
            currentByPoint.TryGetValue(definition.PointId.Value, out var value)
                ? ToCurrent(value)
                : null)).ToArray();
        var currentStatus = current.IsFailure
            ? "Unavailable"
            : points.All(point => point.Current is null)
                ? "NoSamples"
                : points.Any(point => point.Current?.Freshness == Freshness.Stale.ToString())
                    ? "Stale"
                    : points.Any(point => point.Current is not null && point.Current.Quality != DataQuality.Good.ToString())
                        ? "Degraded"
                        : "Fresh";

        return Result.Success(new EquipmentDetailPayload(
            scopeId.Value,
            ToEquipmentSummary(item, context.Value.Registry),
            ToRelation(location),
            currentStatus,
            "Unknown",
            "No protocol connection evidence is available.",
            points));
    }

    private async Task<Result<RegistryContext>> ReadContextAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken)
    {
        var authorization = AuthorizeScope(session, scopeId);
        if (authorization.IsFailure)
        {
            return Result.Failure<RegistryContext>(authorization.Error!);
        }

        var graphTask = facilities.ReadGraphAsync(session, scopeId, cancellationToken);
        var registryTask = equipment.ReadRegistryAsync(session, scopeId, cancellationToken);
        await Task.WhenAll(graphTask, registryTask).ConfigureAwait(false);
        var graph = await graphTask.ConfigureAwait(false);
        var registry = await registryTask.ConfigureAwait(false);
        if (graph.IsFailure || registry.IsFailure)
        {
            return Result.Failure<RegistryContext>((graph.IsFailure ? graph.Error : registry.Error)!);
        }

        return Result.Success(new RegistryContext(graph.Value, registry.Value));
    }

    private Result AuthorizeScope(SessionSnapshot? session, FacilityScopeId scopeId)
    {
        var facility = SessionAuthorization.AuthorizeAccess(session, FacilityPermissions.Read(scopeId), clock);
        if (facility.IsFailure)
        {
            return Result.Failure(facility.Error!);
        }

        var registry = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Read(scopeId), clock);
        return registry.IsFailure ? Result.Failure(registry.Error!) : Result.Success();
    }

    private static FacilityScopeId? TryParseFacilityReadScope(string permission)
    {
        if (!permission.StartsWith(FacilityReadPrefix, StringComparison.Ordinal) ||
            !permission.EndsWith(ReadSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var value = permission[FacilityReadPrefix.Length..^ReadSuffix.Length];
        return value.Length == 32 && Guid.TryParseExact(value, "N", out var scopeId)
            ? FacilityScopeId.From(scopeId)
            : null;
    }

    private static bool Matches(string code, string name, string query) =>
        query.Length == 0 ||
        code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        name.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static LocationSummaryPayload ToLocationSummary(LocationSnapshot item, RegistryContext context) => new(
        item.LocationId.Value,
        item.PhysicalParentId?.Value,
        item.Code,
        item.Name,
        context.Graph.Locations.Count(child => child.PhysicalParentId == item.LocationId),
        context.Registry.Equipment.Count(value => value.LocationId == item.LocationId));

    private static EquipmentSummaryPayload ToEquipmentSummary(
        EquipmentSnapshot item,
        EquipmentRegistry registry) => new(
        item.EquipmentId.Value,
        item.LocationId.Value,
        item.Code,
        item.Name,
        registry.Points.Count(point => point.EquipmentId == item.EquipmentId));

    private static EquipmentSummaryPayload[] EquipmentAt(LocationId locationId, RegistryContext context) =>
        context.Registry.Equipment
            .Where(item => item.LocationId == locationId)
            .Select(item => ToEquipmentSummary(item, context.Registry))
            .ToArray();

    private static LocationRelationPayload ToRelation(LocationSnapshot item) =>
        new(item.LocationId.Value, item.Code, item.Name);

    private static EquipmentCurrentPayload? ToCurrent(RuntimePointPayload value) =>
        value.SourceTimestamp is null || value.ReceiveTimestamp is null || value.ProcessedTimestamp is null
            ? null
            : new EquipmentCurrentPayload(
                value.Value,
                value.Unit,
                value.Quality,
                value.Freshness,
                value.SourceTimestamp.Value,
                value.ReceiveTimestamp.Value,
                value.ProcessedTimestamp.Value,
                value.Quality == DataQuality.Good.ToString() && value.Freshness == Freshness.Fresh.ToString());

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record RegistryContext(FacilityGraph Graph, EquipmentRegistry Registry);
}
