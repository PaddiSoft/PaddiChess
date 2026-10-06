using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

[Collection("Local recognition")]
public class UniversalRecognitionTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures",name));
    private static BoardCalibration Geometry(bool flipped) => new(50,50,570,635,flipped);

    [Theory]
    [InlineData("ocr-unseen-ivory.png",false)]
    [InlineData("ocr-unseen-blue-green.png",true)]
    public async Task OfflineOcrReadsUnregisteredFontsAndPalettesWithoutSkinTemplates(string file, bool flipped)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await BoardGlyphRecognizer.RecognizeAsync(Fixture(file),Geometry(!flipped),true,timeout.Token,detectOrientation:true);
        Assert.Equal(flipped,result.DetectedRedAtTop);
        Assert.Equal(XiangqiGame.InitialFen,result.Fen);
        Assert.True(result.Confident,$"{result.Problem}: {string.Join(',',result.Uncertain)}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideAssignmentLearnsPaletteEvenIfBlackUsesRedAndRedUsesBlue(bool flipped)
    {
        // Known glyphs isolate colour assignment from OCR. The blue/green fixture
        // uses shared 車/馬 names, so side must come from other labelled pieces.
        using var image=SKBitmap.Decode(Fixture("ocr-unseen-blue-green.png"));
        var pixels=image.Pixels;
        for(int i=0;i<pixels.Length;i++) { var p=pixels[i]; pixels[i]=new(p.Green,p.Red,p.Blue,p.Alpha); }
        image.Pixels=pixels;
        using var png=image.Encode(SKEncodedImageFormat.Png,100);
        var glyphs=OpeningGlyphs(true);
        var result=BoardGlyphRecognizer.BuildPosition(png.ToArray(),Geometry(true),true,glyphs);
        Assert.True(result.Confident,string.Join(',',result.Uncertain));
        Assert.Equal(XiangqiGame.InitialFen,result.Fen);
        if (!flipped) return;
        // Same colours on a mirrored board must not swap side identities.
        using var reversed=new SKBitmap(image.Width,image.Height);
        using(var canvas=new SKCanvas(reversed))
        { canvas.Translate(image.Width,image.Height);canvas.RotateDegrees(180);canvas.DrawBitmap(image,0,0); }
        // Calibration tracks exact pixel centres after the transform.
        var cal=new BoardCalibration(image.Width-570,image.Height-635,image.Width-50,image.Height-50,false);
        using var rotated=reversed.Encode(SKEncodedImageFormat.Png,100);
        var mirrorResult=BoardGlyphRecognizer.BuildPosition(rotated.ToArray(),cal,true,glyphs.Reverse().ToArray());
        Assert.Equal(XiangqiGame.InitialFen,mirrorResult.Fen);
        Assert.True(mirrorResult.Confident,string.Join(',',mirrorResult.Uncertain));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    public void SidePaletteUsesGlyphAnchorsAcrossSwappedChannelsAndInvertedThemes(int permutation, bool invert)
    {
        using var image = SKBitmap.Decode(Fixture("ocr-unseen-blue-green.png"));
        var pixels = image.Pixels;
        int[][] channels = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            byte Channel(int channel)
            {
                byte value = channels[permutation][channel] switch { 0 => p.Red, 1 => p.Green, _ => p.Blue };
                return invert ? (byte)(255 - value) : value;
            }
            pixels[i] = new(Channel(0), Channel(1), Channel(2), p.Alpha);
        }
        image.Pixels = pixels;
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var read = BoardGlyphRecognizer.BuildPosition(png.ToArray(), Geometry(true), true, OpeningGlyphs(true));
        Assert.True(read.Confident, $"{read.Problem}: {string.Join(',', read.Uncertain)}");
        Assert.Equal(XiangqiGame.InitialFen, read.Fen);
    }

    [Fact]
    public void IndistinguishableSideColoursStayUncertainInsteadOfAssumingRedRooks()
    {
        using var image=SKBitmap.Decode(Fixture("ocr-unseen-blue-green.png"));
        var pixels=image.Pixels;
        for(int i=0;i<pixels.Length;i++)
        {
            var p=pixels[i]; var v=Math.Min(p.Red,Math.Min(p.Green,p.Blue));
            pixels[i]=new(v,v,v,p.Alpha);
        }
        image.Pixels=pixels;
        using var png=image.Encode(SKEncodedImageFormat.Png,100);
        var result=BoardGlyphRecognizer.BuildPosition(png.ToArray(),Geometry(true),true,OpeningGlyphs(true));
        Assert.False(result.Confident);
        Assert.Contains(new Square(0,0),result.Uncertain);
        Assert.Contains(new Square(8,9),result.Uncertain);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GridLocationSupportsLightDarkAndEqualLuminanceColours(int palette)
    {
        using var image=new SKBitmap(600,660);
        using(var canvas=new SKCanvas(image))
        {
            canvas.Clear(palette switch {1=>SKColors.MidnightBlue,2=>new SKColor(255,0,0),_=>SKColors.Beige});
            using var line=new SKPaint{Color=palette switch {1=>SKColors.Ivory,2=>new SKColor(0,76,0),_=>SKColors.DarkSlateGray},StrokeWidth=2};
            for(int r=0;r<10;r++)canvas.DrawLine(60,60+r*60,540,60+r*60,line);
            for(int f=0;f<9;f++)
            {
                canvas.DrawLine(60+f*60,60,60+f*60,f is 0 or 8?600:300,line);
                if(f is >0 and <8)canvas.DrawLine(60+f*60,360,60+f*60,600,line);
            }
            canvas.DrawLine(240,60,360,180,line);canvas.DrawLine(360,60,240,180,line);
            canvas.DrawLine(240,480,360,600,line);canvas.DrawLine(360,480,240,600,line);
        }
        using var png=image.Encode(SKEncodedImageFormat.Png,100);
        var geometry=BoardLocator.Locate(png.ToArray());
        Assert.NotNull(geometry);
        Assert.InRange(geometry.Left,57,63);Assert.InRange(geometry.Top,57,63);
        Assert.InRange(geometry.Right,537,543);Assert.InRange(geometry.Bottom,597,603);
    }

    private static LocalGlyphOcr.Glyph[] OpeningGlyphs(bool flipped)
    {
        var game=new XiangqiGame();
        return Enumerable.Range(0,90).Select(i =>
        {
            var square=flipped?89-i:i;var p=game.Board[square/9,square%9];
            var index="RNBAKCP".IndexOf(char.ToUpperInvariant(p));
            return new LocalGlyphOcr.Glyph(index<0?"":(char.IsUpper(p)?"车马相仕帅炮兵":"车马象士将砲卒")[index].ToString(),index<0?0:1);
        }).ToArray();
    }
}
