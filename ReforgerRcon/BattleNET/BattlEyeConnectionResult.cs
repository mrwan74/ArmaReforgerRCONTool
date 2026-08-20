using System.ComponentModel;

namespace ReforgerRcon.BattleNET;

public enum BattlEyeConnectionResult
{
    [Description("Connected successfully")]
    Success,

    [Description("Host unreachable or timed out")]
    ConnectionFailed,

    [Description("Invalid login credentials")]
    InvalidLogin
}