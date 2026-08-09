using System.Buffers.Binary;

namespace Yagu.Services.Index;

/// <summary>
/// A file's durable identity plus the USN of the last change recorded for it, as returned by
/// <c>FSCTL_READ_FILE_USN_DATA</c> (one file) or <c>FSCTL_ENUM_USN_DATA</c> (an MFT sweep).
/// </summary>
public readonly record struct UsnFileRecord(UsnFileIdentity Identity, long Usn, FileAttributes Attributes);

/// <summary>
/// Pure parser for <c>USN_RECORD</c> buffers whose <b>USN and attributes</b> matter, not just the change
/// reason (which is what <see cref="UsnRecordParser"/> extracts for journal replay). Fails closed on an
/// unknown major version or a truncated record so a rescan can never treat an unparsed file as unchanged.
/// </summary>
public static class UsnFileRecordParser
{
    // USN_RECORD_V2: FileRef @8 (8), ParentRef @16 (8), Usn @24 (8), TimeStamp @32, Reason @40,
    // SourceInfo @44, SecurityId @48, FileAttributes @52, FileNameLength @56.
    private const int V2FileRefOffset = 8;
    private const int V2UsnOffset = 24;
    private const int V2AttributesOffset = 52;
    private const int V2MinLength = 56;

    // USN_RECORD_V3: FileId @8 (16), ParentFileId @24 (16), Usn @40 (8), TimeStamp @48, Reason @56,
    // SourceInfo @60, SecurityId @64, FileAttributes @68, FileNameLength @72.
    private const int V3FileIdOffset = 8;
    private const int V3UsnOffset = 40;
    private const int V3AttributesOffset = 68;
    private const int V3MinLength = 72;

    /// <summary>Parses a single record from the start of <paramref name="record"/>.</summary>
    public static bool TryParseOne(ReadOnlySpan<byte> record, out UsnFileRecord parsed)
        => TryParseAt(record, 0, out parsed, out _);

    /// <summary>
    /// Parses packed records into <paramref name="sink"/>, stopping at a zero-length end marker, at
    /// <paramref name="maxRecords"/>, or at the buffer end.
    /// </summary>
    public static UsnParseStatus ParseRecords(
        ReadOnlySpan<byte> records,
        ICollection<UsnFileRecord> sink,
        int maxRecords = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int offset = 0;
        int count = 0;
        while (offset + 8 <= records.Length)
        {
            uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(records[offset..]);
            if (recordLength == 0)
                break; // end marker
            if (recordLength < 8 || offset + (long)recordLength > records.Length)
                return UsnParseStatus.Malformed;

            if (!TryParseAt(records, offset, out UsnFileRecord parsed, out UsnParseStatus status))
                return status;

            sink.Add(parsed);
            if (++count >= maxRecords)
                break;
            offset += (int)recordLength;
        }

        return UsnParseStatus.Ok;
    }

    private static bool TryParseAt(
        ReadOnlySpan<byte> records,
        int offset,
        out UsnFileRecord parsed,
        out UsnParseStatus status)
    {
        parsed = default;
        status = UsnParseStatus.Malformed;
        if (offset + 8 > records.Length)
            return false;

        uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(records[offset..]);
        if (recordLength < 8 || offset + (long)recordLength > records.Length)
            return false;

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(records[(offset + 4)..]);
        int usnOffset, attributesOffset, minLength;
        UsnFileIdentity identity;
        switch (major)
        {
            case 2:
                if (recordLength < V2MinLength)
                    return false;
                identity = UsnFileIdentity.FromFileReferenceNumber(
                    BinaryPrimitives.ReadUInt64LittleEndian(records[(offset + V2FileRefOffset)..]));
                usnOffset = V2UsnOffset;
                attributesOffset = V2AttributesOffset;
                minLength = V2MinLength;
                break;
            case 3:
                if (recordLength < V3MinLength)
                    return false;
                identity = UsnFileIdentity.FromFileId128(records.Slice(offset + V3FileIdOffset, 16));
                usnOffset = V3UsnOffset;
                attributesOffset = V3AttributesOffset;
                minLength = V3MinLength;
                break;
            default:
                status = UsnParseStatus.UnknownVersion;
                return false;
        }

        if (recordLength < minLength)
            return false;

        long usn = BinaryPrimitives.ReadInt64LittleEndian(records[(offset + usnOffset)..]);
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(records[(offset + attributesOffset)..]);
        parsed = new UsnFileRecord(identity, usn, attributes);
        status = UsnParseStatus.Ok;
        return true;
    }
}
