using System;

namespace MinorShift.Emuera.Primitives;

/// <summary>
/// 轻量级颜色类型，替代 System.Drawing.Color。
/// 通过隐式转换兼容现有代码。
/// </summary>
public readonly record struct EmuColor(byte R, byte G, byte B, byte A = 255)
{
    // 静态常量
    public static readonly EmuColor Empty = new(0, 0, 0, 0);
    public static readonly EmuColor Transparent = new(0, 0, 0, 0);
    public static readonly EmuColor Black = new(0, 0, 0);
    public static readonly EmuColor White = new(255, 255, 255);
    public static readonly EmuColor Red = new(255, 0, 0);
    public static readonly EmuColor Green = new(0, 128, 0);
    public static readonly EmuColor Blue = new(0, 0, 255);
    public static readonly EmuColor Yellow = new(255, 255, 0);
    public static readonly EmuColor Gray = new(128, 128, 128);
    public static readonly EmuColor DarkBlue = new(0, 0, 139);

    // 静态工厂方法
    public static EmuColor FromArgb(int r, int g, int b)
        => new((byte)r, (byte)g, (byte)b);

    public static EmuColor FromArgb(int a, int r, int g, int b)
        => new((byte)r, (byte)g, (byte)b, (byte)a);

    public static EmuColor FromArgb(int argb)
        => new((byte)((argb >> 16) & 0xFF),
               (byte)((argb >> 8) & 0xFF),
               (byte)(argb & 0xFF),
               (byte)((argb >> 24) & 0xFF));

    public int ToArgb()
        => (A << 24) | (R << 16) | (G << 8) | B;

    public string ToHex()
        => $"#{R:X2}{G:X2}{B:X2}";

    public static EmuColor FromName(string name)
        => TryFromName(name, out var color) ? color : Black;

    /// <summary>
    /// 尝试按名称解析颜色，匹配 System.Drawing.Color.FromName 的失败语义。
    /// 未知名称返回 false 并将 color 置为 Empty（A=0）。
    /// </summary>
    public static bool TryFromName(string name, out EmuColor color)
    {
        // Color is a managed value type from System.Drawing.Primitives (not the
        // Windows-only drawing/bitmap package). Reuse its complete predefined
        // color table so Android accepts the same named colors as desktop Emuera.
        // Retain the portable engine's existing grey alias and transparent value.
        if (string.Equals(name, "grey", StringComparison.OrdinalIgnoreCase))
        {
            color = Gray;
            return true;
        }
        if (string.Equals(name, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = Transparent;
            return true;
        }
        // Desktop Emuera runs on .NET Framework, whose legacy table resolves this
        // name to #8FBC8B (modern .NET uses #8FBC8F). Match the existing game engine,
        // including COLOR_FROMNAME comparisons; covered by the desktop palette fixture.
        if (string.Equals(name, "darkseagreen", StringComparison.OrdinalIgnoreCase))
        {
            color = new(143, 188, 139);
            return true;
        }
        if (!string.IsNullOrEmpty(name))
        {
            var known = System.Drawing.Color.FromName(name);
            if (known.IsKnownColor)
            {
                color = new(known.R, known.G, known.B, known.A);
                return true;
            }
        }
        color = Empty;
        return false;
    }

#if !HEADLESS
    // 隐式转换
    public static implicit operator EmuColor(System.Drawing.Color c)
        => new(c.R, c.G, c.B, c.A);

    public static implicit operator System.Drawing.Color(EmuColor c)
        => System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
#endif
}
