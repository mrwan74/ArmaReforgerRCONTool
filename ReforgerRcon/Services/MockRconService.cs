using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

[SuppressMessage("Security", "S1313:IP address should not be hardcoded", Justification = "RFC 5737 documentation/mock demonstration data")]
public class MockRconService : IRconService
{
    private ServerProfile? _currentProfile;
    private readonly List<PlayerModel> _players = [];
    private readonly List<BanModel> _bans = [];
    private readonly List<DatabasePlayerModel> _dbPlayers = [];

    public RconProtocol CurrentProtocol => _currentProfile?.Protocol ?? RconProtocol.ReforgerBuiltIn;
    public bool IsConnected { get; private set; }
    public int PingMs { get; } = 28;
    public DateTime LastPacketTime { get; private set; } = DateTime.UtcNow;

    public event EventHandler<PlayerModel>? PlayerJoined;
    public event EventHandler<PlayerModel>? PlayerLeft;
    public event EventHandler<string>? OutputReceived;

    private static readonly (int id, string name, string uid, string guid, string ip, int port, int ping, string cc, string cn, string city, string state, bool watch, bool warn, string comment, string[] aliases)[] RealCapturePlayers =
    [
        (1, "AlphaSquadLead", "a1a1a1a1-0000-4000-8000-000000000001", "a1a1a1a1-0000-4000-8000-000000000001", "192.0.2.10", 19999, 18, "de", "Germany", "Frankfurt", "Hesse", false, false, "Regular infantry", ["AlphaSquadLead"]),
        (2, "BravoTactical", "b2b2b2b2-0000-4000-8000-000000000002", "b2b2b2b2-0000-4000-8000-000000000002", "192.0.2.25", 2305, 34, "gb", "United Kingdom", "London", "Greater London", true, false, "VIP Squad Leader", ["BravoTactical"]),
        (3, "CharlieMarksman", "c3c3c3c3-0000-4000-8000-000000000003", "c3c3c3c3-0000-4000-8000-000000000003", "198.51.100.33", 2304, 42, "ua", "Ukraine", "Kyiv", "Kyiv", false, false, "", ["CharlieMarksman"]),
        (4, "DeltaAviator", "d4d4d4d4-0000-4000-8000-000000000004", "d4d4d4d4-0000-4000-8000-000000000004", "198.51.100.37", 2306, 68, "ca", "Canada", "Montreal", "Quebec", false, false, "Transport Pilot", ["DeltaAviator"]),
        (5, "EchoVanguard", "e5e5e5e5-0000-4000-8000-000000000005", "e5e5e5e5-0000-4000-8000-000000000005", "203.0.113.38", 2304, 115, "au", "Australia", "Sydney", "NSW", true, true, "Watchlisted player note", ["EchoVanguard", "Echo_Old"]),
        (6, "FoxtrotSupport", "f6f6f6f6-0000-4000-8000-000000000006", "f6f6f6f6-0000-4000-8000-000000000006", "192.0.2.48", 2307, 45, "us", "United States", "Dallas", "Texas", false, false, "", ["FoxtrotSupport"]),
        (7, "GolfOperator", "a7a7a7a7-0000-4000-8000-000000000007", "a7a7a7a7-0000-4000-8000-000000000007", "198.51.100.22", 2304, 22, "de", "Germany", "Frankfurt", "Hesse", true, false, "Clan Leader", ["GolfOperator", "Golf_Alt"]),
        (8, "HotelGunner", "00112233445566778899aabbccddeeff", "00112233445566778899aabbccddeeff", "203.0.113.50", 60464, 25, "us", "United States", "Chicago", "Illinois", false, false, "BattlEye connected lobby user", ["HotelGunner"])
    ];

    private static readonly (string identity, string name)[] RealServerBans =
    [
        ("11111111-2df8-4d5b-972e-000000000001", "DemoBannedUser01"),
        ("22222222-8137-4eeb-9a1d-000000000002", "DemoBannedUser02"),
        ("33333333-79bf-4953-bc3b-000000000003", "DemoBannedUser03"),
        ("44444444-1ba2-4c75-91f8-000000000004", "DemoBannedUser04"),
        ("55555555-cd9a-43f3-9adc-000000000005", "DemoBannedUser05")
    ];

    public MockRconService()
    {
        SeedMockData();
    }

    private void SeedMockData()
    {
        foreach (var (id, name, uid, guid, ip, port, ping, cc, cn, city, state, watch, warn, comment, aliases) in RealCapturePlayers)
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

            _dbPlayers.Add(new DatabasePlayerModel
            {
                Id = id,
                Name = name,
                Uid = uid,
                Guid = guid,
                LastIp = ip,
                LastPort = port,
                Ping = ping,
                IsOnline = id is 1 or 2 or 7,
                Comment = comment,
                IsWatchlisted = watch,
                HasAliases = warn,
                Aliases = [.. aliases],
                Country = new CountryInfo { Code = cc, Name = cn },
                Location = $"{city}, {cn}",
                LastSeen = DateTime.UtcNow.AddHours(-Random.Shared.Next(1, 72))
            });
        }

        int banIndex = 1;
        foreach (var (identity, name) in RealServerBans)
        {
            _bans.Add(new BanModel
            {
                BanNumber = banIndex++,
                IdentityId = identity,
                BannedName = name,
                Reason = "Demo Server Ban",
                DurationSeconds = 0,
                BannedAt = DateTime.UtcNow.AddDays(-banIndex)
            });
        }
    }

    public async Task<bool> ConnectAsync(ServerProfile profile)
    {
        _currentProfile = profile;
        await Task.Delay(400);
        IsConnected = true;
        LastPacketTime = DateTime.UtcNow;
        OutputReceived?.Invoke(this, $"[SYSTEM] Connected to {profile.ServerIp}:{profile.Port} via {profile.Protocol}");
        OutputReceived?.Invoke(this, "[RCON] Logged in successfully as Administrator.");

        if (_players.Count > 0)
        {
            PlayerJoined?.Invoke(this, _players[0]);
        }

        return true;
    }

    public async Task DisconnectAsync()
    {
        await Task.Delay(100);
        IsConnected = false;
        OutputReceived?.Invoke(this, "[SYSTEM] Disconnected from server.");
    }

    public Task<List<PlayerModel>> GetPlayersAsync()
    {
        LastPacketTime = DateTime.UtcNow;
        return Task.FromResult(_players.ToList());
    }

    public Task<List<BanModel>> GetBansAsync()
    {
        LastPacketTime = DateTime.UtcNow;
        return Task.FromResult(_bans.ToList());
    }

    public Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync() => Task.FromResult(_dbPlayers.ToList());

    public Task KickPlayerAsync(PlayerModel player, string reason)
    {
        _players.Remove(player);
        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#kick {player.Id} {reason}" : $"kick {player.Id} {reason}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Player {player.Name} was kicked ({reason}).");
        PlayerLeft?.Invoke(this, player);
        return Task.CompletedTask;
    }

    public Task BanPlayerAsync(PlayerModel player, long durationSeconds, string reason)
    {
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

        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#ban create {player.Uid} {durationSeconds} {reason}" : $"addBan {player.Guid} {durationSeconds / 60} {reason}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Ban added for {player.Name}.");
        PlayerLeft?.Invoke(this, player);
        return Task.CompletedTask;
    }

    public Task OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp)
    {
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
        return Task.CompletedTask;
    }

    public Task RemoveBanAsync(BanModel ban)
    {
        _bans.Remove(ban);
        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#ban remove {ban.IdentityId}" : $"removeBan {ban.BanNumber}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Ban removed for {ban.IdentityId}.");
        return Task.CompletedTask;
    }

    public Task SendCommandAsync(string rawCommand)
    {
        OutputReceived?.Invoke(this, $"[RCON OUT] {rawCommand}");
        OutputReceived?.Invoke(this, $"[RCON IN] Command executed successfully: {rawCommand}");
        return Task.CompletedTask;
    }

    public Task RestartServerAsync() => SendCommandAsync("#restart");
    public Task ShutdownServerAsync() => SendCommandAsync("#shutdown");
    public Task SendGlobalMessageAsync(string message) => SendCommandAsync($"#say -1 {message}");
    public Task SendAnnouncementAsync(string title, string message) => SendCommandAsync($"#say -1 [ANNOUNCEMENT: {title}] {message}");

    public Task UpdatePlayerCommentAsync(string uid, string comment)
    {
        if (_players.FirstOrDefault(x => x.Uid == uid) is { } p) p.Comment = comment;
        if (_dbPlayers.FirstOrDefault(x => x.Uid == uid) is { } dbP) dbP.Comment = comment;
        return Task.CompletedTask;
    }

    public Task ClearDatabaseAsync()
    {
        _dbPlayers.Clear();
        return Task.CompletedTask;
    }
}