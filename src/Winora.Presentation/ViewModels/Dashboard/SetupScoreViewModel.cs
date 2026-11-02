// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Domain;

namespace Winora.Desktop;

public enum ScoreBand { Good, Fair, Low }

/// <summary>One category of the setup score: how many of its recommended settings are in place.</summary>
public sealed record ScoreCategory(SettingCategory Category, int InPlace, int Total)
{
    public double Fraction => Total == 0 ? 0 : (double)InPlace / Total;
}

/// <summary>The result of one count. <see cref="Percent"/> is null when no setting has a recommended choice that can be read on this PC.</summary>
public sealed record SetupScoreResult(int InPlace, int Total, IReadOnlyList<ScoreCategory> Categories)
{
    public int? Percent => Total == 0 ? null : (int)Math.Round(100.0 * InPlace / Total, MidpointRounding.AwayFromZero);
    public int Remaining => Total - InPlace;
    public ScoreBand Band => Percent switch { >= 80 => ScoreBand.Good, >= 50 => ScoreBand.Fair, _ => ScoreBand.Low };
}

/// <summary>
/// The setup score: the share of the recommended settings that are in place. It reads only the states the settings page already holds. It says nothing about speed.
/// A setting counts when it can be read and applies to this Windows build, and its definition names a recommended choice.
/// </summary>
public static class SetupScore
{
    public static SetupScoreResult Compute(IEnumerable<SettingState> states)
    {
        var perCategory = new Dictionary<SettingCategory, (int InPlace, int Total)>();
        int inPlace = 0, total = 0;
        foreach (var state in states)
        {
            var d = state.Definition;
            if (d.RecommendedChoiceId is not { } recommended || state.Issue is not null || state.Compatibility != SettingCompatibility.Supported) continue;
            total++;
            var ok = SettingFormat.EffectiveChoice(d, state.Actual, state.MatchedChoiceId) == recommended;
            if (ok) inPlace++;
            perCategory.TryGetValue(d.Category, out var c);
            perCategory[d.Category] = (c.InPlace + (ok ? 1 : 0), c.Total + 1);
        }
        var categories = perCategory.OrderBy(p => p.Key).Select(p => new ScoreCategory(p.Key, p.Value.InPlace, p.Value.Total)).ToList();
        return new(inPlace, total, categories);
    }
}

public sealed class ScoreCategoryRow(ScoreCategory category, LocalizationService text)
{
    public string Name => text.Enum(category.Category);
    public double Percent => category.Fraction * 100;
    public string CountText => text.Format("V2OvCategoryCount", category.InPlace, category.Total);
    public string AutomationName => text.Format("V2OvCategoryName", Name, category.InPlace, category.Total);
}

/// <summary>
/// The hero of the Overview page. It recounts when the settings change and when a scan ends, never on a timer. <see cref="AnimationToken"/> grows once per scan (and for the first count),
/// which makes the ring play its fill animation; every other change is shown at once.
/// </summary>
public sealed class SetupScoreViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly SettingsModel settings;
    private readonly Action review;
    private SetupScoreResult result = new(0, 0, []);
    private int token;
    private bool counted;

    public SetupScoreViewModel(LocalizationService text, SettingsModel settings, Action review)
    {
        this.text = text; this.settings = settings; this.review = review;
        ReviewCommand = new(() => this.review(), () => result.Remaining > 0);
        settings.Changed += (_, _) => Recount(false);
        text.PropertyChanged += (_, _) => Rebuild();
    }

    public RelayCommand ReviewCommand { get; }
    public ObservableCollection<ScoreCategoryRow> Categories { get; } = [];
    public SetupScoreResult Result => result;
    public int AnimationToken => token;
    public bool HasScore => result.Percent is not null;
    public bool HasNoScore => !HasScore;
    public double Value => result.Percent ?? 0;
    public ScoreBand Band => result.Band;
    public string Title => text["V2OvScoreTitle"];
    public string Of100 => text["V2OvOf100"];
    public string BandLabel => text[Band switch { ScoreBand.Good => "V2OvBandGood", ScoreBand.Fair => "V2OvBandFair", _ => "V2OvBandLow" }];
    public Tone BandTone => Band switch { ScoreBand.Good => Tone.Success, ScoreBand.Fair => Tone.Caution, _ => Tone.Danger };
    public string Headline => text[Band switch { ScoreBand.Good => "V2OvHeadGood", ScoreBand.Fair => "V2OvHeadFair", _ => "V2OvHeadLow" }];
    public string Detail => HasScore ? text.Format("V2OvDetail", result.InPlace, result.Total, result.Remaining) : text["V2OvNoScore"];
    public string ReviewLabel => text.Format("V2OvReview", result.Remaining);
    public string ReviewHelp => text["V2OvReviewHelp"];
    public string Note => text["V2OvScoreNote"];
    public string CategoriesTitle => text["V2OvCategories"];
    public string RingName => HasScore ? text.Format("V2OvRingName", result.Percent!.Value) : text["V2OvNoScore"];
    public string AutomationName => HasScore ? $"{Title}: {RingName}, {BandLabel}. {Detail}" : $"{Title}: {Detail}";

    /// <summary>Counts again. <paramref name="animate"/> starts the ring animation; the first count always does.</summary>
    public void Recount(bool animate)
    {
        var next = settings.Loaded ? SetupScore.Compute(settings.States) : new SetupScoreResult(0, 0, []);
        var first = !counted && next.Total > 0;
        result = next;
        if (next.Total > 0) counted = true;
        if (animate || first) token++;
        Rebuild();
    }

    private void Rebuild()
    {
        Categories.Clear();
        foreach (var c in result.Categories) Categories.Add(new(c, text));
        ReviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(string.Empty);
    }
}
