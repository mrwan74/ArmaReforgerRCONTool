using System;

namespace ReforgerRcon.BattleNET;

public delegate void BattlEyeMessageEventHandler(BattlEyeMessageEventArgs args);

public class BattlEyeMessageEventArgs(string message, int id) : EventArgs
{
    public string Message { get; } = message;
    public int Id { get; } = id;
}