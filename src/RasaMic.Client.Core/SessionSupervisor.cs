using System.Diagnostics;
using RasaMic.Protocol;

namespace RasaMic.Client.Core;

/// <summary>
/// Keeps a client session alive: Ping/Pong watchdog, then the smart-reconnect
/// ladder with the pinned key. Remembers each successful endpoint in the store.
/// </summary>
public sealed class SessionSupervisor : IDisposable
{
    private readonly KnownServerStore _store;
    private readonly string _clientName;
    private readonly long _features;

    public SessionSupervisor(KnownServerStore store, string clientName, long features = 0)
    {
        _store = store;
        _clientName = clientName;
        _features = features;
    }

    public ClientSession? Session { get; private set; }
    public KnownServer? Known { get; private set; }

    /// <summary>Latest 1 Hz telemetry from the server.</summary>
    public Stats? LastStats { get; private set; }

    /// <summary>"connected", "reconnecting" or "stopped".</summary>
    public event Action<string>? StateChanged;

    /// <summary>
    /// reconnect: overrides the smart-reconnect ladder (e.g. iOS USB listen
    /// mode, where the next tunnel arrives by accepting, not dialing).
    /// </summary>
    public async Task RunAsync(ClientSession initial, KnownServer known, CancellationToken ct, Func<CancellationToken, Task<ClientSession?>>? reconnect = null)
    {
        Session = initial;
        KnownServer current = known;
        Known = current;
        _store.Remember(current);
        StateChanged?.Invoke("connected");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await WatchAsync(Session, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    break;
                Session.Dispose();
                Session = null;
                StateChanged?.Invoke("reconnecting");

                ClientSession? next = null;
                try
                {
                    if (reconnect is not null)
                    {
                        // The server may still hold the dead session's slot for a
                        // moment (Busy); keep retrying like the default ladder does.
                        while (!ct.IsCancellationRequested && next is null)
                        {
                            try
                            {
                                next = await reconnect(ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch
                            {
                                await Task.Delay(500, ct).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        next = await ServerFinder.ConnectWithRetryAsync(current, _clientName, ct, _features).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                if (next is null)
                    break;
                Session = next;
                if (reconnect is null)
                    current = current with { LastIps = new[] { next.RemoteEndPoint!.Address.ToString() } };
                Known = current;
                _store.Remember(current);
                StateChanged?.Invoke("connected");
            }
        }
        finally
        {
            Session?.Dispose();
            Session = null;
            StateChanged?.Invoke("stopped");
        }
    }

    private async Task<bool> WatchAsync(ClientSession session, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long lastPongMs = 0;
        long nextPingMs = 0;
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Ping + watchdog run on a timer; the reader below never uses a timeout.
        // Cancelling a pending NetworkStream read can strand bytes in flight and
        // misalign the next frame read (seen as bogus lengths on adb-reverse TCP).
        Task timer = Task.Run(async () =>
        {
            try
            {
                while (!timerCts.IsCancellationRequested)
                {
                    await Task.Delay(250, timerCts.Token).ConfigureAwait(false);
                    long nowMs = clock.ElapsedMilliseconds;
                    if (nowMs >= nextPingMs)
                    {
                        await session.SendControlAsync(new Ping(Stopwatch.GetTimestamp()), timerCts.Token).ConfigureAwait(false);
                        nextPingMs = nowMs + 2000;
                    }
                    long last = Interlocked.Read(ref lastPongMs);
                    if (last == 0 ? nowMs > 5000 : nowMs - last > 5000)
                    {
                        session.Dispose();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                session.Dispose();
            }
        }, CancellationToken.None);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ControlMessage? msg = await session.ReadControlAsync(ct).ConfigureAwait(false);
                if (msg is null or Bye)
                    return false;
                if (msg is Pong)
                    Interlocked.Exchange(ref lastPongMs, clock.ElapsedMilliseconds);
                else if (msg is Ping p)
                    await session.SendControlAsync(new Pong(p.TsUs), ct).ConfigureAwait(false);
                else if (msg is Stats stats)
                    LastStats = stats;
            }
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            timerCts.Cancel();
            try { await timer.ConfigureAwait(false); } catch { }
        }
    }

    public void Dispose() => Session?.Dispose();
}