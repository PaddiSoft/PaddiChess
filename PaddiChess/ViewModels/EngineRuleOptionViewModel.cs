using System.Globalization;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.ViewModels;

public sealed record RuleChoice(string Value, string Label) { public override string ToString() => Label; }

public sealed class EngineRuleOptionViewModel : ObservableObject
{
    public UciEngineOption Option { get; }
    public string Label { get; }
    public string Hint => Option.Name + " · 引擎默认 " + Option.DefaultValue;
    public bool IsCombo => Option.Type == "combo";
    public bool IsCheck => Option.Type == "check";
    public bool IsSpin => Option.Type == "spin";
    public bool IsText => !IsCombo && !IsCheck && !IsSpin;
    public decimal Minimum => Option.Minimum ?? int.MinValue;
    public decimal Maximum => Option.Maximum ?? int.MaxValue;
    public RuleChoice[] Choices { get; }
    private RuleChoice? _choice;
    private bool _enabled;
    private decimal? _number;
    private string _text;
    public RuleChoice? Choice { get => _choice; set => Set(ref _choice, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public decimal? Number { get => _number; set => Set(ref _number, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public EngineRuleOptionViewModel(UciEngineOption option, string value, Func<string, string> choiceLabel)
    {
        Option = option;
        Label = option.Name switch
        {
            "Repetition Rule" => "循环与长将、长捉", "Sixty Move Rule" => "自然限着判和",
            "Rule60MaxPly" => "自然限着步数（半回合）", "Draw Rule" => "和棋处理",
            "Mate Threat Depth" => "长杀判定搜索深度", _ => option.Name
        };
        Choices = option.Variants.Select(v => new RuleChoice(v, choiceLabel(v))).ToArray();
        _choice = Choices.FirstOrDefault(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        _enabled = value.Equals("true", StringComparison.OrdinalIgnoreCase);
        _number = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
        _text = value;
    }
    public string ReadValue()
    {
        if (IsSpin && Number is { } number && number != decimal.Truncate(number))
            throw new ArgumentException($"{Label} 必须为整数。");
        return Option.ValidateRuleValue(IsCombo ? Choice?.Value ?? "" :
            IsCheck ? Enabled.ToString().ToLowerInvariant() :
            IsSpin ? Number?.ToString("0", CultureInfo.InvariantCulture) ?? "" : Text);
    }
}
