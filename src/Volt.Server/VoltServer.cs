using System.Net;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Volt.Server;

/// <summary>
/// The zero-allocation HTTP/1.1 listener. Accepts connections on a background task
/// and hands each one a pooled VoltConnection.
/// </summary>
public sealed class VoltServer : IDisposable
{
    private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private readonly ConcurrentQueue<VoltConnection> _pool = new();
    private readonly VoltOptions _options;
    private readonly X509Certificate2? _tlsCertificate;
    private Task? _acceptLoop;
    private int _started;

    public VoltServer(VoltOptions options, X509Certificate2? tlsCertificate = null)
    {
        _options = options;
        _tlsCertificate = tlsCertificate;
    }

    public bool IsTls => _tlsCertificate is not null;

    /// <summary>Binds and starts accepting. Idempotent.</summary>
    public void Start(IPEndPoint endpoint)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(endpoint);
        _listener.Listen(1024);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public EndPoint? LocalEndPoint => _started == 1 ? _listener.LocalEndPoint : null;

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync();
            }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            if (_tlsCertificate is not null)
            {
                var tlsStream = new SslStream(new NetworkStream(socket, ownsSocket: false), false);
                try
                {
                    await tlsStream.AuthenticateAsServerAsync(_tlsCertificate, clientCertificateRequired: false,
                        enabledSslProtocols: System.Security.Authentication.SslProtocols.None, checkCertificateRevocation: false);
                }
                catch
                {
                    tlsStream.Dispose();
                    socket.Dispose();
                    continue;
                }
                var tlsConnection = Rent();
                _ = Task.Run(() => tlsConnection.RunAsync(socket, tlsStream));
            }
            else
            {
                var connection = Rent();
                _ = Task.Run(() => connection.RunAsync(socket, new NetworkStream(socket, ownsSocket: false)));
            }
        }
    }

    private VoltConnection Rent()
    {
        if (_pool.TryDequeue(out var connection)) return connection;
        return new VoltConnection(this, _options);
    }

    internal void Return(VoltConnection connection)
    {
        if (_pool.Count < 128) _pool.Enqueue(connection);
    }

    public void Dispose()
    {
        _listener.Dispose();
        while (_pool.TryDequeue(out var connection)) connection.Dispose();
    }
}
