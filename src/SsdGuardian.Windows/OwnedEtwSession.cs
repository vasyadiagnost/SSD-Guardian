using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace SsdGuardian.Windows;

internal sealed class OwnedEtwSession : IDisposable
{
    private const int AlreadyExists = 183;
    private const int PropertiesSize = 120;
    private const int NameBytes = 2048;
    private const int GuidOffset = 24;
    private const int RealTimeSystemLogger = 0x0200_0100;
    private const KernelTraceEventParser.Keywords ProviderFlags =
        KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskFileIO |
        KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit |
        KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;
    private readonly ulong handle;
    private readonly Guid identity;
    private int stopped;

    private OwnedEtwSession(ulong handle, Guid identity)
    {
        this.handle = handle;
        this.identity = identity;
    }

    internal static OwnedEtwSession Start(string name, Guid identity)
    {
        int error = NativeMethods.StartTrace(out ulong handle, name, StartProperties(identity));
        if (error == AlreadyExists && TryStopVerified(name, identity))
        {
            error = NativeMethods.StartTrace(out handle, name, StartProperties(identity));
        }

        if (error != 0)
            throw new Win32Exception(error);
        return new OwnedEtwSession(handle, identity);
    }

    internal (long Events, long Buffers) GetLosses()
    {
        byte[] properties = NewProperties();
        int error = NativeMethods.ControlTrace(handle, null, properties, 0);
        if (error != 0)
            throw new Win32Exception(error);
        return (BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(88)),
            BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(100)));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;

        byte[] properties = NewProperties();
        if (NativeMethods.ControlTrace(handle, null, properties, 0) == 0 &&
            TryReadOwnedHandle(properties, identity, out ulong activeHandle) && activeHandle == handle)
            Stop(handle);
    }

    internal static bool TryStopVerified(string name, Guid identity)
    {
        // The caller holds the shared mutex. A matching GUID identifies a stale Guardian logger.
        byte[] properties = NewProperties();
        return NativeMethods.ControlTrace(0, name, properties, 0) == 0 &&
            TryReadOwnedHandle(properties, identity, out ulong handle) && Stop(handle) == 0;
    }

    internal static bool TryReadOwnedHandle(ReadOnlySpan<byte> properties, Guid identity, out ulong handle)
    {
        handle = 0;
        if (properties.Length < PropertiesSize || identity == Guid.Empty ||
            new Guid(properties.Slice(GuidOffset, 16)) != identity ||
            (BinaryPrimitives.ReadInt32LittleEndian(properties[64..]) & RealTimeSystemLogger) != RealTimeSystemLogger)
            return false;

        handle = BinaryPrimitives.ReadUInt64LittleEndian(properties[8..]);
        return handle != 0 && handle != ulong.MaxValue;
    }

    private static int Stop(ulong handle) => NativeMethods.ControlTrace(handle, null, NewProperties(), 1);

    private static byte[] StartProperties(Guid identity)
    {
        byte[] properties = NewProperties();
        identity.TryWriteBytes(properties.AsSpan(GuidOffset, 16));
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(40), 1); // Query Performance Counter.
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(44), 0x0002_0000); // WNODE_FLAG_TRACED_GUID.
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(48), 64); // KiB per buffer.
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(56), 128); // At most 8 MiB, subject to ETW's CPU minimum.
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(64), RealTimeSystemLogger);
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(68), 1); // Flush in seconds.
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(72), (uint)ProviderFlags);
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(112), 0); // Real-time only, no logfile name.
        return properties;
    }

    private static byte[] NewProperties()
    {
        // EVENT_TRACE_PROPERTIES on x64, followed by logger and file name buffers.
        byte[] properties = new byte[PropertiesSize + NameBytes * 2];
        BinaryPrimitives.WriteInt32LittleEndian(properties, properties.Length);
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(112), PropertiesSize + NameBytes);
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(116), PropertiesSize);
        return properties;
    }
}
