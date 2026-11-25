using System.Collections.Concurrent;

namespace BenchPortal.Api.Can;

/// <summary>Latest decoded value of every signal, updated by the CAN receiver.</summary>
public sealed class LiveValues
{
    private readonly ConcurrentDictionary<string, double> _values = new();
    private long _lastUpdateTicks;
    private long _frameCount;

    public void Update(IReadOnlyDictionary<string, double> decoded)
    {
        foreach (var (name, value) in decoded)
        {
            _values[name] = value;
        }
        Interlocked.Exchange(ref _lastUpdateTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _frameCount);
    }

    public IReadOnlyDictionary<string, double> Snapshot() => new Dictionary<string, double>(_values);

    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>True when a frame arrived during the last second.</summary>
    public bool IsReceiving => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastUpdateTicks) < TimeSpan.TicksPerSecond;
}
