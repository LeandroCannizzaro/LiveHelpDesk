using Avalonia.Media;

namespace LiveHelpDesk.App.Converters;

/// <summary>The overlay colour settings are "#RRGGBB" strings, blank meaning "automatic".</summary>
public static class HexColor
{
    /// <summary>The colour of a setting, or <paramref name="automatic"/> when it is blank or unreadable.</summary>
    public static Color Parse(string? hex, Color automatic) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out var color) ? color : automatic;

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
