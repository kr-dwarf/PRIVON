using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.2 Gate 032-B1 -- the single production authority for loading the official PRIVON PV
/// brand assets (multi-resolution icon, 128x128 square icon, horizontal PV + PRIVON wordmark) from
/// this assembly's OWN embedded resources (see the matching <c>&lt;EmbeddedResource&gt;</c> entries
/// in <c>Privon.App.csproj</c>). No other production file references an
/// <c>Assets.privon-*</c> resource name directly, and this type never constructs a filesystem path
/// of any kind -- the canonical bytes are compiled into PRIVON.exe itself, so branding survives a
/// single-file publish, a moved EXE, or a deleted sibling folder identically to every other
/// embedded resource this project already depends on.
/// </summary>
internal static class BrandResources
{
    private const string IconResourceName = "Privon.App.Assets.privon.ico";
    private const string SquareIconPngResourceName = "Privon.App.Assets.privon-icon-128.png";
    private const string WordmarkResourceName = "Privon.App.Assets.privon-wordmark.png";

    /// <summary>Constructs a fresh, caller-owned <see cref="Icon"/> from the embedded
    /// multi-resolution PRIVON PV icon -- the ONE production source for the tray icon. The returned
    /// <see cref="Icon"/> owns an unmanaged GDI handle; the caller must dispose it.</summary>
    internal static Icon LoadTrayIcon()
    {
        using var stream = OpenResource(IconResourceName);
        return new Icon(stream);
    }

    /// <summary>Decodes the embedded 128x128 RGBA PRIVON PV icon into a frozen, thread-safe
    /// <see cref="BitmapImage"/> suitable for <see cref="System.Windows.Window.Icon"/> -- a single
    /// flat PNG frame renders more predictably there than a multi-frame .ico.</summary>
    internal static BitmapImage LoadWindowIconImage() => DecodeFrozenBitmap(SquareIconPngResourceName);

    /// <summary>Decodes the embedded horizontal PV + PRIVON wordmark into a frozen, thread-safe
    /// <see cref="BitmapImage"/> for display in a WPF <see cref="System.Windows.Controls.Image"/>.</summary>
    internal static BitmapImage LoadWordmarkImage() => DecodeFrozenBitmap(WordmarkResourceName);

    private static Stream OpenResource(string resourceName)
    {
        var assembly = typeof(BrandResources).Assembly;
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded brand resource '{resourceName}' was not found in {assembly.FullName}. " +
                "Privon.App.csproj's EmbeddedResource entries for src/Privon.App/Assets are missing or misconfigured.");
        }

        return stream;
    }

    private static BitmapImage DecodeFrozenBitmap(string resourceName)
    {
        using var stream = OpenResource(resourceName);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
