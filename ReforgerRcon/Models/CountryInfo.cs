namespace ReforgerRcon.Models;

public class CountryInfo
{
    public string Code { get; set; } = "un";
    public string Name { get; set; } = "Unknown Region";
    public string FlagUrl => $"https://flagcdn.com/w2560/{Code.ToLowerInvariant()}.jpg";
}