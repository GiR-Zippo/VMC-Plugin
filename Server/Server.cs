using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace XivBoneServer;

public sealed class Server
{
    private const int MaxFrame = 2 * 1024 * 1024;
    private readonly ConcurrentDictionary<ulong, ClientSession> _clients = new();
    private readonly float _maxDistSq;
    private readonly TimeSpan _stale;
    private readonly bool _debug;
    private readonly Dictionary<CellKey, List<ClientSession>> _grid = new();
    private readonly object _gridLock = new();
    private readonly float _cellSize;

    public Server(float maxDistance, double staleSec, bool debug)
    {
        _maxDistSq = maxDistance <= 0 ? float.PositiveInfinity : maxDistance * maxDistance;
        _stale = TimeSpan.FromSeconds(staleSec);
        _debug = debug;
        _cellSize = maxDistance <= 0 ? float.PositiveInfinity : maxDistance;
    }

    public async Task RunAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"Listening on :{port}");
        _ = Task.Run(() => SweepLoop(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }

            tcp.NoDelay = true;
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            _ = HandleAsync(tcp, ct);
        }
        listener.Stop();
    }

    private async Task HandleAsync(TcpClient tcp, CancellationToken ct)
    {
        var peer = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        var session = new ClientSession(tcp, peer);
        ulong? registered = null;
        if (_debug) Console.WriteLine($"[CONN] + {peer}");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);
        var token = linked.Token;
        var stream = tcp.GetStream();
        var writerTask = WriteLoop(session, stream, token);
        var lenBuf = new byte[4];

        try
        {
            while (!token.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(lenBuf, token);
                var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                if (len < WireHeader.Size || len > MaxFrame)
                {
                    if (_debug) Console.WriteLine($"[DROP] {peer}: ungültige frame_len={len}");
                    break;
                }

                // Frame inkl. Längenpräfix aufbauen, damit er unverändert weitergeleitet werden kann
                var framed = new byte[4 + len];
                lenBuf.CopyTo(framed, 0);
                await stream.ReadExactlyAsync(framed.AsMemory(4, len), token);

                var frame = framed.AsSpan(4);
                if (!WireHeader.TryRead(frame, out var h)) continue;
                if (WireHeader.Size + h.PayloadLength > len) continue;

                // Client registrieren / aktualisieren
                if (registered != h.ClientId)
                {
                    if (registered is { } old) _clients.TryRemove(new KeyValuePair<ulong, ClientSession>(old, session));
                    registered = h.ClientId;
                    if (_clients.TryGetValue(h.ClientId, out var prev) && prev != session)
                        prev.Close(); // alte Verbindung derselben ClientId ersetzen
                    _clients[h.ClientId] = session;
                    session.ClientId = h.ClientId;
                }

                session.Map = h.Map;
                session.World = h.World;
                session.LastSeenTicks = Environment.TickCount64;

                if (h.Type is PacketType.FinalBones or PacketType.Heartbeat && h.PayloadLength >= 12)
                {
                    var p = frame[WireHeader.Size..];
                    session.X = BitConverter.ToSingle(p[0..4]);
                    session.Y = BitConverter.ToSingle(p[4..8]);
                    session.Z = BitConverter.ToSingle(p[8..12]);
                    session.HasPos = true;
                    
                    if (session.HasPos)
                        UpdateCell(session);
                }

                // Heartbeats dienen nur Position/Presence, werden nicht weitergeleitet
                if (h.Type == PacketType.FinalBones)
                    Broadcast(session, framed);
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (registered is { } id)
                _clients.TryRemove(new KeyValuePair<ulong, ClientSession>(id, session));
            RemoveFromGrid(session);
            session.Close();
            try 
            {
                await writerTask;
            }
            catch { }

            if (_debug)
                Console.WriteLine($"[CONN] - {peer} (cid={registered}, aktiv: {_clients.Count})");
        }
    }

    private void UpdateCell(ClientSession s)
    {
        if (!float.IsFinite(s.X) || !float.IsFinite(s.Z)) return;
        var key = new CellKey(s.Map, s.World,
            (int)MathF.Floor(s.X / _cellSize), (int)MathF.Floor(s.Z / _cellSize));

        if (s.InGrid && s.Cell == key) return; // Normalfall, ohne Lock

        lock (_gridLock)
        {
            RemoveLocked(s);
            if (!_grid.TryGetValue(key, out var list))
                _grid[key] = list = new List<ClientSession>();
            list.Add(s);
            s.Cell = key;
            s.InGrid = true;
        }
    }

    private void RemoveFromGrid(ClientSession s)
    {
        lock (_gridLock) RemoveLocked(s);
    }

    private void RemoveLocked(ClientSession s)
    {
        if (!s.InGrid) return;
        if (_grid.TryGetValue(s.Cell, out var list))
        {
            list.Remove(s);
            if (list.Count == 0) _grid.Remove(s.Cell);
        }
        s.InGrid = false;
    }

    private void Broadcast(ClientSession sender, byte[] framed)
    {
        if (!sender.InGrid) return;
        var c = sender.Cell;

        lock (_gridLock)
        {
            for (var ox = -1; ox <= 1; ox++)
                for (var oz = -1; oz <= 1; oz++)
                {
                    if (!_grid.TryGetValue(c with { Cx = c.Cx + ox, Cz = c.Cz + oz }, out var list))
                        continue;

                    foreach (var other in list)
                    {
                        if (ReferenceEquals(other, sender)) continue;
                        var dx = other.X - sender.X;
                        var dy = other.Y - sender.Y;
                        var dz = other.Z - sender.Z;
                        if (dx * dx + dy * dy + dz * dz > _maxDistSq) continue;
                        other.Outbox.Writer.TryWrite(framed);
                    }
                }
        }
    }

    private static async Task WriteLoop(ClientSession s, NetworkStream stream, CancellationToken token)
    {
        try
        {
            await foreach (var frame in s.Outbox.Reader.ReadAllAsync(token))
                await stream.WriteAsync(frame, token);
        }
        catch { s.Close(); }
    }

    private async Task SweepLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = Environment.TickCount64;
            foreach (var s in _clients.Values)
                if (now - s.LastSeenTicks > _stale.TotalMilliseconds)
                    s.Close(); // räumt sich über HandleAsync selbst auf
        }
    }
}