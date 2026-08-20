using System;

namespace ReforgerRcon.Models;

public class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Official Server";
    public string ServerIp { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 19999;
    public string Password { get; set; } = "";
    public RconProtocol Protocol { get; set; } = RconProtocol.ReforgerBuiltIn;
    public bool AutoConnect { get; set; }
}
