using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

[SuppressMessage("Security", "S1313:IP address should not be hardcoded", Justification = "RFC 5737 documentation placeholder subnets for demo simulation")]
public class MockRconService : IRconService
{
    private ServerProfile? _currentProfile;
    private readonly List<PlayerModel> _players = [];
    private readonly List<BanModel> _bans = [];
    private bool _isDisposed;

    public RconProtocol CurrentProtocol => _currentProfile?.Protocol ?? RconProtocol.ReforgerBuiltIn;
    public bool IsConnected { get; private set; }
    public int PingMs { get; } = 28;
    public DateTime LastPacketTime { get; private set; } = DateTime.UtcNow;

    public event EventHandler<PlayerModel>? PlayerJoined;
    public event EventHandler<PlayerModel>? PlayerLeft;
    public event EventHandler<(string Name, int Id, string Reason)>? PlayerKickedStream;
    public event EventHandler<(string Name, int Id, string Guid, string Reason)>? PlayerBannedStream;
    public event EventHandler<(int AdminId, string Endpoint)>? AdminConnectedStream;
    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ConnectionLost;

    private static readonly (int id, string name, string uid, string guid, string ip, int port, int ping, string cc, string cn, string city, string state, bool watch, bool warn, string comment, string[] aliases)[] MockPlayers =
    [
        (1, "VanguardLead", "00000000-0000-4000-8000-000000000001", "00000000000040008000000000000001", "192.0.2.10", 2304, 18, "de", "Germany", "Frankfurt", "Hesse", false, false, "Server Administrator / Test Operator", ["VanguardLead"]),
        (2, "ShadowRecon", "00000000-0000-4000-8000-000000000002", "00000000000040008000000000000002", "192.0.2.25", 2305, 34, "gb", "United Kingdom", "London", "Greater London", true, false, "Squad Leader", ["ShadowRecon", "Shadow_Old"]),
        (3, "SierraMarksman", "00000000-0000-4000-8000-000000000003", "00000000000040008000000000000003", "198.51.100.33", 2304, 42, "fr", "France", "Paris", "Ile-de-France", false, false, "Dedicated Sniper", ["SierraMarksman"]),
        (4, "DeltaAviator", "00000000-0000-4000-8000-000000000004", "00000000000040008000000000000004", "198.51.100.37", 2306, 68, "ca", "Canada", "Montreal", "Quebec", false, false, "Rotary Wing Transport Pilot", ["DeltaAviator"]),
        (5, "EchoOperator", "00000000-0000-4000-8000-000000000005", "00000000000040008000000000000005", "203.0.113.38", 2304, 115, "au", "Australia", "Sydney", "NSW", true, true, "Watchlisted: Frequent team-damage alerts", ["EchoOperator", "Echo_Alt"]),
        (6, "ApexGunner", "00000000-0000-4000-8000-000000000006", "00000000000040008000000000000006", "192.0.2.48", 2307, 45, "us", "United States", "Dallas", "Texas", false, false, "Regular Infantry", ["ApexGunner"]),
        (7, "KiloTactical", "00000000-0000-4000-8000-000000000007", "00000000000040008000000000000007", "198.51.100.22", 2304, 22, "jp", "Japan", "Tokyo", "Tokyo", true, false, "Verified Clan Member", ["KiloTactical"]),
        (8, "Ironclad_99", "00000000-0000-4000-8000-000000000008", "00000000000040008000000000000008", "203.0.113.50", 60464, 25, "us", "United States", "Chicago", "Illinois", false, false, "Heavy Armor Operator", ["Ironclad_99"])
    ];

    private static readonly (string identity, string name, string reason, long durationSeconds)[] MockServerBans =
    [
        ("a0000001-0000-4000-8000-000000000001", "GriefingTarget_1", "Intentional friendly base structure destruction", 0),
        ("a0000002-0000-4000-8000-000000000002", "ExploitUser_2", "Terrain collision clipping / Map geometry exploit", 604800),
        ("a0000003-0000-4000-8000-000000000003", "MemoryMod_3", "Third-party memory modification / Unofficial DLL injection", 0),
        ("a0000004-0000-4000-8000-000000000004", "ToxicityTarget_4", "Excessive verbal toxicity in side radio channel", 86400),
        ("a0000005-0000-4000-8000-000000000005", "SpamBot_5", "Automated chat advertisement spamming", 0),
        ("a0000006-0000-4000-8000-000000000006", "SpawnKiller_6", "Intentional main base spawn teamkilling", 259200),
        ("a0000007-0000-4000-8000-000000000007", "AssetDupe_7", "Logistics asset duplication glitching", 2592000),
        ("a0000008-0000-4000-8000-000000000008", "StreamSniper_8", "Targeted stream sniping / Disruptive gameplay", 604800),
        ("a0000009-0000-4000-8000-000000000009", "VehicleTheft_9", "Stealing friendly transport trucks from main spawn", 86400),
        ("a0000010-0000-4000-8000-000000000010", "AudioSpam_10", "Continuous mic audio spamming over global channel", 21600),
        ("a0000011-0000-4000-8000-000000000011", "BanEvader_11", "Ban evasion attempt / Alternate account", 0)
    ];

    public MockRconService()
    {
        var start = Stopwatch.GetTimestamp();
        SeedMockData();
        AppLogger.Info($"[MockRconService] Initialized demo simulated dataset in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms ({_players.Count} players, {_bans.Count} bans).");
    }

    private void SeedMockData()
    {
        foreach (var (id, name, uid, guid, ip, port, ping, cc, cn, city, state, watch, warn, comment, aliases) in MockPlayers)
        {
            _players.Add(new PlayerModel
            {
                Id = id,
                Name = name,
                Uid = uid,
                Guid = guid,
                Ip = ip,
                Port = port,
                Ping = ping,
                Country = new CountryInfo { Code = cc, Name = cn },
                LocationCity = city,
                LocationState = state,
                IsWatchlisted = watch,
                HasAliases = warn,
                Comment = comment,
                Aliases = [.. aliases]
            });
        }

        int banIndex = 1;
        foreach (var (identity, name, reason, durationSeconds) in MockServerBans)
        {
            _bans.Add(new BanModel
            {
                BanNumber = banIndex++,
                IdentityId = identity,
                BannedName = name,
                Reason = reason,
                DurationSeconds = durationSeconds,
                BannedAt = DateTime.UtcNow.AddDays(-banIndex)
            });
        }
    }

    public async Task<bool> ConnectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = Stopwatch.GetTimestamp();
        _currentProfile = profile;
        await Task.Delay(400, cancellationToken);
        IsConnected = true;
        LastPacketTime = DateTime.UtcNow;

        OutputReceived?.Invoke(this, $"[SYSTEM] Connected to {profile.ServerIp}:{profile.Port} via {profile.Protocol}");
        OutputReceived?.Invoke(this, "[RCON] Logged in successfully as Administrator (Demo Simulation Mode).");

        await PlayerDatabaseStorageService.RecordSeenPlayersAsync(_players, profile.Protocol);

        if (profile.Protocol == RconProtocol.BattlEye)
        {
            AdminConnectedStream?.Invoke(this, (0, "127.0.0.1:5353"));
        }

        AppLogger.Info($"[MockRconService] ConnectAsync simulated in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        return true;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = Stopwatch.GetTimestamp();
        await Task.Delay(100, cancellationToken);
        IsConnected = false;
        await PlayerDatabaseStorageService.SetAllOfflineAsync(CurrentProtocol);
        OutputReceived?.Invoke(this, "[SYSTEM] Disconnected from server.");
        AppLogger.Info($"[MockRconService] Disconnected simulated in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
    }

    public void SimulatePlayerJoin(PlayerModel player)
    {
        _players.Add(player);
        AppLogger.Debug($"[MockRconService:Simulate] PlayerJoined simulated: '{player.Name}' (ID: #{player.Id})");
        PlayerJoined?.Invoke(this, player);
    }

    public void SimulatePlayerLeave(PlayerModel player)
    {
        _players.Remove(player);
        AppLogger.Debug($"[MockRconService:Simulate] PlayerLeft simulated: '{player.Name}' (ID: #{player.Id})");
        PlayerLeft?.Invoke(this, player);
    }

    public void SimulateConnectionDrop()
    {
        IsConnected = false;
        AppLogger.Warn("[MockRconService:Simulate] Connection drop simulated.");
        ConnectionLost?.Invoke(this, "Connection timed out (No packets received)");
    }

    public async Task<List<PlayerModel>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPacketTime = DateTime.UtcNow;
        await PlayerDatabaseStorageService.RecordSeenPlayersAsync(_players, CurrentProtocol);
        return [.. _players];
    }

    public Task<List<BanModel>> GetBansAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPacketTime = DateTime.UtcNow;
        return Task.FromResult(_bans.ToList());
    }

    public Task<List<AdminModel>> GetAdminsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new List<AdminModel>
        {
            new() { Id = 0, Ip = "127.0.0.1", Port = 5353, Country = new CountryInfo { Code = "us", Name = "United States" }, Location = "Localhost Admin" },
            new() { Id = 1, Ip = "192.0.2.55", Port = 6124, Country = new CountryInfo { Code = "de", Name = "Germany" }, Location = "Frankfurt, Germany" }
        });
    }

    public Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync(CancellationToken cancellationToken = default) => PlayerDatabaseStorageService.GetAllAsync(CurrentProtocol);

    public Task<bool> KickPlayerAsync(PlayerModel player, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _players.Remove(player);
        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#kick {player.Id} {reason}" : $"kick {player.Id} {reason}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Player '{player.Name}' kicked!");
        PlayerLeft?.Invoke(this, player);
        PlayerKickedStream?.Invoke(this, (player.Name, player.Id, reason));
        AppLogger.Info($"[MockRconService:Moderation] Simulated kick for '{player.Name}'");
        return Task.FromResult(true);
    }

    public Task<bool> BanPlayerAsync(PlayerModel player, long durationSeconds, string reason, CancellationToken cancellationToken = default)
    {
        return BanPlayerWithOptionalIpAsync(player, durationSeconds, reason, banIp: true, cancellationToken);
    }

    public Task<bool> BanPlayerWithOptionalIpAsync(PlayerModel player, long durationSeconds, string reason, bool banIp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _players.Remove(player);
        var ban = new BanModel
        {
            BanNumber = _bans.Count + 1,
            IdentityId = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? player.Uid : player.Guid,
            BannedName = player.Name,
            Reason = reason,
            DurationSeconds = durationSeconds,
            BannedAt = DateTime.UtcNow
        };
        _bans.Add(ban);

        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#ban create {player.Id} {durationSeconds} {reason}" : $"ban {player.Id} {durationSeconds / 60} {reason}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Ban added for {player.Name}.");
        PlayerLeft?.Invoke(this, player);
        PlayerBannedStream?.Invoke(this, (player.Name, player.Id, player.Guid, reason));
        AppLogger.Info($"[MockRconService:Moderation] Simulated ban for '{player.Name}' ({durationSeconds}s)");
        return Task.FromResult(true);
    }

    public Task<bool> OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ban = new BanModel
        {
            BanNumber = _bans.Count + 1,
            IdentityId = identity,
            BannedName = "Offline Target",
            Reason = reason,
            DurationSeconds = durationSeconds,
            BannedAt = DateTime.UtcNow
        };
        _bans.Add(ban);

        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#ban create {identity} {durationSeconds} {reason}" : $"addBan {identity} {durationSeconds / 60} {reason}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        AppLogger.Info($"[MockRconService:Moderation] Simulated offline ban for '{identity}'");
        return Task.FromResult(true);
    }

    public Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _bans.Remove(ban);
        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#ban remove {ban.IdentityId}" : $"removeBan {ban.BanNumber}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Ban removed for {ban.IdentityId}.");
        AppLogger.Info($"[MockRconService:Moderation] Simulated ban removal for '{ban.IdentityId}'");
        return Task.FromResult(true);
    }

    public Task SendCommandAsync(string rawCommand, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutputReceived?.Invoke(this, $"[RCON OUT] {rawCommand}");
        OutputReceived?.Invoke(this, $"[RCON IN] Command executed successfully: {rawCommand}");
        AppLogger.Trace($"[MockRconService:Command] Simulated raw command: '{rawCommand}'");
        return Task.CompletedTask;
    }

    public Task RestartServerAsync(CancellationToken cancellationToken = default) => SendCommandAsync("#restart", cancellationToken);
    public Task ShutdownServerAsync(CancellationToken cancellationToken = default) => SendCommandAsync("#shutdown", cancellationToken);
    public Task SendGlobalMessageAsync(string message, CancellationToken cancellationToken = default) => SendCommandAsync($"#say -1 {message}", cancellationToken);
    public Task SendAnnouncementAsync(string title, string message, CancellationToken cancellationToken = default) => SendCommandAsync($"#say -1 [ANNOUNCEMENT: {title}] {message}", cancellationToken);

    public Task UpdatePlayerCommentAsync(string uid, string comment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_players.FirstOrDefault(x => x.Uid == uid) is { } p) p.Comment = comment;
        AppLogger.Debug($"[MockRconService:Comment] Simulated comment update for UID '{uid}': '{comment}'");
        return PlayerDatabaseStorageService.UpdateCommentAsync(uid, comment, CurrentProtocol);
    }

    public Task ClearDatabaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppLogger.Warn("[MockRconService:Database] Simulated database purge.");
        return PlayerDatabaseStorageService.ClearDatabaseAsync(CurrentProtocol);
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
                _players.Clear();
                _bans.Clear();
            }
            _isDisposed = true;
        }
    }
}