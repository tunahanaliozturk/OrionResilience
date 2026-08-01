namespace Moongazing.OrionResilience;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Moongazing.Orion.Abstractions.Diagnostics;
using Moongazing.Orion.Abstractions.Time;
using Moongazing.OrionClock;
using Moongazing.OrionResilience.Diagnostics;

/// <summary>
/// Executes an operation with retries, backoff, and an overall timeout, all measured on an
/// <see cref="OrionClock"/> — so every retry delay and the timeout run on the clock's
/// <see cref="TimeProvider"/> and a fake clock fast-forwards the whole thing in tests, no real waits.
/// Each execution emits the family's OpenTelemetry signals (see <see cref="ResilienceDiagnostics"/>).
/// Wave 1 covers retry + timeout; circuit-breaker and hedging land in Wave 2.
/// </summary>
public sealed class ResiliencePipeline
{
    private readonly OrionClock clock;
    private readonly int maxRetries;
    private readonly Backoff backoff;
    private readonly Func<Exception, bool> shouldRetry;
    private readonly TimeSpan timeout;
    private readonly ResilienceDiagnostics diagnostics;
    private readonly Action<int, TimeSpan>? onRetry;

    /// <summary>Create a pipeline that runs its delays and timeout on <paramref name="clock"/>.</summary>
    /// <param name="clock">The clock all delays and the timeout are measured on.</param>
    /// <param name="options">The retry / backoff / timeout configuration.</param>
    public ResiliencePipeline(OrionClock clock, ResiliencePipelineOptions options)
        : this(clock, options, ResilienceDiagnostics.Shared, onRetry: null)
    {
    }

    /// <summary>Create a pipeline reporting through a specific <paramref name="diagnostics"/> instance.</summary>
    /// <param name="clock">The clock all delays and the timeout are measured on.</param>
    /// <param name="options">The retry / backoff / timeout configuration.</param>
    /// <param name="diagnostics">The instrumentation the pipeline records to.</param>
    public ResiliencePipeline(OrionClock clock, ResiliencePipelineOptions options, ResilienceDiagnostics diagnostics)
        : this(clock, options, diagnostics, onRetry: null)
    {
    }

    // The onRetry hook is invoked once per retry with (retryNumber, delay) just before the wait; tests
    // observe retries through it without the engine depending on the test framework. Telemetry rides
    // the same points but goes through the diagnostics instance, not this hook.
    internal ResiliencePipeline(OrionClock clock, ResiliencePipelineOptions options, ResilienceDiagnostics diagnostics, Action<int, TimeSpan>? onRetry)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        options.Validate();
        this.clock = clock;
        maxRetries = options.MaxRetries;
        backoff = options.Backoff;
        shouldRetry = options.ShouldRetry;
        timeout = options.Timeout;
        this.diagnostics = diagnostics;
        this.onRetry = onRetry;
    }

    /// <summary>Execute a value-returning <paramref name="operation"/> through the pipeline.</summary>
    /// <typeparam name="T">The operation's result type.</typeparam>
    /// <param name="operation">The operation to run; it receives a token cancelled by the caller or the timeout.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The operation's result on success.</returns>
    /// <exception cref="TimeoutRejectedException">The timeout budget elapsed.</exception>
    /// <exception cref="RetriesExhaustedException">Every attempt failed with a retryable exception.</exception>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var activity = diagnostics.StartExecute();
        using var timeoutCts = timeout == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var deadline = timeout == Timeout.InfiniteTimeSpan ? OrionDeadline.Never : OrionDeadline.After(clock, timeout);
        var attempt = 0;
        var previousDelay = TimeSpan.Zero;

        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.RecordAttempt();
            try
            {
                var result = await operation(linked.Token).ConfigureAwait(false);
                Complete(activity, OrionTelemetry.Outcomes.Success, attempt);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Complete(activity, OrionTelemetry.Outcomes.Cancelled, attempt);
                throw; // caller-driven cancellation is never downgraded
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw TimedOut(activity, attempt);
            }
            catch (Exception ex)
            {
                var retryable = shouldRetry(ex);
                if (!retryable)
                {
                    Complete(activity, OrionTelemetry.Outcomes.Failure, attempt);
                    throw; // non-retryable: surface the original exception unchanged
                }
                if (attempt > maxRetries || deadline.IsExpired)
                {
                    Complete(activity, OrionTelemetry.Outcomes.Failure, attempt);
                    throw new RetriesExhaustedException(attempt, ex);
                }

                var delay = backoff.Next(attempt, previousDelay);
                previousDelay = delay;
                diagnostics.RecordRetryDelay(delay, attempt);
                onRetry?.Invoke(attempt, delay);

                try
                {
                    await Task.Delay(delay, clock, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Complete(activity, OrionTelemetry.Outcomes.Cancelled, attempt);
                    throw;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    throw TimedOut(activity, attempt);
                }
            }
        }
    }

    /// <summary>Execute a void <paramref name="operation"/> through the pipeline.</summary>
    /// <param name="operation">The operation to run; it receives a token cancelled by the caller or the timeout.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="TimeoutRejectedException">The timeout budget elapsed.</exception>
    /// <exception cref="RetriesExhaustedException">Every attempt failed with a retryable exception.</exception>
    public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return null;
        }, cancellationToken);
    }

    // Record the terminal outcome once, tagging the span with the outcome and final attempt count.
    private void Complete(Activity? activity, string outcome, int attempts)
    {
        diagnostics.RecordOutcome(outcome);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(OrionTelemetry.Tags.Outcome, outcome);
            activity.SetTag(OrionTelemetry.Tags.Attempt, attempts);
            activity.SetStatus(outcome == OrionTelemetry.Outcomes.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }
    }

    private TimeoutRejectedException TimedOut(Activity? activity, int attempts)
    {
        Complete(activity, OrionTelemetry.Outcomes.Timeout, attempts);
        return new TimeoutRejectedException(timeout);
    }
}
