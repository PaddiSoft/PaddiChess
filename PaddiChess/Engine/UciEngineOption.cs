using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace PaddiXiangqi.Engine;

public sealed record UciEngineOption(string Name, string Type, string DefaultValue, int? Minimum, int? Maximum,
    IReadOnlyList<string> Variants)
{
    [JsonIgnore]
    public bool IsRuleOption => !string.IsNullOrWhiteSpace(Name) && (Regex.IsMatch(Name, @"(?:^|[ _-])(?:rules?|repetition|chase)(?:$|[ _-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        Name.StartsWith("Rule", StringComparison.OrdinalIgnoreCase) || Name == "Mate Threat Depth" ||
        Name.Contains("规则") || Name.Contains("长将") || Name.Contains("长捉"));

    public string ValidateRuleValue(string value)
    {
        if (!IsRuleOption || Name.IndexOfAny(['\r', '\n']) >= 0 || value is null || value.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("规则参数必须是引擎声明的单行 UCI 选项。");
        return Type switch
        {
            "combo" => Variants.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"引擎不支持 {Name} = {value}。请重新检测并选择规则。"),
            "check" => bool.TryParse(value, out var enabled) ? enabled.ToString().ToLowerInvariant()
                : throw new ArgumentException($"{Name} 必须为 true 或 false。"),
            "spin" => int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var number) &&
                number >= (Minimum ?? int.MinValue) && number <= (Maximum ?? int.MaxValue)
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : throw new ArgumentException($"{Name} 超出引擎支持的范围。"),
            "string" => value,
            _ => throw new NotSupportedException($"不支持保存 {Name} 的参数类型 {Type}。")
        };
    }

    public static UciEngineOption? Parse(string line)
    {
        var match = Regex.Match(line, @"^option name (.+?) type (check|spin|combo|button|string)(?: (.*))?$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var tail = match.Groups[3].Value;
        var value = Regex.Match(tail, @"(?:^| )default (.*?)(?= min | max | var |$)").Groups[1].Value;
        int? Number(string name) => int.TryParse(Regex.Match(tail, @"(?:^| )" + name + @" (-?\d+)").Groups[1].Value, out var n) ? n : null;
        var variants = Regex.Matches(tail, @"(?:^| )var (.*?)(?= var |$)").Select(item => item.Groups[1].Value).ToArray();
        return new(match.Groups[1].Value, match.Groups[2].Value, value, Number("min"), Number("max"), variants);
    }
}
public sealed record EngineIdentity(string Name, string Author, string? EvalFile, IReadOnlyList<UciEngineOption> Options);
