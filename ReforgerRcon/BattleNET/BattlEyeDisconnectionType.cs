using System.ComponentModel;

namespace ReforgerRcon.BattleNET;

public enum BattlEyeDisconnectionType
{
    [Description("Disconnected manually")]
    Manual,

    [Description("Connection timed out (No packets received)")]
    ConnectionLost,

    [Description("Socket exception encountered")]
    SocketException
}