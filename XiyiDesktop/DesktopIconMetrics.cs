namespace XiyiDesktop;

/// <summary>Logical desktop icon sizes used by the Windows Shell view presets.</summary>
internal static class DesktopIconMetrics
{
    public const double Small = 32;
    public const double Medium = 48;
    public const double Large = 64;

    // Ctrl+wheel can move beyond the three menu presets, like the native desktop.
    public const double Minimum = 16;
    public const double Maximum = 256;
    public const double WheelStep = 4;
}
