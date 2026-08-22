using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

[SuppressMessage("Security", "S1313:IP address should not be hardcoded", Justification = "Realistic documentation/mock demonstration dataset based on actual server responses")]
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
    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ConnectionLost;

    private static readonly (int id, string name, string uid, string guid, string ip, int port, int ping, string cc, string cn, string city, string state, bool watch, bool warn, string comment, string[] aliases)[] MockPlayers =
    [
        (1, "Fisk", "ee6c0f99-2026-477a-84db-bad8c5f617fa", "ee6c0f992026477a84dbbad8c5f617fa", "185.150.189.205", 19999, 18, "de", "Germany", "Frankfurt", "Hesse", false, false, "Server Admin / Testing Operator", ["Fisk"]),
        (2, "GhostNomad", "00000000-0000-4000-8000-000000000002", "00000000000040008000000000000002", "192.0.2.25", 2305, 34, "gb", "United Kingdom", "London", "Greater London", true, false, "VIP Squad Member", ["GhostNomad"]),
        (3, "SierraMarksman", "00000000-0000-4000-8000-000000000003", "00000000000040008000000000000003", "198.51.100.33", 2304, 42, "fr", "France", "Paris", "Ile-de-France", false, false, "", ["SierraMarksman"]),
        (4, "DeltaAviator", "00000000-0000-4000-8000-000000000004", "00000000000040008000000000000004", "198.51.100.37", 2306, 68, "ca", "Canada", "Montreal", "Quebec", false, false, "Dedicated Transport Pilot", ["DeltaAviator"]),
        (5, "EchoOperator", "00000000-0000-4000-8000-000000000005", "00000000000040008000000000000005", "203.0.113.38", 2304, 115, "au", "Australia", "Sydney", "NSW", true, true, "Watchlisted: Frequent team-damage alerts", ["EchoOperator", "Echo_OldCallsign"]),
        (6, "SGT. Goof (Romeo 1-6)", "53aefc9c-112d-433c-a735-1f0a182a6497", "53aefc9c112d433ca7351f0a182a6497", "192.0.2.48", 2307, 45, "us", "United States", "Dallas", "Texas", false, false, "Clan Officer", ["SGT. Goof (Romeo 1-6)"]),
        (7, "KiloTactical", "00000000-0000-4000-8000-000000000007", "00000000000040008000000000000007", "198.51.100.22", 2304, 22, "jp", "Japan", "Tokyo", "Tokyo", true, false, "Verified Clan Member", ["KiloTactical", "Kilo_Alt"]),
        (8, "TangoGunner", "00000000-0000-4000-8000-000000000008", "00000000000040008000000000000008", "203.0.113.50", 60464, 25, "us", "United States", "Chicago", "Illinois", false, false, "Regular Infantry", ["TangoGunner"])
    ];

    private static readonly (string identity, string name, string reason, long durationSeconds)[] MockServerBans =
    [
        ("6f656069-2df8-4d5b-972e-03d1a572433d", "Q8K0", "Griefing friendly base structures", 0),
        ("5c64d8fc-8137-4eeb-9a1d-3eb09f48b94a", "WoollenFlame386", "Terrain collision clipping / Map exploit", 604800),
        ("f022535d-79bf-4953-bc3b-c9321bc4b8d7", "doucey", "Third-party memory modification", 0),
        ("5a7d8533-1ba2-4c75-91f8-86e95da4bc74", "smlynch01", "Severe toxicity in side voice channel", 86400),
        ("4f57acec-cd9a-43f3-9adc-c8ea4d337b8d", "Th3_Dr_Lovee", "Automated chat advertisement spam", 0),
        ("ac1f506e-4475-4a43-99dd-60b873220b28", "Superbad3995", "Intentional spawn teamkilling", 259200),
        ("c52b0e19-d51b-484c-85f4-c541b3266fcf", "TheEl Mayo87", "Asset duplication exploit", 2592000),
        ("f73a4891-c27d-4791-b1be-384d713d16d6", "ATL02Batch", "Targeted stream sniping", 604800),
        ("1cb6c796-05be-4b73-9716-ec9048badd0c", "DL40", "Destroying friendly logistics trucks", 86400),
        ("0e0b14ae-73b5-4b49-91c6-ff43865019b5", "Kilodub9", "Server rule #3 violation", 0),
        ("52f6e089-df67-4fe4-8c58-aa1a04375f36", "жопа", "Toxic name & chat abuse", 0),
        ("60afdb01-fca6-4c85-b7e4-c2ea3879a547", "The Prophet", "Continuous audio spam in radio", 21600),
        ("6939078b-eb67-434e-bf18-75cdd6b2875c", "DoubleTT", "Ban evasion attempt", 0),
        ("fe8ed53b-94a5-4152-8805-2764b325cea2", "Rae Lil Black", "Inappropriate behavior", 0),
        ("a3a5e205-c051-46f9-a39b-65eb380aa54e", "red_rav3n", "Rule #4 violation", 0),
        ("35d3ca07-763b-491d-a5d2-ec8f9746882c", "SAFE T GUY", "Asset griefing", 0),
        ("8811d91d-63f4-400f-b3e8-b3554f744bde", "guljian", "Teamkill exploit", 0),
        ("de5ba0e4-5c47-433b-80f2-60fb708155af", "ChaseAlottatail", "Excessive verbal harassment", 0),
        ("3f3c6905-f79b-424b-b3ec-a386f487c3c8", "Blue CrawDaddy", "Trolling main spawns", 0),
        ("188e7996-b8e3-4dcc-9075-5cfeec82ac49", "CURRY306441", "Rule violation", 0),
        ("b1c55f85-8e13-4b40-a66b-4c42057f4edf", "Josh", "Disruptive gameplay", 0),
        ("764f2227-640b-48cf-93b5-d53ec9e7b60b", "Gooberman", "Intentional server crash attempt", 0)
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
        await Task.Delay(400, CancellationToken.None);
        IsConnected = true;
        LastPacketTime = DateTime.UtcNow;

        OutputReceived?.Invoke(this, $"[SYSTEM] Connected to {profile.ServerIp}:{profile.Port} via {profile.Protocol}");
        OutputReceived?.Invoke(this, "[RCON] Logged in successfully as Administrator (Demo Simulation Mode).");

        await PlayerDatabaseStorageService.RecordSeenPlayersAsync(_players);

        if (_players.Count > 0)
        {
            PlayerJoined?.Invoke(this, _players[0]);
        }

        return true;
    }

    public async Task DisconnectAsync()
    {
        await Task.Delay(100, CancellationToken.None);
        IsConnected = false;
        await PlayerDatabaseStorageService.SetAllOfflineAsync();
        OutputReceived?.Invoke(this, "[SYSTEM] Disconnected from server.");
    }

    public void SimulateConnectionDrop()
    {
        IsConnected = false;
        ConnectionLost?.Invoke(this, "Connection timed out (No packets received)");
    }

    public async Task<List<PlayerModel>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPacketTime = DateTime.UtcNow;
        await PlayerDatabaseStorageService.RecordSeenPlayersAsync(_players);
        return [.. _players];
    }

    public Task<List<BanModel>> GetBansAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPacketTime = DateTime.UtcNow;
        return Task.FromResult(_bans.ToList());
    }

    public Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync(CancellationToken cancellationToken = default) => PlayerDatabaseStorageService.GetAllAsync();

    public Task<bool> KickPlayerAsync(PlayerModel player, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _players.Remove(player);
        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#kick {player.Id} {reason}" : $"kick {player.Id} {reason}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Player '{player.Name}' kicked!");
        PlayerLeft?.Invoke(this, player);
        return Task.FromResult(true);
    }

    public Task<bool> BanPlayerAsync(PlayerModel player, long durationSeconds, string reason, CancellationToken cancellationToken = default)
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

        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#ban create {player.Id} {durationSeconds} {reason}" : $"addBan {player.Guid} {durationSeconds / 60} {reason}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Ban added for {player.Name}.");
        PlayerLeft?.Invoke(this, player);
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
        return Task.FromResult(true);
    }

    public Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _bans.Remove(ban);
        var cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? $"#ban remove {ban.IdentityId}" : $"removeBan {ban.BanNumber}";
        OutputReceived?.Invoke(this, $"[RCON OUT] {cmd}");
        OutputReceived?.Invoke(this, $"[RCON IN] Ban removed for {ban.IdentityId}.");
        return Task.FromResult(true);
    }

    public Task SendCommandAsync(string rawCommand)
    {
        OutputReceived?.Invoke(this, $"[RCON OUT] {rawCommand}");
        OutputReceived?.Invoke(this, $"[RCON IN] Command executed successfully: {rawCommand}");
        return Task.CompletedTask;
    }

    public Task RestartServerAsync(CancellationToken cancellationToken = default) => SendCommandAsync("#restart");
    public Task ShutdownServerAsync(CancellationToken cancellationToken = default) => SendCommandAsync("#shutdown");
    public Task SendGlobalMessageAsync(string message, CancellationToken cancellationToken = default) => SendCommandAsync($"#say -1 {message}");
    public Task SendAnnouncementAsync(string title, string message, CancellationToken cancellationToken = default) => SendCommandAsync($"#say -1 [ANNOUNCEMENT: {title}] {message}");

    public Task UpdatePlayerCommentAsync(string uid, string comment)
    {
        if (_players.FirstOrDefault(x => x.Uid == uid) is { } p) p.Comment = comment;
        return PlayerDatabaseStorageService.UpdateCommentAsync(uid, comment);
    }

    public Task ClearDatabaseAsync() => PlayerDatabaseStorageService.ClearAsync();

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