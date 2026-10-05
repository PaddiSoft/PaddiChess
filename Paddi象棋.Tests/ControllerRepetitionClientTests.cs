using System.Net;
using System.Text;
using System.Text.Json;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Tests;

public class ControllerRepetitionClientTests
{
    private static XiangqiGame RepeatedPosition()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        game.LoadFen("4k4/9/3R5/9/9/4P4/9/9/9/4K4 w - - 0 1");
        string[] cycle = ["d7e7", "e9d9", "e7d7", "d9e9"];
        foreach (var move in cycle.Concat(cycle)) Assert.True(game.TryMoveUci(move, out _));
        Assert.Contains("d7e7", game.AllControllerLegalMoves().Select(move => move.Uci));
        return game;
    }

    [Fact]
    public async Task RealPikafishAvoidsLosingPerpetualCheckWithoutClientWhitelist()
    {
        var game = RepeatedPosition();
        var allowed = game.AllControllerLegalMoves().Select(move => move.Uci).ToArray();
        await using var engine = new PikafishClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await engine.SearchAsync(game.StartFen, game.UciMoveList,
            new EngineSettings(20, 1, 16, 1, 4) { MultiPvOverride = 1 }, null, timeout.Token);
        Assert.NotEqual("d7e7", result.BestMove);
        Assert.Contains(result.BestMove, allowed);
        Assert.True(game.TryMoveUci(result.BestMove, out _));
    }

    [Fact]
    public async Task ModelReceivesNativeRuleCandidatesAndCannotReintroducePerpetualCheck()
    {
        var game = RepeatedPosition();
        await using var rules = new PikafishRulesClient();
        var result = await rules.GetAllowedMovesAsync(game.StartFen, game.UciMoveList, game.CurrentFen());
        var allowed = result.Allowed;
        Assert.Contains("d7e7", result.Excluded);
        Assert.Contains("d7c7", allowed);
        using var handler = new RepetitionHandler();
        using var http = new HttpClient(handler);
        var reply = await new LlmChessClient(http).ChooseMoveAsync(game.CurrentFen(), allowed,
            new("https://fixture.invalid/v1", "fixture-only-token", "fixture-model"));
        Assert.Equal(2, handler.Requests);
        Assert.Equal("d7c7", reply.Move);
        Assert.True(game.TryMoveUci(reply.Move, out _));
    }

    private sealed class RepetitionHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var tools = body.RootElement.GetProperty("tools");
            var submit = tools.EnumerateArray().Single(tool => tool.GetProperty("function").GetProperty("name").GetString() == "submit_move");
            var choices = submit.GetProperty("function").GetProperty("parameters").GetProperty("properties")
                .GetProperty("move").GetProperty("enum").EnumerateArray().Select(move => move.GetString()).ToArray();
            Assert.DoesNotContain("d7e7", choices);
            Assert.Contains("d7c7", choices);
            var chosen = Requests == 1 ? "d7e7" : "d7c7";
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = chosen } } } }), Encoding.UTF8, "application/json") };
        }
    }
}
