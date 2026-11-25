using BenchPortal.Api.Can;

namespace BenchPortal.Tests;

public class CanTests
{
    private static readonly SignalDatabase Db = SignalDatabase.Load(TestPaths.Config("can_signals.json"));

    [Fact]
    public void Loads_the_shared_signal_database()
    {
        Assert.Equal(3, Db.Messages.Count());
        Assert.Contains(Db.Signals, s => s.Name == "oil_pressure_bar" && s.Factor == 0.01);
    }

    [Fact]
    public void Decodes_signed_little_endian_signals_like_the_simulator()
    {
        // Same vector as the C++ test: torque -250 N.m -> raw -2500 -> bytes 3C F6.
        var data = new byte[8];
        data[0] = 0xB8; data[1] = 0x0B;   // speed 3000 rpm
        data[2] = 0x3C; data[3] = 0xF6;   // torque -250.0 N.m
        data[4] = 0x50;                   // throttle 80 * 0.5 = 40 %
        data[5] = 1;                      // bench state running
        var values = Db.Decode(new CanFrame(0x100, 8, data));

        Assert.Equal(3000, values["speed_rpm"]);
        Assert.Equal(-250.0, values["torque_Nm"], 6);
        Assert.Equal(40.0, values["throttle_pct"], 6);
        Assert.Equal(1, values["bench_state"]);
    }

    [Fact]
    public void Ignores_unknown_identifiers()
    {
        Assert.Empty(Db.Decode(new CanFrame(0x7FF, 8, new byte[8])));
    }

    [Fact]
    public void Udp_format_round_trips_and_rejects_bad_sizes()
    {
        var frame = new CanFrame(0x200, 8, [1, 2, 3, 4, 5, 6, 7, 8]);
        var bytes = frame.ToUdp();
        Assert.Equal(13, bytes.Length);
        Assert.True(CanFrame.TryParseUdp(bytes, out var back));
        Assert.Equal(frame.Id, back!.Id);
        Assert.Equal(frame.Data, back.Data);
        Assert.False(CanFrame.TryParseUdp(bytes.AsSpan(0, 12), out _));
    }
}
