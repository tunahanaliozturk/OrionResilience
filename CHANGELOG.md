<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionResilience are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.0] - 2026-07-29

The first release — the Orion family's Wave 1 resilience foundation: retry and timeout, executed on
the shared clock so tests fast-forward, with OpenTelemetry by default.

### Added

- **`ResiliencePipeline`** — executes an operation with retries, backoff, and an overall timeout,
  all measured on an `OrionClock`. Every backoff `Task.Delay` and the timeout
  `CancellationTokenSource` are created through the clock's `TimeProvider`, so under
  `FakeOrionClock` a whole retry sequence fast-forwards deterministically — a 4-retry pipeline with a
  5-second cap completes in-test in microseconds, not seconds. A caller-driven
  `OperationCanceledException` always propagates unchanged and is never reclassified as a timeout.
- **`Backoff`** presets — `Exponential` (configurable factor), `DecorrelatedJitter` (overflow-safe,
  with an injectable sampler so jitter is deterministic in tests), and `Constant`, each with an
  optional cap.
- **`ResiliencePipelineOptions`** — `MaxRetries` (total attempts are `1 + MaxRetries`), `Backoff`,
  `Timeout` (defaults to no timeout), and declared retryability via `RetryOn<TException>()` /
  `RetryOn(predicate)`, OR-composed; the default retries every exception until you narrow it.
- **Typed failures** — `TimeoutRejectedException` (carries the elapsed budget) and
  `RetriesExhaustedException` (carries the attempt count, wraps the last fault as `InnerException`).
- **OpenTelemetry by default** — `ResilienceDiagnostics`, built on the family's
  `OrionInstrumentation` spine: a `Moongazing.OrionResilience` meter/activity-source carrying
  `orion.resilience.attempts` (counter), `orion.resilience.retry.delay` (histogram, ms, tagged with
  the attempt) and `orion.resilience.outcome` (counter, tagged with a frozen
  `success`/`failure`/`timeout`/`cancelled` outcome), plus an execution span. Emits by default
  through a shared instance; hand the pipeline a `ResilienceDiagnostics` you own for DI-managed
  lifetime or per-instance scoping.
- Binds to `Orion.Abstractions` 1.2.0 (`OrionDeadline`, telemetry spine) and `OrionClock` 0.9.0.
- Multi-targets `net8.0`/`net9.0`/`net10.0`; `IsAotCompatible`; a NativeAOT publish smoke test in
  CI that exercises the presets and the retry / timeout / exhaustion paths and runs the native binary
  with `-warnaserror`.
