using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using ReforgerRcon.Services;

namespace ReforgerRcon.BattleNET;

internal static class Helpers
{
    public static string Hex2Ascii(string hexString)
    {
        ArgumentNullException.ThrowIfNull(hexString);
        var j = 0;
        var tmp = new byte[hexString.Length / 2];
        for (var i = 0; i <= hexString.Length - 2; i += 2)
        {
            tmp[j] = byte.Parse(hexString.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            j++;
        }
        return Bytes2String(tmp);
    }

    public static byte[] String2Bytes(string s) => Encoding.UTF8.GetBytes(s);

    public static string Bytes2String(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public static string Bytes2String(byte[] bytes, int index, int count) => Encoding.UTF8.GetString(bytes, index, count);

    public static string StringValueOf(Enum value)
    {
        var name = value.ToString();
        FieldInfo? fi = value.GetType().GetField(name);
        if (fi != null)
        {
            var attributes = (DescriptionAttribute[])fi.GetCustomAttributes(typeof(DescriptionAttribute), false);
            if (attributes.Length > 0)
            {
                return attributes[0].Description;
            }
        }
        return name;
    }

    public static object EnumValueOf(string value, Type enumType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(enumType);

        var matchingName = Enum.GetNames(enumType)
            .FirstOrDefault(name => StringValueOf((Enum)Enum.Parse(enumType, name)).Equals(value, StringComparison.OrdinalIgnoreCase));

        if (matchingName != null)
        {
            return Enum.Parse(enumType, matchingName);
        }

        AppLogger.Warn($"[Helpers:EnumValueOf] Failed resolving enum '{value}' for type '{enumType.Name}'.");
        throw new ArgumentException($"The string '{value}' is not a valid description or value of enum '{enumType.Name}'.");
    }
}