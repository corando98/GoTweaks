using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NLog;
using Windows.Data.Json;
using Windows.Foundation.Collections;

namespace XboxGamingBar.IPC
{
    /// <summary>
    /// Named Pipe client for IPC with the helper.
    /// Uses P/Invoke to bypass UWP NamedPipeClientStream limitations.
    /// </summary>
    public class NamedPipeClient : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Pipe name - must match the helper's pipe name
        /// </summary>
        private const string PipeName = @"\\.\pipe\GoTweaksHelper";

        // Win32 constants
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_PIPE_BUSY = 231;

        // P/Invoke declarations
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipeW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpNamedPipeName,
            uint nTimeOut);

        private SafeFileHandle _pipeHandle;
        private FileStream _pipeStream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private readonly object _writeLock = new object();
        private CancellationTokenSource _cancellationTokenSource;
        private Task _readerTask;
        private bool _isDisposed;
        private bool _isConnected;
        private int _lastError; // Track last Win32 error for diagnostics

        // Request/response tracking. Seeded per process (task #12): the
        // desktop app and the Game Bar widget run as separate processes, and
        // both connect to the helper. The helper routes responses back to the
        // requesting client by RequestId, so the two clients' id ranges must
        // never overlap — a random 14-bit base shifted high gives each process
        // 65k+ requests of headroom (no int overflow) with a collision chance
        // of 1 in 16384.
        private int _nextRequestId = new Random().Next(1, 1 << 14) << 16;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ValueSet>> _pendingRequests =
            new ConcurrentDictionary<int, TaskCompletionSource<ValueSet>>();

        /// <summary>
        /// Event raised when a message is received from the helper
        /// </summary>
        public event EventHandler<PipeMessageEventArgs> MessageReceived;

        /// <summary>
        /// Event raised when connected to the helper
        /// </summary>
        public event EventHandler Connected;

        /// <summary>
        /// Event raised when disconnected from the helper
        /// </summary>
        public event EventHandler Disconnected;

        /// <summary>
        /// Whether currently connected to the helper
        /// </summary>
        public bool IsConnected => _isConnected && _pipeHandle != null && !_pipeHandle.IsInvalid && !_pipeHandle.IsClosed;

        /// <summary>
        /// Attempts to connect to the helper's named pipe
        /// </summary>
        /// <param name="timeoutMs">Connection timeout in milliseconds</param>
        /// <returns>True if connected successfully</returns>
        public async Task<bool> ConnectAsync(int timeoutMs = 5000)
        {
            if (IsConnected)
            {
                Logger.Debug("Already connected to helper pipe");
                return true;
            }

            Logger.Info($"Connecting to helper pipe: {PipeName}");

            try
            {
                // Try to connect with timeout
                var connected = await Task.Run(() => TryConnect(timeoutMs));

                if (!connected)
                {
                    Logger.Warn($"Failed to connect to helper pipe (timeout or not available) - last Win32 error: {_lastError}");
                    return false;
                }

                // Create streams for reading/writing
                _pipeStream = new FileStream(_pipeHandle, FileAccess.ReadWrite, 4096, true);
                _reader = new StreamReader(_pipeStream, Encoding.UTF8);
                _writer = new StreamWriter(_pipeStream, Encoding.UTF8) { AutoFlush = false };

                _isConnected = true;
                _cancellationTokenSource = new CancellationTokenSource();

                // Start reading messages in background
                _readerTask = Task.Run(() => ReadMessagesAsync(_cancellationTokenSource.Token));

                Logger.Info("Connected to helper pipe successfully");
                Connected?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error connecting to helper pipe: {ex.Message}");
                Cleanup();
                return false;
            }
        }

        /// <summary>
        /// Tries to connect to the pipe using Win32 API
        /// </summary>
        private bool TryConnect(int timeoutMs)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline)
            {
                // Try to open the pipe
                _pipeHandle = CreateFileW(
                    PipeName,
                    GENERIC_READ | GENERIC_WRITE,
                    0, // No sharing
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);

                if (!_pipeHandle.IsInvalid)
                {
                    Logger.Info("Pipe handle acquired successfully");
                    return true;
                }

                int error = Marshal.GetLastWin32Error();
                _lastError = error; // Track last error for logging

                if (error == ERROR_PIPE_BUSY)
                {
                    // Pipe is busy, wait for it
                    if (!WaitNamedPipeW(PipeName, (uint)Math.Min(1000, (deadline - DateTime.Now).TotalMilliseconds)))
                    {
                        // Timeout waiting for pipe
                        continue;
                    }
                }
                else
                {
                    // Pipe doesn't exist or access denied, wait a bit and retry
                    Thread.Sleep(100);
                }
            }

            return false;
        }

        /// <summary>
        /// Sends a message to the helper
        /// </summary>
        public bool SendMessage(string message)
        {
            if (!IsConnected)
            {
                Logger.Debug("Cannot send message - not connected to helper");
                return false;
            }

            try
            {
                lock (_writeLock)
                {
                    _writer.WriteLine(message);
                    _writer.Flush();
                }
                Logger.Debug($"Sent: {message.Substring(0, Math.Min(100, message.Length))}...");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending message to helper: {ex.Message}");
                HandleDisconnection();
                return false;
            }
        }

        /// <summary>
        /// Sends a ValueSet as JSON to the helper (fire and forget)
        /// </summary>
        public bool SendValueSet(ValueSet valueSet)
        {
            try
            {
                var json = ValueSetToJson(valueSet, 0);
                return SendMessage(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error serializing ValueSet: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a request and waits for the response.
        /// Similar to AppServiceConnection.SendMessageAsync.
        /// </summary>
        /// <param name="request">The request ValueSet to send</param>
        /// <param name="timeoutMs">Timeout in milliseconds (default 10 seconds)</param>
        /// <returns>The response ValueSet, or null on timeout/error</returns>
        public async Task<ValueSet> SendRequestAsync(ValueSet request, int timeoutMs = 10000)
        {
            if (!IsConnected)
            {
                Logger.Debug("Cannot send request - not connected to helper pipe");
                return null;
            }

            // Assign a unique request ID
            int requestId = Interlocked.Increment(ref _nextRequestId);

            // Create a TaskCompletionSource to wait for the response
            var tcs = new TaskCompletionSource<ValueSet>();
            _pendingRequests[requestId] = tcs;

            try
            {
                // Send the request with the request ID
                var json = ValueSetToJson(request, requestId);
                if (!SendMessage(json))
                {
                    _pendingRequests.TryRemove(requestId, out _);
                    return null;
                }

                // Wait for the response with timeout
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    cts.Token.Register(() => tcs.TrySetCanceled());
                    try
                    {
                        return await tcs.Task;
                    }
                    catch (TaskCanceledException)
                    {
                        Logger.Warn($"Request {requestId} timed out after {timeoutMs}ms");
                        return null;
                    }
                }
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        /// <summary>
        /// Converts a ValueSet to JSON string with optional request ID
        /// </summary>
        private string ValueSetToJson(ValueSet valueSet, int requestId)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"RequestId\":{requestId}");
            foreach (var kvp in valueSet)
            {
                sb.Append(",");
                sb.Append($"\"{EscapeJson(kvp.Key)}\":");
                sb.Append(ValueToJson(kvp.Value));
            }
            sb.Append("}");
            return sb.ToString();
        }

        private string ValueToJson(object value)
        {
            if (value == null) return "null";
            if (value is string s) return $"\"{EscapeJson(s)}\"";
            if (value is bool b) return b ? "true" : "false";
            if (value is int || value is long || value is double || value is float) return value.ToString();
            return $"\"{EscapeJson(value.ToString())}\"";
        }

        private string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        /// <summary>
        /// Reads messages from the pipe
        /// </summary>
        private async Task ReadMessagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    var line = await _reader.ReadLineAsync();

                    if (line == null)
                    {
                        // Helper disconnected
                        Logger.Info("Helper disconnected (end of stream)");
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Logger.Debug($"Received from helper: {line.Substring(0, Math.Min(100, line.Length))}...");

                        // Check for RequestId to see if this is a response to a pending request
                        int requestId = ParseRequestId(line);
                        if (requestId > 0 && _pendingRequests.TryRemove(requestId, out var tcs))
                        {
                            // This is a response to a pending request
                            var response = ParseJsonToValueSet(line);
                            tcs.TrySetResult(response);
                        }
                        else
                        {
                            // This is an async push message from helper
                            MessageReceived?.Invoke(this, new PipeMessageEventArgs(line));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                Logger.Debug($"Pipe read error: {ex.Message}");
            }
            finally
            {
                HandleDisconnection();
            }
        }

        /// <summary>
        /// Parse RequestId from JSON message
        /// </summary>
        private int ParseRequestId(string json)
        {
            try
            {
                var match = Regex.Match(json, @"""RequestId""\s*:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
                {
                    return id;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Parse JSON to ValueSet using proper JSON parsing
        /// </summary>
        private ValueSet ParseJsonToValueSet(string json)
        {
            var result = new ValueSet();
            try
            {
                json = json.Trim();
                if (!json.StartsWith("{") || !json.EndsWith("}"))
                    return result;

                // Use Windows.Data.Json for proper JSON parsing (handles escaped strings correctly)
                if (!JsonObject.TryParse(json, out JsonObject jsonObj))
                {
                    Logger.Debug("Failed to parse JSON response");
                    return result;
                }

                foreach (var key in jsonObj.Keys)
                {
                    var jsonValue = jsonObj.GetNamedValue(key);
                    switch (jsonValue.ValueType)
                    {
                        case JsonValueType.String:
                            result[key] = jsonValue.GetString();
                            break;
                        case JsonValueType.Number:
                            var num = jsonValue.GetNumber();
                            // Return as int if it's a whole number
                            if (num == Math.Floor(num) && num >= int.MinValue && num <= int.MaxValue)
                                result[key] = (int)num;
                            else
                                result[key] = num;
                            break;
                        case JsonValueType.Boolean:
                            result[key] = jsonValue.GetBoolean();
                            break;
                        case JsonValueType.Null:
                            result[key] = null;
                            break;
                        case JsonValueType.Object:
                            result[key] = jsonValue.Stringify();
                            break;
                        case JsonValueType.Array:
                            result[key] = jsonValue.Stringify();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error parsing JSON to ValueSet: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Handles disconnection from the helper
        /// </summary>
        private void HandleDisconnection()
        {
            if (!_isConnected) return;

            _isConnected = false;
            Logger.Info("Disconnected from helper pipe");

            // Cancel all pending requests
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetCanceled();
            }
            _pendingRequests.Clear();

            Cleanup();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Disconnects from the helper
        /// </summary>
        public void Disconnect()
        {
            Logger.Info("Disconnecting from helper pipe...");
            _cancellationTokenSource?.Cancel();

            try
            {
                _readerTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch { }

            HandleDisconnection();
        }

        /// <summary>
        /// Cleans up resources
        /// </summary>
        private void Cleanup()
        {
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _pipeStream?.Dispose(); } catch { }
            try { _pipeHandle?.Dispose(); } catch { }

            _reader = null;
            _writer = null;
            _pipeStream = null;
            _pipeHandle = null;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Disconnect();
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
