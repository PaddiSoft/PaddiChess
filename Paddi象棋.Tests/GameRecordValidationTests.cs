using System.Text;
using System.Text.Json;
using PaddiXiangqi.Core;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public class GameRecordValidationTests
{
    private static async Task<GameRecord> Read(string json, CancellationToken ct = default)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await GameRecordStorage.ReadValidatedAsync(stream, ct);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"Format\":\"PaddiXiangqi/99\"}")]
    [InlineData("{\"Format\":null}")]
    [InlineData("{\"StartFen\":null}")]
    [InlineData("{\"StartFen\":\"invalid\"}")]
    [InlineData("{\"Moves\":null}")]
    [InlineData("{\"Moves\":[null]}")]
    [InlineData("{\"Moves\":[\"a0a9\"]}")]
    [InlineData("{\"CurrentPly\":-1}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"CurrentPly\":2}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"AgreedDrawPly\":0}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"AgreedDrawPly\":2}")]
    [InlineData("{\"Moves\":[\"h2e2\",\"h9g7\"],\"AgreedDrawPly\":1}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"CurrentPly\":1,\"PendingDrawOfferPly\":1}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"CurrentPly\":1,\"PendingDrawOfferPly\":1,\"PendingDrawOfferingRed\":false}")]
    [InlineData("{\"PendingDrawOfferPly\":0,\"PendingDrawOfferingRed\":false}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"PendingDrawOfferingRed\":true}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"PendingDrawExplanation\":\"提和\"}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"CurrentPly\":1,\"AgreedDrawPly\":1,\"PendingDrawOfferPly\":1,\"PendingDrawOfferingRed\":true}")]
    [InlineData("{\"Moves\":[\"h2e2\"],\"CurrentPly\":0,\"PendingDrawOfferPly\":1,\"PendingDrawOfferingRed\":true}")]
    public async Task MalformedOrContradictoryRecordNeverReturnsAPartiallyValidatedGame(string json)
        => await Assert.ThrowsAsync<FormatException>(() => Read(json));

    [Fact]
    public async Task ExplicitNullOptionalCollectionsAreCompatibleWithOlderRecords()
    {
        var record = await Read("""
            {"Moves":["h2e2"],"CurrentPly":1,"Title":null,
             "Notes":null,"Scores":null,"ScoreLabels":null,"LlmThoughts":null}
            """);
        Assert.Equal("未命名棋谱", record.Title);
        Assert.Equal(["h2e2"], record.Moves);
        Assert.Empty(record.Notes);
        Assert.Empty(record.Scores);
        Assert.Empty(record.ScoreLabels);
        Assert.Empty(record.LlmThoughts);
        Assert.Equal(XiangqiGame.InitialFen, record.StartFen);
    }

    [Fact]
    public async Task OptionalMetadataUsesOnlyExistingPliesAndNeverSynthesizesModelThinking()
    {
        var record = await Read("""
            {"Moves":["h2e2"],"CurrentPly":1,
             "Notes":{"-1":"invalid","0":"开始","1":null,"2":"future"},
             "Scores":{"-1":5,"0":10,"1":20,"2":30},
             "ScoreLabels":{"-1":"invalid","1":"红优 20 分","2":"future"},
             "LlmThoughts":{
               "0":[{"Ply":0,"VisibleThinking":"invalid"}],
               "1":[null,{"Ply":2,"VisibleThinking":"wrong move"},
                     {"Ply":1,"Side":null,"Model":null,"MoveNotation":null,"UciMove":null,
                      "Action":null,"RequestedReasoningEffort":null,"VisibleThinking":null,"Explanation":"原有说明"}],
               "2":[{"Ply":2,"VisibleThinking":"future"}]}}
            """);
        Assert.Single(record.Notes);
        Assert.Equal("开始", record.Notes[0]);
        Assert.Equal([0, 1], record.Scores.Keys.Order().ToArray());
        Assert.Equal("红优 20 分", Assert.Single(record.ScoreLabels).Value);
        var thought = Assert.Single(Assert.Single(record.LlmThoughts).Value);
        Assert.Equal(1, thought.Ply);
        Assert.Null(thought.VisibleThinking);
        Assert.Equal("原有说明", thought.Explanation);
        Assert.Equal("", thought.UciMove);
        Assert.Equal("", thought.Model);
        Assert.Equal("", thought.Side);
        Assert.Equal("", thought.Action);
        Assert.Equal("", thought.MoveNotation);
        Assert.Equal("", thought.RequestedReasoningEffort);
    }

    [Fact]
    public async Task AcceptedDrawCanBeReviewedBeforeItsFinalPlyWithoutLosingTheAgreement()
    {
        var record = await Read(JsonSerializer.Serialize(new GameRecord
        {
            Moves = ["h2e2", "h9g7"], CurrentPly = 1, AgreedDrawPly = 2,
            LlmThoughts = new() { [2] = [new() { Ply = 2, Action = "接受和棋", VisibleThinking = "原有回应" }] }
        }));
        Assert.Equal(2, record.AgreedDrawPly);
        Assert.Equal(1, record.CurrentPly);
        Assert.Equal("原有回应", record.LlmThoughts[2][0].VisibleThinking);
    }

    [Fact]
    public async Task PendingDrawRemainsAttachedToTheUnplayedOpponentsTurn()
    {
        var record = await Read(JsonSerializer.Serialize(new GameRecord
        {
            Moves = ["h2e2"], CurrentPly = 1, PendingDrawOfferPly = 1,
            PendingDrawOfferingRed = true, PendingDrawExplanation = "原有提和说明"
        }));
        Assert.True(record.PendingDrawOfferingRed);
        Assert.Equal(record.CurrentPly, record.PendingDrawOfferPly);
        Assert.Equal("原有提和说明", record.PendingDrawExplanation);
        Assert.Null(record.AgreedDrawPly);
    }

    [Fact]
    public async Task CancelledReadNeverReturnsARecord()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read("{}", cancellation.Token));
    }
}
