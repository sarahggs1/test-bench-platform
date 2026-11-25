using BenchPortal.Api.Acquisition;
using BenchPortal.Api.Can;
using BenchPortal.Api.Data;
using BenchPortal.Api.Modbus;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var configDir = Path.GetFullPath(cfg.GetValue("Bench:ConfigDir", Path.Combine(AppContext.BaseDirectory, "config"))!);

builder.Services.AddSingleton(SignalDatabase.Load(Path.Combine(configDir, "can_signals.json")));
builder.Services.AddSingleton(LimitSet.Load(Path.Combine(configDir, "limits.json")));
builder.Services.AddSingleton(new Database(cfg.GetValue("Bench:Database", "Data Source=bench_portal.db")!));
builder.Services.AddSingleton<LiveValues>();
builder.Services.AddSingleton(new ModbusTcpClient(cfg.GetValue("Bench:ModbusHost", "127.0.0.1")!, cfg.GetValue("Bench:ModbusPort", 1502)));
builder.Services.AddSingleton<IBenchControl, BenchControl>();
builder.Services.AddSingleton<AcquisitionService>();
builder.Services.AddHostedService<CanUdpReceiver>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles(); // the Angular portal build is served from wwwroot

var api = app.MapGroup("/api");

// Business errors become 400/404/409 responses with a readable message.
static IResult Guard(Func<IResult> action)
{
    try { return action(); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Microsoft.Data.Sqlite.SqliteException ex) { return Results.BadRequest(new { error = ex.Message }); }
}

// ---- bench, live values and signals
api.MapGet("/benches", (Database db) => db.Benches());
api.MapGet("/bench/status", async (IBenchControl bench, LiveValues live, AcquisitionService acq) =>
    new { modbus = await bench.ReadStatusAsync(), canReceiving = live.IsReceiving, canFrames = live.FrameCount, activeRun = acq.Active });
api.MapPost("/bench/reset", async (IBenchControl bench) => { await bench.ResetFaultAsync(); return Results.NoContent(); });
api.MapGet("/live", (LiveValues live) => live.Snapshot());
api.MapGet("/signals", (SignalDatabase s) => s.Messages);
api.MapGet("/limits", (LimitSet l) => l);

// ---- configurations (draft -> validated -> deployed)
api.MapGet("/configurations", (Database db) => db.Configurations());
api.MapPost("/configurations", (Database db, NewConfiguration body) =>
    Guard(() => Results.Created($"/api/configurations", db.AddConfiguration(body))));
api.MapPost("/configurations/{id:long}/status", (Database db, long id, StatusChange body) =>
    Guard(() => Results.Ok(db.SetConfigurationStatus(id, body.Status))));

// ---- tests and runs
api.MapGet("/tests", (Database db) => db.Tests());
api.MapPost("/tests", (Database db, NewTest body) =>
{
    if (string.IsNullOrWhiteSpace(body.Name) || body.DurationS is < 1 or > 3600 || body.SpeedRpm is < 0 or > 7000 || body.LoadNm is < 0 or > 400)
    {
        return Results.BadRequest(new { error = "Invalid test: name required, duration 1-3600 s, speed 0-7000 rpm, load 0-400 N.m" });
    }
    return Guard(() => Results.Created("/api/tests", db.AddTest(body)));
});
api.MapPost("/tests/{id:long}/start", async (AcquisitionService acq, long id) =>
{
    try { return Results.Ok(await acq.StartAsync(id)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapGet("/runs", (Database db, long? testId) => db.Runs(testId));
api.MapGet("/runs/{id:long}", (Database db, long id) => db.Run(id) is { } run ? Results.Ok(run) : Results.NotFound());
api.MapPost("/runs/{id:long}/stop", (AcquisitionService acq, long id) =>
    acq.RequestStop(id) ? Results.Accepted() : Results.Conflict(new { error = "This run is not in progress" }));
api.MapGet("/runs/{id:long}/measurements", (Database db, long id, string signal, int? maxPoints) =>
    db.Measurements(id, signal, maxPoints ?? 2000));

// ---- release notes
api.MapGet("/release-notes", (Database db) => db.ReleaseNotes());
api.MapPost("/release-notes", (Database db, NewReleaseNote body) => Results.Created("/api/release-notes", db.AddReleaseNote(body)));

app.MapFallbackToFile("index.html");
app.Run();

public sealed record StatusChange(string Status);

public partial class Program;
