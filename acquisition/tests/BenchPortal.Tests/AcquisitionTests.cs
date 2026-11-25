using System.Text.Json;
using BenchPortal.Api.Acquisition;
using BenchPortal.Api.Can;
using BenchPortal.Api.Data;
using BenchPortal.Api.Modbus;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenchPortal.Tests;

/// <summary>Bench double: records commands, can be switched to fault.</summary>
internal sealed class FakeBench : IBenchControl
{
    public List<string> Commands { get; } = [];
    public BenchState State { get; set; } = BenchState.Stopped;
    public bool Connected { get; set; } = true;

    public Task ApplySetpointsAsync(ushort speedRpm, ushort loadNm, CancellationToken ct = default)
    {
        Commands.Add($"setpoints {speedRpm} {loadNm}");
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct = default) { Commands.Add("start"); State = BenchState.Running; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct = default) { Commands.Add("stop"); State = BenchState.Stopped; return Task.CompletedTask; }
    public Task ResetFaultAsync(CancellationToken ct = default) { Commands.Add("reset"); State = BenchState.Stopped; return Task.CompletedTask; }

    public Task<BenchStatus> ReadStatusAsync(CancellationToken ct = default) => Task.FromResult(new BenchStatus(
        Connected, State, (ushort)(State == BenchState.Fault ? 2 : 0),
        State == BenchState.Fault ? ["Oil pressure low"] : [], 0, 0, 0, 0, Connected ? null : "offline"));
}

public class AcquisitionTests
{
    private static readonly LimitSet Limits = LimitSet.Load(TestPaths.Config("limits.json"));

    private static (Database, FakeBench, LiveValues, AcquisitionService) Setup()
    {
        var db = new Database(TestPaths.TempDatabase());
        var bench = new FakeBench();
        var live = new LiveValues();
        live.Update(new Dictionary<string, double> { ["speed_rpm"] = 3000, ["coolant_temp_C"] = 88, ["oil_pressure_bar"] = 3.2 });
        var acq = new AcquisitionService(db, bench, live, Limits, NullLogger<AcquisitionService>.Instance)
        {
            SamplePeriod = TimeSpan.FromMilliseconds(20),
            SupervisionPeriod = TimeSpan.FromMilliseconds(50),
        };
        return (db, bench, live, acq);
    }

    private static NewTest Test(int durationS = 1) => new("Endurance 3000 rpm", 1, 1, "2026-12-01", 3000, 150, durationS, null);

    [Fact]
    public async Task Completed_run_is_recorded_analysed_and_passes()
    {
        var (db, bench, _, acq) = Setup();
        var test = db.AddTest(Test() with { DurationS = 1 });

        var run = await acq.StartAsync(test.Id);
        await acq.WaitAsync();

        var done = db.Run(run.Id)!;
        Assert.Equal("completed", done.Status);
        Assert.True(done.Samples > 10);
        Assert.Equal(["setpoints 3000 150", "start", "stop"], bench.Commands);
        Assert.Equal(88, db.Measurements(run.Id, "coolant_temp_C").First().Value);
        Assert.Equal("done", db.Test(test.Id)!.Status);
        // Limits are ignored during the first 5 s (warm-up), so a 1 s run has no check and passes.
        Assert.Equal("pass", done.Verdict);
    }

    [Fact]
    public async Task Bench_fault_ends_the_run_and_fails_it()
    {
        var (db, bench, _, acq) = Setup();
        var test = db.AddTest(Test(durationS: 30));

        var run = await acq.StartAsync(test.Id);
        await Task.Delay(150);
        bench.State = BenchState.Fault;
        await acq.WaitAsync();

        var done = db.Run(run.Id)!;
        Assert.Equal("fault", done.Status);
        Assert.Equal("fail", done.Verdict);
        Assert.DoesNotContain("stop", bench.Commands); // the bench already tripped
        var summary = JsonSerializer.Deserialize<RunSummary>(done.SummaryJson!, AcquisitionService.JsonOptions)!;
        Assert.Equal(["Oil pressure low"], summary.Alarms);
    }

    [Fact]
    public async Task Operator_can_stop_a_run()
    {
        var (db, _, _, acq) = Setup();
        var run = await acq.StartAsync(db.AddTest(Test(durationS: 30)).Id);
        Assert.True(acq.RequestStop(run.Id));
        await acq.WaitAsync();
        Assert.Equal("stopped", db.Run(run.Id)!.Status);
        Assert.Equal("fail", db.Run(run.Id)!.Verdict); // planned duration not reached
    }

    [Fact]
    public async Task Refuses_to_start_on_a_configuration_that_is_not_deployed()
    {
        var (db, _, _, acq) = Setup();
        var draft = db.AddConfiguration(new NewConfiguration(1, "1.1.0-rc1", "New oil pressure filter"));
        var test = db.AddTest(Test() with { ConfigurationId = draft.Id });
        await Assert.ThrowsAsync<InvalidOperationException>(() => acq.StartAsync(test.Id));
    }

    [Fact]
    public async Task Refuses_to_start_twice_or_when_bench_is_offline()
    {
        var (db, bench, _, acq) = Setup();
        await acq.StartAsync(db.AddTest(Test(durationS: 30)).Id);
        var second = db.AddTest(Test());
        await Assert.ThrowsAsync<InvalidOperationException>(() => acq.StartAsync(second.Id));
        acq.RequestStop(acq.Active!.RunId);
        await acq.WaitAsync();

        bench.Connected = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => acq.StartAsync(second.Id));
    }
}

public class AnalysisAndDatabaseTests
{
    private static readonly LimitSet Limits = LimitSet.Load(TestPaths.Config("limits.json"));

    [Fact]
    public void Limit_violation_after_warm_up_fails_the_run()
    {
        var samples = Enumerable.Range(0, 100)
            .Select(i => new Measurement(i * 0.1, "coolant_temp_C", i < 80 ? 95 : 107)) // exceeds 105 from t = 8 s
            .ToList();
        var summary = RunAnalysis.Analyse(samples, Limits, "completed", []);
        var check = Assert.Single(summary.Checks);
        Assert.False(check.Passed);
        Assert.Equal(8.0, check.FirstViolationS!.Value, 6);
        Assert.Equal("fail", RunAnalysis.Verdict(summary));
    }

    [Fact]
    public void Configuration_must_be_validated_before_deployment()
    {
        var db = new Database(TestPaths.TempDatabase());
        var config = db.AddConfiguration(new NewConfiguration(1, "1.1.0", "Higher sampling rate"));
        Assert.Throws<InvalidOperationException>(() => db.SetConfigurationStatus(config.Id, ConfigurationStatus.Deployed));

        db.SetConfigurationStatus(config.Id, ConfigurationStatus.Validated);
        db.SetConfigurationStatus(config.Id, ConfigurationStatus.Deployed);

        var deployed = db.Configurations().Where(c => c.Status == ConfigurationStatus.Deployed).ToList();
        Assert.Equal("1.1.0", Assert.Single(deployed).Version); // the previous one was retired
    }

    [Fact]
    public void Measurements_are_decimated_for_display()
    {
        var db = new Database(TestPaths.TempDatabase());
        var test = db.AddTest(new NewTest("t", 1, null, "2026-12-01", 1000, 0, 10, null));
        var run = db.StartRun(test.Id);
        db.InsertMeasurements(run.Id, Enumerable.Range(0, 1000).Select(i => new Measurement(i * 0.01, "speed_rpm", i)));
        var points = db.Measurements(run.Id, "speed_rpm", maxPoints: 100);
        Assert.Equal(100, points.Count);
        Assert.Equal(0, points[0].Value);
    }
}
