// NativeAOT smoke test. Publishing this with PublishAot=true must produce zero trim/AOT warnings,
// and running it must exit 0 - that pair is OrionResilience's AOT exit criterion. Assertions are
// runtime checks, not a test framework, so the point is to prove these paths survive trimming.
using Moongazing.OrionClock;
using Moongazing.OrionResilience;
using Moongazing.OrionResilience.Diagnostics;

// Backoff presets compute the shapes they advertise.
var exponential = Backoff.Exponential(TimeSpan.FromMilliseconds(200), cap: TimeSpan.FromSeconds(5));
Check(exponential.Next(1, TimeSpan.Zero) == TimeSpan.FromMilliseconds(200), "exponential first delay wrong");
Check(exponential.Next(5, TimeSpan.Zero) <= TimeSpan.FromSeconds(5), "exponential cap not honoured");
Check(Backoff.Constant(TimeSpan.FromMilliseconds(50)).Next(3, TimeSpan.Zero) == TimeSpan.FromMilliseconds(50), "constant delay wrong");
var jittered = Backoff.DecorrelatedJitter(TimeSpan.FromMilliseconds(20), cap: TimeSpan.FromSeconds(1), sampler: static () => 0.5);
Check(jittered.Next(1, TimeSpan.Zero) <= TimeSpan.FromSeconds(1), "jitter cap not honoured");

// The telemetry surface constructs and names its instruments.
using var diagnostics = new ResilienceDiagnostics();
Check(diagnostics.Meter.Name == ResilienceDiagnostics.MeterName, "meter name wrong");
Check(diagnostics.RetryDelay is not null && diagnostics.Attempts is not null && diagnostics.Outcome is not null, "instruments missing");

var clock = new OrionClock();

// The retry path: fail twice with a tiny constant backoff, then succeed.
var retryPipeline = new ResiliencePipeline(clock, new ResiliencePipelineOptions
{
    MaxRetries = 3,
    Backoff = Backoff.Constant(TimeSpan.FromMilliseconds(1)),
}, diagnostics);

var attempts = 0;
var value = await retryPipeline.ExecuteAsync(async ct =>
{
    await Task.Yield();
    if (++attempts < 3)
    {
        throw new InvalidOperationException("transient");
    }
    return 42;
});
Check(value == 42 && attempts == 3, "retry path did not recover as expected");

// Retries exhausted surfaces the typed exception wrapping the last fault.
var exhausting = new ResiliencePipeline(clock, new ResiliencePipelineOptions
{
    MaxRetries = 1,
    Backoff = Backoff.Constant(TimeSpan.FromMilliseconds(1)),
}, diagnostics);
try
{
    await exhausting.ExecuteAsync<int>(_ => throw new InvalidOperationException("always"));
    Check(false, "exhausted retries did not throw");
}
catch (RetriesExhaustedException ex)
{
    Check(ex.Attempts == 2 && ex.InnerException is InvalidOperationException, "RetriesExhaustedException shape wrong");
}

// The timeout path: an operation slower than the budget is rejected as a timeout.
var timeoutPipeline = new ResiliencePipeline(clock, new ResiliencePipelineOptions
{
    MaxRetries = 0,
    Timeout = TimeSpan.FromMilliseconds(20),
}, diagnostics);
try
{
    await timeoutPipeline.ExecuteAsync(async ct => await Task.Delay(TimeSpan.FromSeconds(30), clock, ct));
    Check(false, "timeout path did not throw");
}
catch (TimeoutRejectedException ex)
{
    Check(ex.Timeout == TimeSpan.FromMilliseconds(20), "TimeoutRejectedException budget wrong");
}

Console.WriteLine("OrionResilience AOT smoke test passed.");
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine($"AOT smoke test failed: {message}");
        Environment.Exit(1);
    }
}
