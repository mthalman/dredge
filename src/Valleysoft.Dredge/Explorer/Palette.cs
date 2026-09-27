namespace Valleysoft.Dredge.Explorer;

// Renderer-agnostic styling mapped to Terminal.Gui by Paint.
internal readonly record struct Rgb(int R, int G, int B)
{
    public string ToHex() => $"{R:X2}{G:X2}{B:X2}";

    public static Rgb Hex(int value) => new((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
}

[Flags]
internal enum Deco
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Strikethrough = 16,
}

internal sealed record Sty(Rgb? Foreground, Rgb? Background = null, Deco Deco = Deco.None)
{
    public static readonly Sty Plain = new((Rgb?)null);
}

internal enum ThemeKind { Dark, Light, NoColor }

// Palette grounded in the "dredge" subject: layers are sediment strata, the
// focused thing is the river channel cutting through them. Presenters read the
// tokens; Apply swaps the whole palette before any view is built.
internal static class Theme
{
    public static ThemeKind Kind { get; private set; } = ThemeKind.Dark;
    public static bool NoColor => Kind == ThemeKind.NoColor;

    public static Rgb Ground { get; private set; }
    public static Rgb Channel { get; private set; }
    public static Rgb ChannelDeep { get; private set; }
    public static Rgb Graphite { get; private set; }
    public static Rgb Foam { get; private set; }
    public static Rgb Silt { get; private set; }
    public static Rgb Shale { get; private set; }
    public static Rgb KeycapBg { get; private set; }
    public static Rgb Kelp { get; private set; }
    public static Rgb Ochre { get; private set; }
    public static Rgb OchreDeep { get; private set; }
    public static Rgb Garnet { get; private set; }
    public static Rgb GarnetDeep { get; private set; }
    public static Rgb KelpDeep { get; private set; }
    public static Rgb Bedrock1 { get; private set; }
    public static Rgb Bedrock2 { get; private set; }
    public static Rgb Sand1 { get; private set; }
    public static Rgb Sand2 { get; private set; }
    // Wasted strata carry the size text, so they keep at least 4.5:1 contrast with Foam.
    public static Rgb StratumWaste { get; private set; }
    public static Rgb Shared2 { get; private set; }
    // Backgrounds that shade a layer's size in proportion to it, readable under Foam text.
    public static Rgb Rose { get; private set; }
    public static Rgb Mauve { get; private set; }
    public static Rgb DfKeyword { get; private set; }
    public static Rgb DfString { get; private set; }
    public static Rgb DfLiteral { get; private set; }
    public static Rgb DfSymbol { get; private set; }
    public static Rgb JsonNumber { get; private set; }
    public static Rgb DirName { get; private set; }

    static Theme() => Apply(ThemeKind.Dark);

    public static ThemeKind Parse(string? setting, bool noColorEnvironment)
    {
        if (noColorEnvironment)
        {
            return ThemeKind.NoColor;
        }
        return setting switch
        {
            null or "" or "dark" => ThemeKind.Dark,
            "light" => ThemeKind.Light,
            _ => throw new InvalidOperationException(
                $"Invalid explore.theme value '{setting}'; expected dark or light."),
        };
    }

    public static void Apply(ThemeKind kind)
    {
        Kind = kind;
        if (kind == ThemeKind.Light)
        {
            Ground = Rgb.Hex(0xFAFAF7);
            Channel = Rgb.Hex(0x0B7A76);
            ChannelDeep = Rgb.Hex(0xCFEDEB);
            Graphite = Rgb.Hex(0xECEFF3);
            Foam = Rgb.Hex(0x1F2328);
            Silt = Rgb.Hex(0x57606A);
            Shale = Rgb.Hex(0x9AA3AD);
            KeycapBg = Rgb.Hex(0xD6DCE3);
            Kelp = Rgb.Hex(0x2E7D32);
            KelpDeep = Rgb.Hex(0xDDF2D8);
            Ochre = Rgb.Hex(0x946200);
            OchreDeep = Rgb.Hex(0xB98A1E);
            Garnet = Rgb.Hex(0xC0283B);
            GarnetDeep = Rgb.Hex(0xFBE1E5);
            Bedrock1 = Rgb.Hex(0x91AABB);
            Bedrock2 = Rgb.Hex(0xBDCDD7);
            Sand1 = Rgb.Hex(0xBC875B);
            Sand2 = Rgb.Hex(0xD7B287);
            StratumWaste = Rgb.Hex(0xD9818A);
            Shared2 = Rgb.Hex(0xB4BBC4);
            Rose = Rgb.Hex(0xC24E60);
            Mauve = Rgb.Hex(0x8E5E87);
            DfKeyword = Rgb.Hex(0x8250DF);
            DfString = Rgb.Hex(0x953800);
            DfLiteral = Rgb.Hex(0x0550AE);
            DfSymbol = Rgb.Hex(0x9A6700);
            JsonNumber = Rgb.Hex(0x116329);
            DirName = Rgb.Hex(0x0550AE);
            return;
        }

        Ground = Rgb.Hex(0x1B1F27);
        Channel = Rgb.Hex(0x3FB6B2);
        ChannelDeep = Rgb.Hex(0x153A3B);
        // Dark enough that Silt text on it still clears 4.5:1.
        Graphite = Rgb.Hex(0x262B33);
        Foam = Rgb.Hex(0xE3E7ED);
        Silt = Rgb.Hex(0x8B93A1);
        Shale = Rgb.Hex(0x4A5260);
        KeycapBg = Rgb.Hex(0x39404B);
        Kelp = Rgb.Hex(0x8CC46E);
        KelpDeep = Rgb.Hex(0x1E3A22);
        Ochre = Rgb.Hex(0xEFBF3F);
        OchreDeep = Rgb.Hex(0xC99E2E);
        Garnet = Rgb.Hex(0xE5566E);
        GarnetDeep = Rgb.Hex(0x4A1E27);
        Bedrock1 = Rgb.Hex(0x324451);
        Bedrock2 = Rgb.Hex(0x4C6371);
        Sand1 = Rgb.Hex(0x684728);
        Sand2 = Rgb.Hex(0x825A34);
        StratumWaste = Rgb.Hex(0x963C4F);
        Shared2 = Rgb.Hex(0x5A6270);
        Rose = Rgb.Hex(0xF29CA8);
        Mauve = Rgb.Hex(0xB48EAD);
        // Reused from dredge's `image dockerfile` colorizer for consistency.
        DfKeyword = new(194, 133, 191);
        DfString = new(202, 145, 120);
        DfLiteral = new(150, 220, 254);
        DfSymbol = new(250, 200, 31);
        JsonNumber = Rgb.Hex(0xB5CEA8);
        DirName = Rgb.Hex(0x9FC4E8);
    }

    public static Sty S(Rgb fg, Rgb? bg = null, Deco d = Deco.None) => new(fg, bg, d);
}
