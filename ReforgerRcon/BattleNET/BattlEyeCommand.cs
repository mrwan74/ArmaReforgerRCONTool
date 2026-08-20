using System;
using System.ComponentModel;

namespace ReforgerRcon.BattleNET;

public enum BattlEyeCommand
{
    [Description("#init")]
    Init,

    [Description("#shutdown")]
    Shutdown,

    [Description("#reassign")]
    Reassign,

    [Description("#restart")]
    Restart,

    [Description("#lock")]
    Lock,

    [Description("#unlock")]
    Unlock,

    [Description("#mission ")]
    Mission,

    [Description("missions")]
    Missions,

    [Description("RConPassword ")]
    RConPassword,

    [Description("MaxPing ")]
    MaxPing,

    [Description("kick ")]
    Kick,

    [Description("players")]
    Players,

    [Description("Say ")]
    Say,

    [Description("loadBans")]
    LoadBans,

    [Description("loadScripts")]
    LoadScripts,

    [Description("loadEvents")]
    LoadEvents,

    [Description("bans")]
    Bans,

    [Description("ban ")]
    Ban,

    [Description("addBan ")]
    AddBan,

    [Description("removeBan ")]
    RemoveBan,

    [Description("writeBans")]
    WriteBans,

    [Description("admins")]
    Admins
}