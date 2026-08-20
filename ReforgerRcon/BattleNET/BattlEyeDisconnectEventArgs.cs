using System;

namespace ReforgerRcon.BattleNET;

public delegate void BattlEyeConnectEventHandler(BattlEyeConnectEventArgs args);
public delegate void BattlEyeDisconnectEventHandler(BattlEyeDisconnectEventArgs args);

public class BattlEyeConnectEventArgs(BattlEyeLoginCredentials loginDetails, BattlEyeConnectionResult connectionResult) : EventArgs
{
    public BattlEyeLoginCredentials LoginDetails { get; } = loginDetails;
    public BattlEyeConnectionResult ConnectionResult { get; } = connectionResult;
    public string Message { get; } = Helpers.StringValueOf(connectionResult);
}

public class BattlEyeDisconnectEventArgs(BattlEyeLoginCredentials loginDetails, BattlEyeDisconnectionType? disconnectionType) : EventArgs
{
    public BattlEyeLoginCredentials LoginDetails { get; } = loginDetails;
    public BattlEyeDisconnectionType? DisconnectionType { get; } = disconnectionType;
    public string Message { get; } = disconnectionType.HasValue ? Helpers.StringValueOf(disconnectionType.Value) : "Disconnected";
}