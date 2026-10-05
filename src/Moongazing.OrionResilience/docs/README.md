# OrionResilience

Retry with backoff presets and an overall timeout for .NET, run on OrionClock: every backoff wait and the timeout use the clock's `TimeProvider`, so tests fast-forward a whole retry sequence with a fake clock and no real waiting. OpenTelemetry by default.

![ExecuteAsync: each failed attempt is checked for caller cancellation, the timeout, ShouldRetry and the remaining retries; a retryable fault waits Backoff.Next on the clock and runs the next attempt](https://raw.githubusercontent.com/tunahanaliozturk/OrionResilience/master/docs/diagrams/execute-retry.png)

## Install

    dotnet add package OrionResilience

Targets `net8.0`, `net9.0` and `net10.0`. Depends on `Orion.Abstractions` and `OrionClock`.

## Quick start

```csharp
using Moongazing.OrionClock;
using Moongazing.OrionResilience;

var clock = new OrionClock(); // in DI, resolve OrionClock (registered by AddOrionClock)

var pipeline = new ResiliencePipeline(clock, new ResiliencePipelineOptions
{
    MaxRetries = 4,                       // 1 initial attempt + 4 retries
    Backoff = Backoff.DecorrelatedJitter(
        baseDelay: TimeSpan.FromMilliseconds(200),
        cap: TimeSpan.FromSeconds(5)),
    Timeout = TimeSpan.FromSeconds(10),   // overall budget for all attempts
}.RetryOn<HttpRequestException>()
 .RetryOn<TimeoutException>());

HttpResponseMessage response = await pipeline.ExecuteAsync(
    async ct => await httpClient.SendAsync(request, ct),
    cancellationToken);
```

`ExecuteAsync(Func<CancellationToken, Task>)` covers operations without a result.

## Options

`ResiliencePipelineOptions`:

| Option | Default | Meaning |
|--------|---------|---------|
| `MaxRetries` | `0` | Retries after the first attempt; total attempts are `1 + MaxRetries`. |
| `Backoff` | `Backoff.Exponential(200 ms, cap: 5 s)` | The delay before each retry (factor 2). |
| `Timeout` | `Timeout.InfiniteTimeSpan` | Overall budget for all attempts and their waits. |
| `ShouldRetry` | every exception | The first `RetryOn<TException>()` / `RetryOn(predicate)` replaces the default; later calls OR onto it. |

Backoff presets: `Backoff.Exponential(baseDelay, factor = 2.0, cap = null)`, `Backoff.DecorrelatedJitter(baseDelay, cap, sampler = null)` (pass a sampler for deterministic jitter in tests) and `Backoff.Constant(delay, cap = null)`.

## Failure semantics

- A caller-driven `OperationCanceledException` propagates unchanged; it is never reported as a timeout.
- `TimeoutRejectedException` (with the `Timeout` budget) when the budget elapses during an attempt or a backoff wait.
- An exception `ShouldRetry` rejects is rethrown unchanged.
- `RetriesExhaustedException` (with `Attempts`, last fault as `InnerException`) when a retryable fault has no retry or timeout budget left.

## Testing

Pass a `FakeOrionClock` (package `OrionClock.Testing`) and call `Advance`: the backoff waits and the timeout fire with no real waiting.

## Telemetry and AOT

- Meter and activity source `Moongazing.OrionResilience`: counters `orion.resilience.attempts` and `orion.resilience.outcome` (tagged `orion.outcome`), histogram `orion.resilience.retry.delay` (ms), and an `OrionResilience.execute` span per call.
- Emits through `ResilienceDiagnostics.Shared` by default; pass your own `ResilienceDiagnostics` to the pipeline constructor to scope it.
- AOT- and trim-compatible (`IsAotCompatible`); CI publishes a NativeAOT smoke test.

## Related packages

- `OrionClock` - the clock every wait and timeout runs on; `OrionClock.Testing` has `FakeOrionClock` for tests.
- `Orion.Abstractions` - the shared contracts spine (`OrionDeadline`, telemetry); `Orion.Abstractions.Testing` has `DeterministicFaultInjector`.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionResilience
- Changelog: https://github.com/tunahanaliozturk/OrionResilience/blob/master/CHANGELOG.md
- License: MIT
