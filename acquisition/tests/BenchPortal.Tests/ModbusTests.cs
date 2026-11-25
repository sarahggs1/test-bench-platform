using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BenchPortal.Api.Modbus;

namespace BenchPortal.Tests;

/// <summary>Modbus client tests against a small in-process Modbus TCP server.</summary>
public sealed class ModbusTests : IAsyncLifetime
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ushort[] _registers = new ushort[14];
    private readonly CancellationTokenSource _cts = new();
    private Task? _server;

    public Task InitializeAsync()
    {
        _listener.Start();
        _registers[10] = 2;      // state = fault
        _registers[11] = 0b10;   // oil pressure low
        _server = ServeAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _server!; } catch { /* listener stopped */ }
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task ServeAsync(CancellationToken ct)
    {
        using var client = await _listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        var header = new byte[7];
        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, ct);
            var pdu = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
            await stream.ReadExactlyAsync(pdu, ct);
            var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
            var arg = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));

            byte[] reply;
            if (pdu[0] == 0x03 && address + arg <= _registers.Length)
            {
                reply = new byte[2 + 2 * arg];
                reply[0] = 0x03;
                reply[1] = (byte)(2 * arg);
                for (var i = 0; i < arg; i++) BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2 + 2 * i), _registers[address + i]);
            }
            else if (pdu[0] == 0x06 && address <= 2)
            {
                _registers[address] = arg;
                reply = pdu;
            }
            else
            {
                reply = [(byte)(pdu[0] | 0x80), 0x02];
            }

            var response = new byte[7 + reply.Length];
            header.AsSpan(0, 4).CopyTo(response);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(reply.Length + 1));
            response[6] = header[6];
            reply.CopyTo(response, 7);
            await stream.WriteAsync(response, ct);
        }
    }

    [Fact]
    public async Task Writes_setpoints_and_reads_status()
    {
        await using var modbus = new ModbusTcpClient("127.0.0.1", Port);
        var bench = new BenchControl(modbus);

        await bench.ApplySetpointsAsync(3200, 120);
        await bench.StartAsync();
        Assert.Equal(new ushort[] { 3200, 120, 1 }, _registers[..3]);

        var status = await bench.ReadStatusAsync();
        Assert.True(status.Connected);
        Assert.Equal(BenchState.Fault, status.State);
        Assert.Equal(["Oil pressure low"], status.AlarmNames);
        Assert.Equal(3200, status.SpeedSetpoint);
    }

    [Fact]
    public async Task Reports_modbus_exceptions()
    {
        await using var modbus = new ModbusTcpClient("127.0.0.1", Port);
        var ex = await Assert.ThrowsAsync<ModbusException>(() => modbus.WriteSingleRegisterAsync(10, 1));
        Assert.Equal(0x02, ex.Code); // illegal data address: read-only register
    }

    [Fact]
    public async Task Status_reports_disconnected_bench_instead_of_throwing()
    {
        await using var modbus = new ModbusTcpClient("127.0.0.1", 1, timeout: TimeSpan.FromMilliseconds(300));
        var status = await new BenchControl(modbus).ReadStatusAsync();
        Assert.False(status.Connected);
        Assert.NotNull(status.Error);
    }
}
