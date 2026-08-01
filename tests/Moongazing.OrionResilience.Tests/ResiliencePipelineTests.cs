namespace Moongazing.OrionResilience.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Moongazing.Orion.Abstractions.Testing;
using Moongazing.OrionClock.Testing;
using Moongazing.OrionResilience.Diagnostics;

using Xunit;

/// <summary>
/// The Wave 1 exit criteria and core behaviour: retries and their backoff run on a
/// <see cref="FakeOrionClock"/>, so a whole retry sequence fast-forwards in-test with no real waits;
/// retryability is honoured; and the timeout budget is enforced deterministically.
/// </summary>
public sealed class ResiliencePipelineTests
{
    // Exponential 200ms base under a 5s cap: the four waits a 4-retry run produces.
    private static readonly double[] ExpectedExponentialDelaysMs = [200.0, 400.0, 800.0, 1600.0];

    // Records the (retryNumber, delay) of every retry the engine performs, via the internal hook.
    private static (ResiliencePipeline Pipeline, List<TimeSpan> Delays) Build(FakeOrionClock clock, ResiliencePipelineOptions options)
    {
        var delays = new List<TimeSpan>();
        var pipeline = new ResiliencePipeline(clock, options, new ResilienceDiagnostics(), (_, delay) => delays.Add(delay));
        return (pipeline, delays);
    }

    [Fact]
    public async Task A_four_retry_pipeline_recovers_and_records_exactly_four_delays_under_a_fake_clock()
    {
        // Wave 1 exit criterion: a 4-retry pipeline with a 5s cap runs entirely under a fake clock
        // (no real delay) and records exactly four backoff waits.
        var clock = new FakeOrionClock();
        var options = new ResiliencePipelineOptions
        {
            MaxRetries = 4,
            Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(200), cap: TimeSpan.FromSeconds(5)),
        };
        var (pipeline, delays) = Build(clock, options);

        // Fails the first four attempts, succeeds on the fifth.
        var faults = DeterministicFaultInjector.FailFirst(4);

        var execution = pipeline.ExecuteAsync(async ct =>
        {
            faults.Next();          // throws on attempts 1..4
            await Task.CompletedTask;
            return 42;
        });

        // Drive every scheduled backoff to completion by advancing the fake clock, not by waiting.
        await PumpAsync(clock, execution);
        var result = await execution;

        Assert.Equal(42, result);
        Assert.Equal(5, faults.Attempts);        // 4 failures + 1 success
        Assert.Equal(4, delays.Count);           // exactly four backoff waits
        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromSeconds(5))); // capped
        // Exponential 200ms base: 200ms, 400ms, 800ms, 1600ms.
        Assert.Equal(ExpectedExponentialDelaysMs, delays.ConvertAll(d => d.TotalMilliseconds).ToArray());
    }

    [Fact]
    public async Task Exhausting_the_retries_throws_RetriesExhausted_wrapping_the_last_fault()
    {
        var clock = new FakeOrionClock();
        var (pipeline, delays) = Build(clock, new ResiliencePipelineOptions
        {
            MaxRetries = 3,
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(1)),
        });
        var faults = DeterministicFaultInjector.AlwaysFail(() => new InvalidOperationException("down"));

        var execution = pipeline.ExecuteAsync<int>(_ => { faults.Next(); return Task.FromResult(0); });
        await PumpAsync(clock, execution);

        var ex = await Assert.ThrowsAsync<RetriesExhaustedException>(() => execution);
        Assert.Equal(4, ex.Attempts);                 // 1 + 3 retries
        Assert.Equal(3, delays.Count);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task A_non_retryable_exception_is_surfaced_immediately_and_unchanged()
    {
        var clock = new FakeOrionClock();
        var (pipeline, delays) = Build(clock, new ResiliencePipelineOptions
        {
            MaxRetries = 5,
        }.RetryOn<TimeoutException>()); // only TimeoutException is retryable

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new InvalidOperationException("not retryable");
        }));

        Assert.Equal(1, attempts);   // no retry
        Assert.Empty(delays);
    }

    [Fact]
    public async Task RetryOn_retries_the_configured_exception_type()
    {
        var clock = new FakeOrionClock();
        var (pipeline, delays) = Build(clock, new ResiliencePipelineOptions
        {
            MaxRetries = 2,
            Backoff = Backoff.Constant(TimeSpan.FromMilliseconds(500)),
        }.RetryOn<TimeoutException>());

        var faults = DeterministicFaultInjector.FailFirst(1, () => new TimeoutException());
        var execution = pipeline.ExecuteAsync(async ct => { faults.Next(); await Task.CompletedTask; return "ok"; });
        await PumpAsync(clock, execution);

        Assert.Equal("ok", await execution);
        Assert.Single(delays);
    }

    [Fact]
    public async Task The_timeout_budget_is_enforced_on_the_fake_clock()
    {
        var clock = new FakeOrionClock();
        var (pipeline, _) = Build(clock, new ResiliencePipelineOptions
        {
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(2),
        });

        // An operation that never completes until its token is cancelled.
        var execution = pipeline.ExecuteAsync<int>(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, clock, ct);
            return 0;
        });

        // Advancing past the 2s budget fires the timeout timer on the fake clock.
        clock.Advance(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => execution);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_OperationCanceled_not_a_timeout()
    {
        var clock = new FakeOrionClock();
        var (pipeline, _) = Build(clock, new ResiliencePipelineOptions { MaxRetries = 3, Timeout = TimeSpan.FromMinutes(5) });
        using var cts = new CancellationTokenSource();

        var execution = pipeline.ExecuteAsync<int>(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, clock, ct);
            return 0;
        }, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    // Advance the fake clock in small steps until the execution completes, so any scheduled
    // Task.Delay timers fire without real waiting. Bounded so a bug can't hang the test forever.
    private static async Task PumpAsync(FakeOrionClock clock, Task execution)
    {
        for (var i = 0; i < 1000 && !execution.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Yield();
        }
    }
}
