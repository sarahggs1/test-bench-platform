using System.Text.Json;
using BenchPortal.Api.Data;

namespace BenchPortal.Api.Acquisition;

public sealed record Limit(double? Min, double? Max);

public sealed record LimitSet(double IgnoreFirstS, IReadOnlyDictionary<string, Limit> Limits)
{
    public static LimitSet Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var limits = new Dictionary<string, Limit>();
        foreach (var p in root.GetProperty("limits").EnumerateObject())
        {
            double? Get(string name) => p.Value.TryGetProperty(name, out var v) ? v.GetDouble() : null;
            limits[p.Name] = new Limit(Get("min"), Get("max"));
        }
        return new LimitSet(root.GetProperty("ignore_first_s").GetDouble(), limits);
    }
}

public sealed record SignalSummary(string Signal, int Count, double Min, double Max, double Mean);

public sealed record LimitCheck(string Signal, double? Min, double? Max, double Observed, bool Passed, double? FirstViolationS);

public sealed record RunSummary(string EndReason, IReadOnlyList<SignalSummary> Signals, IReadOnlyList<LimitCheck> Checks, IReadOnlyList<string> Alarms);

/// <summary>Post-run analysis: statistics per signal and acceptance checks. Pure functions, unit tested.</summary>
public static class RunAnalysis
{
    public static RunSummary Analyse(IReadOnlyList<Measurement> samples, LimitSet limits, string endReason, IReadOnlyList<string> alarms)
    {
        var bySignal = samples.GroupBy(m => m.Signal).ToDictionary(g => g.Key, g => g.OrderBy(m => m.TimeS).ToList());

        var summaries = bySignal
            .Select(kv => new SignalSummary(kv.Key, kv.Value.Count, kv.Value.Min(m => m.Value), kv.Value.Max(m => m.Value), kv.Value.Average(m => m.Value)))
            .OrderBy(s => s.Signal)
            .ToList();

        var checks = new List<LimitCheck>();
        foreach (var (signal, limit) in limits.Limits)
        {
            if (!bySignal.TryGetValue(signal, out var values))
            {
                continue;
            }
            var window = values.Where(m => m.TimeS >= limits.IgnoreFirstS).ToList();
            if (window.Count == 0)
            {
                continue;
            }
            var violation = window.FirstOrDefault(m => (limit.Min is { } lo && m.Value < lo) || (limit.Max is { } hi && m.Value > hi));
            var observed = limit.Max is not null ? window.Max(m => m.Value) : window.Min(m => m.Value);
            checks.Add(new LimitCheck(signal, limit.Min, limit.Max, Math.Round(observed, 3), violation is null, violation?.TimeS));
        }

        return new RunSummary(endReason, summaries, checks, alarms);
    }

    /// <summary>A run passes when it completed its planned duration, raised no alarm and met every limit.</summary>
    public static string Verdict(RunSummary s) =>
        s.EndReason == "completed" && s.Alarms.Count == 0 && s.Checks.All(c => c.Passed) ? "pass" : "fail";
}
