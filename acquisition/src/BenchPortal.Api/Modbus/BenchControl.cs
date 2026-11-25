namespace BenchPortal.Api.Modbus;

public enum BenchState : ushort
{
    Stopped = 0,
    Running = 1,
    Fault = 2,
}

public sealed record BenchStatus(
    bool Connected,
    BenchState State,
    ushort Alarms,
    IReadOnlyList<string> AlarmNames,
    ushort SpeedSetpoint,
    ushort LoadSetpoint,
    ushort RunTimeS,
    ushort SpeedRpm,
    string? Error = null);

/// <summary>Bench commands and status over Modbus (register map documented in docs/interfaces.md).</summary>
public interface IBenchControl
{
    Task ApplySetpointsAsync(ushort speedRpm, ushort loadNm, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task ResetFaultAsync(CancellationToken ct = default);
    Task<BenchStatus> ReadStatusAsync(CancellationToken ct = default);
}

public sealed class BenchControl(ModbusTcpClient modbus) : IBenchControl
{
    public const ushort RegSpeedSetpoint = 0;
    public const ushort RegLoadSetpoint = 1;
    public const ushort RegCommand = 2;
    public const ushort RegState = 10;

    private static readonly (ushort Bit, string Name)[] AlarmBits =
    [
        (1 << 0, "Coolant over-temperature"),
        (1 << 1, "Oil pressure low"),
    ];

    public async Task ApplySetpointsAsync(ushort speedRpm, ushort loadNm, CancellationToken ct = default)
    {
        await modbus.WriteSingleRegisterAsync(RegSpeedSetpoint, speedRpm, ct);
        await modbus.WriteSingleRegisterAsync(RegLoadSetpoint, loadNm, ct);
    }

    public Task StartAsync(CancellationToken ct = default) => modbus.WriteSingleRegisterAsync(RegCommand, 1, ct);

    public Task StopAsync(CancellationToken ct = default) => modbus.WriteSingleRegisterAsync(RegCommand, 0, ct);

    public Task ResetFaultAsync(CancellationToken ct = default) => modbus.WriteSingleRegisterAsync(RegCommand, 2, ct);

    public async Task<BenchStatus> ReadStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var setpoints = await modbus.ReadHoldingRegistersAsync(0, 2, ct);
            var status = await modbus.ReadHoldingRegistersAsync(RegState, 4, ct);
            var alarms = status[1];
            return new BenchStatus(
                true,
                (BenchState)status[0],
                alarms,
                AlarmBits.Where(a => (alarms & a.Bit) != 0).Select(a => a.Name).ToList(),
                setpoints[0],
                setpoints[1],
                status[2],
                status[3]);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return new BenchStatus(false, BenchState.Stopped, 0, [], 0, 0, 0, 0, ex.Message);
        }
    }
}
