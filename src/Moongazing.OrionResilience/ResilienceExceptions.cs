namespace Moongazing.OrionResilience;

using System;

/// <summary>
/// Thrown when a retry strategy runs out of attempts. Carries how many attempts ran, with the last
/// failure as the <see cref="Exception.InnerException"/> so a caller can inspect the underlying fault.
/// <para>In Wave 2 the execute surface additionally returns an <c>OrionResult</c> whose error is a
/// typed <c>RetriesExhausted</c>, so callers can branch on data instead of catching.</para>
/// </summary>
public sealed class RetriesExhaustedException : Exception
{
    /// <summary>The total number of attempts made (the first try plus every retry).</summary>
    public int Attempts { get; }

    /// <summary>Create the exception after <paramref name="attempts"/> attempts, wrapping the last fault.</summary>
    /// <param name="attempts">The total attempts made.</param>
    /// <param name="lastException">The exception the final attempt threw.</param>
    public RetriesExhaustedException(int attempts, Exception lastException)
        : base($"The operation still failed after {attempts} attempt(s).", lastException)
        => Attempts = attempts;
}

/// <summary>
/// Thrown when an operation exceeds its configured timeout budget. Distinct from a caller-driven
/// <see cref="OperationCanceledException"/>, which always propagates unchanged.
/// <para>In Wave 2 the execute surface additionally returns an <c>OrionResult</c> whose error is a
/// typed <c>Timeout</c>.</para>
/// </summary>
public sealed class TimeoutRejectedException : Exception
{
    /// <summary>The timeout budget that was exceeded.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Create the exception for the <paramref name="timeout"/> that elapsed.</summary>
    /// <param name="timeout">The elapsed timeout budget.</param>
    public TimeoutRejectedException(TimeSpan timeout)
        : base($"The operation exceeded its {timeout.TotalMilliseconds:0}ms timeout.")
        => Timeout = timeout;
}
