using System.Text.Json;
using System.Text.Json.Serialization;

namespace BenchPortal.Api.Can;

public sealed record SignalDefinition(
    string Name,
    [property: JsonPropertyName("start_byte")] int StartByte,
    int Length,
    bool Signed,
    double Factor,
    double Offset,
    string Unit);

public sealed record MessageDefinition(
    uint Id,
    string Name,
    [property: JsonPropertyName("period_ms")] int PeriodMs,
    IReadOnlyList<SignalDefinition> Signals);

/// <summary>
/// CAN signal database (DBC-like), loaded from config/can_signals.json, shared with the C++ simulator.
/// </summary>
public sealed class SignalDatabase
{
    private readonly Dictionary<uint, MessageDefinition> _messages;

    public SignalDatabase(IEnumerable<MessageDefinition> messages)
    {
        _messages = messages.ToDictionary(m => m.Id);
    }

    public IEnumerable<MessageDefinition> Messages => _messages.Values;

    public IEnumerable<SignalDefinition> Signals => _messages.Values.SelectMany(m => m.Signals);

    public static SignalDatabase Load(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var messages = doc.RootElement.GetProperty("messages").Deserialize<List<MessageDefinition>>(options)
                       ?? throw new InvalidDataException("No messages in signal database");
        return new SignalDatabase(messages);
    }

    /// <summary>Decode every signal of a frame. Returns an empty dictionary for an unknown identifier.</summary>
    public IReadOnlyDictionary<string, double> Decode(CanFrame frame)
    {
        if (!_messages.TryGetValue(frame.Id, out var message))
        {
            return new Dictionary<string, double>();
        }
        return message.Signals.ToDictionary(s => s.Name, s => DecodeSignal(frame.Data, s));
    }

    public static double DecodeSignal(ReadOnlySpan<byte> data, SignalDefinition s)
    {
        ulong bits = 0;
        for (var i = 0; i < s.Length; i++)
        {
            bits |= (ulong)data[s.StartByte + i] << (8 * i);
        }
        long raw = (long)bits;
        var width = 8 * s.Length;
        if (s.Signed && (bits & (1UL << (width - 1))) != 0)
        {
            raw -= 1L << width; // two's complement
        }
        return raw * s.Factor + s.Offset;
    }
}
