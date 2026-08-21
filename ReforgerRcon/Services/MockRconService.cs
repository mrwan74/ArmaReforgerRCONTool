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

    private static readonly (int id, string name, string uid, string guid, string ip, int port, int ping, string cc, string cn, string city, string state, bool watch, bool warn, string comment, string[] aliases)[] MockPlayers =
    [
        (1, "ViperActual", "00000000-0000-4000-8000-000000000001", "00000000000040008000000000000001", "192.0.2.10", 19999, 18, "de", "Germany", "Frankfurt", "Hesse", false, false, "Squad Leader - Alpha", ["ViperActual"]),
        (2, "GhostNomad", "00000000-0000-4000-8000-000000000002", "00000000000040008000000000000002", "192.0.2.25", 2305, 34, "gb", "United Kingdom", "London", "Greater London", true, false, "VIP Squad Member", ["GhostNomad"]),
        (3, "SierraMarksman", "00000000-0000-4000-8000-000000000003", "00000000000040008000000000000003", "198.51.100.33", 2304, 42, "fr", "France", "Paris", "Ile-de-France", false, false, "", ["SierraMarksman"]),
        (4, "DeltaAviator", "00000000-0000-4000-8000-000000000004", "00000000000040008000000000000004", "198.51.100.37", 2306, 68, "ca", "Canada", "Montreal", "Quebec", false, false, "Dedicated Transport Pilot", ["DeltaAviator"]),
        (5, "EchoOperator", "00000000-0000-4000-8000-000000000005", "00000000000040008000000000000005", "203.0.113.38", 2304, 115, "au", "Australia", "Sydney", "NSW", true, true, "Watchlisted: Frequent team-damage alerts", ["EchoOperator", "Echo_OldCallsign"]),
        (6, "RavenSupport", "00000000-0000-4000-8000-000000000006", "00000000000040008000000000000006", "192.0.2.48", 2307, 45, "us", "United States", "Dallas", "Texas", false, false, "Logistics Officer", ["RavenSupport"]),
        (7, "KiloTactical", "00000000-0000-4000-8000-000000000007", "00000000000040008000000000000007", "198.51.100.22", 2304, 22, "jp", "Japan", "Tokyo", "Tokyo", true, false, "Verified Clan Member", ["KiloTactical", "Kilo_Alt"]),
        (8, "TangoGunner", "00000000-0000-4000-8000-000000000008", "00000000000040008000000000000008", "203.0.113.50", 60464, 25, "us", "United States", "Chicago", "Illinois", false, false, "Regular Infantry", ["TangoGunner"])
    ];

    private static readonly (string identity, string name, string reason, long durationSeconds)[] MockServerBans =
    [
        ("00000000-0000-4000-8000-000000000010", "DemoTroll_01", "Griefing friendly base structures", 0),
        ("00000000-0000-4000-8000-000000000011", "ExploitTester_99", "Terrain collision clipping / Map exploit", 604800),
        ("00000000-0000-4000-8000-000000000012", "SpeedHacker_Demo", "Third-party memory modification / Speedhack", 0),
        ("00000000-0000-4000-8000-000000000013", "ToxicUser_Demo", "Severe toxicity in side voice channel", 86400),
        ("00000000-0000-4000-8000-000000000014", "SpamBot_Test", "Automated chat advertisement spam", 0),
        ("00000000-0000-4000-8000-000000000015", "Teamkiller_Mock", "Intentional spawn teamkilling", 259200),
        ("00000000-0000-4000-8000-000000000016", "GlitchAbuser_Demo", "Asset duplication exploit", 2592000),
        ("00000000-0000-4000-8000-000000000017", "StreamSniper_Test", "Targeted stream sniping / Community violation", 604800),
        ("00000000-0000-4000-8000-000000000018", "AssetDestroyer_01", "Destroying friendly logistics trucks at main base", 86400),
        ("00000000-0000-4000-8000-000000000019", "DemoBannedUser_09", "Server rule #3 violation (Econ exploitation)", 0),
        ("00000000-0000-4000-8000-000000000020", "DemoBannedUser_10", "Impersonating server administrator", 0),
        ("00000000-0000-4000-8000-000000000021", "MicSpammer_Demo", "Continuous audio spam in global radio", 21600),
        ("00000000-0000-4000-8000-000000000022", "DemoBannedUser_12", "Ban evasion attempt (Linked HWID)", 0),
        ("00000000-0000-4000-8000-000000000023", "DemoBannedUser_13", "Unauthorized GM tool execution attempt", 0),
        ("00000000-0000-4000-8000-000000000024", "DemoBannedUser_14", "Intentional server crash exploit", 0)
    ];

    public MockRconService()
    {
        SeedMockData();
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

    public async Task<bool> ConnectAsync(ServerProfile profile)
    {
        _currentProfile = profile;
        await Task.Delay(400);
        IsConnected = true;
        LastPacketTime = DateTime.UtcNow;
        OutputReceived?.Invoke(this, $"[SYSTEM] Connected to {profile.ServerIp}:{profile.Port} via {profile.Protocol}");
        OutputReceived?.Invoke(this, "[RCON] Logged in successfully as Administrator (Demo Mode).");

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