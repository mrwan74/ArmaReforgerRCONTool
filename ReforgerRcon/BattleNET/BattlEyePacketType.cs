namespace ReforgerRcon.BattleNET;

internal enum BattlEyePacketType : byte
{
    Login = 0x00,
    Command = 0x01,
    ServerMessage = 0x02
}