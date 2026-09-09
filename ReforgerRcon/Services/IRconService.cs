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
    string LastConnectionError { get; }
    RconProtocol? DetectedProtocolMismatch { get; }

    event EventHandler<string>? OutputReceived;
    event EventHandler<PlayerModel>? PlayerJoined;
    event EventHandler<PlayerModel>? PlayerLeft;
    event EventHandler<(string Name, int Id, string Reason)>? PlayerKickedStream;
    event EventHandler<(string Name, int Id, string Guid, string Reason)>? PlayerBannedStream;
    event EventHandler<(int AdminId, string Endpoint)>? AdminConnectedStream;
    event EventHandler<string>? ConnectionLost;
    event EventHandler<RconProtocol>? ProtocolMismatchDetected;

    Task<bool> ConnectAsync(ServerProfile profile, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<List<PlayerModel>> GetPlayersAsync(CancellationToken cancellationToken = default);
    Task<List<BanModel>> GetBansAsync(int maxPages = 0, CancellationToken cancellationToken = default);
    Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<DatabasePlayerModel>> GetPagedDatabasePlayersAsync(DatabaseQueryParameters parameters, CancellationToken cancellationToken = default);
    Task<List<AdminModel>> GetAdminsAsync(CancellationToken cancellationToken = default);

    Task<bool> KickPlayerAsync(PlayerModel player, string reason, CancellationToken cancellationToken = default);
    Task<bool> BanPlayerAsync(PlayerModel player, long durationSeconds, string reason, CancellationToken cancellationToken = default);
    Task<bool> BanPlayerWithOptionalIpAsync(PlayerModel player, long durationSeconds, string reason, bool banIp, CancellationToken cancellationToken = default);
    Task<bool> OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp, CancellationToken cancellationToken = default);
    Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default);

    Task SendCommandAsync(string rawCommand, CancellationToken cancellationToken = default);
    Task RestartServerAsync(CancellationToken cancellationToken = default);
    Task ShutdownServerAsync(CancellationToken cancellationToken = default);
    Task SendGlobalMessageAsync(string message, CancellationToken cancellationToken = default);
    Task SendAnnouncementAsync(string title, string message, CancellationToken cancellationToken = default);
    Task UpdatePlayerCommentAsync(string uid, string comment, CancellationToken cancellationToken = default);
    Task ClearDatabaseAsync(CancellationToken cancellationToken = default);
}