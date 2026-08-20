using System.Net;

namespace ReforgerRcon.BattleNET;

public struct BattlEyeLoginCredentials(IPAddress host, int port, string password)
{
    public IPAddress Host { get; set; } = host;
    public int Port { get; set; } = port;
    public string Password { get; set; } = password;
}