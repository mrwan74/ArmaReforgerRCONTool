using System;
using System.Diagnostics.CodeAnalysis;

namespace ReforgerRcon.BattleNET;

[SuppressMessage("Security", "S2257:Use a standard cryptographic algorithm", Justification = "IEEE 802.3 CRC32 algorithm is strictly required by the BattlEye RCon wire protocol specification")]
public static class CRC32
{
    private const uint DefaultPolynomial = 0xEDB88320;
    private static readonly uint[] Table = InitializeTable(DefaultPolynomial);

    private static uint[] InitializeTable(uint polynomial)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int j = 0; j < 8; j++)
            {
                if ((entry & 1) == 1)
                    entry = (entry >> 1) ^ polynomial;
                else
                    entry >>= 1;
            }
            table[i] = entry;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> buffer)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < buffer.Length; i++)
        {
            crc = (crc >> 8) ^ Table[(buffer[i] ^ crc) & 0xFF];
        }
        return ~crc;
    }
}