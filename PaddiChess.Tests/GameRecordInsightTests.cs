using System.Text.Json;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Tests;

public class GameRecordInsightTests
{
    [Fact]
    public void ModelThinkingSurvivesRecordRoundTripWithoutCredentials()
    {
        var original = new GameRecord
        {
            Moves = ["h2e2"],
            AgreedDrawPly = 1,
            PendingDrawOfferPly = null,
            LlmThoughts = new Dictionary<int, List<LlmThoughtRecord>>
            {
                [1] = [new()
                {
                    Ply = 1,
                    Side = "红方",
                    Model = "example-model",
                    MoveNotation = "炮二平五",
                    UciMove = "h2e2",
                    RequestedReasoningEffort = "high",
                    SentReasoningEffort = "high",
                    VisibleThinking = "中炮控制中路。",
                    Explanation = "选择中炮开局。",
                    RecordedAt = DateTimeOffset.UtcNow
                }, new()
                {
                    Ply = 1,
                    Side = "黑方",
                    Model = "opponent-model",
                    Action = "接受和棋",
                    MoveNotation = "接受和棋",
                    VisibleThinking = "局面均衡，同意和棋。"
                }]
            }
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<GameRecord>(json)!;

        Assert.Equal(2, restored.LlmThoughts[1].Count);
        Assert.Equal("中炮控制中路。", restored.LlmThoughts[1][0].VisibleThinking);
        Assert.Equal("high", restored.LlmThoughts[1][0].RequestedReasoningEffort);
        Assert.Equal("h2e2", restored.LlmThoughts[1][0].UciMove);
        Assert.Equal("接受和棋", restored.LlmThoughts[1][1].Action);
        Assert.Equal(1, restored.AgreedDrawPly);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BaseUrl", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingModelDrawOfferSurvivesRecordRoundTrip()
    {
        var original = new GameRecord
        {
            Moves = ["h2e2"],
            CurrentPly = 1,
            PendingDrawOfferPly = 1,
            PendingDrawOfferingRed = true,
            PendingDrawExplanation = "局面均衡，提议和棋"
        };
        var restored = JsonSerializer.Deserialize<GameRecord>(JsonSerializer.Serialize(original))!;
        Assert.Equal(1, restored.PendingDrawOfferPly);
        Assert.True(restored.PendingDrawOfferingRed);
        Assert.Equal(original.PendingDrawExplanation, restored.PendingDrawExplanation);
    }
}
