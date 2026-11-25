using System.Buffers.Binary;
using SsdGuardian.Core;
using SsdGuardian.Windows;

internal static class RecoveryChecks
{
    internal static (string Name, Action Run)[] Scenarios =>
    [
        ("ETW recovery retries failure and resets backoff after a healthy connection", RetryAndRecover),
        ("ETW recovery caps retries and honors cooperative cancellation", RetryCapAndCancel),
        ("ETW stale recovery verifies the logger GUID, mode and handle", VerifySession),
        ("Windows storage IDs distinguish device identifiers from port and malformed IDs", VerifyStorageId),
        ("Duplicate Windows disk identifiers stay uncertain", CheckDuplicateIds)
    ];

    private static void RetryAndRecover()
    {
        var clock = new TestTimeProvider();
        var attempts = 0;
        var delays = new List<double>();
        var failures = new List<Exception?>();
        EtwRetry.Run(() =>
        {
            if (++attempts <= 2)
                throw new IOException("simulated stalled trace");
            clock.Advance(TimeSpan.FromSeconds(15));
        }, failures.Add, delay =>
        {
            delays.Add(delay.TotalSeconds);
            return attempts == 3;
        }, CancellationToken.None, clock);

        Require(attempts == 3 && delays.SequenceEqual([1d, 2d, 1d]), "incorrect retry cycle");
        Require(failures[0] is IOException && failures[1] is IOException && failures[2] is null,
            "the retry loop did not report both start failures and disconnection");
    }

    private static void RetryCapAndCancel()
    {
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        var delays = new List<double>();
        EtwRetry.Run(() =>
        {
            if (++attempts == 8)
                stop.Cancel();
        }, _ => { }, delay =>
        {
            delays.Add(delay.TotalSeconds);
            return false;
        }, stop.Token, new TestTimeProvider());

        Require(attempts == 8 && delays.SequenceEqual([1d, 2d, 4d, 8d, 16d, 30d, 30d]),
            "retry backoff or shutdown was incorrect");
    }

    private static void VerifySession()
    {
        byte[] properties = new byte[120];
        Guid session = DiskWriteMonitor.ProductionSessionId;
        session.TryWriteBytes(properties.AsSpan(24));
        BinaryPrimitives.WriteUInt64LittleEndian(properties.AsSpan(8), 53);
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(64), 0x0200_0100);
        Require(OwnedEtwSession.TryReadOwnedHandle(properties, session, out ulong handle) && handle == 53,
            "the app's real-time kernel session was not recognized");
        Require(!OwnedEtwSession.TryReadOwnedHandle(properties, Guid.NewGuid(), out _), "foreign logger accepted");
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(64), 0x100);
        Require(!OwnedEtwSession.TryReadOwnedHandle(properties, session, out _), "wrong logger mode accepted");
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(64), 0x0200_0100);
        BinaryPrimitives.WriteUInt64LittleEndian(properties.AsSpan(8), ulong.MaxValue);
        Require(!OwnedEtwSession.TryReadOwnedHandle(properties, session, out _), "invalid handle accepted");
    }

    private static void VerifyStorageId()
    {
        byte[] descriptor = new byte[12 + 24 * 3];
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(4), descriptor.Length);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(8), 3);
        WriteIdentifier(descriptor.AsSpan(12), 3, association: 1, next: 24, "AFFEAFFEAFFEAFFE");
        WriteIdentifier(descriptor.AsSpan(36), 2, association: 0, next: 24, "0102030405060708");
        WriteIdentifier(descriptor.AsSpan(60), 3, association: 0, next: 0, "5001020304050607");
        Require(StorageInventory.ReadStandardDeviceId(descriptor) == "vpd\0" + "3\0" + "5001020304050607",
            "device-scoped NAA identifier was not selected");

        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(46), 16);
        Require(StorageInventory.ReadStandardDeviceId(descriptor) is null, "invalid descriptor overlap was accepted");
    }

    private static void WriteIdentifier(Span<byte> buffer, int type, int association, ushort next, string hex)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, 1); // Binary identifier.
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..], next);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[12..], association);
        Convert.FromHexString(hex).CopyTo(buffer[16..]);
    }

    private static void CheckDuplicateIds()
    {
        var inventory = StorageInventory.DiscardDuplicateIdentities([
            new(0, "Disk A", true, []) { PersistentId = "duplicate" },
            new(1, "Disk B", true, []) { PersistentId = "duplicate" },
            new(2, "Disk C", true, []) { PersistentId = "valid" }
        ]);
        Require(inventory[0].PersistentId is null && inventory[1].PersistentId is null &&
            inventory[2].PersistentId == "valid", "ambiguous hardware identity was retained");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        internal void Advance(TimeSpan amount) => ticks += amount.Ticks;
    }
}
