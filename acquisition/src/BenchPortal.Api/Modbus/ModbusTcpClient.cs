using System.Buffers.Binary;
using System.Net.Sockets;

namespace BenchPortal.Api.Modbus;

public sealed class ModbusException(byte function, byte code)
    : Exception($"Modbus exception {code} on function 0x{function:X2}")
{
    public byte Code { get; } = code;
}

/// <summary>Minimal Modbus TCP client: read holding registers (03) and write single register (06).</summary>
public sealed class ModbusTcpClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _tcp;
    private ushort _transaction;

    public ModbusTcpClient(string host, int port, byte unitId = 1, TimeSpan? timeout = null)
    {
        _host = host;
        _port = port;
        _unitId = unitId;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort count, CancellationToken ct = default)
    {
        var pdu = new byte[5];
        pdu[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        var response = await TransactAsync(pdu, ct);
        if (response.Length < 2 || response[1] != 2 * count)
        {
            throw new IOException("Unexpected Modbus response length");
        }
        var values = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + 2 * i));
        }
        return values;
    }

    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
    {
        var pdu = new byte[5];
        pdu[0] = 0x06;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        var response = await TransactAsync(pdu, ct);
        if (!response.AsSpan().SequenceEqual(pdu))
        {
            throw new IOException("Modbus write was not echoed");
        }
    }

    private async Task<byte[]> TransactAsync(byte[] pdu, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            try
            {
                var reused = _tcp is { Connected: true };
                try
                {
                    return await SendAsync(pdu, timeout.Token);
                }
                catch (Exception ex) when (reused && ex is IOException or SocketException)
                {
                    // The server closed an idle connection (e.g. bench controller restarted): reconnect once.
                    _tcp?.Dispose();
                    _tcp = null;
                    return await SendAsync(pdu, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // Drop the connection so the next request reconnects cleanly.
                _tcp?.Dispose();
                _tcp = null;
                if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"No Modbus response from {_host}:{_port}");
                }
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<byte[]> SendAsync(byte[] pdu, CancellationToken ct)
    {
        if (_tcp is not { Connected: true })
        {
            _tcp?.Dispose();
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(_host, _port, ct);
        }
        var stream = _tcp.GetStream();

        var id = ++_transaction;
        var request = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(pdu.Length + 1));
        request[6] = _unitId;
        pdu.CopyTo(request, 7);
        await stream.WriteAsync(request, ct);

        var header = new byte[7];
        await stream.ReadExactlyAsync(header, ct);
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != id)
        {
            throw new IOException("Modbus transaction identifier mismatch");
        }
        var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
        await stream.ReadExactlyAsync(body, ct);

        if ((body[0] & 0x80) != 0)
        {
            throw new ModbusException(pdu[0], body[1]);
        }
        return body;
    }

    public ValueTask DisposeAsync()
    {
        _tcp?.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
