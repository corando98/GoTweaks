using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace XboxGamingBarHelper.IPC
{
    /// <summary>
    /// Named Pipe server for IPC with the widget(s).
    /// Uses proper ACLs to allow UWP apps to connect (even when helper is elevated).
    ///
    /// Multi-client (task #12): with the desktop app and the Game Bar widget
    /// split into separate processes, BOTH connect here. Property pushes are
    /// broadcast to every client (which keeps the two UIs in sync for free);
    /// request responses are routed back to the client that sent the request
    /// via a RequestId → client map, so no call site had to change. Widget-side
    /// RequestIds are seeded per process so two clients can't collide.
    /// </summary>
    public class NamedPipeServer : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Pipe name - simple name without LOCAL\ prefix
        /// The ACLs handle UWP access permissions
        /// </summary>
        public const string PipeName = "GoTweaksHelper";

        /// <summary>
        /// Full pipe path for display/debugging
        /// </summary>
        public static readonly string FullPipePath = $"\\\\.\\pipe\\{PipeName}";

        /// <summary>
        /// Widget + desktop app + one spare for reconnect churn.
        /// </summary>
        private const int MaxClients = 4;

        private sealed class ClientConnection
        {
            public int Id;
            public NamedPipeServerStream Stream;
            public StreamReader Reader;
            public StreamWriter Writer;
            public readonly object WriteLock = new object();
        }

        private readonly List<ClientConnection> _clients = new List<ClientConnection>();
        private readonly object _clientsLock = new object();
        private int _nextClientId;

        // RequestId → client that sent it, so SendMessage can route the
        // response without any call-site changes. Bounded: entries are removed
        // when the response goes out, and the queue prunes leaks from requests
        // that never get a reply.
        private readonly ConcurrentDictionary<int, ClientConnection> _pendingRequestClients = new ConcurrentDictionary<int, ClientConnection>();
        private readonly ConcurrentQueue<int> _pendingRequestOrder = new ConcurrentQueue<int>();
        private const int MaxPendingRequestEntries = 512;

        private static readonly Regex RequestIdRegex = new Regex(@"""RequestId""\s*:\s*(\d+)", RegexOptions.Compiled);

        private CancellationTokenSource _cancellationTokenSource;
        private Task _listenerTask;
        private bool _isDisposed;

        /// <summary>
        /// Event raised when a message is received from a widget client
        /// </summary>
        public event EventHandler<PipeMessageEventArgs> MessageReceived;

        /// <summary>
        /// Event raised when a widget client connects
        /// </summary>
        public event EventHandler Connected;

        /// <summary>
        /// Event raised when a widget client disconnects
        /// </summary>
        public event EventHandler Disconnected;

        /// <summary>
        /// Whether at least one client is currently connected
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (_clientsLock)
                {
                    return _clients.Any(c => c.Stream?.IsConnected == true);
                }
            }
        }

        /// <summary>
        /// Starts the pipe server and begins listening for connections
        /// </summary>
        public void Start()
        {
            if (_listenerTask != null)
            {
                Logger.Warn("Pipe server already started");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _listenerTask = Task.Run(() => AcceptLoop(_cancellationTokenSource.Token));
            Logger.Info($"Named pipe server started: {FullPipePath}");
        }

        /// <summary>
        /// Stops the pipe server
        /// </summary>
        public void Stop()
        {
            Logger.Info("Stopping pipe server...");
            _cancellationTokenSource?.Cancel();

            List<ClientConnection> clients;
            lock (_clientsLock)
            {
                clients = new List<ClientConnection>(_clients);
                _clients.Clear();
            }
            foreach (var client in clients)
            {
                CloseClient(client, notify: false);
            }

            try
            {
                _listenerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            _listenerTask = null;
            Logger.Info("Pipe server stopped");
        }

        /// <summary>
        /// Sends a message to widget clients. Messages carrying a RequestId we
        /// saw arrive from a specific client are routed back to that client
        /// only (request/response); everything else is broadcast to all
        /// connected clients (property pushes — keeps multiple UIs in sync).
        /// </summary>
        public bool SendMessage(string message)
        {
            // Response routing: match the RequestId back to its source client.
            var match = RequestIdRegex.Match(message);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out int requestId)
                && requestId > 0
                && _pendingRequestClients.TryRemove(requestId, out var sourceClient))
            {
                if (SendToClient(sourceClient, message))
                {
                    return true;
                }
                // Source client died before its response — nothing useful to do.
                Logger.Debug($"Response for request {requestId} dropped (client {sourceClient.Id} gone)");
                return false;
            }

            // Broadcast path.
            List<ClientConnection> clients;
            lock (_clientsLock)
            {
                clients = new List<ClientConnection>(_clients);
            }

            if (clients.Count == 0)
            {
                Logger.Debug("Cannot send message - not connected");
                return false;
            }

            bool anySent = false;
            foreach (var client in clients)
            {
                anySent |= SendToClient(client, message);
            }
            return anySent;
        }

        private bool SendToClient(ClientConnection client, string message)
        {
            if (client?.Stream?.IsConnected != true)
            {
                return false;
            }

            try
            {
                lock (client.WriteLock)
                {
                    client.Writer?.WriteLine(message);
                    client.Writer?.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending message to client {client.Id}: {ex.Message}");
                RemoveClient(client);
                return false;
            }
        }

        /// <summary>
        /// Accept loop — keeps a listening pipe instance available and spawns a
        /// read task per connected client.
        /// </summary>
        private void AcceptLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipeWithSecurity();
                    Logger.Info("Waiting for widget connection...");
                    pipe.WaitForConnection();

                    if (cancellationToken.IsCancellationRequested)
                    {
                        pipe.Dispose();
                        break;
                    }

                    var client = new ClientConnection
                    {
                        Id = Interlocked.Increment(ref _nextClientId),
                        Stream = pipe,
                        Reader = new StreamReader(pipe, Encoding.UTF8),
                        Writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = false },
                    };

                    int clientCount;
                    lock (_clientsLock)
                    {
                        _clients.Add(client);
                        clientCount = _clients.Count;
                    }

                    Logger.Info($"Widget connected via named pipe (client {client.Id}, {clientCount} connected)");
                    Connected?.Invoke(this, EventArgs.Empty);

                    Task.Run(() => ReadMessages(client, cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    try { pipe?.Dispose(); } catch { }
                    break;
                }
                catch (IOException ex)
                {
                    Logger.Debug($"Pipe IO error while accepting (likely shutdown): {ex.Message}");
                    try { pipe?.Dispose(); } catch { }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Pipe server accept error: {ex.Message}");
                    try { pipe?.Dispose(); } catch { }
                    // Brief delay so a persistent failure can't spin the loop.
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Thread.Sleep(500);
                    }
                }
            }
        }

        /// <summary>
        /// Creates a named pipe server instance with security that allows UWP apps to connect
        /// </summary>
        private NamedPipeServerStream CreatePipeWithSecurity()
        {
            // Create security that allows:
            // 1. Current user (full control)
            // 2. ALL APPLICATION PACKAGES (S-1-15-2-1) - allows UWP apps to connect
            var pipeSecurity = new PipeSecurity();

            // Allow current user full control
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            // Allow ALL APPLICATION PACKAGES (this is the key for UWP access)
            // SID S-1-15-2-1 = ALL_APPLICATION_PACKAGES
            var allAppPackagesSid = new SecurityIdentifier("S-1-15-2-1");
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                allAppPackagesSid,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            // Also allow ALL RESTRICTED APPLICATION PACKAGES for extra compatibility
            // SID S-1-15-2-2 = ALL_RESTRICTED_APPLICATION_PACKAGES
            try
            {
                var allRestrictedAppPackagesSid = new SecurityIdentifier("S-1-15-2-2");
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    allRestrictedAppPackagesSid,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
            }
            catch
            {
                // S-1-15-2-2 may not exist on older Windows versions
            }

            return new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                MaxClients,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096, // inBufferSize
                4096, // outBufferSize
                pipeSecurity);
        }

        /// <summary>
        /// Reads messages from one connected client
        /// </summary>
        private void ReadMessages(ClientConnection client, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && client.Stream.IsConnected)
                {
                    var line = client.Reader.ReadLine();
                    if (line == null)
                    {
                        // Client disconnected
                        Logger.Info($"Widget client {client.Id} disconnected (end of stream)");
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Logger.Debug($"Received from client {client.Id}: {line.Substring(0, Math.Min(100, line.Length))}...");
                        TrackRequestSource(client, line);
                        MessageReceived?.Invoke(this, new PipeMessageEventArgs(line));
                    }
                }
            }
            catch (IOException)
            {
                // Normal disconnection
            }
            catch (ObjectDisposedException)
            {
                // Closed during shutdown
            }
            catch (Exception ex)
            {
                Logger.Error($"Pipe client {client.Id} read error: {ex.Message}");
            }
            finally
            {
                RemoveClient(client);
            }
        }

        /// <summary>
        /// Remembers which client a RequestId came from so SendMessage can
        /// route the response. Bounded against requests that never get replies.
        /// </summary>
        private void TrackRequestSource(ClientConnection client, string message)
        {
            var match = RequestIdRegex.Match(message);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int requestId) || requestId <= 0)
            {
                return;
            }

            _pendingRequestClients[requestId] = client;
            _pendingRequestOrder.Enqueue(requestId);
            while (_pendingRequestOrder.Count > MaxPendingRequestEntries && _pendingRequestOrder.TryDequeue(out int stale))
            {
                // Only remove if it's still the stale entry (a response may have
                // already removed it; an id reuse would have re-added it).
                _pendingRequestClients.TryRemove(stale, out _);
            }
        }

        private void RemoveClient(ClientConnection client)
        {
            bool removed;
            lock (_clientsLock)
            {
                removed = _clients.Remove(client);
            }

            if (!removed)
            {
                return;
            }

            CloseClient(client, notify: true);
        }

        private void CloseClient(ClientConnection client, bool notify)
        {
            Logger.Info($"Widget client {client.Id} disconnected");

            try { client.Reader?.Dispose(); } catch { }
            try { client.Writer?.Dispose(); } catch { }
            try { client.Stream?.Dispose(); } catch { }

            if (notify)
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Stop();
            _cancellationTokenSource?.Dispose();
        }
    }

    /// <summary>
    /// Event args for pipe messages
    /// </summary>
    public class PipeMessageEventArgs : EventArgs
    {
        public string Message { get; }

        public PipeMessageEventArgs(string message)
        {
            Message = message;
        }
    }
}
