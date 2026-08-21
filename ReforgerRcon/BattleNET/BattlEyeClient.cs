using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.Services;

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
    private BattlEyeDisconnectionType? _disconnectionType;
    private volatile bool _keepRunning;
    private byte _sequenceNumber;
    private int _currentResendPacket = -1;
    private bool _isDisposed;

    private readonly ConcurrentDictionary<byte, (byte[] Packet, string Command, DateTime SentTime)> _pendingCommands = new();
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<string>> _pendingCommandTcs = new();
    private readonly ConcurrentDictionary<byte, MultiPacketBuffer> _multiPacketResponses = new();
    private readonly BattlEyeLoginCredentials _loginCredentials = loginCredentials;
    private readonly Lock _syncLock = new();

    public bool Connected => _socket is { Connected: true };
    public bool ReconnectOnPacketLoss { get; set; } = true;
    public int CommandQueue => _pendingCommands.Count;
    public int LastPingMs { get; private set; }

    public event BattlEyeMessageEventHandler? BattlEyeMessageReceived;
    public event BattlEyeConnectEventHandler? BattlEyeConnected;
    public event BattlEyeDisconnectEventHandler? BattlEyeDisconnected;

    public Task<BattlEyeConnectionResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ConnectInternal(5, cancellationToken), cancellationToken);
    }

    public BattlEyeConnectionResult Connect()
    {
        return ConnectInternal(5, CancellationToken.None);
    }

    private BattlEyeConnectionResult ConnectInternal(int retryCounter, CancellationToken ct)
    {
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

            try
            {
                _socket?.Dispose();
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ReceiveBufferSize = 65535,
                    SendBufferSize = 65535,
                    ReceiveTimeout = 5000,
                    SendTimeout = 5000
                };

                var remoteEp = new IPEndPoint(_loginCredentials.Host, _loginCredentials.Port);
                AppLogger.Info($"[BattlEyeClient] Initializing connection to {remoteEp} (Attempt #{6 - retryCounter})...");
                _socket.Connect(remoteEp);

                byte[] loginPacket = ConstructPacket(PacketTypeLogin, sequenceNumber: null, _loginCredentials.Password);
                AppLogger.Trace($"[BattlEyeClient] Outgoing Login Packet ({loginPacket.Length} bytes): {Convert.ToHexString(loginPacket)}");
                _socket.Send(loginPacket);
                _lastPacketSent = DateTime.UtcNow;

                var receiveBuffer = new byte[4096];
                int bytesReceived = _socket.Receive(receiveBuffer, receiveBuffer.Length, SocketFlags.None);
                AppLogger.Trace($"[BattlEyeClient] Received Handshake Response ({bytesReceived} bytes): {Convert.ToHexString(receiveBuffer, 0, bytesReceived)}");

                if (ValidatePacket(receiveBuffer, bytesReceived, out ReadOnlySpan<byte> payload) &&
                    payload.Length >= 2 &&
                    payload[0] == PacketTypeLogin)
                {
                    if (payload[1] == 0x01)
                    {
                        AppLogger.Info($"[BattlEyeClient] Handshake SUCCESS: Logged in to {remoteEp}.");
                        OnConnect(_loginCredentials, BattlEyeConnectionResult.Success);
                        StartReceiveLoop();
                        return BattlEyeConnectionResult.Success;
                    }

                    AppLogger.Warn($"[BattlEyeClient] Handshake REJECTED: Invalid password for {remoteEp}.");
                    OnConnect(_loginCredentials, BattlEyeConnectionResult.InvalidLogin);
                    return BattlEyeConnectionResult.InvalidLogin;
                }
            }
            catch (SocketException sockEx)
            {
                AppLogger.Error($"[BattlEyeClient] Socket error connecting to {_loginCredentials.Host}:{_loginCredentials.Port} ({sockEx.SocketErrorCode})", sockEx);

                if (_disconnectionType == BattlEyeDisconnectionType.ConnectionLost && retryCounter > 0 && !ct.IsCancellationRequested)
                {
                    Disconnect(BattlEyeDisconnectionType.ConnectionLost);
                    Thread.Sleep(1000);
                    return ConnectInternal(retryCounter - 1, ct);
                }

                OnConnect(_loginCredentials, BattlEyeConnectionResult.ConnectionFailed);
                return BattlEyeConnectionResult.ConnectionFailed;
            }
            catch (ObjectDisposedException dispEx)
            {
                AppLogger.Warn($"[BattlEyeClient] Socket was disposed during connection attempt: {dispEx.Message}");
                OnConnect(_loginCredentials, BattlEyeConnectionResult.ConnectionFailed);
                return BattlEyeConnectionResult.ConnectionFailed;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[BattlEyeClient] Unexpected error during connection to {_loginCredentials.Host}:{_loginCredentials.Port}", ex);
                OnConnect(_loginCredentials, BattlEyeConnectionResult.ConnectionFailed);
                return BattlEyeConnectionResult.ConnectionFailed;
            }

            AppLogger.Warn("[BattlEyeClient] Handshake timed out: No response from server.");
            OnConnect(_loginCredentials, BattlEyeConnectionResult.ConnectionFailed);
            return BattlEyeConnectionResult.ConnectionFailed;
        }
    }

    public byte SendCommand(string command, bool log = true)
    {
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
                AppLogger.Warn($"[BattlEyeClient] Cannot send command '{command}': Socket disconnected.");
                return seq;
            }

            byte[] packet = ConstructPacket(PacketTypeCommand, seq, command);
            _lastPacketSent = DateTime.UtcNow;

            if (log)
            {
                _pendingCommands[seq] = (packet, command, _lastPacketSent);
            }

            AppLogger.Debug($"[BattlEyeClient] Outgoing Command (Seq: {seq}, Cmd: '{command}', Bytes: {packet.Length}): {Convert.ToHexString(packet)}");
            SendRaw(packet);
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[BattlEyeClient] Socket error sending command (Seq: {seq}, Cmd: '{command}'): {sockEx.SocketErrorCode}", sockEx);
        }
        catch (ObjectDisposedException dispEx)
        {
            AppLogger.Warn($"[BattlEyeClient] Socket disposed while sending command '{command}': {dispEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeClient] Unexpected failure sending command '{command}' (Seq: {seq})", ex);
        }

        return seq;
    }

    public async Task<string> SendCommandWithResponseAsync(string command, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte seq = SendCommand(command, log: true);
        _pendingCommandTcs[seq] = tcs;

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() =>
        {
            if (_pendingCommandTcs.TryRemove(seq, out var pending))
            {
                pending.TrySetCanceled();
            }
        });

        try
        {
            return await tcs.Task;
        }
        catch (TaskCanceledException)
        {
            AppLogger.Trace($"[BattlEyeClient] Command '{command}' (Seq: {seq}) direct response wait period completed.");
            return string.Empty;
        }
    }

    public void SendCommand(BattlEyeCommand command, string parameters = "")
    {
        SendCommand(Helpers.StringValueOf(command) + parameters, true);
    }

    private void SendKeepAlive()
    {
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
            SendRaw(keepAlivePacket);
            AppLogger.Trace($"[BattlEyeClient] KeepAlive Heartbeat sent (Seq: {seq}).");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Warn($"[BattlEyeClient] Socket error sending keepalive: {sockEx.SocketErrorCode}");
        }
        catch (ObjectDisposedException dispEx)
        {
            AppLogger.Debug($"[BattlEyeClient] Socket disposed during keepalive transmission: {dispEx.Message}");
        }
    }

    private void SendServerMessageAcknowledge(byte sequenceNumber)
    {
        try
        {
            if (_socket is not { Connected: true }) return;

            byte[] ackPacket = ConstructPacket(PacketTypeServerMessage, sequenceNumber, command: null);
            _lastPacketSent = DateTime.UtcNow;
            SendRaw(ackPacket);
            AppLogger.Trace($"[BattlEyeClient] Outgoing Server Message ACK (Seq: {sequenceNumber}, Bytes: {ackPacket.Length}): {Convert.ToHexString(ackPacket)}");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[BattlEyeClient] Socket error sending Server Message ACK (Seq: {sequenceNumber}): {sockEx.SocketErrorCode}", sockEx);
        }
        catch (ObjectDisposedException dispEx)
        {
            AppLogger.Debug($"[BattlEyeClient] Socket disposed during server message ACK (Seq: {sequenceNumber}): {dispEx.Message}");
        }
    }

    private void SendRaw(byte[] packet)
    {
        try
        {
            _socket?.Send(packet);
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[BattlEyeClient] SocketException in SendRaw: {sockEx.SocketErrorCode}", sockEx);
        }
        catch (ObjectDisposedException)
        {
            AppLogger.Debug("[BattlEyeClient] SendRaw aborted: Socket disposed.");
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
            AppLogger.Warn($"[BattlEyeClient] Received packet length ({length}) is below 7-byte header minimum.");
            return false;
        }

        if (buffer[0] != HeaderByteB || buffer[1] != HeaderByteE || buffer[6] != HeaderByteSplit)
        {
            AppLogger.Warn($"[BattlEyeClient] Malformed header bytes: [{buffer[0]:X2} {buffer[1]:X2}] Split: {buffer[6]:X2}");
            return false;
        }

        uint expectedChecksum = (uint)(buffer[2] | (buffer[3] << 8) | (buffer[4] << 16) | (buffer[5] << 24));
        ReadOnlySpan<byte> payloadBytes = buffer.AsSpan(6, length - 6);

        uint actualChecksum = CRC32.Compute(payloadBytes);
        if (actualChecksum != expectedChecksum)
        {
            AppLogger.Warn($"[BattlEyeClient] CRC32 mismatch (Expected: {expectedChecksum:X8}, Got: {actualChecksum:X8}).");
            return false;
        }

        payload = buffer.AsSpan(7, length - 7);
        return true;
    }

    public void Disconnect() => Disconnect(BattlEyeDisconnectionType.Manual);

    private void Disconnect(BattlEyeDisconnectionType? disconnectionType)
    {
        _keepRunning = false;
        AppLogger.Info($"[BattlEyeClient] Disconnecting (Type: {disconnectionType?.ToString() ?? "Manual"})...");

        lock (_syncLock)
        {
            if (disconnectionType == BattlEyeDisconnectionType.ConnectionLost)
                _disconnectionType = BattlEyeDisconnectionType.ConnectionLost;

            try
            {
                if (_socket is { Connected: true })
                {
                    if (OperatingSystem.IsWindows())
                    {
                        _socket.Shutdown(SocketShutdown.Both);
                    }
                    _socket.Close();
                }
            }
            catch (SocketException sockEx)
            {
                AppLogger.Debug($"[BattlEyeClient] SocketException during disconnect: {sockEx.SocketErrorCode}");
            }
            catch (ObjectDisposedException dispEx)
            {
                AppLogger.Debug($"[BattlEyeClient] Socket already disposed during disconnect: {dispEx.Message}");
            }
        }

        if (disconnectionType != null)
            OnDisconnect(_loginCredentials, disconnectionType);
    }

    private void StartReceiveLoop()
    {
        Task.Run(async () =>
        {
            var buffer = new byte[65536];

            while (_socket is { Connected: true } && _keepRunning)
            {
                try
                {
                    if (_socket.Available > 0)
                    {
                        int bytesRead = _socket.Receive(buffer);
                        if (ValidatePacket(buffer, bytesRead, out ReadOnlySpan<byte> payload))
                        {
                            ProcessReceivedPayload(payload);
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
                    AppLogger.Warn($"[BattlEyeClient] SocketException in receive loop: {ex.SocketErrorCode} ({ex.Message})");
                    if (_keepRunning)
                    {
                        Disconnect(BattlEyeDisconnectionType.SocketException);
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    AppLogger.Debug("[BattlEyeClient] Receive loop terminated: Socket closed.");
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLogger.Error("[BattlEyeClient] Unexpected error in receive worker loop.", ex);
                }

                await Task.Delay(10);
            }

            if (_keepRunning && ReconnectOnPacketLoss)
            {
                AppLogger.Info("[BattlEyeClient] Automatic reconnect triggered.");
                _ = ConnectAsync();
            }
        });
    }

    private bool CheckKeepAliveAndTimeout()
    {
        var now = DateTime.UtcNow;
        var timeoutClient = (now - _lastPacketSent).TotalSeconds;
        var timeoutServer = (now - _lastPacketReceived).TotalSeconds;

        if (timeoutClient >= 15 && _pendingCommands.IsEmpty)
        {
            SendKeepAlive();
        }

        if (timeoutServer >= 35)
        {
            AppLogger.Warn("[BattlEyeClient] Timed out: 35 seconds without server response.");
            Disconnect(BattlEyeDisconnectionType.ConnectionLost);
            return false;
        }

        return true;
    }

    private void RetryPendingCommands()
    {
        if (_pendingCommands.IsEmpty || _socket is not { Available: 0 }) return;

        var now = DateTime.UtcNow;
        foreach (var (seq, pending) in _pendingCommands)
        {
            if ((now - pending.SentTime).TotalMilliseconds >= 1200 &&
                (_currentResendPacket == -1 || _currentResendPacket == seq))
            {
                _currentResendPacket = seq;
                _pendingCommands[seq] = (pending.Packet, pending.Command, now);
                AppLogger.Trace($"[BattlEyeClient] Retransmitting unacknowledged command (Seq: {seq}, Cmd: '{pending.Command}')...");
                SendRaw(pending.Packet);
                break;
            }
        }
    }

    private void ProcessReceivedPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return;

        _lastPacketReceived = DateTime.UtcNow;
        LastPingMs = Math.Max(1, (int)(_lastPacketReceived - _lastPacketSent).TotalMilliseconds);

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
                AppLogger.Warn($"[BattlEyeClient] Unknown packet type received: 0x{packetType:X2}");
                break;
        }
    }

    private void ProcessCommandResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return;

        byte seq = payload[1];
        _pendingCommands.TryRemove(seq, out _);
        if (_currentResendPacket == seq)
        {
            _currentResendPacket = -1;
        }

        if (payload.Length == 2)
        {
            AppLogger.Trace($"[BattlEyeClient] Empty Command ACK (Seq: {seq}).");
            if (_pendingCommandTcs.TryRemove(seq, out var pendingTcs))
            {
                pendingTcs.TrySetResult(string.Empty);
            }
            return;
        }

        if (payload.Length >= 5 && payload[2] == 0x00)
        {
            byte totalPackets = payload[3];
            byte packetIndex = payload[4];

            string chunkText = Encoding.UTF8.GetString(payload[5..]);
            AppLogger.Trace($"[BattlEyeClient] Multi-packet chunk (Seq: {seq}, Index: {packetIndex + 1}/{totalPackets}, Length: {chunkText.Length}).");

            var buffer = _multiPacketResponses.GetOrAdd(seq, _ => new MultiPacketBuffer());
            if (buffer.TryAddChunk(packetIndex, totalPackets, chunkText, out var fullMessage))
            {
                _multiPacketResponses.TryRemove(seq, out _);
                AppLogger.Debug($"[BattlEyeClient] Completed Multi-Packet Response (Seq: {seq}, Total Length: {fullMessage.Length}).");

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
            AppLogger.Debug($"[BattlEyeClient] Command Response received (Seq: {seq}, Length: {responseText.Length}): '{responseText}'");

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
            AppLogger.Debug($"[BattlEyeClient] Server Message received (Seq: {seq}): '{message}'");
            OnBattlEyeMessage(message, 256);
        }
    }

    private void OnBattlEyeMessage(string message, int id) => BattlEyeMessageReceived?.Invoke(new BattlEyeMessageEventArgs(message, id));
    private void OnConnect(BattlEyeLoginCredentials loginDetails, BattlEyeConnectionResult connectionResult) => BattlEyeConnected?.Invoke(new BattlEyeConnectEventArgs(loginDetails, connectionResult));
    private void OnDisconnect(BattlEyeLoginCredentials loginDetails, BattlEyeDisconnectionType? disconnectionType) => BattlEyeDisconnected?.Invoke(new BattlEyeDisconnectEventArgs(loginDetails, disconnectionType));

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
                Disconnect(BattlEyeDisconnectionType.Manual);
                lock (_syncLock)
                {
                    _socket?.Dispose();
                    _socket = null;
                }
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