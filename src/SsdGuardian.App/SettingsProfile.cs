using System.Text.Json;
using System.Text.Json.Nodes;
using SsdGuardian.Core;

namespace SsdGuardian.App;

internal sealed record SettingsProfile(GuardianOptions Options, UiLanguage Language)
{
    private const string FileName = "settings.json";

    internal static SettingsProfile Load(ApplicationStore store, out Exception? warning)
    {
        var language = UiLanguage.English;
        try
        {
            var document = store.Read<JsonObject>(FileName);
            if (document?[nameof(Language)] is JsonValue languageValue &&
                languageValue.TryGetValue<string>(out var name) &&
                string.Equals(name, nameof(UiLanguage.Russian), StringComparison.OrdinalIgnoreCase))
            {
                language = UiLanguage.Russian;
            }

            var options = document?.Deserialize<GuardianOptions>(ApplicationStore.JsonOptions) ?? new GuardianOptions();
            options.Validate(language);
            warning = null;
            return new SettingsProfile(options, language);
        }
        catch (Exception ex) when (ApplicationStore.IsStorageError(ex) || ex is ArgumentOutOfRangeException)
        {
            warning = ex;
            return new SettingsProfile(new GuardianOptions(), language);
        }
    }

    internal void Save(ApplicationStore store)
    {
        var document = JsonSerializer.SerializeToNode(Options, ApplicationStore.JsonOptions)!.AsObject();
        document[nameof(Language)] = Language.ToString();
        store.Write(FileName, document);
    }
}
