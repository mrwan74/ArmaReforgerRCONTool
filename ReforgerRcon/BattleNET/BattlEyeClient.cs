using ReforgerRcon.Services;
using ReforgerRcon.Services.Parsers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    private int _sequenceNumberCounter = -1;
    private int _currentResendPacket = -1;
    private bool _isDisposed;
    private long _totalPacketsSent;
    private long _totalPacketsReceived;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _totalKeepAlivesSent;
    private long _totalRetransmissions;

    private Task? _receiveTask;
    private PeriodicTimer? _maintenanceTimer;

    private readonly ConcurrentDictionary<byte, (byte[] Packet, string Command, long SentTimestamp)> _pendingCommands = new();
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<string>> _pendingCommandTcs = new();
    private readonly ConcurrentDictionary<byte, MultiPacketBuffer> _multiPacketResponses = new();
    private readonly BattlEyeLoginCredentials _loginCredentials = loginCredentials;
    private readonly Lock _syncLock = new();

    public bool Connected => Volatile.Read(ref _socket) is { Connected: true };
    public bool ReconnectOnPacketLoss { get; set; } = true;
    public int CommandQueue => _pendingCommands.Count;
    public int LastPingMs { get; private set; }
    public string LastErrorDiagnostic { get; private set; } = string.Empty;

    public event BattlEyeMessageEventHandler? BattlEyeMessageReceived;
    public event BattlEyeConnectEventHandler? BattlEyeConnected;
    public event BattlEyeDisconnectEventHandler? BattlEyeDisconnected;

    [SuppressMessage("AsyncUsage", "PH_S005:DiscourageAsyncSuffix", Justification = "Adheres to TAP pattern conventions for async APIs")]
    public Task<BattlEyeConnectionResult> ConnectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ConnectInternal(3, cancellationToken), cancellationToken);

    public BattlEyeConnectionResult Connect() => ConnectInternal(3, CancellationToken.None);

    private BattlEyeConnectionResult ConnectInternal(int totalRetries, CancellationToken ct)
    {
        using var timing = AppLogger.Measure($"BattlEyeClient.ConnectInternal({_loginCredentials.Host}:{_loginCredentials.Port})");

        _lastPacketSent = DateTime.UtcNow;
        _lastPacketReceived = DateTime.UtcNow;
        _currentResendPacket = -1;
        _pendingCommands.Clear();
        _pendingCommandTcs.Clear();
        _multiPacketResponses.Clear();
        _keepRunning = true;
        LastErrorDiagnostic = string.Empty;
        Interlocked.Exchange(ref _sequenceNumberCounter, -1);

        var remoteEp = new IPEndPoint(_loginCredentials.Host, _loginCredentials.Port);

        for (int attempt = 1; attempt <= totalRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                LastErrorDiagnostic = "Connection canceled by user or timed out.";
                OnConnect(_loginCredentials, BattlEyeConnectionResult.ConnectionFailed);
                return BattlEyeConnectionResult.ConnectionFailed;
            }

            Socket? localSocket = null;
            try
            {
                Interlocked.Exchange(ref _socket, null)?.Dispose();

                localSocket = new Socket(_loginCredentials.Host.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                {
                    ReceiveBufferSize = 524288,
                    SendBufferSize = 65535,
                    ExclusiveAddressUse = false
                };

                localSocket.Connect(remoteEp);

                byte[] loginPacket = ConstructPacket(PacketTypeLogin, sequenceNumber: null, _loginCredentials.Password);
                var handshakeStartTimestamp = Stopwatch.GetTimestamp();
                localSocket.Send(loginPacket);
                Interlocked.Increment(ref _totalPacketsSent);
                Interlocked.Add(ref _totalBytesSent, loginPacket.Length);
                _lastPacketSent = DateTime.UtcNow;

                var receiveBuffer = new byte[8192];
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(TimeSpan.FromMilliseconds(1000));

                var receiveTask = localSocket.ReceiveAsync(receiveBuffer.AsMemory(), SocketFlags.None, handshakeCts.Token).AsTask();
                int bytesReceived = receiveTask.GetAwaiter().GetResult();

                Interlocked.Increment(ref _totalPacketsReceived);
                Interlocked.Add(ref _totalBytesReceived, bytesReceived);
                var handshakeRtt = (int)Stopwatch.GetElapsedTime(handshakeStartTimestamp).TotalMilliseconds;

                if (ValidatePacket(receiveBuffer, bytesReceived, out ReadOnlySpan<byte> payload) &&
                    payload.Length >= 2 &&
                    payload[0] == PacketTypeLogin)
                {
                    if (payload[1] == 0x01)
                    {
                        UpdatePing(handshakeRtt);
                        Volatile.Write(ref _socket, localSocket);
                        AppLogger.Info($"[BattlEyeClient:Connect] Handshake SUCCESS with {remoteEp} in {handshakeRtt}ms (Ping: {LastPingMs}ms).");

                        StartAsyncReceiveLoop(localSocket);
                        OnConnect(_loginCredentials, BattlEyeConnectionResult.Success);
                        return BattlEyeConnectionResult.Success;
                    }

                    LastErrorDiagnostic = $"Invalid RCON password for {remoteEp}. Authentication was rejected by the server.";
                    localSocket.Dispose();
                    OnConnect(_loginCredentials, BattlEyeConnectionResult.InvalidLogin);
                    return BattlEyeConnectionResult.InvalidLogin;
                }

                localSocket.Dispose();
            }
            catch (OperationCanceledException)
            {
                localSocket?.Dispose();
                LastErrorDiagnostic = $"Connection timed out waiting for {remoteEp}.";
                if (attempt < totalRetries && !ct.IsCancellationRequested) Thread.Sleep(20);
            }
            catch (SocketException sockEx)
            {
                localSocket?.Dispose();
                LastErrorDiagnostic = sockEx.SocketErrorCode switch
                {
                    SocketError.NetworkUnreachable => $"Network unreachable to {remoteEp}.",
                    SocketError.ConnectionRefused => $"Connection refused by {remoteEp}. Port closed or server offline.",
                    SocketError.TimedOut => $"Connection timed out waiting for {remoteEp}.",
                    _ => $"Socket error ({sockEx.SocketErrorCode}): {sockEx.Message}"
                };

                if (attempt < totalRetries && !ct.IsCancellationRequested) Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                localSocket?.Dispose();
                LastErrorDiagnostic = $"Unexpected connection error: {ex.Message}";
                if (attempt < totalRetries && !ct.IsCancellationRequested) Thread.Sleep(20);
            }
        }

        if (string.IsNullOrWhiteSpace(LastErrorDiagnostic))
        {
            LastErrorDiagnostic = $"Connection timed out after {totalRetries} attempts.";
        }

        OnConnect(_loginCredentials, BattlEyeConnectionResult.ConnectionFailed);
        return BattlEyeConnectionResult.ConnectionFailed;
    }

    public byte SendCommand(string command, bool log = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        byte seq = (byte)(Interlocked.Increment(ref _sequenceNumberCounter) & 0xFF);

        try
        {
            var socketRef = Volatile.Read(ref _socket);
            if (socketRef is not { Connected: true }) return seq;

            byte[] packet = ConstructPacket(PacketTypeCommand, seq, command);
            _lastPacketSent = DateTime.UtcNow;

            if (log) _pendingCommands[seq] = (packet, command, Stopwatch.GetTimestamp());
            SendRaw(packet);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient:Command] Error sending Seq={seq}: {ex.Message}", ex);
        }

        return seq;
    }

    [SuppressMessage("AsyncUsage", "PH_S005:DiscourageAsyncSuffix", Justification = "Adheres to TAP pattern conventions for async APIs")]
    public async Task<string?> SendCommandWithResponseAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (cancellationToken.IsCancellationRequested) return null;

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte seq = SendCommand(command, log: true);
        _pendingCommandTcs[seq] = tcs;

        try
        {
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingCommandTcs.TryRemove(seq, out _);
        }
    }

    public void SendCommand(BattlEyeCommand command, string parameters = "") =>
        SendCommand(Helpers.StringValueOf(command) + parameters, true);

    private void SendKeepAlive()
    {
        byte seq = (byte)(Interlocked.Increment(ref _sequenceNumberCounter) & 0xFF);
        try
        {
            var socketRef = Volatile.Read(ref _socket);
            if (socketRef is not { Connected: true }) return;

            byte[] keepAlivePacket = ConstructPacket(PacketTypeCommand, seq, command: null);
            _lastPacketSent = DateTime.UtcNow;
            _pendingCommands[seq] = (keepAlivePacket, "KeepAlive", Stopwatch.GetTimestamp());
            SendRaw(keepAlivePacket);
            Interlocked.Increment(ref _totalKeepAlivesSent);
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[BattlEyeClient:KeepAlive] Notice: {ex.Message}");
        }
    }

    private void SendServerMessageAcknowledge(byte sequenceNumber)
    {
        try
        {
            var socketRef = Volatile.Read(ref _socket);
            if (socketRef is not { Connected: true }) return;

            byte[] ackPacket = ConstructPacket(PacketTypeServerMessage, sequenceNumber, command: null);
            _lastPacketSent = DateTime.UtcNow;
            SendRaw(ackPacket);
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[BattlEyeClient:ACK] Notice: {ex.Message}");
        }
    }

    private void SendRaw(byte[] packet)
    {
        try
        {
            var socketRef = Volatile.Read(ref _socket);
            socketRef?.Send(packet);
            Interlocked.Increment(ref _totalPacketsSent);
            Interlocked.Add(ref _totalBytesSent, packet.Length);
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[BattlEyeClient:SendRaw] Notice: {ex.Message}");
        }
    }

    private void UpdatePing(int sampleRttMs)
    {
        if (sampleRttMs <= 0) sampleRttMs = 1;
        lock (_syncLock)
        {
            LastPingMs = LastPingMs <= 0
                ? sampleRttMs
                : (int)Math.Round((LastPingMs * 0.7) + (sampleRttMs * 0.3));
        }
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
        if (sequenceNumber.HasValue) payload[offset++] = sequenceNumber.Value;
        if (commandBytes != null && commandLength > 0) Buffer.BlockCopy(commandBytes, 0, payload, offset, commandLength);

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
        if (length < 7 || buffer[0] != HeaderByteB || buffer[1] != HeaderByteE || buffer[6] != HeaderByteSplit) return false;

        uint expectedChecksum = (uint)(buffer[2] | (buffer[3] << 8) | (buffer[4] << 16) | (buffer[5] << 24));
        ReadOnlySpan<byte> payloadBytes = buffer.AsSpan(6, length - 6);

        if (CRC32.Compute(payloadBytes) != expectedChecksum) return false;

        payload = buffer.AsSpan(7, length - 7);
        return true;
    }

    public void Disconnect() => Disconnect(BattlEyeDisconnectionType.Manual);

    private void Disconnect(BattlEyeDisconnectionType? disconnectionType)
    {
        _keepRunning = false;

        _maintenanceTimer?.Dispose();
        _maintenanceTimer = null;

        // Give the 15ms poll slice up to 25ms to exit cleanly before closing the socket handle.
        // This eliminates the WSA_OPERATION_ABORTED SocketException entirely.
        try
        {
            _receiveTask?.Wait(25);
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[BattlEyeClient:Disconnect] Notice awaiting receive task: {ex.Message}");
        }

        var socketToClose = Interlocked.Exchange(ref _socket, null);
        if (socketToClose != null)
        {
            try
            {
                socketToClose.Close();
                socketToClose.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[BattlEyeClient:Disconnect] Socket close notice: {ex.Message}");
            }
        }

        if (disconnectionType != null)
            OnDisconnect(_loginCredentials, disconnectionType);
    }

    private void StartAsyncReceiveLoop(Socket activeSocket)
    {
        _maintenanceTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        _receiveTask = Task.Run(() =>
        {
            var buffer = new byte[65536];

            while (_keepRunning)
            {
                try
                {
                    // 15ms poll slice: returns in microseconds as soon as a packet arrives.
                    // When disconnecting, exits cleanly without triggering WSA_OPERATION_ABORTED SocketException.
                    if (activeSocket.Poll(15_000, SelectMode.SelectRead))
                    {
                        if (!_keepRunning) break;

                        int bytesRead = activeSocket.Receive(buffer, SocketFlags.None);
                        if (bytesRead == 0) break;

                        Interlocked.Increment(ref _totalPacketsReceived);
                        Interlocked.Add(ref _totalBytesReceived, bytesRead);

                        if (ValidatePacket(buffer, bytesRead, out ReadOnlySpan<byte> payload))
                        {
                            ProcessReceivedPayload(payload);
                        }
                    }
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_keepRunning) AppLogger.Trace($"[BattlEyeClient:Receive] Notice: {ex.Message}");
                    break;
                }
            }

            if (_keepRunning && ReconnectOnPacketLoss) _ = ConnectAsync(CancellationToken.None);
        }, CancellationToken.None);

        Task.Run(async () =>
        {
            var timer = _maintenanceTimer;
            if (timer == null) return;

            try
            {
                while (_keepRunning && await timer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastPacketSent).TotalSeconds >= 10 && _pendingCommands.IsEmpty) SendKeepAlive();

                    if ((now - _lastPacketReceived).TotalSeconds >= 35)
                    {
                        AppLogger.Warn("[BattlEyeClient:KeepAlive] Connection timed out (35s without incoming packets).");
                        Disconnect(BattlEyeDisconnectionType.ConnectionLost);
                        break;
                    }

                    RetryPendingCommands();
                }
            }
            catch (ObjectDisposedException ex)
            {
                AppLogger.Trace($"[BattlEyeClient:Maintenance] Periodic timer disposed during loop exit: {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[BattlEyeClient:Maintenance] Notice: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private void RetryPendingCommands()
    {
        if (_pendingCommands.IsEmpty) return;

        foreach (var (seq, pending) in _pendingCommands)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(pending.SentTimestamp).TotalMilliseconds;
            if (elapsedMs >= 1200 && (_currentResendPacket == -1 || _currentResendPacket == seq))
            {
                _currentResendPacket = seq;
                _pendingCommands[seq] = (pending.Packet, pending.Command, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _totalRetransmissions);
                SendRaw(pending.Packet);
                break;
            }
        }
    }

    private void ProcessReceivedPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return;

        _lastPacketReceived = DateTime.UtcNow;
        switch (payload[0])
        {
            case PacketTypeCommand:
                ProcessCommandResponse(payload);
                break;
            case PacketTypeServerMessage:
                ProcessServerMessage(payload);
                break;
        }
    }

    private void ProcessCommandResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return;

        byte seq = payload[1];
        if (_pendingCommands.TryRemove(seq, out var pendingCommand))
        {
            UpdatePing((int)Stopwatch.GetElapsedTime(pendingCommand.SentTimestamp).TotalMilliseconds);
        }

        if (_currentResendPacket == seq) _currentResendPacket = -1;

        if (payload.Length == 2)
        {
            if (_pendingCommandTcs.TryRemove(seq, out var pendingTcs)) pendingTcs.TrySetResult(string.Empty);
            return;
        }

        if (payload[2] == 0x00)
        {
            if (payload.Length > 5 && payload.Length != 11 && payload[3] > 0 && payload[3] <= 32 && payload[4] < payload[3])
            {
                byte totalPackets = payload[3];
                byte packetIndex = payload[4];

                string chunkText = Encoding.UTF8.GetString(payload[5..]);
                var buffer = _multiPacketResponses.GetOrAdd(seq, _ => new MultiPacketBuffer());
                if (buffer.TryAddChunk(packetIndex, totalPackets, chunkText, out var fullMessage))
                {
                    _multiPacketResponses.TryRemove(seq, out _);
                    if (_pendingCommandTcs.TryRemove(seq, out var pendingTcs)) pendingTcs.TrySetResult(fullMessage);
                    OnBattlEyeMessage(fullMessage, seq);
                }
                return;
            }

            if (_pendingCommandTcs.TryRemove(seq, out var emptyAckTcs)) emptyAckTcs.TrySetResult(string.Empty);
            return;
        }

        string responseText = Encoding.UTF8.GetString(payload[2..]);
        if (_pendingCommandTcs.TryRemove(seq, out var directTcs)) directTcs.TrySetResult(responseText);
        OnBattlEyeMessage(responseText, seq);
    }

    private void ProcessServerMessage(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return;

        byte seq = payload[1];
        SendServerMessageAcknowledge(seq);

        if (payload.Length > 2)
        {
            OnBattlEyeMessage(Encoding.UTF8.GetString(payload[2..]), 256);
        }
    }

    private void OnBattlEyeMessage(string message, int id)
    {
        try { BattlEyeMessageReceived?.Invoke(new BattlEyeMessageEventArgs(message, id)); }
        catch (Exception ex) { AppLogger.Error($"[BattlEyeClient:Event] Message subscriber error: {ex.Message}", ex); }
    }

    private void OnConnect(BattlEyeLoginCredentials loginDetails, BattlEyeConnectionResult connectionResult)
    {
        try { BattlEyeConnected?.Invoke(new BattlEyeConnectEventArgs(loginDetails, connectionResult)); }
        catch (Exception ex) { AppLogger.Error($"[BattlEyeClient:Event] Connect subscriber error: {ex.Message}", ex); }
    }

    private void OnDisconnect(BattlEyeLoginCredentials loginDetails, BattlEyeDisconnectionType? disconnectionType)
    {
        try { BattlEyeDisconnected?.Invoke(new BattlEyeDisconnectEventArgs(loginDetails, disconnectionType)); }
        catch (Exception ex) { AppLogger.Error($"[BattlEyeClient:Event] Disconnect subscriber error: {ex.Message}", ex); }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                Disconnect(BattlEyeDisconnectionType.Manual);
                _socket?.Dispose();
            }
            _isDisposed = true;
        }
    }
    private sealed class MultiPacketBuffer
    {
        private readonly Lock _lock = new();
        private readonly SortedDictionary<byte, string> _chunks = [];

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