using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SsdGuardian.Core;

namespace SsdGuardian.Windows;

internal sealed class StorageInventory
{
    private const int DeviceProperty = 0;
    private const int DeviceIdProperty = 2;
    private const int SeekPenaltyProperty = 7;
    private const uint DriveRemote = 4;
    private static readonly string WindowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static int nextVersion;
    private readonly VolumeAlias[] aliases;
    private readonly HashSet<char> remoteDrives;
    private readonly HashSet<int> ssdNumbers;
    private readonly HashSet<int> confirmedNonSsdNumbers;

    private StorageInventory(DiskInfo[] disks, VolumeAlias[] aliases, HashSet<char> remoteDrives)
    {
        Disks = Array.AsReadOnly(disks);
        this.aliases = aliases;
        this.remoteDrives = remoteDrives;
        ssdNumbers = disks.Where(disk => disk.IsSsd).Select(disk => disk.Number).ToHashSet();
        confirmedNonSsdNumbers = disks.Where(disk => disk.IsTypeKnown && !disk.IsSsd).Select(disk => disk.Number).ToHashSet();
    }

    public IReadOnlyList<DiskInfo> Disks { get; }
    public int Version { get; } = Interlocked.Increment(ref nextVersion);
    public bool IsSsd(int number) => ssdNumbers.Contains(number);
    public bool IsConfirmedNonSsd(int number) => confirmedNonSsdNumbers.Contains(number);

    public static StorageInventory Discover()
    {
        var remoteDrives = FindRemoteDrives();
        var volumes = EnumerateVolumes().ToArray();
        var aliases = new List<VolumeAlias>();
        foreach (var volume in volumes)
        {
            int? disk = volume.DiskNumbers.Length == 1 ? volume.DiskNumbers[0] : null;
            string preferred = volume.Mounts.FirstOrDefault() ?? volume.GuidPath;
            aliases.Add(new(volume.GuidPath, preferred, disk));
            foreach (string mount in volume.Mounts)
                aliases.Add(new(mount, mount, disk));
            foreach (string device in volume.Devices.Take(1))
                aliases.Add(new(device.TrimEnd('\\') + '\\', preferred, disk));
        }

        const string physicalPrefix = "PhysicalDrive";
        int[] numbers = QueryDosNames(null)
            .Where(name => name.StartsWith(physicalPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => int.TryParse(name.AsSpan(physicalPrefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out int number) ? number : -1)
            .Where(number => number >= 0)
            .Concat(volumes.SelectMany(volume => volume.DiskNumbers))
            .Distinct().Order().ToArray();

        var disks = new List<DiskInfo>(numbers.Length);
        foreach (int number in numbers)
        {
            using var device = NativeMethods.OpenDevice($@"\\.\PhysicalDrive{number}");
            string? label = null;
            bool? isSsd = null;
            string? persistentId = null;
            if (!device.IsInvalid)
            {
                byte[] descriptors = new byte[1024];
                if (QueryProperty(device, DeviceProperty, descriptors, out int descriptorLength) && descriptorLength >= 36)
                {
                    string model = ReadDescriptorString(descriptors, descriptorLength, 16);
                    if (model.Length != 0)
                        label = model;

                    uint bus = BinaryPrimitives.ReadUInt32LittleEndian(descriptors.AsSpan(28));
                    // Layered devices do not expose the media type of their physical member disks here.
                    bool directStorage = bus is 1 or 2 or 3 or 4 or 10 or 11 or 12 or 13 or 17 or 19 ||
                        (bus == 7 && descriptors[10] == 0);
                    if (directStorage)
                    {
                        string vendor = ReadDescriptorString(descriptors, descriptorLength, 12);
                        string serial = ReadDescriptorString(descriptors, descriptorLength, 24);
                        persistentId = ReadPersistentId(device, bus, vendor, model, serial);
                        if (QueryProperty(device, SeekPenaltyProperty, descriptors, out int penaltyLength) &&
                            penaltyLength >= 9 && descriptors[8] <= 1)
                            isSsd = descriptors[8] == 0;
                    }
                }
            }

            string[] mounted = volumes.Where(volume => volume.DiskNumbers.Contains(number))
                .SelectMany(volume => volume.Mounts.Length > 0 ? volume.Mounts : [volume.GuidPath])
                .ToArray();
            disks.Add(new DiskInfo(number, label ?? $"Disk {number}", isSsd == true, Array.AsReadOnly(mounted))
            {
                UsesDefaultName = label is null,
                IsTypeKnown = isSsd.HasValue,
                PersistentId = persistentId
            });
        }

        return new(DiscardDuplicateIdentities(disks), aliases.OrderByDescending(item => item.Prefix.Length).ToArray(), remoteDrives);
    }

    internal static DiskInfo[] DiscardDuplicateIdentities(IReadOnlyList<DiskInfo> disks)
    {
        var duplicates = disks.Where(disk => disk.PersistentId is not null)
            .GroupBy(disk => disk.PersistentId!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return disks.Select(disk => disk.PersistentId is { } id && duplicates.Contains(id)
            ? disk with { PersistentId = null } : disk).ToArray();
    }

    private static string? ReadPersistentId(SafeFileHandle device, uint bus, string vendor, string model, string serial)
    {
        byte[] descriptor = new byte[4096];
        if (QueryProperty(device, DeviceIdProperty, descriptor, out int length) &&
            ReadStandardDeviceId(descriptor.AsSpan(0, length)) is { } standard)
            return DigestIdentity(standard);

        // Preferred native buses name the unit itself. USB bridges can report their own serial.
        if (bus is 3 or 10 or 11 or 17 or 19 && model.Length != 0 && IsUsefulSerial(serial))
            return DigestIdentity($"serial\0{bus}\0{vendor.ToUpperInvariant()}\0{model.ToUpperInvariant()}\0{serial.ToUpperInvariant()}");
        return null;
    }

    internal static string? ReadStandardDeviceId(ReadOnlySpan<byte> descriptor)
    {
        const int headerLength = 12;
        const int identifierHeaderLength = 16;
        if (descriptor.Length < headerLength)
            return null;

        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
        if (declaredSize < headerLength || declaredSize > descriptor.Length)
            return null;
        descriptor = descriptor[..(int)declaredSize];
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
        int offset = headerLength;
        int bestType = 0;
        string? best = null;
        for (uint index = 0; index < count && descriptor.Length - offset >= identifierHeaderLength; index++)
        {
            ReadOnlySpan<byte> item = descriptor[offset..];
            int codeSet = BinaryPrimitives.ReadInt32LittleEndian(item);
            int type = BinaryPrimitives.ReadInt32LittleEndian(item[4..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(item[8..]);
            int next = BinaryPrimitives.ReadUInt16LittleEndian(item[10..]);
            int association = BinaryPrimitives.ReadInt32LittleEndian(item[12..]);
            if (size > item.Length - identifierHeaderLength)
                return null;

            // VPD page 83 identifiers scoped to the device, ranked by the Windows storage provider's order.
            if (association == 0 && type >= bestType && IsStandardId(type, codeSet, size))
            {
                ReadOnlySpan<byte> value = item.Slice(identifierHeaderLength, size);
                if (value.ContainsAnyExcept((byte)0) && value.ContainsAnyExcept((byte)0xff))
                {
                    var candidate = $"vpd\0{type}\0{Convert.ToHexString(value)}";
                    if (type > bestType || StringComparer.Ordinal.Compare(candidate, best) < 0)
                    {
                        best = candidate;
                        bestType = type;
                    }
                }
            }

            if (index + 1 == count)
                return best;
            if (next < identifierHeaderLength + size || next > item.Length)
                return null;
            offset += next;
        }
        return null;
    }

    private static bool IsStandardId(int type, int codeSet, int size) => type switch
    {
        2 => codeSet == 1 && size is 8 or 12 or 16, // EUI identifiers.
        3 => codeSet == 1 && size is 8 or 16, // NAA identifiers.
        8 => codeSet is 2 or 3 && size is >= 4 and <= 256, // SCSI name string.
        _ => false
    };

    private static bool IsUsefulSerial(string serial) => serial.Length is >= 4 and <= 128 &&
        !serial.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
        !serial.Equals("none", StringComparison.OrdinalIgnoreCase) &&
        serial.Any(character => char.IsAsciiLetterOrDigit(character) && character != '0') &&
        serial.Any(character => character != serial[0]);

    private static string DigestIdentity(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public MappedFile MapPath(string? rawPath)
    {
        if (string.IsNullOrEmpty(rawPath))
            return default;

        string path = rawPath;
        if (path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            path = string.Concat(@"\\?\", path.AsSpan(4));
        const string systemRoot = @"\SystemRoot\";
        if (WindowsPath.Length > 0 && path.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase))
            path = string.Concat(WindowsPath.AsSpan().TrimEnd('\\'), path.AsSpan(systemRoot.Length - 1));
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\Device\Mup\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\Device\LanmanRedirector\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\Device\WebDavRedirector\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\Device\Rdbss\", StringComparison.OrdinalIgnoreCase) ||
            (path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2 && path[2] is not '?' and not '.'))
            return new(null, null, true);

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 7 && path[5] == ':')
            path = path[4..];
        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\' && remoteDrives.Contains(char.ToUpperInvariant(path[0])))
            return new(null, null, true);

        foreach (var alias in aliases)
        {
            if (path.StartsWith(alias.Prefix, StringComparison.OrdinalIgnoreCase))
                return new(alias.Disk, alias.DisplayPrefix.Equals(alias.Prefix, StringComparison.OrdinalIgnoreCase)
                    ? path : string.Concat(alias.DisplayPrefix, path.AsSpan(alias.Prefix.Length)), false);
        }

        return new(null, path, false);
    }

    private static HashSet<char> FindRemoteDrives()
    {
        uint drives = NativeMethods.GetLogicalDrives();
        var remote = new HashSet<char>();
        for (int index = 0; index < 26; index++)
        {
            char letter = (char)('A' + index);
            if ((drives & (1u << index)) != 0 && NativeMethods.GetDriveType($@"{letter}:\") == DriveRemote)
                remote.Add(letter);
        }

        return remote;
    }

    private static IEnumerable<Volume> EnumerateVolumes()
    {
        var name = new StringBuilder(128);
        IntPtr search = NativeMethods.FindFirstVolume(name, name.Capacity);
        if (search == new IntPtr(-1))
            yield break;

        try
        {
            do
            {
                string path = name.ToString();
                string devicePath = path.TrimEnd('\\');
                using var device = NativeMethods.OpenDevice(devicePath);
                int[] disks = device.IsInvalid ? [] : QueryDiskExtents(device);
                yield return new(path, disks, GetMounts(path), QueryDosNames(devicePath[4..]));
                name.Clear();
            }
            while (NativeMethods.FindNextVolume(search, name, name.Capacity));
        }
        finally
        {
            NativeMethods.FindVolumeClose(search);
        }
    }

    private static int[] QueryDiskExtents(SafeFileHandle device)
    {
        // VOLUME_DISK_EXTENTS has an aligned header and a 24-byte DISK_EXTENT on x64.
        byte[] output = new byte[8 + 24 * 16];
        bool ok = NativeMethods.DeviceIoControl(device, NativeMethods.IoctlVolumeGetVolumeDiskExtents,
            null, 0, output, output.Length, out int returned, IntPtr.Zero);
        if (!ok && returned >= 4)
        {
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(output);
            if (count is > 16 and <= 1024)
            {
                output = new byte[8 + 24 * (int)count];
                ok = NativeMethods.DeviceIoControl(device, NativeMethods.IoctlVolumeGetVolumeDiskExtents,
                    null, 0, output, output.Length, out returned, IntPtr.Zero);
            }
        }

        if (!ok || returned < 8)
            return [];

        uint extents = BinaryPrimitives.ReadUInt32LittleEndian(output);
        if (extents > (returned - 8) / 24)
            return [];

        var disks = new HashSet<int>();
        for (int index = 0; index < extents; index++)
            disks.Add(BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8 + index * 24)));

        return disks.ToArray();
    }

    private static bool QueryProperty(SafeFileHandle device, int property, byte[] descriptor, out int returned)
    {
        // STORAGE_PROPERTY_QUERY, PropertyStandardQuery, with no additional parameter data.
        byte[] query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, property);
        return NativeMethods.DeviceIoControl(device, NativeMethods.IoctlStorageQueryProperty,
            query, query.Length, descriptor, descriptor.Length, out returned, IntPtr.Zero) &&
            returned >= 0 && returned <= descriptor.Length;
    }

    private static string ReadDescriptorString(byte[] descriptor, int length, int offsetOfField)
    {
        int offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(offsetOfField));
        if (offset <= 0 || offset >= length)
            return "";

        int end = Array.IndexOf(descriptor, (byte)0, offset, length - offset);
        return end < 0 ? "" : Encoding.ASCII.GetString(descriptor, offset, end - offset).Trim();
    }

    private static string[] GetMounts(string volume)
    {
        char[] result = new char[256];
        if (NativeMethods.GetVolumePathNamesForVolumeName(volume, result, result.Length, out int required))
            return MultiString(result, result.Length);
        if (required <= result.Length || required > 65536)
            return [];

        result = new char[required];
        return NativeMethods.GetVolumePathNamesForVolumeName(volume, result, result.Length, out _)
            ? MultiString(result, result.Length) : [];
    }

    private static string[] QueryDosNames(string? volume)
    {
        for (int length = volume is null ? 32768 : 1024; length <= 262144; length *= 2)
        {
            char[] buffer = new char[length];
            uint count = NativeMethods.QueryDosDevice(volume, buffer, buffer.Length);
            if (count != 0)
                return MultiString(buffer, (int)count);
            if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 122)
                break;
        }

        return [];
    }

    private static string[] MultiString(char[] buffer, int length) =>
        new string(buffer, 0, length).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private sealed record Volume(string GuidPath, int[] DiskNumbers, string[] Mounts, string[] Devices);
    private sealed record VolumeAlias(string Prefix, string DisplayPrefix, int? Disk);
    public readonly record struct MappedFile(int? Disk, string? Path, bool IsNetwork);
}
