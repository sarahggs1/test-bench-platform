namespace BenchPortal.Api.Data;

public sealed record Bench(long Id, string Code, string Name, string Description);

/// <summary>Software configuration of a bench. Lifecycle: draft -> validated -> deployed.</summary>
public sealed record BenchConfiguration(
    long Id,
    long BenchId,
    string Version,
    string Description,
    string Status,
    string CreatedAt,
    string? DeployedAt);

public sealed record TestDefinition(
    long Id,
    string Name,
    long BenchId,
    long? ConfigurationId,
    string PlannedDate,
    int SpeedRpm,
    int LoadNm,
    int DurationS,
    string Status,
    string Notes);

public sealed record TestRun(
    long Id,
    long TestId,
    string StartedAt,
    string? EndedAt,
    string Status,
    int Samples,
    string? Verdict,
    string? SummaryJson);

public sealed record ReleaseNote(long Id, string Version, string Date, string Component, string Notes);

public sealed record Measurement(double TimeS, string Signal, double Value);

public sealed record NewTest(string Name, long BenchId, long? ConfigurationId, string PlannedDate, int SpeedRpm, int LoadNm, int DurationS, string? Notes);

public sealed record NewConfiguration(long BenchId, string Version, string Description);

public sealed record NewReleaseNote(string Version, string Component, string Notes);

public static class ConfigurationStatus
{
    public const string Draft = "draft";
    public const string Validated = "validated";
    public const string Deployed = "deployed";

    /// <summary>Allowed transitions: a configuration must be validated before it is deployed.</summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (Draft, Validated) => true,
        (Validated, Deployed) => true,
        (Validated, Draft) => true,
        _ => false,
    };
}
