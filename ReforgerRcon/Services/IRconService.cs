using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

public interface IRconService
{
    RconProtocol CurrentProtocol { get; }
    bool IsConnected { get; }
    int PingMs { get; }
    DateTime LastPacketTime { get; }

    event EventHandler<string>? OutputReceived;
    event EventHandler<PlayerModel>? PlayerJoined;
    event EventHandler<PlayerModel>? PlayerLeft;

    Task<bool> ConnectAsync(ServerProfile profile);
    Task DisconnectAsync();

    Task<List<PlayerModel>> GetPlayersAsync();
    Task<List<BanModel>> GetBansAsync();
    Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync();

    Task KickPlayerAsync(PlayerModel player, string reason);
    Task BanPlayerAsync(PlayerModel player, long durationSeconds, string reason);
    Task OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp);
    Task RemoveBanAsync(BanModel ban);

    Task SendCommandAsync(string rawCommand);
    Task RestartServerAsync();
    Task ShutdownServerAsync();
    Task SendGlobalMessageAsync(string message);
    Task SendAnnouncementAsync(string title, string message);
    Task UpdatePlayerCommentAsync(string uid, string comment);
    Task ClearDatabaseAsync();
}