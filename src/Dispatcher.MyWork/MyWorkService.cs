using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.MyWork;

public sealed class MyWorkService
{
    private readonly MyWorkStore store;
    private readonly IWallClock clock;

    public MyWorkService(MyWorkStore store, IWallClock clock)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<Result<WorkAssignmentProjection>> AcceptSourceAssignmentAsync(
        WorkAssignmentProjection projection, CancellationToken cancellationToken = default) =>
        store.AcceptAsync(projection, cancellationToken);

    public Task<Result> RebuildOwnerAsync(
        string sourceOwner, IReadOnlyCollection<WorkAssignmentProjection> projections,
        CancellationToken cancellationToken = default) =>
        store.RebuildOwnerAsync(sourceOwner, projections, cancellationToken);

    public async Task<Result<IReadOnlyList<WorkAssignmentProjection>>> ReadAsync(
        MyWorkUserContext? context, CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            return Failure<IReadOnlyList<WorkAssignmentProjection>>(
                "permission.denied", "A person context is required for My Work.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(context.Session, MyWorkPermissions.Read, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<IReadOnlyList<WorkAssignmentProjection>>(authorization.Error!);
        }

        var projections = await store.ReadForPersonAsync(context.PersonId, cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<WorkAssignmentProjection>>(projections
            .Where(item => item.RequiredPermissions.All(context.Session.Permissions.Allows))
            .ToArray());
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
