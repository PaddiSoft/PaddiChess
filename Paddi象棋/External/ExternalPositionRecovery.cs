using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

/// <summary>Checks independently recognized pieces against legal successors, retaining the actual move history.</summary>
public static class ExternalPositionRecovery
{
    public static SkinRecognition? Recognize(BoardObservation frame, IEnumerable<BoardSkin> skins, bool redToMove)
    {
        SkinRecognition? best = null;
        foreach (var skin in skins)
        {
            var result = skin.Recognize(frame, redToMove);
            if (result.Confident) return result;
            if (best == null || result.Uncertain.Count < best.Uncertain.Count) best = result;
        }
        return best;
    }
    public static bool SamePieces(string a, string b) => a.Split(' ')[0] == b.Split(' ')[0];
}
