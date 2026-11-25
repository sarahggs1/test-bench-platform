using System.Net;
using System.Net.Sockets;

namespace BenchPortal.Api.Can;

/// <summary>Receives CAN-over-UDP frames from the bench and keeps the live values up to date.</summary>
public sealed class CanUdpReceiver(SignalDatabase signals, LiveValues live, IConfiguration config, ILogger<CanUdpReceiver> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = config.GetValue("Bench:CanUdpPort", 20000);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        log.LogInformation("Listening for CAN frames on UDP port {Port}", port);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (CanFrame.TryParseUdp(datagram.Buffer, out var frame))
            {
                live.Update(signals.Decode(frame!));
            }
            else
            {
                log.LogWarning("Ignored malformed CAN datagram of {Length} bytes", datagram.Buffer.Length);
            }
        }
    }
}
