using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using SsdGuardian.Core;

namespace SsdGuardian.App;

internal sealed record DiagnosticEntry(DateTimeOffset Time, string Message)
{
    public string? MessageRussian { get; init; }

    internal string GetMessage(UiLanguage language) => language == UiLanguage.Russian ? MessageRussian ?? Message : Message;
}

internal sealed class ApplicationStore
{
    private const int MaxFileBytes = 1024 * 1024;
    internal const int RetainedEntries = 100;
    private readonly string _directory;
    private long _writtenBytes;
    private long _writeOperations;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    internal ApplicationStore(string directory)
    {
        _directory = directory;
    }

    internal string PathTo(string name) => Path.Combine(_directory, name);
    internal long WrittenBytes => Interlocked.Read(ref _writtenBytes);
    internal long WriteOperations => Interlocked.Read(ref _writeOperations);

    internal static bool IsStorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or System.Security.SecurityException;

    internal T? Read<T>(string file) where T : class
    {
        var path = PathTo(file);
        using var stream = OpenIfPresent(path);
        if (stream is null)
        {
            return null;
        }

        if (stream.Length > MaxFileBytes)
        {
            throw new InvalidDataException(UiText.Get("The data file exceeds 1 MB.", "Размер файла данных превышает 1 МБ."));
        }

        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }

    internal void Write<T>(string file, T value)
    {
        var bytes = WriteTo(PathTo(file), value);
        Interlocked.Add(ref _writtenBytes, bytes);
        Interlocked.Increment(ref _writeOperations);
    }

    internal static long WriteTo<T>(string fullPath, T value, int maxBytes = MaxFileBytes)
    {
        var fullFile = Path.GetFullPath(fullPath);
        var directory = Path.GetDirectoryName(fullFile)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            long bytes;
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                bytes = stream.Length;
                if (bytes > maxBytes)
                {
                    var limit = DisplayText.Bytes(maxBytes);
                    throw new InvalidDataException(UiText.Get($"The data file exceeds {limit}.", $"Размер файла данных превышает {limit}."));
                }
            }

            File.Move(temporary, fullFile, overwrite: true);
            return bytes;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static FileStream? OpenIfPresent(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
