using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PaddiXiangqi;

/// <summary>Shared, decoded artwork. Board animation never opens or decodes an asset.</summary>
public static class BrandAssets
{
    private static readonly Lazy<Bitmap> LogoImage = new(() => Load("Logo.png"));
    private static readonly Lazy<Bitmap> WoodImage = new(() => Load("BoardTexture.png"));
    private static readonly Lazy<Bitmap> BackdropImage = new(() => Load("WorkspaceBackdrop.png"));
    private static readonly Lazy<WindowIcon> AppIcon = new(() => new WindowIcon(Logo));

    public static Bitmap Logo => LogoImage.Value;
    public static Bitmap BoardTexture => WoodImage.Value;
    public static Bitmap WorkspaceBackdrop => BackdropImage.Value;
    public static WindowIcon Icon => AppIcon.Value;

    private static Bitmap Load(string filename)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://{typeof(BrandAssets).Assembly.GetName().Name}/Assets/Brand/{filename}"));
        return new Bitmap(stream);
    }
}
