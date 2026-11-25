namespace BenchPortal.Api.Can;

/// <summary>
/// A classic CAN frame. On the network it travels in a 13-byte UDP datagram:
/// 4 bytes identifier (little-endian), 1 byte DLC, 8 data bytes.
/// </summary>
public sealed record CanFrame(uint Id, byte Dlc, byte[] Data)
{
    public const int UdpSize = 13;

    public static bool TryParseUdp(ReadOnlySpan<byte> bytes, out CanFrame? frame)
    {
        frame = null;
        if (bytes.Length != UdpSize || bytes[4] > 8)
        {
            return false;
        }
        var id = (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24);
        frame = new CanFrame(id, bytes[4], bytes.Slice(5, 8).ToArray());
        return true;
    }

    public byte[] ToUdp()
    {
        var bytes = new byte[UdpSize];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), Id);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes, 0, 4);
        }
        bytes[4] = Dlc;
        Data.CopyTo(bytes, 5);
        return bytes;
    }
}
