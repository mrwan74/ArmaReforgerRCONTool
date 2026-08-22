using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

public interface IRconService : IDisposable
{
    RconProtocol CurrentProtocol { get; }
    bool IsConnected { get; }
    int PingMs { get; }
    DateTime LastPacketTime { get; }

    event EventHandler<string>? OutputReceived;
    event EventHandler<PlayerModel>? PlayerJoined;
    event EventHandler<PlayerModel>? PlayerLeft;
    event EventHandler<string>? ConnectionLost;

    Task<bool> ConnectAsync(ServerProfile profile);
    Task DisconnectAsync();

    Task<List<PlayerModel>> GetPlayersAsync(CancellationToken cancellationToken = default);
    Task<List<BanModel>> GetBansAsync(CancellationToken cancellationToken = default);
    Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync(CancellationToken cancellationToken = default);

    Task<bool> KickPlayerAsync(PlayerModel player, string reason, CancellationToken cancellationToken = default);
    Task<bool> BanPlayerAsync(PlayerModel player, long durationSeconds, string reason, CancellationToken cancellationToken = default);
    Task<bool> OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp, CancellationToken cancellationToken = default);
    Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default);

    Task SendCommandAsync(string rawCommand);
    Task RestartServerAsync(CancellationToken cancellationToken = default);
    Task ShutdownServerAsync(CancellationToken cancellationToken = default);
    Task SendGlobalMessageAsync(string message, CancellationToken cancellationToken = default);
    Task SendAnnouncementAsync(string title, string message, CancellationToken cancellationToken = default);
    Task UpdatePlayerCommentAsync(string uid, string comment);
    Task ClearDatabaseAsync();
}