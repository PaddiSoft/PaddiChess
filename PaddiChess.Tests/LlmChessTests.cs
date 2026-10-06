using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Tests;

public class LlmChessTests
{
    private static readonly LlmConnectionSettings Settings =
        new("https://example.test/v1", "test-token-do-not-print", "test-model");
    private static readonly string[] LegalMoves = ["h2e2", "b2e2"];

    [Fact]
    public async Task ToolCall_UsesLegalMoveEnumAndReturnsValidatedMove()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("submit_move", body);
            Assert.Contains("h2e2", body);
            Assert.Contains("b2e2", body);
            Assert.DoesNotContain(Settings.ApiKey, body);
            return Task.FromResult(JsonResponse("""
                {"choices":[{"message":{"tool_calls":[{"function":{"name":"submit_move","arguments":"{\"move\":\"h2e2\",\"explanation\":\"控制中路\"}"}}]}}]}
                """));
        });
        var client = new LlmChessClient(new HttpClient(handler));
        var chosen = await client.ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves, Settings);
        Assert.Equal("h2e2", chosen.Move);
        Assert.Equal("控制中路", chosen.Explanation);
        Assert.Null(chosen.VisibleThinking);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RequestedReasoningEffort_IsSentAndReportedAsSentNotProvenApplied()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return JsonResponse("""{"choices":[{"message":{"content":"h2e2"}}]}""");
        });
        var progress = new RecordingProgress();
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings with { ReasoningEffort = "HIGH" },
            progress: progress);
        Assert.Contains("\"reasoning_effort\":\"high\"", bodies[0]);
        Assert.Equal(new LlmReasoningEffortStatus("high", "high", false), result.ReasoningEffortStatus);
        Assert.Contains(progress.Events, item => item.Stage == LlmProgressStage.Completed
            && item.ReasoningEffortStatus?.Sent == "high" && item.ReasoningEffortStatus.FellBack == false);
    }

    [Fact]
    public async Task AutomaticReasoningEffort_OmitsParameter()
    {
        string? body = null;
        var handler = new ScriptedHandler(async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"content":"h2e2"}}]}""");
        });
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings);
        Assert.DoesNotContain("reasoning_effort", body);
        Assert.Equal(new LlmReasoningEffortStatus(null, null, false), result.ReasoningEffortStatus);
    }

    [Fact]
    public async Task ExplicitEffortRejection_StopsAndAsksUserToChoose()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"message":"Unsupported reasoning_effort for this model","param":"reasoning_effort"}}""")
                }
                : JsonResponse("""{"choices":[{"message":{"content":"h2e2"}}]}""");
        });
        var progress = new RecordingProgress();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
                XiangqiGame.InitialFen, LegalMoves, Settings with { ReasoningEffort = "medium" },
                progress: progress));
        Assert.Equal(1, handler.Count);
        Assert.Contains("\"reasoning_effort\":\"medium\"", bodies[0]);
        Assert.Contains("medium", error.Message);
        Assert.Contains("自行选择", error.Message);
        Assert.DoesNotContain(Settings.ApiKey, error.Message);
        Assert.DoesNotContain(progress.Events, item => item.ReasoningEffortStatus?.FellBack == true);
    }

    [Fact]
    public async Task UnclassifiedBadRequest_DoesNotChangeRequestedEffort()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : JsonResponse("""{"choices":[{"message":{"content":"b2e2"}}]}""");
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
                XiangqiGame.InitialFen, LegalMoves, Settings with { ReasoningEffort = "low" }));
        Assert.Equal(1, handler.Count);
        Assert.Contains("\"reasoning_effort\":\"low\"", bodies[0]);
        Assert.Contains("400", error.Message);
    }

    [Fact]
    public async Task MessageOnlyUnprocessableEffort_AlsoRequiresUserChoice()
    {
        var calls = 0;
        var actual = new ScriptedHandler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                { Content = new StringContent("""{"detail":"reasoning_effort is not supported by this model"}""") }
            : JsonResponse("""{"choices":[{"message":{"content":"h2e2"}}]}""")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LlmChessClient(new HttpClient(actual)).ChooseMoveAsync(
                XiangqiGame.InitialFen, LegalMoves, Settings with { ReasoningEffort = "high" }));
        Assert.Equal(1, calls);
        Assert.Contains("high", error.Message);
        Assert.Contains("自行选择", error.Message);
    }

    [Fact]
    public async Task ToolsRejection_KeepsRequestedReasoningEffort()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"message":"tools are not supported","param":"tools"}}""")
                }
                : JsonResponse("""{"choices":[{"message":{"content":"h2e2"}}]}""");
        });
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings with { ReasoningEffort = "low" });
        Assert.Equal(2, handler.Count);
        Assert.Contains("\"reasoning_effort\":\"low\"", bodies[1]);
        Assert.DoesNotContain("tool_choice", bodies[1]);
        Assert.Equal("low", result.ReasoningEffortStatus?.Sent);
        Assert.False(result.ReasoningEffortStatus?.FellBack);
    }

    [Fact]
    public async Task InvalidEffort_IsRejectedBeforeApiCall()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => new LlmChessClient(new HttpClient(handler))
            .ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves,
                Settings with { ReasoningEffort = "turbo" }));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ModelNameError_DoesNotTriggerReasoningOrShapeRetry()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"model does not exist","param":"model"}}""")
            }));
        var progress = new RecordingProgress();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
                XiangqiGame.InitialFen, LegalMoves, Settings with { ReasoningEffort = "high" },
                progress: progress));
        Assert.Equal(1, handler.Count);
        Assert.DoesNotContain(Settings.ApiKey, error.Message);
        Assert.DoesNotContain(progress.Events, item => item.ReasoningEffortStatus?.FellBack == true);
    }

    [Fact]
    public async Task ExplicitReasoningContent_AndProgress_AreReturnedWithoutInventingText()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse("""
            {"choices":[{"message":{"reasoning_content":"先看中路，再选择平炮。","tool_calls":[{"function":{"name":"submit_move","arguments":"{\"move\":\"h2e2\",\"explanation\":\"控制中路\"}"}}]}}]}
            """)));
        var progress = new RecordingProgress();
        var client = new LlmChessClient(new HttpClient(handler));
        var result = await client.ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves, Settings,
            progress: progress);
        Assert.Equal("h2e2", result.Move);
        Assert.Equal("先看中路，再选择平炮。", result.VisibleThinking);
        Assert.Contains(progress.Events, item => item.Stage == LlmProgressStage.Preparing);
        Assert.Contains(progress.Events, item => item.Stage == LlmProgressStage.Requesting);
        Assert.Contains(progress.Events, item => item.Stage == LlmProgressStage.Reading);
        Assert.Contains(progress.Events, item => item.Stage == LlmProgressStage.Validating);
        Assert.Contains(progress.Events, item => item.Stage == LlmProgressStage.Completed
            && item.VisibleThinking == result.VisibleThinking);
    }

    [Fact]
    public async Task ProviderSummaryArray_IsVisibleButApiKeyIsRedacted()
    {
        var response = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        summary = new[] { new { type = "summary_text", text = $"重点观察中路，凭据 {Settings.ApiKey}。" } },
                        content = $"{{\"move\":\"b2e2\",\"explanation\":\"守住中路 {Settings.ApiKey}\"}}"
                    }
                }
            }
        });
        var progress = new RecordingProgress();
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse(response)));
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings, progress: progress);
        Assert.Equal("b2e2", result.Move);
        Assert.Contains("重点观察中路", result.VisibleThinking);
        Assert.Contains("[密钥已隐藏]", result.VisibleThinking);
        Assert.Contains("[密钥已隐藏]", result.Explanation);
        Assert.DoesNotContain(Settings.ApiKey, result.VisibleThinking);
        Assert.DoesNotContain(Settings.ApiKey, result.Explanation);
        Assert.DoesNotContain(Settings.ApiKey, string.Join(' ', progress.Events.Select(item =>
            item.Message + item.VisibleThinking)));
    }

    [Fact]
    public async Task ReasoningTypedContent_IsSeparateFromFinalMove()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse("""
            {"choices":[{"message":{"content":[{"type":"reasoning","text":"尝试比较 h2e2 和 b2e2"},{"type":"text","text":"{\"move\":\"h2e2\",\"explanation\":\"出炮\"}"}]}}]}
            """)));
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings);
        Assert.Equal("h2e2", result.Move);
        Assert.Equal("尝试比较 h2e2 和 b2e2", result.VisibleThinking);
        Assert.Equal("出炮", result.Explanation);
    }

    [Fact]
    public async Task StreamingReasoning_ArrivesBeforeMove_AndFragmentedToolCallIsValidated()
    {
        var first = SseEvent(new { choices = new[] { new { delta = new
            { reasoning_content = "先分析中路局势，再寻找稳妥的出炮选择。" } } } });
        var second = SseEvent(new { choices = new[] { new { delta = new
            { tool_calls = new[] { new { index = 0, function = new
                { name = "submit_move", arguments = "{\"move\":\"" } } } } } } });
        var third = SseEvent(new { choices = new[] { new { delta = new
            { tool_calls = new[] { new { index = 0, function = new
                { arguments = "h2e2\",\"explanation\":\"占中路\"}" } } } } } } })
            + "data: [DONE]\n\n";
        var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Contains("\"stream\":true", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Task.FromResult(SseResponse(new DelayedChunksStream([first, second, third], 250)));
        });
        var progress = new SignalProgress();
        var client = new LlmChessClient(new HttpClient(handler));
        var streamSettings = Settings with { ApiKey = "short-test-key", StreamResponses = true };
        var pending = client.ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves,
            streamSettings, progress: progress);
        await progress.FirstThinking.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(pending.IsCompleted);
        var result = await pending;
        Assert.Equal("h2e2", result.Move);
        Assert.Equal("占中路", result.Explanation);
        Assert.Equal("先分析中路局势，再寻找稳妥的出炮选择。", result.VisibleThinking);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task StreamingRejected_FallsBackToNonStreamingWithTools()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count <= 2
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : JsonResponse("""{"choices":[{"message":{"content":"{\"move\":\"b2e2\"}"}}]}""");
        });
        var progress = new RecordingProgress();
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings with { StreamResponses = true }, progress: progress);
        Assert.Equal("b2e2", result.Move);
        Assert.Equal(3, handler.Count);
        Assert.Contains("\"stream\":true", bodies[0]);
        Assert.Contains("tool_choice", bodies[0]);
        Assert.Contains("\"stream\":true", bodies[1]);
        Assert.DoesNotContain("tool_choice", bodies[1]);
        Assert.Contains("\"stream\":false", bodies[2]);
        Assert.Contains("tool_choice", bodies[2]);
        Assert.Contains(progress.Events, item => item.Stage == LlmProgressStage.Retrying);
    }

    [Fact]
    public async Task ToolUnsupported_StillUsesStreamingWhenAvailable()
    {
        var bodies = new List<string>();
        var stream = SseEvent(new { choices = new[] { new { delta = new
            { reasoning_summary = "先稳住中路。", content = "h2e2" } } } }) + "data: [DONE]\n\n";
        var handler = new ScriptedHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : SseResponse(new MemoryStream(Encoding.UTF8.GetBytes(stream)));
        });
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings with { StreamResponses = true });
        Assert.Equal("h2e2", result.Move);
        Assert.Equal("先稳住中路。", result.VisibleThinking);
        Assert.Equal(2, handler.Count);
        Assert.Contains("tool_choice", bodies[0]);
        Assert.Contains("\"stream\":true", bodies[1]);
        Assert.DoesNotContain("tool_choice", bodies[1]);
    }

    [Fact]
    public async Task PlainStreamingModel_DoesNotFabricateThinking()
    {
        var response = SseEvent(new { choices = new[] { new { delta = new
            { content = "{\"move\":\"b2e2\",\"explanation\":\"出炮\"}" } } } })
            + "data: [DONE]\n\n";
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            SseResponse(new MemoryStream(Encoding.UTF8.GetBytes(response)))));
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings with { StreamResponses = true });
        Assert.Equal("b2e2", result.Move);
        Assert.Equal("出炮", result.Explanation);
        Assert.Null(result.VisibleThinking);
    }

    [Fact]
    public async Task StreamingReasoningNeverRevealsSplitApiKey()
    {
        var key = Settings.ApiKey;
        var first = SseEvent(new { choices = new[] { new { delta = new
            { reasoning_content = "先比较可行着法与对手威胁。" + key[..8] } } } });
        var second = SseEvent(new { choices = new[] { new { delta = new
            { reasoning_content = key[8..] + "然后选择平炮。" } } } });
        var third = SseEvent(new { choices = new[] { new { delta = new { content = "h2e2" } } } })
            + "data: [DONE]\n\n";
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            SseResponse(new DelayedChunksStream([first, second, third], 130))));
        var progress = new RecordingProgress();
        var result = await new LlmChessClient(new HttpClient(handler)).ChooseMoveAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings with { StreamResponses = true }, progress: progress);
        Assert.Equal("h2e2", result.Move);
        Assert.Contains("[密钥已隐藏]", result.VisibleThinking);
        Assert.DoesNotContain(key, result.VisibleThinking);
        Assert.DoesNotContain(key[..8], string.Join(' ', progress.Events.Select(item => item.VisibleThinking)));
    }

    [Fact]
    public async Task InvalidFirstMove_RetriesWithCorrectionThenAcceptsLegalMove()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1
                ? JsonResponse("""{"choices":[{"message":{"content":"{\"move\":\"a0a9\"}"}}]}""")
                : JsonResponse("""{"choices":[{"message":{"content":"```json\n{\"move\":\"b2e2\",\"explanation\":\"平炮\"}\n```"}}]}""");
        });
        var client = new LlmChessClient(new HttpClient(handler));
        var chosen = await client.ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves, Settings);
        Assert.Equal("b2e2", chosen.Move);
        Assert.Equal("平炮", chosen.Explanation);
        Assert.Equal(2, handler.Count);
        using var retry = JsonDocument.Parse(bodies[1]);
        Assert.Contains("上一次回答无效", retry.RootElement.GetProperty("messages")[1]
            .GetProperty("content").GetString());
    }

    [Fact]
    public async Task RepeatedIllegalMoves_FailWithoutReturningAnIllegalMove()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            JsonResponse("""{"choices":[{"message":{"tool_calls":[{"function":{"name":"submit_move","arguments":"{\"move\":\"a0a9\"}"}}]}}]}""")));
        var client = new LlmChessClient(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves, Settings));
        Assert.Contains("连续两次未返回合法着法", error.Message);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task SuppliedMoveList_IsCheckedAgainstTheFenBeforeCallingApi()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        var client = new LlmChessClient(new HttpClient(handler));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ChooseMoveAsync(
            XiangqiGame.InitialFen, ["a0a9"], Settings));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task UnsupportedTools_FallsBackToPlainChatCompletion()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : JsonResponse("""{"choices":[{"message":{"content":"h2e2"}}]}""");
        });
        var client = new LlmChessClient(new HttpClient(handler));
        var chosen = await client.ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves, Settings);
        Assert.Equal("h2e2", chosen.Move);
        Assert.Equal(2, handler.Count);
        Assert.Contains("tool_choice", bodies[0]);
        Assert.DoesNotContain("tool_choice", bodies[1]);
    }

    [Fact]
    public async Task Cancellation_StopsWaitingForApi()
    {
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse("{}");
        });
        var client = new LlmChessClient(new HttpClient(handler));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves, Settings, cancel.Token));
    }

    [Fact]
    public async Task ModelList_UsesOpenAiCompatibleModelsEndpoint()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Settings.ApiKey, request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain(Settings.ApiKey, request.RequestUri.AbsoluteUri);
            return Task.FromResult(JsonResponse("""{"data":[{"id":"beta"},{"id":"alpha"},{"id":"alpha"}]}"""));
        });
        var client = new LlmChessClient(new HttpClient(handler));
        var models = await client.ListModelsAsync(Settings with { Model = "" });
        Assert.Equal(new[] { "alpha", "beta" }, models);
    }

    [Theory]
    [InlineData("https://example.test", "/v1/models")]
    [InlineData("https://example.test/v1", "/v1/models")]
    [InlineData("https://example.test/v1/", "/v1/models")]
    [InlineData("https://example.test/v1/chat/completions", "/v1/models")]
    [InlineData("https://example.test/v1/models", "/v1/models")]
    [InlineData("https://example.test/gateway/v1/chat/completions", "/gateway/v1/models")]
    public async Task ModelList_NormalizesSupportedProviderUrls(string baseUrl, string expectedPath)
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse("""{"data":[{"id":"test-model"}]}"""));
        });
        var models = await new LlmChessClient(new HttpClient(handler)).ListModelsAsync(Settings with
        {
            BaseUrl = baseUrl,
            Model = ""
        });
        Assert.Equal(["test-model"], models);
    }

    [Fact]
    public async Task ModelList_IgnoresMalformedIdsAndNeverReturnsApiKey()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse(
            $$"""{"data":[{"id":"zeta"},{"id":null},{"name":"missing"},{"id":"  "},{"id":"contains-{{Settings.ApiKey}}"},{"id":"alpha"}]}""")));
        var models = await new LlmChessClient(new HttpClient(handler)).ListModelsAsync(Settings);
        Assert.Equal(["alpha", "zeta"], models);
    }

    [Fact]
    public async Task ModelList_RejectsInvalidResponseAndSanitizesApiErrors()
    {
        var invalid = new LlmChessClient(new HttpClient(new ScriptedHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"models":["alpha"]}""")))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.ListModelsAsync(Settings));

        var rejected = new LlmChessClient(new HttpClient(new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(Settings.ApiKey)
            }))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.ListModelsAsync(Settings));
        Assert.DoesNotContain(Settings.ApiKey, error.Message);
    }

    [Fact]
    public async Task ModelList_CancellationStopsPendingRequest()
    {
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse("{}");
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LlmChessClient(new HttpClient(handler)).ListModelsAsync(Settings, cancel.Token));
    }

    [Fact]
    public async Task UnauthorizedError_DoesNotLeakApiKey()
    {
        var handler = new ScriptedHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = new LlmChessClient(new HttpClient(handler));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ChooseMoveAsync(XiangqiGame.InitialFen, LegalMoves, Settings));
        Assert.DoesNotContain(Settings.ApiKey, error.Message);
    }

    [Fact]
    public async Task TurnAction_OffersDrawOnlyWithALegalMove()
    {
        var handler = new ScriptedHandler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var tool = body.RootElement.GetProperty("tools")[0].GetProperty("function");
            Assert.Equal("submit_turn_action", tool.GetProperty("name").GetString());
            var properties = tool.GetProperty("parameters").GetProperty("properties");
            Assert.Equal(["move", "offer_draw"], properties.GetProperty("action")
                .GetProperty("enum").EnumerateArray().Select(x => x.GetString()!).ToArray());
            Assert.Equal(LegalMoves, properties.GetProperty("move").GetProperty("enum")
                .EnumerateArray().Select(x => x.GetString()!).ToArray());
            return JsonResponse("""
                {"choices":[{"message":{"reasoning_content":"局面均衡，可以试着提和。","tool_calls":[{"function":{"name":"submit_turn_action","arguments":"{\"action\":\"offer_draw\",\"move\":\"h2e2\",\"explanation\":\"局面接近\"}"}}]}}]}
                """);
        });
        var chosen = await new LlmChessClient(new HttpClient(handler)).ChooseTurnActionAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings, allowDrawOffer: true);
        Assert.Equal(LlmTurnActionKind.OfferDraw, chosen.Kind);
        Assert.Equal("h2e2", chosen.Move);
        Assert.Equal("局面接近", chosen.Explanation);
        Assert.Contains("局面均衡", chosen.VisibleThinking);
    }

    [Fact]
    public async Task TurnAction_RejectsAnOfferWithIllegalOrMissingMove()
    {
        var calls = 0;
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse(++calls == 1
            ? """{"choices":[{"message":{"content":"{\"action\":\"offer_draw\"}"}}]}"""
            : """{"choices":[{"message":{"content":"{\"action\":\"offer_draw\",\"move\":\"a0a9\"}"}}]}""")));
        var client = new LlmChessClient(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ChooseTurnActionAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings, allowDrawOffer: true));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TurnAction_DisallowedDrawOfferIsRetriedAsOrdinaryMove()
    {
        var calls = 0;
        var handler = new ScriptedHandler((request, _) =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("submit_move", body);
            return Task.FromResult(JsonResponse(++calls == 1
                ? """{"choices":[{"message":{"content":"{\"action\":\"offer_draw\",\"move\":\"h2e2\"}"}}]}"""
                : """{"choices":[{"message":{"content":"{\"move\":\"b2e2\"}"}}]}"""));
        });
        var chosen = await new LlmChessClient(new HttpClient(handler)).ChooseTurnActionAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings, allowDrawOffer: false);
        Assert.Equal(LlmTurnActionKind.Move, chosen.Kind);
        Assert.Equal("b2e2", chosen.Move);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DrawReply_CanAcceptWithoutMove()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("submit_turn_action", body);
            using var payload = JsonDocument.Parse(body);
            Assert.Contains("对方提出求和", payload.RootElement.GetProperty("messages")[1]
                .GetProperty("content").GetString());
            return Task.FromResult(JsonResponse("""
                {"choices":[{"message":{"tool_calls":[{"function":{"name":"submit_turn_action","arguments":"{\"action\":\"accept_draw\",\"explanation\":\"同意\"}"}}]}}]}
                """));
        });
        var result = await new LlmChessClient(new HttpClient(handler)).RespondToDrawOfferAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings, "对方提出求和");
        Assert.Equal(LlmTurnActionKind.AcceptDraw, result.Kind);
        Assert.Null(result.Move);
        Assert.Equal("同意", result.Explanation);
    }

    [Fact]
    public async Task DrawReply_DeclineRequiresLegalMoveAndRetries()
    {
        var calls = 0;
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse(++calls == 1
            ? """{"choices":[{"message":{"content":"{\"action\":\"decline_draw\",\"move\":\"a0a9\"}"}}]}"""
            : """{"choices":[{"message":{"content":"{\"action\":\"decline_draw\",\"move\":\"b2e2\"}"}}]}""")));
        var result = await new LlmChessClient(new HttpClient(handler)).RespondToDrawOfferAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings);
        Assert.Equal(LlmTurnActionKind.DeclineDraw, result.Kind);
        Assert.Equal("b2e2", result.Move);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DrawReply_RejectsUnsolicitedMoveOrSecondOffer()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse(
            """{"choices":[{"message":{"content":"{\"action\":\"offer_draw\",\"move\":\"h2e2\"}"}}]}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LlmChessClient(new HttpClient(handler)).RespondToDrawOfferAsync(
                XiangqiGame.InitialFen, LegalMoves, Settings));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task StreamingDrawReply_AcceptsStructuredToolAction()
    {
        var response = SseEvent(new { choices = new[] { new { delta = new
            { tool_calls = new[] { new { index = 0, function = new
                { name = "submit_turn_action", arguments = "{\"action\":\"accept_draw\"}" } } } } } } })
            + "data: [DONE]\n\n";
        var handler = new ScriptedHandler((_, _) => Task.FromResult(
            SseResponse(new MemoryStream(Encoding.UTF8.GetBytes(response)))));
        var result = await new LlmChessClient(new HttpClient(handler)).RespondToDrawOfferAsync(
            XiangqiGame.InitialFen, LegalMoves, Settings with { StreamResponses = true });
        Assert.Equal(LlmTurnActionKind.AcceptDraw, result.Kind);
        Assert.Null(result.Move);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string SseEvent(object payload) => "data: " + JsonSerializer.Serialize(payload) + "\n\n";

    private static HttpResponseMessage SseResponse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class RecordingProgress : IProgress<LlmProgress>
    {
        public List<LlmProgress> Events { get; } = [];
        public void Report(LlmProgress value) => Events.Add(value);
    }

    private sealed class SignalProgress : IProgress<LlmProgress>
    {
        public TaskCompletionSource FirstThinking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(LlmProgress value)
        {
            if (value.VisibleThinking?.Contains("先分析", StringComparison.Ordinal) == true)
                FirstThinking.TrySetResult();
        }
    }

    private sealed class DelayedChunksStream(string[] chunks, int delayMs) : Stream
    {
        private readonly byte[][] _chunks = chunks.Select(Encoding.UTF8.GetBytes).ToArray();
        private int _index;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_index >= _chunks.Length) return 0;
            if (_index > 0 && _offset == 0) await Task.Delay(delayMs, cancellationToken);
            var chunk = _chunks[_index];
            var count = Math.Min(buffer.Length, chunk.Length - _offset);
            chunk.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == chunk.Length) { _index++; _offset = 0; }
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
