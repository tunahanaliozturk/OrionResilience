namespace Moongazing.OrionResilience.Diagnostics;

using System.Diagnostics;
using System.Diagnostics.Metrics;

using Moongazing.Orion.Abstractions.Diagnostics;

/// <summary>
/// OpenTelemetry instrumentation for a <see cref="ResiliencePipeline"/>. Built on the Orion family's
/// <see cref="OrionInstrumentation"/> spine, so it shares the family's naming and static-tag
/// conventions: a <see cref="Meter"/> and <see cref="ActivitySource"/> named
/// <c>Moongazing.OrionResilience</c> (subscribe by that name) carrying the attempt counter
/// <c>orion.resilience.attempts</c>, the backoff histogram <c>orion.resilience.retry.delay</c>
/// (milliseconds), and the terminal-outcome counter <c>orion.resilience.outcome</c> tagged with a
/// frozen <see cref="OrionTelemetry.Outcomes"/> value. Multi-tenant / multi-region labels configured
/// through <see cref="OrionInstrumentation.SetStaticTags"/> are stamped onto every measurement.
/// <para>
/// A process-wide <see cref="Shared"/> instance makes telemetry emit by default, so a pipeline
/// constructed without wiring still reports. A host that wants a DI-managed lifetime can construct
/// its own instance and hand it to the pipeline; dispose it to release the meter.
/// </para>
/// </summary>
public sealed class ResilienceDiagnostics : OrionInstrumentation
{
    /// <summary>The meter / activity-source name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionResilience";

    /// <summary>The activity name of the span covering one <c>ExecuteAsync</c> call.</summary>
    public const string ExecuteActivityName = "OrionResilience.execute";

    private static readonly Lazy<ResilienceDiagnostics> SharedInstance =
        new(static () => new ResilienceDiagnostics());

    /// <summary>Create the meter and its instruments.</summary>
    public ResilienceDiagnostics()
        : base(OrionTelemetry.ScopeName("OrionResilience"), MeterVersion.Value)
    {
        Attempts = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("resilience", "attempts"),
            unit: "{attempt}",
            description: "Individual operation attempts executed, across all pipelines.");

        RetryDelay = Meter.CreateHistogram<double>(
            OrionTelemetry.MetricName("resilience", "retry.delay"),
            unit: "ms",
            description: "The backoff delay waited before each retry, in milliseconds, tagged with the 1-based attempt.");

        Outcome = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("resilience", "outcome"),
            unit: "{execution}",
            description: "Completed executions, tagged outcome (success/failure/timeout/cancelled).");
    }

    /// <summary>The process-wide default instance, so telemetry emits without explicit wiring.</summary>
    public static ResilienceDiagnostics Shared => SharedInstance.Value;

    /// <summary>Counts individual operation attempts (one per try, including the first).</summary>
    public Counter<long> Attempts { get; }

    /// <summary>Records the backoff delay (ms) waited before each retry.</summary>
    public Histogram<double> RetryDelay { get; }

    /// <summary>Counts completed executions, tagged with their terminal outcome.</summary>
    public Counter<long> Outcome { get; }

    /// <summary>Start the span covering one execution, or null when nothing is listening.</summary>
    /// <returns>The started <see cref="Activity"/>, or null.</returns>
    public Activity? StartExecute() =>
        ActivitySource.StartActivity(ExecuteActivityName, ActivityKind.Internal);

    /// <summary>Record one attempt.</summary>
    public void RecordAttempt() => Attempts.Add(1, StaticTags);

    /// <summary>Record the backoff delay waited before a retry, tagged with the attempt that just failed.</summary>
    /// <param name="delay">The delay waited.</param>
    /// <param name="attempt">The 1-based number of the attempt that failed and triggered the wait.</param>
    public void RecordRetryDelay(TimeSpan delay, int attempt) =>
        RetryDelay.Record(delay.TotalMilliseconds, Tag(new KeyValuePair<string, object?>(OrionTelemetry.Tags.Attempt, attempt)));

    /// <summary>Record the terminal outcome of an execution.</summary>
    /// <param name="outcome">One of <see cref="OrionTelemetry.Outcomes"/>.</param>
    public void RecordOutcome(string outcome) =>
        Outcome.Add(1, Tag(new KeyValuePair<string, object?>(OrionTelemetry.Tags.Outcome, outcome)));
}
