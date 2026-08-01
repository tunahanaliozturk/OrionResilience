namespace Moongazing.OrionResilience.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;

using Moongazing.Orion.Abstractions.Diagnostics;
using Moongazing.Orion.Abstractions.Testing;
using Moongazing.OrionClock.Testing;
using Moongazing.OrionResilience.Diagnostics;

using Xunit;

/// <summary>
/// The Wave 1 telemetry exit criterion: a 4-retry pipeline under a fake clock records exactly four
/// <c>orion.resilience.retry.delay</c> measurements (and the attempt / outcome instruments report
/// consistently), observed through a name-agnostic <see cref="MeterListener"/> scoped by reference to
/// one <see cref="ResilienceDiagnostics"/> instance so parallel tests never cross-count.
/// </summary>
public sealed class ResilienceTelemetryTests
{
    [Fact]
    public async Task A_four_retry_recovery_records_exactly_four_delay_measurements_and_a_single_success_outcome()
    {
        using var diagnostics = new ResilienceDiagnostics();
        var delays = new List<double>();
        var attempts = 0L;
        var outcomes = new Dictionary<string, long>(StringComparer.Ordinal);

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (OrionInstrumentation.ListensTo(instrument, diagnostics))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument == diagnostics.RetryDelay)
            {
                lock (delays) { delays.Add(value); }
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument == diagnostics.Attempts)
            {
                Interlocked.Add(ref attempts, value);
            }
            else if (instrument == diagnostics.Outcome)
            {
                var outcome = OutcomeOf(tags);
                lock (outcomes) { outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + value; }
            }
        });
        listener.Start();

        var clock = new FakeOrionClock();
        var pipeline = new ResiliencePipeline(clock, new ResiliencePipelineOptions
        {
            MaxRetries = 4,
            Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(200), cap: TimeSpan.FromSeconds(5)),
        }, diagnostics);

        var faults = DeterministicFaultInjector.FailFirst(4);
        var execution = pipeline.ExecuteAsync(async ct => { faults.Next(); await Task.CompletedTask; return 1; });
        for (var i = 0; i < 1000 && !execution.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Yield();
        }
        await execution;

        listener.RecordObservableInstruments();

        Assert.Equal(4, delays.Count);                       // exactly four backoff waits recorded
        Assert.Equal(5, Interlocked.Read(ref attempts));     // 4 failures + 1 success
        Assert.Equal(1, outcomes.GetValueOrDefault(OrionTelemetry.Outcomes.Success));
        Assert.Empty(FailureOutcomes(outcomes));
    }

    private static string OutcomeOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == OrionTelemetry.Tags.Outcome)
            {
                return tag.Value?.ToString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static IEnumerable<string> FailureOutcomes(Dictionary<string, long> outcomes)
    {
        foreach (var (outcome, count) in outcomes)
        {
            if (outcome != OrionTelemetry.Outcomes.Success && count > 0)
            {
                yield return outcome;
            }
        }
    }
}
