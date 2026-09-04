using ReforgerRcon.Services;
using ReforgerRcon.Services.Parsers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.BattleNET;

public class BattlEyeClient(BattlEyeLoginCredentials loginCredentials) : IDisposable
{
    private const byte HeaderByteB = 0x42;
    private const byte HeaderByteE = 0x45;
    private const byte HeaderByteSplit = 0xFF;

    private const byte PacketTypeLogin = 0x00;
    private const byte PacketTypeCommand = 0x01;
    private const byte PacketTypeServerMessage = 0x02;

    private Socket? _socket;
    private DateTime _lastPacketSent = DateTime.UtcNow;
    private DateTime _lastPacketReceived = DateTime.UtcNow;
    private volatile bool _keepRunning;
    private byte _sequenceNumber;
    private int _currentResendPacket = -1;
    private bool _isDisposed;
    private long _totalPacketsSent;
    private long _totalPacketsReceived;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _totalKeepAlivesSent;

    private readonly ConcurrentDictionary<byte, (byte[] Packet, string Command, long SentTimestamp)> _pendingCommands = new();
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<string>> _pendingCommandTcs = new();
    private readonly ConcurrentDictionary<byte, MultiPacketBuffer> _multiPacketResponses = new();
    private readonly BattlEyeLoginCredentials _loginCredentials = loginCredentials;
    private readonly Lock _syncLock = new();

    public bool Connected => _socket is { Connected: true };
    public bool ReconnectOnPacketLoss { get; set; } = true;
    public int CommandQueue => _pendingCommands.Count;
    public int LastPingMs { get; private set; }
    public string LastErrorDiagnostic { get; private set; } = string.Empty;

    public event BattlEyeMessageEventHandler? BattlEyeMessageReceived;
    public event BattlEyeConnectEventHandler? BattlEyeConnected;
    public event BattlEyeDisconnectEventHandler? BattlEyeDisconnected;

    [SuppressMessage("AsyncUsage", "PH_S005:DiscourageAsyncSuffix", Justification = "Adheres to TAP pattern conventions for async APIs")]
    public Task<BattlEyeConnectionResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        AppLogger.Trace($"[BattlEyeClient:ConnectAsync] Targeting endpoint {_loginCredentials.Host}:{_loginCredentials.Port}.");
        return Task.Run(() => ConnectInternal(3, cancellationToken), cancellationToken);
    }

    public BattlEyeConnectionResult Connect()
    {
        AppLogger.Trace($"[BattlEyeClient:Connect] Synchronous Connect targeting {_loginCredentials.Host}:{_loginCredentials.Port}.");
        return ConnectInternal(3, CancellationToken.None);
    }

    private BattlEyeConnectionResult ConnectInternal(int totalRetries, CancellationToken ct)
    {
        using var timing = AppLogger.Measure($"BattlEyeClient.ConnectInternal({_loginCredentials.Host}:{_loginCredentials.Port})");

        lock (_syncLock)
        {
            _lastPacketSent = DateTime.UtcNow;
            _lastPacketReceived = DateTime.UtcNow;
            _sequenceNumber = 0;
            _currentResendPacket = -1;
            _pendingCommands.Clear();
            _pendingCommandTcs.Clear();
            _multiPacketResponses.Clear();
            _keepRunning = true;
            LastErrorDiagnostic = string.Empty;

            var remoteEp = new IPEndPoint(_loginCredentials.Host, _loginCredentials.Port);
            AppLogger.Debug($"[BattlEyeClient:Connect] Beginning UDP handshake with {remoteEp} (Max attempts: {totalRetries}, PasswordLength: {_loginCredentials.Password?.Length ?? 0}, AddressFamily: {_loginCredentials.Host.AddressFamily}).");

            for (int attempt = 1; attempt <= totalRetries; attempt++)
            {
                var attemptStart = Stopwatch.GetTimestamp();

                if (ct.IsCancellationRequested)
                {
                    LastErrorDiagnostic = "Connection canceled by user or timed out.";
                    AppLogger.Warn($"[BattlEyeClient:Connect] Handshake canceled via CancellationToken on attempt #{attempt} for {remoteEp}.");
                    OnConnect(_loginCredentials, BattlEyeConnectionResult.ConnectionFailed);
                    return BattlEyeConnectionResult.ConnectionFailed;
                }

                try
                {
                    if (_socket != null)
                    {
                        AppLogger.Trace("[BattlEyeClient:Connect] Cleaning up stale socket before attempt.");
                        _socket.Dispose();
                    }

                    _socket = new Socket(_loginCredentials.Host.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                    {
                        ReceiveBufferSize = 262144,
                        SendBufferSize = 65535,
                        ReceiveTimeout = 1000,
                        SendTimeout = 1000,
                        ExclusiveAddressUse = false
                    };

                    AppLogger.Info($"[BattlEyeClient:Connect] Connecting UDP socket to {remoteEp} (Attempt #{attempt}/{totalRetries}, RcvBuf={_socket.ReceiveBufferSize}, SndBuf={_socket.SendBufferSize})...");
                    _socket.Connect(remoteEp);

                    byte[] loginPacket = ConstructPacket(PacketTypeLogin, sequenceNumber: null, _loginCredentials.Password);
                    AppLogger.Trace($"[BattlEyeClient:Connect] Outgoing login packet ({loginPacket.Length} bytes, CRC={Convert.ToHexString(loginPacket.AsSpan(2, 4))}).");

                    var handshakeStartTimestamp = Stopwatch.GetTimestamp();
                    _socket.Send(loginPacket);
                    Interlocked.Increment(ref _totalPacketsSent);
                    Interlocked.Add(ref _totalBytesSent, loginPacket.Length);
                    _lastPacketSent = DateTime.UtcNow;

                    var receiveBuffer = new byte[8192];
                    int bytesReceived = _socket.Receive(receiveBuffer, receiveBuffer.Length, SocketFlags.None);
                    Interlocked.Increment(ref _totalPacketsReceived);
                    Interlocked.Add(ref _totalBytesReceived, bytesReceived);
                    var handshakeRtt = (int)Stopwatch.GetElapsedTime(handshakeStartTimestamp).TotalMilliseconds;

                    AppLogger.Trace($"[BattlEyeClient:Connect] Received handshake response ({bytesReceived} bytes in {handshakeRtt}ms).");

                    if (ValidatePacket(receiveBuffer, bytesReceived, out ReadOnlySpan<byte> payload) &&
                        payload.Length >= 2 &&
                        payload[0] == PacketTypeLogin)
                    {
                        if (payload[1] == 0x01)
                        {
                            UpdatePing(handshakeRtt);
                            var totalAttemptMs = Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds;
                            AppLogger.Info($"[BattlEyeClient:Connect] Handshake SUCCESS: Authenticated with {remoteEp} in {handshakeRtt}ms (AttemptDuration: {totalAttemptMs:F2}ms, Ping: {LastPingMs}ms).");
                            OnConnect(_loginCredentials, BattlEyeConnectionResult.Success);
                            StartReceiveLoop();
                            return BattlEyeConnectionResult.Success;
                        }

                        LastErrorDiagnostic = $"Invalid RCON password for {remoteEp}. Authentication was rejected by the server.";
                        AppLogger.Warn($"[BattlEyeClient:Connect] Handshake REJECTED: Invalid password response from {remoteEp} (Payload byte: 0x{payload[1]:X2}).");
                        OnConnect(_loginCredentials, BattlEyeConnectionResult.InvalidLogin);
                        return BattlEyeConnectionResult.InvalidLogin;
                    }

                    AppLogger.Warn($"[BattlEyeClient:Connect] Handshake validation failed on attempt #{attempt} (Bytes received: {bytesReceived}).");
                }
                catch (SocketException sockEx)
                {
                    var attemptMs = Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds;
                    if (sockEx.SocketErrorCode == SocketError.NetworkUnreachable)
                    {
                        LastErrorDiagnostic = $"Network is unreachable ({_loginCredentials.Host}:{_loginCredentials.Port}). Verify your machine or container has an active default network route.";
                    }
                    else if (sockEx.SocketErrorCode == SocketError.ConnectionRefused)
                    {
                        LastErrorDiagnostic = $"Connection refused by {_loginCredentials.Host}:{_loginCredentials.Port}. Server is offline or RCON port is closed in firewall.";
                    }
                    else if (sockEx.SocketErrorCode == SocketError.TimedOut)
                    {
                        LastErrorDiagnostic = $"Connection timed out waiting for {_loginCredentials.Host}:{_loginCredentials.Port}. Verify server IP, port, and firewall rules.";
                    }
                    else
                    {
                        LastErrorDiagnostic = $"Socket error ({sockEx.SocketErrorCode}): {sockEx.Message}";
                    }

                    AppLogger.Warn($"[BattlEyeClient:Connect] Attempt #{attempt} socket error in {attemptMs:F2}ms (Code: {sockEx.SocketErrorCode}, Native: {sockEx.NativeErrorCode}): {sockEx.Message}");
                    if (attempt < totalRetries && !ct.IsCancellationRequested)
                    {
                        Thread.Sleep(50);
                    }
                }
                catch (ObjectDisposedException dispEx)
                {
                    LastErrorDiagnostic = "Socket disposed during connection attempt.";
                    AppLogger.Warn($"[BattlEyeClient:Connect] Socket disposed during connection attempt #{attempt}: {dispEx.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    var attemptMs = Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds;
                    LastErrorDiagnostic = $"Unexpected connection error: {ex.Message}";
                    AppLogger.Error($"[BattlEyeClient:Connect] Unexpected error during handshake attempt #{attempt} ({attemptMs:F2}ms) to {remoteEp}: {ex.Message}", ex);
                    if (attempt < totalRetries && !ct.IsCancellationRequested)
                    {
                        Thread.Sleep(50);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(LastErrorDiagnostic))
            {
                LastErrorDiagnostic = $"Connection timed out after {totalRetries} attempts waiting for {remoteEp}. Verify server IP, port ({_loginCredentials.Port}), and firewall rules.";
            }

            AppLogger.Warn($"[BattlEyeClient:Connect] Handshake timed out: No valid response from server {remoteEp} after {totalRetries} attempts.");
            OnConnect(_loginCredentials, BattlEyeConnectionResult.ConnectionFailed);
            return BattlEyeConnectionResult.ConnectionFailed;
        }
    }

    public byte SendCommand(string command, bool log = true)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        byte seq;
        lock (_syncLock)
        {
            seq = _sequenceNumber;
            _sequenceNumber = (byte)((_sequenceNumber == 255) ? 0 : _sequenceNumber + 1);
        }

        try
        {
            if (_socket is not { Connected: true })
            {
                AppLogger.Warn($"[BattlEyeClient:Command] Cannot send '{AppLogger.SanitizeSensitiveData(command)}': Socket disconnected.");
                return seq;
            }

            byte[] packet = ConstructPacket(PacketTypeCommand, seq, command);
            _lastPacketSent = DateTime.UtcNow;

            if (log)
            {
                _pendingCommands[seq] = (packet, command, Stopwatch.GetTimestamp());
                AppLogger.Trace($"[BattlEyeClient:Command] Registered pending command Seq={seq} (QueueSize={_pendingCommands.Count}).");
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[BattlEyeClient:Command] Outgoing (Seq={seq}, Cmd='{AppLogger.SanitizeSensitiveData(command)}', Bytes={packet.Length}, Prep={elapsedMs:F2}ms)");
            SendRaw(packet);
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[BattlEyeClient:Command] Socket error sending (Seq={seq}, Cmd='{AppLogger.SanitizeSensitiveData(command)}'): {sockEx.SocketErrorCode}", sockEx);
        }
        catch (ObjectDisposedException dispEx)
        {
            AppLogger.Warn($"[BattlEyeClient:Command] Socket disposed while sending '{AppLogger.SanitizeSensitiveData(command)}': {dispEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient:Command] Unexpected error sending '{AppLogger.SanitizeSensitiveData(command)}' (Seq={seq}): {ex.Message}", ex);
        }

        return seq;
    }

    [SuppressMessage("AsyncUsage", "PH_S005:DiscourageAsyncSuffix", Justification = "Adheres to TAP pattern conventions for async APIs")]
    public async Task<string> SendCommandWithResponseAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (cancellationToken.IsCancellationRequested) return string.Empty;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte seq = SendCommand(command, log: true);
        _pendingCommandTcs[seq] = tcs;

        AppLogger.Trace($"[BattlEyeClient:CommandResponse] Awaiting direct response for '{AppLogger.SanitizeSensitiveData(command)}' (Seq={seq}, Timeout={timeout.TotalMilliseconds}ms)...");

        try
        {
            var delayTask = Task.Delay(timeout, CancellationToken.None);
            var cancelTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using (cancellationToken.Register(static s => ((TaskCompletionSource?)s)?.TrySetResult(), cancelTcs).ConfigureAwait(false))
            {
                var completedTask = await Task.WhenAny(tcs.Task, delayTask, cancelTcs.Task).ConfigureAwait(false);

                if (completedTask == tcs.Task)
                {
                    var result = await tcs.Task.ConfigureAwait(false);
                    var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    AppLogger.Debug($"[BattlEyeClient:CommandResponse] Fulfilled for '{AppLogger.SanitizeSensitiveData(command)}' in {elapsedMs:F2}ms (Seq={seq}, Length={result.Length} chars).");
                    return result;
                }

                var timeoutElapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                if (completedTask == cancelTcs.Task)
                {
                    AppLogger.Trace($"[BattlEyeClient:CommandResponse] Direct response wait cancelled after {timeoutElapsedMs:F2}ms (Seq={seq}).");
                }
                else
                {
                    AppLogger.Trace($"[BattlEyeClient:CommandResponse] Direct response wait timed out after {timeoutElapsedMs:F2}ms (Seq={seq}).");
                }

                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[BattlEyeClient:CommandResponse] Response notice (Seq={seq}): {ex.Message}");
            return string.Empty;
        }
        finally
        {
            _pendingCommandTcs.TryRemove(seq, out _);
        }
    }

    public void SendCommand(BattlEyeCommand command, string parameters = "")
    {
        var rawCommand = Helpers.StringValueOf(command) + parameters;
        AppLogger.Trace($"[BattlEyeClient:EnumCommand] Enum command dispatched: {command} -> '{AppLogger.SanitizeSensitiveData(rawCommand)}'");
        SendCommand(rawCommand, true);
    }

    private void SendKeepAlive()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        byte seq;
        lock (_syncLock)
        {
            seq = _sequenceNumber;
            _sequenceNumber = (byte)((_sequenceNumber == 255) ? 0 : _sequenceNumber + 1);
        }

        try
        {
            if (_socket is not { Connected: true }) return;

            byte[] keepAlivePacket = ConstructPacket(PacketTypeCommand, seq, command: null);
            _lastPacketSent = DateTime.UtcNow;
            _pendingCommands[seq] = (keepAlivePacket, "KeepAlive", Stopwatch.GetTimestamp());
            SendRaw(keepAlivePacket);
            var count = Interlocked.Increment(ref _totalKeepAlivesSent);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[BattlEyeClient:Heartbeat] KeepAlive #{count} sent in {elapsedMs:F2}ms (Seq={seq}, QueueSize={_pendingCommands.Count}).");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Warn($"[BattlEyeClient:Heartbeat] Socket error on keepalive: {sockEx.SocketErrorCode}");
        }
        catch (ObjectDisposedException dispEx)
        {
            AppLogger.Debug($"[BattlEyeClient:Heartbeat] Socket disposed on keepalive: {dispEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient:Heartbeat] Unexpected error on keepalive: {ex.Message}", ex);
        }
    }

    private void SendServerMessageAcknowledge(byte sequenceNumber)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            if (_socket is not { Connected: true }) return;

            byte[] ackPacket = ConstructPacket(PacketTypeServerMessage, sequenceNumber, command: null);
            _lastPacketSent = DateTime.UtcNow;
            SendRaw(ackPacket);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[BattlEyeClient:ACK] Sent Server Message ACK in {elapsedMs:F2}ms (Seq={sequenceNumber}, Bytes={ackPacket.Length}).");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[BattlEyeClient:ACK] Socket error on Server Message ACK (Seq={sequenceNumber}): {sockEx.SocketErrorCode}", sockEx);
        }
        catch (ObjectDisposedException dispEx)
        {
            AppLogger.Debug($"[BattlEyeClient:ACK] Socket disposed during ACK (Seq={sequenceNumber}): {dispEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient:ACK] Unexpected error during ACK (Seq={sequenceNumber}): {ex.Message}", ex);
        }
    }

    private void SendRaw(byte[] packet)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            _socket?.Send(packet);
            Interlocked.Increment(ref _totalPacketsSent);
            Interlocked.Add(ref _totalBytesSent, packet.Length);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[BattlEyeClient:SendRaw] Sent {packet.Length} bytes in {elapsedMs:F2}ms (TotalSent={_totalPacketsSent}).");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[BattlEyeClient:SendRaw] SocketException ({packet.Length} bytes): {sockEx.SocketErrorCode} - {sockEx.Message}", sockEx);
        }
        catch (ObjectDisposedException)
        {
            AppLogger.Debug("[BattlEyeClient:SendRaw] Aborted: Socket disposed.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient:SendRaw] General exception ({packet.Length} bytes): {ex.Message}", ex);
        }
    }

    private void UpdatePing(int sampleRttMs)
    {
        if (sampleRttMs <= 0) sampleRttMs = 1;
        lock (_syncLock)
        {
            if (LastPingMs <= 0)
            {
                LastPingMs = sampleRttMs;
            }
            else
            {
                LastPingMs = (int)Math.Round((LastPingMs * 0.7) + (sampleRttMs * 0.3));
            }
        }
        AppLogger.Trace($"[BattlEyeClient:Ping] UDP RTT Sample: {sampleRttMs}ms (Smoothed: {LastPingMs}ms).");
    }

    private static byte[] ConstructPacket(byte packetType, byte? sequenceNumber, string? command)
    {
        int commandLength = 0;
        byte[]? commandBytes = null;
        if (!string.IsNullOrEmpty(command))
        {
            commandBytes = Encoding.UTF8.GetBytes(command);
            commandLength = commandBytes.Length;
        }

        int payloadLength = 2 + (sequenceNumber.HasValue ? 1 : 0) + commandLength;
        var payload = new byte[payloadLength];
        payload[0] = HeaderByteSplit;
        payload[1] = packetType;

        int offset = 2;
        if (sequenceNumber.HasValue)
        {
            payload[offset++] = sequenceNumber.Value;
        }

        if (commandBytes != null && commandLength > 0)
        {
            Buffer.BlockCopy(commandBytes, 0, payload, offset, commandLength);
        }

        uint checksum = CRC32.Compute(payload);

        var packet = new byte[6 + payloadLength];
        packet[0] = HeaderByteB;
        packet[1] = HeaderByteE;
        packet[2] = (byte)(checksum & 0xFF);
        packet[3] = (byte)((checksum >> 8) & 0xFF);
        packet[4] = (byte)((checksum >> 16) & 0xFF);
        packet[5] = (byte)((checksum >> 24) & 0xFF);

        Buffer.BlockCopy(payload, 0, packet, 6, payloadLength);
        return packet;
    }

    private static bool ValidatePacket(byte[] buffer, int length, out ReadOnlySpan<byte> payload)
    {
        payload = [];
        if (length < 7)
        {
            AppLogger.Warn($"[BattlEyeClient:Validation] Packet rejected: Length ({length} bytes) is below minimum 7-byte header.");
            return false;
        }

        if (buffer[0] != HeaderByteB || buffer[1] != HeaderByteE || buffer[6] != HeaderByteSplit)
        {
            var headerHex = Convert.ToHexString(buffer, 0, Math.Min(length, 8));
            AppLogger.Warn($"[BattlEyeClient:Validation] Malformed packet header: [{headerHex}]. Expected '42 45 .. FF'");
            return false;
        }

        uint expectedChecksum = (uint)(buffer[2] | (buffer[3] << 8) | (buffer[4] << 16) | (buffer[5] << 24));
        ReadOnlySpan<byte> payloadBytes = buffer.AsSpan(6, length - 6);

        uint actualChecksum = CRC32.Compute(payloadBytes);
        if (actualChecksum != expectedChecksum)
        {
            AppLogger.Warn($"[BattlEyeClient:Validation] CRC32 mismatch (Expected: 0x{expectedChecksum:X8}, Computed: 0x{actualChecksum:X8}).");
            return false;
        }

        payload = buffer.AsSpan(7, length - 7);
        return true;
    }

    public void Disconnect() => Disconnect(BattlEyeDisconnectionType.Manual);

    private void Disconnect(BattlEyeDisconnectionType? disconnectionType)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        _keepRunning = false;
        AppLogger.Info($"[BattlEyeClient:Disconnect] Session ending (Type: {disconnectionType?.ToString() ?? "Manual"}). Stats: PacketsSent={_totalPacketsSent} ({_totalBytesSent} B), PacketsRcvd={_totalPacketsReceived} ({_totalBytesReceived} B), KeepAlives={_totalKeepAlivesSent}, PendingCmds={_pendingCommands.Count}...");

        lock (_syncLock)
        {
            try
            {
                if (_socket != null)
                {
                    try
                    {
                        if (_socket.Connected)
                        {
                            _socket.Shutdown(SocketShutdown.Both);
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
                    {
                        // Ignore expected socket shutdown exceptions
                    }

                    _socket.Close();
                    _socket.Dispose();
                    _socket = null;
                }

                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                AppLogger.Debug($"[BattlEyeClient:Disconnect] Network socket closed and disposed in {elapsedMs:F2}ms.");
            }
            catch (SocketException sockEx)
            {
                AppLogger.Debug($"[BattlEyeClient:Disconnect] SocketException during close: {sockEx.Message}");
            }
            catch (ObjectDisposedException)
            {
                AppLogger.Debug("[BattlEyeClient:Disconnect] Socket already disposed.");
            }
            catch (IOException ioEx)
            {
                AppLogger.Debug($"[BattlEyeClient:Disconnect] IOException during close: {ioEx.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[BattlEyeClient:Disconnect] Unexpected error closing socket: {ex.Message}", ex);
            }
        }

        if (disconnectionType != null)
            OnDisconnect(_loginCredentials, disconnectionType);
    }

    private void StartReceiveLoop()
    {
        AppLogger.Info("[BattlEyeClient:ReceiveLoop] Starting dedicated UDP socket receive worker loop...");

        Task.Run(async () =>
        {
            var buffer = new byte[131072];

            while (_socket is { Connected: true } && _keepRunning)
            {
                try
                {
                    while (_socket is { Available: > 0 })
                    {
                        var readStart = Stopwatch.GetTimestamp();
                        int bytesRead = _socket.Receive(buffer);
                        Interlocked.Increment(ref _totalPacketsReceived);
                        Interlocked.Add(ref _totalBytesReceived, bytesRead);
                        var readElapsedMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                        AppLogger.Trace($"[BattlEyeClient:ReceiveLoop] Read {bytesRead} bytes from socket in {readElapsedMs:F2}ms.");

                        if (ValidatePacket(buffer, bytesRead, out ReadOnlySpan<byte> payload))
                        {
                            var processStart = Stopwatch.GetTimestamp();
                            ProcessReceivedPayload(payload);
                            var processElapsedMs = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds;
                            AppLogger.Trace($"[BattlEyeClient:ReceiveLoop] Processed payload ({payload.Length} bytes) in {processElapsedMs:F2}ms.");
                        }
                    }

                    if (!CheckKeepAliveAndTimeout())
                    {
                        break;
                    }

                    RetryPendingCommands();
                }
                catch (SocketException ex)
                {
                    if (_keepRunning)
                    {
                        AppLogger.Warn($"[BattlEyeClient:ReceiveLoop] SocketException in loop: {ex.SocketErrorCode} ({ex.Message})");
                        Disconnect(BattlEyeDisconnectionType.SocketException);
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    AppLogger.Debug("[BattlEyeClient:ReceiveLoop] Loop terminated: Socket closed.");
                    break;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLogger.Error($"[BattlEyeClient:ReceiveLoop] Unexpected loop error: {ex.Message}", ex);
                }

                try
                {
                    await Task.Delay(4).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            AppLogger.Debug($"[BattlEyeClient:ReceiveLoop] Worker loop exited. KeepRunning: {_keepRunning}, Reconnect: {ReconnectOnPacketLoss}");

            if (_keepRunning && ReconnectOnPacketLoss)
            {
                AppLogger.Info("[BattlEyeClient:ReceiveLoop] Automatic reconnect triggered.");
                _ = ConnectAsync(CancellationToken.None);
            }
        });
    }

    private bool CheckKeepAliveAndTimeout()
    {
        var now = DateTime.UtcNow;
        var timeoutClient = (now - _lastPacketSent).TotalSeconds;
        var timeoutServer = (now - _lastPacketReceived).TotalSeconds;

        if (timeoutClient >= 10 && _pendingCommands.IsEmpty)
        {
            AppLogger.Trace($"[BattlEyeClient:KeepAlive] Keepalive interval elapsed ({timeoutClient:F1}s since last packet). Dispatching keepalive.");
            SendKeepAlive();
        }

        if (timeoutServer >= 35)
        {
            AppLogger.Warn($"[BattlEyeClient:KeepAlive] Connection timed out: {timeoutServer:F1}s elapsed without incoming packet.");
            Disconnect(BattlEyeDisconnectionType.ConnectionLost);
            return false;
        }

        return true;
    }

    private void RetryPendingCommands()
    {
        if (_pendingCommands.IsEmpty || _socket is not { Available: 0 }) return;

        foreach (var (seq, pending) in _pendingCommands)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(pending.SentTimestamp).TotalMilliseconds;
            if (elapsedMs >= 1200 && (_currentResendPacket == -1 || _currentResendPacket == seq))
            {
                _currentResendPacket = seq;
                _pendingCommands[seq] = (pending.Packet, pending.Command, Stopwatch.GetTimestamp());
                AppLogger.Warn($"[BattlEyeClient:Retry] Retransmitting unacknowledged command (Seq={seq}, Cmd='{AppLogger.SanitizeSensitiveData(pending.Command)}', Elapsed={elapsedMs:F1}ms)...");
                SendRaw(pending.Packet);
                break;
            }
        }
    }

    private void ProcessReceivedPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return;

        _lastPacketReceived = DateTime.UtcNow;
        byte packetType = payload[0];

        switch (packetType)
        {
            case PacketTypeCommand:
                ProcessCommandResponse(payload);
                break;

            case PacketTypeServerMessage:
                ProcessServerMessage(payload);
                break;

            default:
                AppLogger.Warn($"[BattlEyeClient:Payload] Unknown packet type byte: 0x{packetType:X2} (Length: {payload.Length}).");
                break;
        }
    }

    private void ProcessCommandResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return;

        byte seq = payload[1];
        if (_pendingCommands.TryRemove(seq, out var pendingCommand))
        {
            var rttMs = (int)Stopwatch.GetElapsedTime(pendingCommand.SentTimestamp).TotalMilliseconds;
            UpdatePing(rttMs);
            AppLogger.Trace($"[BattlEyeClient:Response] Acknowledged pending command (Seq={seq}, Cmd='{AppLogger.SanitizeSensitiveData(pendingCommand.Command)}', RTT={rttMs}ms).");
        }

        if (_currentResendPacket == seq)
        {
            _currentResendPacket = -1;
        }

        if (payload.Length == 2)
        {
            AppLogger.Trace($"[BattlEyeClient:Response] Empty Command ACK (Seq={seq}).");
            if (_pendingCommandTcs.TryRemove(seq, out var pendingTcs))
            {
                pendingTcs.TrySetResult(string.Empty);
            }
            return;
        }

        if (payload.Length >= 5 && payload[2] == 0x00 && payload[3] > 0 && payload[3] <= 32 && payload[4] < payload[3])
        {
            byte totalPackets = payload[3];
            byte packetIndex = payload[4];

            string chunkText = Encoding.UTF8.GetString(payload[5..]);
            AppLogger.Trace($"[BattlEyeClient:Response] Multi-packet chunk received (Seq={seq}, Index={packetIndex + 1}/{totalPackets}, Length={chunkText.Length} chars).");

            var buffer = _multiPacketResponses.GetOrAdd(seq, _ => new MultiPacketBuffer());
            if (buffer.TryAddChunk(packetIndex, totalPackets, chunkText, out var fullMessage))
            {
                _multiPacketResponses.TryRemove(seq, out _);
                AppLogger.Debug($"[BattlEyeClient:Response] Completed Multi-Packet Response (Seq={seq}, Length={fullMessage.Length} chars across {totalPackets} chunks).");

                if (_pendingCommandTcs.TryRemove(seq, out var pendingTcs))
                {
                    pendingTcs.TrySetResult(fullMessage);
                }

                OnBattlEyeMessage(fullMessage, seq);
            }
        }
        else
        {
            string responseText = Encoding.UTF8.GetString(payload[2..]);
            AppLogger.Debug($"[BattlEyeClient:Response] Command Response received (Seq={seq}, Length={responseText.Length} chars): '{AppLogger.SanitizeSensitiveData(responseText)}'");

            if (_pendingCommandTcs.TryRemove(seq, out var pendingTcs))
            {
                pendingTcs.TrySetResult(responseText);
            }

            OnBattlEyeMessage(responseText, seq);
        }
    }

    private void ProcessServerMessage(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return;

        byte seq = payload[1];
        SendServerMessageAcknowledge(seq);

        if (payload.Length > 2)
        {
            string message = Encoding.UTF8.GetString(payload[2..]);
            AppLogger.Debug($"[BattlEyeClient:ServerMessage] Server Event received (Seq={seq}, Length={message.Length} chars): '{AppLogger.SanitizeSensitiveData(message)}'");
            OnBattlEyeMessage(message, 256);
        }
    }

    private void OnBattlEyeMessage(string message, int id)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            BattlEyeMessageReceived?.Invoke(new BattlEyeMessageEventArgs(message, id));
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[BattlEyeClient:Event] Dispatched BattlEyeMessageReceived (Id={id}) in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient:Event] Subscriber error in BattlEyeMessageReceived: {ex.Message}", ex);
        }
    }

    private void OnConnect(BattlEyeLoginCredentials loginDetails, BattlEyeConnectionResult connectionResult)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AppLogger.Debug($"[BattlEyeClient:Event] Invoking BattlEyeConnected with result: {connectionResult}");
            BattlEyeConnected?.Invoke(new BattlEyeConnectEventArgs(loginDetails, connectionResult));
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[BattlEyeClient:Event] BattlEyeConnected handlers finished in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient:Event] Subscriber error in BattlEyeConnected: {ex.Message}", ex);
        }
    }

    private void OnDisconnect(BattlEyeLoginCredentials loginDetails, BattlEyeDisconnectionType? disconnectionType)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AppLogger.Debug($"[BattlEyeClient:Event] Invoking BattlEyeDisconnected with type: {disconnectionType?.ToString() ?? "None"}");
            BattlEyeDisconnected?.Invoke(new BattlEyeDisconnectEventArgs(loginDetails, disconnectionType));
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[BattlEyeClient:Event] BattlEyeDisconnected handlers finished in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient:Event] Subscriber error in BattlEyeDisconnected: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                var startTimestamp = Stopwatch.GetTimestamp();
                AppLogger.Info("[BattlEyeClient:Dispose] Disposing BattlEyeClient instance...");
                Disconnect(BattlEyeDisconnectionType.Manual);
                lock (_syncLock)
                {
                    if (_socket != null)
                    {
                        try
                        {
                            _socket.Dispose();
                        }
                        catch
                        {
                            // Ignore disposal errors
                        }
                        _socket = null;
                    }
                }
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                AppLogger.Debug($"[BattlEyeClient:Dispose] BattlEyeClient disposal complete in {elapsedMs:F2}ms.");
            }
            _isDisposed = true;
        }
    }

    private sealed class MultiPacketBuffer
    {
        private readonly Lock _lock = new();
        private readonly SortedDictionary<byte, string> _chunks = [];
        public DateTime FirstReceived { get; } = DateTime.UtcNow;

        public bool TryAddChunk(byte packetIndex, byte totalPackets, string chunkText, out string fullMessage)
        {
            lock (_lock)
            {
                _chunks[packetIndex] = chunkText;

                if (_chunks.Count == totalPackets)
                {
                    fullMessage = string.Concat(_chunks.Values);
                    return true;
                }

                fullMessage = string.Empty;
                return false;
            }
        }
    }
}