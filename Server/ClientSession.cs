using System.Net.Sockets;
using System.Threading.Channels;

namespace XivBoneServer;

public sealed class ClientSession
{
    public ulong ClientId;
    public uint Map, World;
    public float X, Y, Z;
    public volatile bool HasPos;
    public long LastSeenTicks;
    public readonly string Peer;
    public CellKey Cell;
    public bool InGrid;
    public readonly TcpClient Tcp;
    public readonly CancellationTokenSource Cts = new();
    public byte[]? LastKeyFrame;
    public long KeyVersion;
    public readonly Dictionary<ulong, long> KnownKeys = new();   // nur unter _gridLock benutzen

    // Pro Client eine Sende-Queue: ein langsamer Empfänger bremst niemanden, älteste Frames fallen weg
    public readonly Channel<byte[]> Outbox = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public ClientSession(TcpClient tcp, string peer) { Tcp = tcp; Peer = peer; }

    public void Close()
    {
        try { Cts.Cancel(); } catch { }
        Outbox.Writer.TryComplete();
        try { Tcp.Close(); } catch { }
    }
}