using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Sessions;

/// <summary>Independent identity evidence is mandatory before acting on an external board.</summary>
public static class ExternalInputVerification
{
    public static BoardMatch Check(ExternalBoardTracker tracker, XiangqiGame game,
        BoardObservation observation, SkinRecognition? independentRead)
    {
        // Do not fill unknown identities from the tracker's baseline: that baseline
        // is precisely what this independent check needs to verify.
        if (independentRead is not { Confident: true })
            return new(false, [], independentRead?.Error ?? 1, "落子前尚未确认全部棋子身份");
        return tracker.MatchRecognizedPosition(independentRead, game, frame: observation);
    }
}
