using System.Globalization;

namespace SsdGuardian.App;

internal sealed record LaunchOptions(bool Tray, bool Demo, bool RequestElevation, string DataDirectory,
    TimeSpan? SmokeDuration, string? SmokeOutput)
{
    private const int MaxSmokeSeconds = 12 * 60 * 60;

    internal static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var tray = false;
        var demo = false;
        var requestElevation = false;
        var smokeTest = false;
        var smokeSeconds = 20;
        string? smokeOutput = null;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSD Guardian");

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--tray":
                    tray = true;
                    break;
                case "--demo":
                    demo = true;
                    break;
                case "--request-elevation":
                    requestElevation = true;
                    break;
                case "--data-dir":
                    directory = Path.GetFullPath(ReadValue(args, ref i));
                    break;
                case "--smoke-test":
                    smokeTest = true;
                    break;
                case "--smoke-seconds":
                    if (!int.TryParse(ReadValue(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out smokeSeconds) ||
                        smokeSeconds is < 5 or > MaxSmokeSeconds)
                    {
                        throw new ArgumentException(UiText.Get($"The smoke test duration must be between 5 and {MaxSmokeSeconds} seconds.",
                            $"Допустимая длительность проверки составляет от 5 до {MaxSmokeSeconds} секунд."));
                    }

                    break;
                case "--smoke-output":
                    smokeOutput = Path.GetFullPath(ReadValue(args, ref i));
                    break;
                default:
                    throw new ArgumentException(UiText.Get($"Unknown option {args[i]}.", $"Неизвестный параметр {args[i]}."));
            }
        }

        return new LaunchOptions(tray, demo, requestElevation, directory,
            smokeTest ? TimeSpan.FromSeconds(smokeSeconds) : null,
            smokeTest ? smokeOutput ?? Path.Combine(directory, "smoke-test.json") : null);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int position)
    {
        if (++position >= args.Count)
        {
            var option = args[position - 1];
            throw new ArgumentException(UiText.Get($"Option {option} requires a value.", $"Для параметра {option} требуется значение."));
        }

        return args[position];
    }
}
