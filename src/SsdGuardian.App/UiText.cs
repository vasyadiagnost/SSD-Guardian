using System.Globalization;
using SsdGuardian.Core;

namespace SsdGuardian.App;

internal static class UiText
{
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    internal static UiLanguage Language { get; set; } = UiLanguage.English;
    internal static CultureInfo Culture => Language == UiLanguage.Russian ? RussianCulture : EnglishCulture;

    internal static string Get(string english, string russian) => Language == UiLanguage.Russian ? russian : english;
}
