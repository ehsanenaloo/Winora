// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Winora.Desktop;

public sealed class LocalizationService : ObservableObject
{
    private static readonly CultureInfo Fallback = CultureInfo.GetCultureInfo("en");
    private readonly ResourceManager resources = new("Winora.Desktop.Resources.Strings", typeof(LocalizationService).Assembly);
    public CultureInfo Culture { get; private set; } = Fallback;
    public string this[string key] => resources.GetString(key, Culture) ?? resources.GetString(key, Fallback) ?? resources.GetString("UnknownResult", Fallback)!;
    /// <summary>The text of a key in one language, whatever language the window shows (the search index holds both). Null when neither language has it.</summary>
    public string? Lookup(string key, string language) =>
        resources.GetString(key, CultureInfo.GetCultureInfo(language == "fa" ? "fa" : "en")) ?? resources.GetString(key, Fallback);
    public bool Has(string key) => resources.GetString(key, Culture) is not null || resources.GetString(key, Fallback) is not null;
    /// <summary>Formats a text. In Persian the number arguments (counts, sizes, parts of dates) are written with Persian digits; text arguments such as paths and versions stay as they are.</summary>
    public string Format(string key, params object[] args) =>
        string.Format(Culture, this[key], Language == "fa" ? args.Select(a => DigitConverter.IsNumber(a) ? DigitConverter.Format(a, Culture) : a).ToArray() : args);
    /// <summary>A whole number for running text: Persian digits in Persian, Latin digits otherwise.</summary>
    public string FormatNumber(long value) => Language == "fa" ? DigitConverter.Format(value, Culture) : value.ToString("N0", Culture);
    /// <summary>
    /// Formats a text whose wording depends on a count. English needs a singular form ("1 change"), kept as the key with "One" at the end; Persian keeps the
    /// same form after any number, so its "One" text may equal the plain one.
    /// </summary>
    public string Plural(string key, long count, params object[] args) => Format(count == 1 && Has(key + "One") ? key + "One" : key, args);
    /// <summary>The digits of an already formatted number, date or time: Persian digits in Persian, unchanged otherwise. Versions, ids and addresses must not go through this.</summary>
    public string Digits(string text) => Language == "fa" ? DigitConverter.ToPersian(text) : text;
    /// <summary>A number with a fixed count of decimals and the culture's group and decimal marks ("3٫03" in Persian, "3.03" in English).</summary>
    public string Number(double value, int decimals = 0) => Digits(value.ToString("N" + decimals, Culture));
    /// <summary>A percentage with the culture's percent sign ("۷۶٪" in Persian, "76%" in English).</summary>
    public string Percent(double percent, int decimals = 0) => Format("PercentFormat", Number(percent, decimals));
    /// <summary>A byte count as one number and unit string ("3.03 GB", "۳٫۰۳ گیگابایت").</summary>
    public string Size(long bytes) => SizeFormat.Bytes(this, bytes);
    /// <summary>A local date and time in the selected culture (the Persian calendar in Persian) with the digits of the language.</summary>
    public string When(DateTime local, string format = "g") => Digits(local.ToString(format, Culture));
    /// <summary>A moment shown in local time, in the selected culture and digits.</summary>
    public string When(DateTimeOffset moment, string format = "g") => When(moment.ToLocalTime().DateTime, format);
    /// <summary>
    /// Latin data shown inside right-to-left text (names, paths, drive letters, addresses, versions, hardware strings): pinned to left-to-right order so
    /// WPF does not mirror it. Unchanged in English. Use <see cref="Bidi.Strip"/> before comparing or copying.
    /// </summary>
    public string Ltr(string? text) => Bidi.Ltr(text, Direction);
    /// <summary>Latin values (addresses, adapter names) joined with the list separator of the language; each value keeps its order in a right-to-left row.</summary>
    /// <summary>A Latin part between Persian separators: pinned, and fenced with right-to-left marks so the separators next to it stay in the page direction.</summary>
    public string LtrPart(string? text) => Direction == TextDirection.RightToLeft ? Bidi.RightToLeftMark + Ltr(text) + Bidi.RightToLeftMark : text ?? "";
    public string LtrList(IEnumerable<string?> values) => Bidi.Join(this["ListSeparator"], Direction, [.. values]);
    /// <summary>A name that may be Latin or Persian (program, device, plan): pinned like <see cref="Ltr"/> only when it has no right-to-left letter.</summary>
    public string Name(string? text) => text is null || Bidi.HasRightToLeft(text) ? text ?? "" : Ltr(text);
    /// <summary>Resource key of an enum label: "Enum_" + type name + "_" + member name.</summary>
    public static string EnumKey<TEnum>(TEnum value) where TEnum : struct, Enum => $"Enum_{typeof(TEnum).Name}_{value}";
    /// <summary>Localized label of an enum member; the member name is only a last resort for members without a resource.</summary>
    public string Enum<TEnum>(TEnum value) where TEnum : struct, Enum => Has(EnumKey(value)) ? this[EnumKey(value)] : value.ToString();
    public TextDirection Direction => Culture.TwoLetterISOLanguageName == "fa" ? TextDirection.RightToLeft : TextDirection.LeftToRight;
    public string Language => Culture.TwoLetterISOLanguageName;
    /// <summary>Raised with the two-letter language code each time a language is applied. The window project hooks the type scale to it.</summary>
    public static event Action<string>? LanguageApplied;
    public void SetLanguage(string language)
    {
        Culture = CultureInfo.GetCultureInfo(language == "fa" ? "fa" : "en");
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        LanguageApplied?.Invoke(Culture.TwoLetterISOLanguageName);
        OnPropertyChanged(nameof(Culture));
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged("Item[]");
    }
}
