namespace Moongazing.OrionResilience;

using System;

/// <summary>
/// The configuration for a <see cref="ResiliencePipeline"/>: how many retries, the backoff between
/// them, which exceptions count as retryable, and an overall timeout budget. Wave 1 covers retry and
/// timeout; the circuit-breaker and hedging knobs arrive in Wave 2.
/// </summary>
public sealed class ResiliencePipelineOptions
{
    private static readonly Func<Exception, bool> DefaultRetryEverything = static _ => true;

    private Func<Exception, bool> shouldRetry = DefaultRetryEverything;

    /// <summary>
    /// The number of retries after the first attempt (so total attempts are <c>1 + MaxRetries</c>).
    /// Zero disables retrying. Defaults to 0.
    /// </summary>
    public int MaxRetries { get; set; }

    /// <summary>The delay between retries. Defaults to exponential backoff (200ms base, 5s cap).</summary>
    public Backoff Backoff { get; set; } = Backoff.Exponential(TimeSpan.FromMilliseconds(200), cap: TimeSpan.FromSeconds(5));

    /// <summary>
    /// The overall time budget for the whole execution (all attempts and their backoff). Defaults to
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> (no timeout). When it elapses, execution
    /// stops with a <see cref="TimeoutRejectedException"/>.
    /// </summary>
    public TimeSpan Timeout { get; set; } = System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>
    /// The retryability predicate: return true for an exception that a retry might recover from.
    /// Defaults to retrying every exception. Set it directly, or build it with
    /// <see cref="RetryOn{TException}"/> / <see cref="RetryOn(Func{Exception, bool})"/>.
    /// </summary>
    public Func<Exception, bool> ShouldRetry
    {
        get => shouldRetry;
        set => shouldRetry = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Retry only <typeparamref name="TException"/> (and its subtypes). Composable: calling it more
    /// than once retries any of the listed types. Replaces the default "retry everything".
    /// </summary>
    /// <typeparam name="TException">The retryable exception type.</typeparam>
    /// <returns>This options instance, for chaining.</returns>
    public ResiliencePipelineOptions RetryOn<TException>()
        where TException : Exception
    {
        var previous = RetriesAreOpen() ? null : shouldRetry;
        shouldRetry = previous is null
            ? static ex => ex is TException
            : ex => ex is TException || previous(ex);
        return this;
    }

    /// <summary>Retry an exception when <paramref name="predicate"/> returns true (OR-composed with prior <c>RetryOn</c> calls).</summary>
    /// <param name="predicate">The retryability predicate.</param>
    /// <returns>This options instance, for chaining.</returns>
    public ResiliencePipelineOptions RetryOn(Func<Exception, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var previous = RetriesAreOpen() ? null : shouldRetry;
        shouldRetry = previous is null ? predicate : ex => predicate(ex) || previous(ex);
        return this;
    }

    // The default predicate retries everything; the first RetryOn replaces it (rather than OR-ing
    // onto "always true", which would keep retrying everything). Detect that default by reference.
    private bool RetriesAreOpen() => ReferenceEquals(shouldRetry, DefaultRetryEverything);

    internal void Validate()
    {
        if (MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), MaxRetries, "MaxRetries cannot be negative.");
        }
        ArgumentNullException.ThrowIfNull(Backoff);
        if (Timeout <= TimeSpan.Zero && Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "Timeout must be positive or Timeout.InfiniteTimeSpan.");
        }
    }
}
