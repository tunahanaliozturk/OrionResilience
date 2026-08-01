namespace Moongazing.OrionResilience;

using System;

/// <summary>
/// The delay a retry waits before its next attempt, as one of a few named presets. Every preset is
/// capped, so a backoff can never run away, and the jittered preset draws from an injectable sampler
/// so a test can make it deterministic. The retry engine threads the previous delay in, so stateful
/// forms (decorrelated jitter) work without the preset holding per-execution state — one
/// <see cref="Backoff"/> instance is safe to share across concurrent executions.
/// </summary>
public sealed class Backoff
{
    // (retryNumber 1-based, previousDelay, sample in [0,1)) -> delay.
    private readonly Func<int, TimeSpan, double, TimeSpan> compute;
    private readonly Func<double> sampler;

    private Backoff(Func<int, TimeSpan, double, TimeSpan> compute, Func<double>? sampler)
    {
        this.compute = compute;
        this.sampler = sampler ?? Random.Shared.NextDouble;
    }

    /// <summary>
    /// The delay for retry number <paramref name="retryNumber"/> (1-based), given the
    /// <paramref name="previousDelay"/> the last retry waited (<see cref="TimeSpan.Zero"/> for the first).
    /// </summary>
    /// <param name="retryNumber">The 1-based retry number.</param>
    /// <param name="previousDelay">The delay the previous retry used.</param>
    /// <returns>The delay to wait, never negative.</returns>
    public TimeSpan Next(int retryNumber, TimeSpan previousDelay)
    {
        var delay = compute(retryNumber, previousDelay, sampler());
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>A fixed <paramref name="delay"/> before every retry, optionally capped (a no-op cap for a constant).</summary>
    /// <param name="delay">The constant delay.</param>
    /// <param name="cap">An optional maximum; the delay is clamped to it.</param>
    public static Backoff Constant(TimeSpan delay, TimeSpan? cap = null)
    {
        RequireNonNegative(delay, nameof(delay));
        var ceiling = Ceiling(cap, nameof(cap), delay);
        return new Backoff((_, _, _) => Min(delay, ceiling), sampler: null);
    }

    /// <summary>
    /// Exponentially growing delay: <c>baseDelay * factor^(retryNumber - 1)</c>, clamped to
    /// <paramref name="cap"/> (default: no cap). The classic "back off harder each time" curve.
    /// </summary>
    /// <param name="baseDelay">The first retry's delay.</param>
    /// <param name="factor">The growth factor per retry; must be at least 1.</param>
    /// <param name="cap">The maximum delay; defaults to uncapped.</param>
    public static Backoff Exponential(TimeSpan baseDelay, double factor = 2.0, TimeSpan? cap = null)
    {
        RequireNonNegative(baseDelay, nameof(baseDelay));
        if (factor < 1.0 || double.IsNaN(factor))
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The growth factor must be at least 1.");
        }
        var ceiling = Ceiling(cap, nameof(cap), baseDelay);
        return new Backoff((retryNumber, _, _) =>
        {
            var scaled = baseDelay.Ticks * Math.Pow(factor, retryNumber - 1);
            var ticks = scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
            return Min(TimeSpan.FromTicks(ticks), ceiling);
        }, sampler: null);
    }

    /// <summary>
    /// Decorrelated jitter (the AWS "exponential backoff and jitter" recommendation): the next delay
    /// is a random value in <c>[baseDelay, previousDelay * 3]</c>, clamped to <paramref name="cap"/>.
    /// It spreads a fleet of retriers out so they do not resynchronise into a thundering herd.
    /// </summary>
    /// <param name="baseDelay">The floor each delay draws from.</param>
    /// <param name="cap">The maximum delay (required — jitter is unbounded without it).</param>
    /// <param name="sampler">
    /// An optional source of uniform samples in <c>[0,1)</c>; defaults to <see cref="Random.Shared"/>.
    /// Pass a deterministic sampler to make the jitter reproducible in a test.
    /// </param>
    public static Backoff DecorrelatedJitter(TimeSpan baseDelay, TimeSpan cap, Func<double>? sampler = null)
    {
        RequireNonNegative(baseDelay, nameof(baseDelay));
        if (cap < baseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(cap), cap, "The cap must be at least the base delay.");
        }
        return new Backoff((_, previousDelay, sample) =>
        {
            var lower = baseDelay;
            var prev = previousDelay <= TimeSpan.Zero ? baseDelay : previousDelay;
            var upperTicks = Math.Min(cap.Ticks, SaturatingTriple(prev.Ticks));
            var upper = TimeSpan.FromTicks(Math.Max(lower.Ticks, upperTicks));
            var span = upper - lower;
            var delay = lower + TimeSpan.FromTicks((long)(sample * span.Ticks));
            return Min(delay, cap);
        }, sampler);
    }

    private static long SaturatingTriple(long ticks)
        => ticks > long.MaxValue / 3 ? long.MaxValue : ticks * 3;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Ceiling(TimeSpan? cap, string paramName, TimeSpan floor)
    {
        if (cap is null)
        {
            return TimeSpan.MaxValue;
        }
        if (cap.Value < floor)
        {
            throw new ArgumentOutOfRangeException(paramName, cap.Value, "The cap must be at least the base delay.");
        }
        return cap.Value;
    }

    private static void RequireNonNegative(TimeSpan value, string paramName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "A delay cannot be negative.");
        }
    }
}
