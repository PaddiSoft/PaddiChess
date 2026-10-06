using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;
using PaddiXiangqi.Sessions;

namespace PaddiXiangqi.Tests;

public class SessionArchitectureTests
{
    [Fact]
    public void BusinessLayersAreSeparateAssembliesAndCannotAccessAvalonia()
    {
        var layers = new[] { typeof(XiangqiGame), typeof(PikafishClient), typeof(ExternalBoardTracker),
            typeof(GameRecordStorage), typeof(ExternalSynchronizationSession) };
        Assert.Equal(5, layers.Select(t => t.Assembly).Distinct().Count());
        foreach (var layer in layers)
            Assert.DoesNotContain(layer.Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Avalonia"));
        Assert.DoesNotContain(typeof(XiangqiGame).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("PaddiChess."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SynchronizationConfirmsSubmittedMoveThenImmediateReplyWithoutMutatingHostGame(bool flipped)
    {
        var local = new XiangqiGame(); var target = new XiangqiGame();
        BoardObservation Frame() => BoardObservation.Read(ExternalBoardTests.Render(target, flipped), new(40, 40, 440, 490, flipped));
        var original = Frame(); var tracker = new ExternalBoardTracker(original, local);
        var session = new ExternalSynchronizationSession();
        SynchronizationUpdate Observe(BoardObservation frame, int ms, string? pending = null) =>
            session.Observe(frame, tracker, local, pending, false, null, null, TimeSpan.FromMilliseconds(ms));
        Assert.False(Observe(original, 0).Confirmed);
        Assert.True(Observe(original, 33).Confirmed);
        Assert.True(target.TryMoveUci("e3e4", out _));
        var own = Frame();
        Assert.False(Observe(own, 66, "e3e4").Confirmed);
        var ownUpdate = Observe(own, 99, "e3e4");
        Assert.True(ownUpdate.Confirmed); Assert.Equal(0, local.Ply);
        Assert.True(local.TryMoveUci(Assert.Single(ownUpdate.Match.Moves), out _));
        tracker.Accept(own, local); session.OnCommitted(local, 0);
        Assert.True(target.TryMoveUci("h9g7", out _)); var reply = Frame();
        Assert.False(Observe(reply, 132).Confirmed);
        Assert.False(Observe(reply, 165).Confirmed);
        var replyUpdate = Observe(reply, 212);
        Assert.True(replyUpdate.Confirmed); Assert.Equal(1, local.Ply);
        Assert.True(local.TryMoveUci(Assert.Single(replyUpdate.Match.Moves), out _));
        Assert.True(local.RedToMove); Assert.Equal(target.CurrentFen(), local.CurrentFen());
    }

    [Fact]
    public async Task CancelledControllerDecisionNeverStartsNativeProcessesOrRequestsModel()
    {
        await using var engine = new PikafishClient(); await using var rules = new PikafishRulesClient();
        var game = new XiangqiGame();
        var request = new ControllerDecisionRequest(game.CurrentFen(), game.StartFen, "",
            game.AllLegalMoves().Select(m => m.Uci).ToHashSet(), new(20,1,16,1,10), true,
            new("http://127.0.0.1:1", "unused", "test"));
        var service = new ControllerDecisionService(rules, new LlmChessClient());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DecideAsync(request, engine, null, null, null, new(true)));
        Assert.Equal(0, rules.ProcessStartCount); Assert.Equal(0, engine.ProcessStartCount);
    }

    [Fact]
    public void BundledCasualSelectionRejectsAllDisallowedCandidatesWithoutInventingMove()
    {
        var result = new SearchResult("a0a1", [new(1, 1, 0, null, 1, "b0c2")]);
        Assert.Throws<InvalidOperationException>(() => EngineMoveSelector.Select(result, 0, true, true,
            new HashSet<string> { "e3e4" }, ["e3e4"]));
        Assert.Equal("a0a1", EngineMoveSelector.Select(result, 0, true, false, new HashSet<string>()));
    }
}
