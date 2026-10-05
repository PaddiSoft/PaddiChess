using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

public static class BuiltInBoardSkins
{
    // Regression reference supplied with the iPhone-mirroring bug report. Only the board is retained.
    public const string JjReferenceFen = "r1bakabnr/9/1cn3c2/p1p1p1p1p/9/6P2/P1P1P3P/4C2C1/9/RNBAKABNR w - - 0 1";
    public static BoardCalibration JjReferenceGeometry { get; } = new(36, 35, 570, 620, false);
    private static readonly Lazy<IReadOnlyList<BoardSkin>> Cache = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "BoardSkins", "jj-classic.png");
        var skins = new List<BoardSkin>();
        var game = new XiangqiGame(); game.LoadFen(JjReferenceFen);
        if (File.Exists(path)) skins.Add(BoardSkin.Learn("JJ 经典棋子 · iPhone 镜像", BoardObservation.Read(File.ReadAllBytes(path), JjReferenceGeometry), game));
        var web = Path.Combine(AppContext.BaseDirectory, "Assets", "BoardSkins", "web-default.skin.json");
        if (File.Exists(web)) skins.Add(BoardSkin.Load(web));
        return skins;
    });
    public static IReadOnlyList<BoardSkin> All => Cache.Value;
}
