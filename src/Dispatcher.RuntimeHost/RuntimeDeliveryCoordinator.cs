using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public sealed record RuntimeDownstreamRetryPolicy
{
    public RuntimeDownstreamRetryPolicy(
        int maxAttempts,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            initialBackoff,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            maximumBackoff,
            TimeSpan.Zero);
        if (initialBackoff > maximumBackoff)
        {
            throw new ArgumentException(
                "Initial downstream retry backoff must not exceed maximum backoff.",
                nameof(initialBackoff));
        }

        MaxAttempts = maxAttempts;
        InitialBackoff = initialBackoff;
        MaximumBackoff = maximumBackoff;
    }

    public int MaxAttempts { get; }

    public TimeSpan InitialBackoff { get; }

    public TimeSpan MaximumBackoff { get; }
}

public delegate bool RuntimeDownstreamTransientFailureClassifier(
    Exception exception);

public delegate Task RuntimeDownstreamRetryDelay(
    TimeSpan delay,
    CancellationToken cancellationToken);

public sealed class RuntimeDeliveryCoordinator
{
    private readonly CoreRuntimeStore coreStore;
    private readonly RuntimeHistoryDeliveryProcessor history;
    private readonly RuntimeAlarmDeliveryProcessor alarm;
    private readonly RuntimeEventDeliveryProcessor events;
    private readonly RuntimeDefinitionBindingState definitionBinding;
    private readonly RuntimeDownstreamRetryPolicy retryPolicy;
    private readonly RuntimeDownstreamTransientFailureClassifier
        isTransientFailure;
    private readonly RuntimeDownstreamRetryDelay delay;

    public RuntimeDeliveryCoordinator(
        CoreRuntimeStore coreStore,
        RuntimeHistoryDeliveryProcessor history,
        RuntimeAlarmDeliveryProcessor alarm,
        RuntimeEventDeliveryProcessor events,
        Guid configurationRevisionId,
        RevisionNumber alarmDefinitionEpoch,
        RuntimeDownstreamRetryPolicy retryPolicy,
        RuntimeDownstreamTransientFailureClassifier isTransientFailure,
        RuntimeDownstreamRetryDelay? delay = null)
        : this(
            coreStore,
            history,
            alarm,
            events,
            new RuntimeDefinitionBindingState(configurationRevisionId, alarmDefinitionEpoch),
            retryPolicy,
            isTransientFailure,
            delay)
    {
    }

    public RuntimeDeliveryCoordinator(
        CoreRuntimeStore coreStore,
        RuntimeHistoryDeliveryProcessor history,
        RuntimeAlarmDeliveryProcessor alarm,
        RuntimeEventDeliveryProcessor events,
        RuntimeDefinitionBindingState definitionBinding,
        RuntimeDownstreamRetryPolicy retryPolicy,
        RuntimeDownstreamTransientFailureClassifier isTransientFailure,
        RuntimeDownstreamRetryDelay? delay = null)
    {
        ArgumentNullException.ThrowIfNull(coreStore);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(definitionBinding);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(isTransientFailure);
        this.coreStore = coreStore;
        this.history = history;
        this.alarm = alarm;
        this.events = events;
        this.definitionBinding = definitionBinding;
        this.retryPolicy = retryPolicy;
        this.isTransientFailure = isTransientFailure;
        this.delay = delay ?? Task.Delay;
    }

    public RuntimeDeliveryProcessor CreateDeliveryProcessor() =>
        new(ProcessAsync);

    public async Task<Result> ProcessAsync(
        RuntimeProcessingDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        var definitions = definitionBinding.Read();
        var bound = await coreStore.EnsureDeliveryDefinitionEpochsAsync(
            delivery.ScopeId,
            delivery.ObligationPosition,
            definitions.ConfigurationRevisionId,
            definitions.AlarmDefinitionEpoch,
            cancellationToken).ConfigureAwait(false);
        if (bound.IsFailure)
        {
            return bound;
        }

        var current = await ReloadAsync(
            delivery,
            cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure(current.Error!);
        }

        var historyResult = await RunStageAsync(
            current.Value,
            static async (processor, item, token) =>
                ToResult(await processor.ProcessAsync(item, token)
                    .ConfigureAwait(false)),
            history,
            cancellationToken).ConfigureAwait(false);
        if (historyResult.IsFailure)
        {
            return historyResult;
        }

        current = await ReloadAsync(
            delivery,
            cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure(current.Error!);
        }

        var alarmResult = await RunStageAsync(
            current.Value,
            static async (processor, item, token) =>
                ToResult(await processor.ProcessAsync(item, token)
                    .ConfigureAwait(false)),
            alarm,
            cancellationToken).ConfigureAwait(false);
        if (alarmResult.IsFailure)
        {
            return alarmResult;
        }

        current = await ReloadAsync(
            delivery,
            cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure(current.Error!);
        }

        var eventResult = await RunStageAsync(
            current.Value,
            static async (processor, item, token) =>
                ToResult(await processor.ProcessAsync(item, token)
                    .ConfigureAwait(false)),
            events,
            cancellationToken).ConfigureAwait(false);
        if (eventResult.IsFailure)
        {
            return eventResult;
        }

        current = await ReloadAsync(
            delivery,
            cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure(current.Error!);
        }

        return current.Value.HistoryState ==
                   RuntimeDownstreamDeliveryState.Completed &&
               current.Value.AlarmState ==
                   RuntimeDownstreamDeliveryState.Completed &&
               current.Value.EventState ==
                   RuntimeDownstreamDeliveryState.Completed
            ? Result.Success()
            : Failure(
                "runtime.delivery_pipeline_incomplete",
                "The downstream pipeline returned without completing every stage.");
    }

    private async Task<Result> RunStageAsync<TProcessor>(
        RuntimeProcessingDelivery delivery,
        Func<
            TProcessor,
            RuntimeProcessingDelivery,
            CancellationToken,
            Task<Result>> process,
        TProcessor processor,
        CancellationToken cancellationToken)
        where TProcessor : class
    {
        var current = delivery;
        var retryDelay = retryPolicy.InitialBackoff;
        for (var attempt = 1; attempt <= retryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                return await process(
                    processor,
                    current,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and
                not StackOverflowException &&
                isTransientFailure(exception) &&
                attempt < retryPolicy.MaxAttempts)
            {
                await delay(
                    retryDelay,
                    cancellationToken).ConfigureAwait(false);
                retryDelay = NextBackoff(retryDelay);
                var reloaded = await ReloadAsync(
                    delivery,
                    cancellationToken).ConfigureAwait(false);
                if (reloaded.IsFailure)
                {
                    return Result.Failure(reloaded.Error!);
                }

                current = reloaded.Value;
            }
        }

        throw new InvalidOperationException(
            "The bounded downstream retry loop exited unexpectedly.");
    }

    private async Task<Result<RuntimeProcessingDelivery>> ReloadAsync(
        RuntimeProcessingDelivery expected,
        CancellationToken cancellationToken)
    {
        var current = await coreStore.LoadPendingDeliveryAsync(
            expected.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<RuntimeProcessingDelivery>(
                "runtime.delivery_missing",
                "The pending processing delivery disappeared during downstream processing.");
        }

        if (current.ObligationPosition != expected.ObligationPosition)
        {
            return Failure<RuntimeProcessingDelivery>(
                "runtime.delivery_identity",
                "Another processing delivery replaced the expected downstream identity.");
        }

        return Result.Success(current);
    }

    private TimeSpan NextBackoff(TimeSpan current)
    {
        if (current >= retryPolicy.MaximumBackoff)
        {
            return retryPolicy.MaximumBackoff;
        }

        var doubledTicks =
            current.Ticks > retryPolicy.MaximumBackoff.Ticks / 2
                ? retryPolicy.MaximumBackoff.Ticks
                : checked(current.Ticks * 2);
        return TimeSpan.FromTicks(
            Math.Min(
                doubledTicks,
                retryPolicy.MaximumBackoff.Ticks));
    }

    private static Result ToResult<TValue>(Result<TValue> result) =>
        result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.Error!);

    private static Result Failure(
        string code,
        string message) =>
        Result.Failure(
            new OperationError(
                ErrorCode.From(code),
                message));

    private static Result<TValue> Failure<TValue>(
        string code,
        string message) =>
        Result.Failure<TValue>(
            new OperationError(
                ErrorCode.From(code),
                message));
}
