using System.Diagnostics;
using System.Text.Json;
using BenchPortal.Api.Can;
using BenchPortal.Api.Data;
using BenchPortal.Api.Modbus;

namespace BenchPortal.Api.Acquisition;

public sealed record ActiveRun(long RunId, long TestId, double ElapsedS, int DurationS, int SamplesRecorded);

/// <summary>
/// Runs a test on the bench: applies setpoints over Modbus, starts the bench, records the live CAN values
/// at a fixed rate, supervises the bench state, stops at the end of the planned duration (or on fault or
/// operator stop), then analyses the run and stores the verdict.
/// </summary>
public sealed class AcquisitionService(Database db, IBenchControl bench, LiveValues live, LimitSet limits, ILogger<AcquisitionService> log)
{
    public TimeSpan SamplePeriod { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan SupervisionPeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private RunContext? _current;

    private sealed class RunContext(long runId, TestDefinition test)
    {
        public long RunId { get; } = runId;
        public TestDefinition Test { get; } = test;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public CancellationTokenSource StopRequested { get; } = new();
        public List<Measurement> Samples { get; } = [];
        public Task? Loop { get; set; }
    }

    public ActiveRun? Active
    {
        get
        {
            lock (_gate)
            {
                return _current is null
                    ? null
                    : new ActiveRun(_current.RunId, _current.Test.Id, Math.Round(_current.Clock.Elapsed.TotalSeconds, 1), _current.Test.DurationS, _current.Samples.Count);
            }
        }
    }

    public async Task<TestRun> StartAsync(long testId, CancellationToken ct = default)
    {
        var test = db.Test(testId) ?? throw new KeyNotFoundException($"Test {testId} not found");
        if (test.ConfigurationId is { } configId && db.Configuration(configId)?.Status != ConfigurationStatus.Deployed)
        {
            throw new InvalidOperationException("The test's configuration is not deployed on the bench: validate and deploy it first.");
        }

        var status = await bench.ReadStatusAsync(ct);
        if (!status.Connected)
        {
            throw new InvalidOperationException($"Bench not reachable over Modbus: {status.Error}");
        }
        if (status.State == BenchState.Fault)
        {
            throw new InvalidOperationException("The bench is in fault: reset it before starting a test.");
        }

        RunContext ctx;
        lock (_gate)
        {
            if (_current is not null)
            {
                throw new InvalidOperationException($"Run {_current.RunId} is already in progress.");
            }
            ctx = _current = new RunContext(db.StartRun(testId).Id, test);
        }

        try
        {
            await bench.ApplySetpointsAsync((ushort)test.SpeedRpm, (ushort)test.LoadNm, ct);
            await bench.StartAsync(ct);
        }
        catch
        {
            lock (_gate)
            {
                db.FinishRun(ctx.RunId, "error", "fail", "{}");
                _current = null;
            }
            throw;
        }

        db.SetTestStatus(testId, "running");
        ctx.Loop = Task.Run(() => RecordAsync(ctx));
        log.LogInformation("Run {Run} started for test {Test}", ctx.RunId, testId);
        return db.Run(ctx.RunId)!;
    }

    public bool RequestStop(long runId)
    {
        lock (_gate)
        {
            if (_current?.RunId != runId)
            {
                return false;
            }
            _current.StopRequested.Cancel();
            return true;
        }
    }

    /// <summary>Wait for the active run (if any) to finish. Used by tests and on shutdown.</summary>
    public Task WaitAsync()
    {
        lock (_gate)
        {
            return _current?.Loop ?? Task.CompletedTask;
        }
    }

    private async Task RecordAsync(RunContext run)
    {
        var endReason = "completed";
        var alarms = new List<string>();
        var flushed = 0;
        var nextSupervision = TimeSpan.Zero;

        try
        {
            while (true)
            {
                var t = run.Clock.Elapsed;
                if (t.TotalSeconds >= run.Test.DurationS)
                {
                    break;
                }
                if (run.StopRequested.IsCancellationRequested)
                {
                    endReason = "stopped";
                    break;
                }

                var now = Math.Round(t.TotalSeconds, 3);
                foreach (var (signal, value) in live.Snapshot())
                {
                    run.Samples.Add(new Measurement(now, signal, value));
                }

                if (t >= nextSupervision)
                {
                    nextSupervision = t + SupervisionPeriod;
                    var status = await bench.ReadStatusAsync();
                    if (status.State == BenchState.Fault)
                    {
                        endReason = "fault";
                        alarms.AddRange(status.AlarmNames);
                        break;
                    }
                    if (!status.Connected)
                    {
                        endReason = "connection_lost";
                        break;
                    }
                    // Write the samples recorded so far, in one transaction.
                    db.InsertMeasurements(run.RunId, run.Samples.Skip(flushed).ToList());
                    flushed = run.Samples.Count;
                }

                try
                {
                    await Task.Delay(SamplePeriod, run.StopRequested.Token);
                }
                catch (OperationCanceledException)
                {
                    // stop requested: handled at the top of the loop
                }
            }

            if (endReason != "fault")
            {
                await bench.StopAsync();
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Run {Run} aborted", run.RunId);
            endReason = "error";
        }
        finally
        {
            db.InsertMeasurements(run.RunId, run.Samples.Skip(flushed).ToList());
            var summary = RunAnalysis.Analyse(run.Samples, limits, endReason, alarms);
            var verdict = RunAnalysis.Verdict(summary);
            db.FinishRun(run.RunId, endReason, verdict, JsonSerializer.Serialize(summary, JsonOptions));
            db.SetTestStatus(run.Test.Id, verdict == "pass" ? "done" : "failed");
            log.LogInformation("Run {Run} ended ({Reason}), verdict {Verdict}", run.RunId, endReason, verdict);
            lock (_gate)
            {
                _current = null;
            }
        }
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
