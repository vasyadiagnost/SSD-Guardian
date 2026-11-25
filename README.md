# SSD Guardian v0.1.2

[Руководство на русском](README.ru.md)

SSD Guardian is a portable Windows 11 x64 utility that monitors physical writes by SSD, shows logical writes by process and file, and alerts on sustained or intense rewriting.

## Start SSD Guardian

1. Extract `SSD-Guardian-v0.1.2-win-x64.zip` to a new folder and run `SSDGuardian.exe` inside `SSD Guardian`. The archive includes the .NET runtime.
2. Select **Run as administrator** and approve the Windows UAC prompt to enable ETW monitoring.
3. Close the main window or minimize it to the tray to keep monitoring. Select **Exit** from the tray menu to stop monitoring.

English is the default. Open **Settings → Language**, select **Русский**, and save to apply Russian immediately. The profile remembers your language. History and diagnostic messages saved by earlier versions retain their original wording.

Settings, anomaly history, and diagnostics live in `%LOCALAPPDATA%\SSD Guardian`. Use `--data-dir "D:\profile"` for a separate profile. Startup at sign-in is opt-in. Try a sample alert with `SSDGuardian.exe --demo`.

## Read the counters

Physical counters are per SSD. Logical counters show file requests by process identity and file on a known SSD volume. Both include byte and operation counts across 10, 30, and 60 minutes and the current session.

Click any process column header to cycle through descending, ascending, and the default ordering by recent logical writes. An arrow marks the selected column and direction.

An anomaly needs a write threshold and evidence such as repeated writes with concurrent physical activity on the same SSD. Defaults are 1 GB over 30 minutes and 5 GB over 10 minutes. Alert details show the evidence, let you snooze alerts for an hour, and offer a confirmation step before terminating a process.

## Build and test

Run `./build.ps1` from a Windows x64 checkout with .NET SDK 10.0.404. For an extracted SDK, pass `-Dotnet 'C:\path\to\dotnet.exe'`. The script runs the automated tests and writes the portable ZIP, its SHA-256, and a test log to `artifacts`.

On an elevated test machine, run `dotnet run --project tests/SsdGuardian.Windows.Tests -c Release -- --live` for a separate ETW recovery, SSD write, and UDP loopback check. Run `./tests/Measure-Guardian.ps1 -Executable 'C:\path\SSDGuardian.exe' -Seconds 7200` for a two-hour resource sample using an isolated profile.

- [Research and architecture (Russian)](docs/research.md)
- [Performance measurements (Russian)](docs/performance.md)
- [Known limitations (Russian)](docs/limitations.md)

## License

SSD Guardian is licensed under the [MIT License](LICENSE). Bundled third-party components retain their respective licenses and notices.
