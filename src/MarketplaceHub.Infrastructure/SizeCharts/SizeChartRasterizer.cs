using System.Runtime.InteropServices;
using NetVips;

namespace MarketplaceHub.Infrastructure.SizeCharts;

// The owner's skill file draws its chart as an SVG and photographs it with Playwright/Chromium, which Claude's code
// container does not have (12 Nov 2026: every Oluştur ended in 502 AI_RENDERER_MISSING). The container now writes the
// SVG the skill builds and the panel turns it into the 1200×1800 PNG here, with libvips' SVG loader (librsvg) and the
// skill's own Montserrat (size-chart-fonts/, picked by a fontconfig file of its own so every host draws the same text).
public static class SizeChartRasterizer
{
    public const string FontDirectory = "size-chart-fonts";
    private const int MaxSvgBytes = 6 * 1024 * 1024;
    private static readonly Lazy<bool> Configured = new(Configure);

    public static string FontConfigPath => Path.Combine(AppContext.BaseDirectory, FontDirectory, "fonts.conf");

    // A complete 1200×1800 PNG on white, or SizeChartException for anything that is not such an SVG.
    public static byte[] PngFromSvg(byte[] svg)
    {
        if (svg.Length is 0 or > MaxSvgBytes || !LooksLikeSvg(svg))
            throw new SizeChartException("AI_IMAGE_INVALID", "Claude'un ürettiği dosya bir SVG çizimi değil; görsel eklenmedi. Tekrar deneyin.", 502);
        _ = Configured.Value;
        // ProductMediaRenditions blocks every untrusted loader (SVG among them) when it first runs, so the SVG buffer
        // loader is opened again for each chart; the renditions refuse an SVG source themselves.
        Operation.Block("VipsForeignLoadSvgBuffer", false);
        try
        {
            using var image = Image.SvgloadBuffer(svg, dpi: 72, unlimited: false, failOn: Enums.FailOn.Error);
            if (image.Width != SizeChartService.Width || image.Height != SizeChartService.Height)
                throw new SizeChartException("SIZE_CHART_WRONG_SIZE", $"Beden tablosu {SizeChartService.Width}×{SizeChartService.Height} olmalı; çizilen SVG {image.Width}×{image.Height} geldi, tekrar deneyin.", 502);
            using var opaque = image.HasAlpha() ? image.Flatten(background: [255, 255, 255]) : image.Copy();
            using var srgb = opaque.Colourspace(Enums.Interpretation.Srgb);
            return srgb.PngsaveBuffer(compression: 9, keep: Enums.ForeignKeep.None);
        }
        catch (VipsException exception)
        {
            throw new SizeChartException("AI_IMAGE_INVALID", $"Claude'un ürettiği SVG çizilemedi; görsel eklenmedi. ({Short(exception.Message)})", 502);
        }
    }

    internal static bool LooksLikeSvg(byte[] bytes)
    {
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        return head.Contains("<svg", StringComparison.Ordinal);
    }

    private static bool Configure()
    {
        // fontconfig reads FONTCONFIG_FILE from the native environment when it first draws text; .NET's own
        // Environment.SetEnvironmentVariable does not reach it on Linux, so libc's setenv is called (an explicit
        // FONTCONFIG_FILE of the host wins). Without the file the host's fonts are used.
        if (File.Exists(FontConfigPath) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FONTCONFIG_FILE")))
        {
            Environment.SetEnvironmentVariable("FONTCONFIG_FILE", FontConfigPath);
            try
            {
                if (OperatingSystem.IsLinux()) SetNativeEnvironment("FONTCONFIG_FILE", FontConfigPath);
                else if (OperatingSystem.IsWindows()) _ = PutEnv("FONTCONFIG_FILE", FontConfigPath);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
        }
        return true;
    }

    private static void SetNativeEnvironment(string name, string value)
    {
        // glibc answers to libc.so.6; "libc" alone needs the development link.
        try { _ = SetEnv6(name, value, 0); }
        catch (DllNotFoundException) { _ = SetEnv(name, value, 0); }
    }

    [DllImport("libc.so.6", EntryPoint = "setenv", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int SetEnv6(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int SetEnv(string name, string value, int overwrite);

    [DllImport("ucrtbase", EntryPoint = "_putenv_s", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int PutEnv(string name, string value);

    private static string Short(string value)
    {
        var line = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? value;
        return line.Length <= 160 ? line : line[..160] + "…";
    }
}
