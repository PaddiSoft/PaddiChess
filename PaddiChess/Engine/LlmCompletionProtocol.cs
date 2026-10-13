using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static PaddiXiangqi.Engine.LlmCompletionProtocol;
using static PaddiXiangqi.Engine.LlmCompletionTransport;

namespace PaddiXiangqi.Engine;

/// <summary>Parses JSON, SSE deltas and tool calls without choosing or applying a chess move.</summary>
internal static class LlmCompletionProtocol
{
    internal static readonly Regex UciMoveRegex = new(@"(?<![a-z0-9])[a-i][0-9][a-i][0-9](?![a-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static CompletionMove ParseCompletion(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return default;
        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object) return default;
        if (first.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            var thinking = ReadVisibleThinking(message) ?? ReadVisibleThinking(first) ?? ReadVisibleThinking(root);
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("function", out var function)
                        || function.ValueKind != JsonValueKind.Object) continue;
                    if (function.TryGetProperty("name", out var name)
                        && name.GetString() is not ("submit_move" or "submit_turn_action")) continue;
                    if (function.TryGetProperty("arguments", out var arguments))
                    {
                        var toolMove = ParseStructuredMove(arguments);
                        if (toolMove.Move is not null || toolMove.Action is not null)
                            return toolMove with { VisibleThinking = thinking ?? toolMove.VisibleThinking };
                    }
                }
            }
            if (message.TryGetProperty("function_call", out var legacyCall) && legacyCall.ValueKind == JsonValueKind.Object
                && legacyCall.TryGetProperty("arguments", out var legacyArguments))
            {
                var toolMove = ParseStructuredMove(legacyArguments);
                if (toolMove.Move is not null || toolMove.Action is not null)
                    return toolMove with { VisibleThinking = thinking ?? toolMove.VisibleThinking };
            }
            if (message.TryGetProperty("content", out var content))
            {
                var parsed = ParseContent(content);
                return parsed with { VisibleThinking = thinking ?? parsed.VisibleThinking };
            }
        }
        if (first.TryGetProperty("text", out var text))
        {
            var parsed = ParseContent(text);
            return parsed with { VisibleThinking = ReadVisibleThinking(first) ?? ReadVisibleThinking(root) };
        }
        return default;
    }

    internal static CompletionMove ParseContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Array)
        {
            var combined = string.Join(' ', content.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.Object
                    && !IsReasoningPart(part)
                    && part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                .Select(part => part.GetProperty("text").GetString()));
            return ParseText(combined) with { VisibleThinking = ReadReasoningParts(content) };
        }
        return content.ValueKind == JsonValueKind.String ? ParseText(content.GetString() ?? "") : default;
    }

    internal static bool IsReasoningPart(JsonElement part) =>
        part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
        && type.GetString() is "reasoning" or "reasoning_text" or "reasoning_summary" or "summary_text";

    internal static string? ReadReasoningParts(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return null;
        var parts = content.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object && IsReasoningPart(part))
            .Select(ReadDisplayText).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        return parts.Length == 0 ? null : string.Join("\n", parts);
    }

    internal static string? ReadVisibleThinking(JsonElement parent)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        foreach (var field in new[] { "reasoning_summary", "summary", "reasoning_content", "reasoning_text", "reasoning" })
            if (parent.TryGetProperty(field, out var element) && ReadDisplayText(element) is { Length: > 0 } text)
                return text;
        return parent.TryGetProperty("content", out var content) ? ReadReasoningParts(content) : null;
    }

    internal static string? ReadDisplayText(JsonElement element, int depth = 0)
    {
        if (depth > 4) return null;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()?.Trim();
            case JsonValueKind.Array:
            {
                var lines = element.EnumerateArray().Select(item => ReadDisplayText(item, depth + 1))
                    .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
                return lines.Length == 0 ? null : string.Join("\n", lines);
            }
            case JsonValueKind.Object:
                foreach (var field in new[] { "text", "content", "summary", "reasoning_summary", "reasoning_content" })
                    if (element.TryGetProperty(field, out var value)
                        && ReadDisplayText(value, depth + 1) is { Length: > 0 } text)
                        return text;
                return null;
            default: return null;
        }
    }

    internal static CompletionMove ParseText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return default;
        var structured = ParseJsonText(text);
        if (structured.Move is not null || structured.Action is not null) return structured;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            structured = ParseJsonText(text[start..(end + 1)]);
            if (structured.Move is not null || structured.Action is not null) return structured;
        }
        var found = UciMoveRegex.Matches(text).Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return found.Length == 1 ? new CompletionMove(found[0], text.Length > 4 ? ShortExplanation(text) : null) : default;
    }

    internal static CompletionMove ParseStructuredMove(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString() ?? "";
            return value.Length == 4 && UciMoveRegex.IsMatch(value)
                ? new CompletionMove(value, null) : ParseJsonText(value);
        }
        if (element.ValueKind != JsonValueKind.Object) return default;
        string? move = null;
        foreach (var name in new[] { "move", "uci", "bestmove" })
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                move = property.GetString();
                break;
            }
        }
        string? explanation = element.TryGetProperty("explanation", out var reason)
            && reason.ValueKind == JsonValueKind.String ? ShortExplanation(reason.GetString()) : null;
        string? action = element.TryGetProperty("action", out var decision)
            && decision.ValueKind == JsonValueKind.String ? decision.GetString()?.Trim().ToLowerInvariant() : null;
        return new CompletionMove(move?.Trim(), explanation, ReadVisibleThinking(element), action);
    }

    internal static CompletionMove ParseJsonText(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return ParseStructuredMove(json.RootElement);
        }
        catch (JsonException) { return default; }
    }

    internal static string? ShortExplanation(string? explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation)) return null;
        return explanation.Trim();
    }

    internal static string? SanitizeVisibleText(string? text, string apiKey, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Trim();
        var key = apiKey.Trim();
        if (key.Length != 0) clean = clean.Replace(key, "[密钥已隐藏]", StringComparison.Ordinal);
        return clean.Length <= maxLength ? clean : clean[..maxLength] + "…";
    }

    internal static async Task<CompletionMove> ReadStreamCompletionAsync(HttpResponseMessage response,
        LlmConnectionSettings settings, int attempt, IProgress<LlmProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096, leaveOpen: true);
            var eventData = new StringBuilder();
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var calls = new SortedDictionary<int, StreamToolCall>();
            var legacyCall = new StreamToolCall();
            CompletionMove? finalMessage = null;
            var sawData = false;
            var done = false;
            long lastProgressMs = 0;

            void Append(string? value, StringBuilder target)
            {
                if (string.IsNullOrEmpty(value)) return;
                if (target.Length + value.Length > 1_000_000)
                    throw new InvalidOperationException("大模型流式响应过长，已停止读取。");
                target.Append(value);
            }

            void ProcessEvent()
            {
                var data = eventData.ToString().Trim();
                eventData.Clear();
                if (data.Length == 0) return;
                if (data == "[DONE]") { done = true; return; }
                sawData = true;
                using var chunk = JsonDocument.Parse(data);
                var root = chunk.RootElement;
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0) return;
                var choice = choices[0];
                if (choice.ValueKind != JsonValueKind.Object) return;
                if (choice.TryGetProperty("message", out var completeMessage)
                    && completeMessage.ValueKind == JsonValueKind.Object)
                    finalMessage = ParseCompletion(root);
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) return;

                var visible = ReadVisibleThinking(delta);
                if (visible is not null)
                {
                    Append(visible, reasoning);
                    var now = Environment.TickCount64;
                    if (now - lastProgressMs >= 120)
                    {
                        lastProgressMs = now;
                        var previewLength = Math.Min(reasoning.Length, 5000 + settings.ApiKey.Length);
                        var snapshot = SafeStreamingSnapshot(reasoning.ToString(0, previewLength),
                            settings.ApiKey, 5000);
                        if (snapshot is not null)
                            progress?.Report(new LlmProgress(LlmProgressStage.Reading,
                                "正在接收模型返回的思考内容…", attempt, snapshot));
                    }
                }
                if (delta.TryGetProperty("content", out var contentDelta))
                {
                    if (contentDelta.ValueKind == JsonValueKind.String)
                        Append(contentDelta.GetString(), content);
                    else if (contentDelta.ValueKind == JsonValueKind.Array)
                        foreach (var part in contentDelta.EnumerateArray())
                            if (part.ValueKind == JsonValueKind.Object && !IsReasoningPart(part)
                                && part.TryGetProperty("text", out var text)
                                && text.ValueKind == JsonValueKind.String)
                                Append(text.GetString(), content);
                }
                if (delta.TryGetProperty("tool_calls", out var toolDeltas)
                    && toolDeltas.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tool in toolDeltas.EnumerateArray())
                    {
                        if (tool.ValueKind != JsonValueKind.Object) continue;
                        var index = tool.TryGetProperty("index", out var indexValue)
                            && indexValue.TryGetInt32(out var parsedIndex) ? parsedIndex : 0;
                        if (index < 0 || index > 64) continue;
                        if (!calls.TryGetValue(index, out var call)) calls[index] = call = new StreamToolCall();
                        if (tool.TryGetProperty("function", out var function)
                            && function.ValueKind == JsonValueKind.Object)
                            AppendFunctionDelta(function, call, Append);
                    }
                }
                if (delta.TryGetProperty("function_call", out var legacy)
                    && legacy.ValueKind == JsonValueKind.Object)
                    AppendFunctionDelta(legacy, legacyCall, Append);
            }

            while (!done && await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) { ProcessEvent(); continue; }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                if (eventData.Length != 0) eventData.Append('\n');
                eventData.Append(line.AsSpan(5).TrimStart());
            }
            if (!done && eventData.Length > 0) ProcessEvent();
            if (!sawData) throw new RequestShapeUnsupportedException();

            var thinking = reasoning.Length > 0 ? reasoning.ToString() : null;
            if (finalMessage?.VisibleThinking is { } finalThinking
                && (thinking is null || finalThinking.Length > thinking.Length))
                thinking = finalThinking;
            foreach (var call in calls.Values.Append(legacyCall))
            {
                if (call.Arguments.Length == 0 || call.Name.Length != 0
                    && call.Name.ToString() is not ("submit_move" or "submit_turn_action")) continue;
                var parsed = ParseJsonText(call.Arguments.ToString());
                if (parsed.Move is not null || parsed.Action is not null)
                    return parsed with { VisibleThinking = thinking ?? parsed.VisibleThinking };
            }
            if (finalMessage is { Move: not null } or { Action: not null })
            {
                var complete = finalMessage.Value;
                return complete with { VisibleThinking = thinking ?? complete.VisibleThinking };
            }
            if (content.Length == 0) throw new RequestShapeUnsupportedException();
            var textMove = ParseText(content.ToString());
            return textMove with { VisibleThinking = thinking ?? textMove.VisibleThinking };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取大模型流式响应超时。");
        }
        catch (JsonException)
        {
            throw new RequestShapeUnsupportedException();
        }
    }

    internal static void AppendFunctionDelta(JsonElement function, StreamToolCall call,
        Action<string?, StringBuilder> append)
    {
        if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && name.GetString() is { Length: > 0 } namePart
            && call.Name.ToString() != namePart)
            append(namePart, call.Name);
        if (function.TryGetProperty("arguments", out var args))
        {
            if (args.ValueKind == JsonValueKind.String) append(args.GetString(), call.Arguments);
            else if (args.ValueKind == JsonValueKind.Object) append(args.GetRawText(), call.Arguments);
        }
    }

    // Keep the last key-length-minus-one raw characters private until a later chunk arrives.
    // A complete key anywhere in the safe prefix is masked before reporting a snapshot.
    internal static string? SafeStreamingSnapshot(string raw, string apiKey, int maxLength)
    {
        var key = apiKey.Trim();
        var safeLength = key.Length == 0 ? raw.Length : Math.Max(0, raw.Length - key.Length + 1);
        if (safeLength == 0) return null;
        var masked = raw.ToCharArray();
        if (key.Length != 0)
        {
            for (var index = raw.IndexOf(key, StringComparison.Ordinal); index >= 0;
                 index = raw.IndexOf(key, index + key.Length, StringComparison.Ordinal))
                for (var i = index; i < Math.Min(index + key.Length, safeLength); i++) masked[i] = '●';
        }
        var snapshot = new string(masked, 0, Math.Min(safeLength, maxLength)).Trim();
        return snapshot.Length == 0 ? null : snapshot;
    }

    internal readonly record struct CompletionMove(string? Move, string? Explanation,
        string? VisibleThinking = null, string? Action = null);
    internal enum TurnRequestMode { MoveOnly, MoveOrOfferDraw, AnswerDrawOffer }
    internal sealed class StreamToolCall
    {
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
    internal sealed class RequestShapeUnsupportedException : Exception { }
    internal enum BadRequestCause { Unknown, ReasoningEffort, RequestShape, Fatal }
}
