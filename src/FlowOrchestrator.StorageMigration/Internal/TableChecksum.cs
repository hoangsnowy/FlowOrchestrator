using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.Internal;

/// <summary>
/// Order-independent checksum of a table's rows, computed in the tool from normalized values so the same data
/// on SQL Server and PostgreSQL hashes equal.
/// </summary>
/// <remarks>
/// <para>Each row is serialized canonically (a type tag per value; strings as UTF-16LE with a length prefix;
/// integers widened to 64 bits; timestamps as UTC ticks truncated to the microsecond — the precision both
/// backends keep), hashed with SHA-256, and the first 128 bits are added (mod 2^128) into the table sum.
/// Addition, unlike XOR, does not cancel two identical rows, and the primary key makes rows unique anyway.</para>
/// <para>Consequence of the microsecond truncation: a sub-microsecond difference between the two sides is
/// not detected. That is the precision PostgreSQL stores, so it is the most the comparison can mean.</para>
/// <para>Cost: O(row bytes) per row, one reused buffer, no per-row allocation.</para>
/// </remarks>
internal sealed class TableChecksum
{
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private UInt128 _sum;

    /// <summary>Rows added so far.</summary>
    public long Count { get; private set; }

    /// <summary>Adds one row (values in <see cref="StorageTable.Columns"/> order).</summary>
    public void Add(StorageTable table, object?[] row)
    {
        _buffer.ResetWrittenCount();
        for (var i = 0; i < table.Columns.Length; i++)
            WriteValue(_buffer, table.Columns[i].Kind, row[i]);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(_buffer.WrittenSpan, hash);
        _sum += BinaryPrimitives.ReadUInt128LittleEndian(hash);
        Count++;
    }

    /// <summary>The checksum as 32 lower-case hex digits.</summary>
    public string Value => _sum.ToString("x32", CultureInfo.InvariantCulture);

    /// <summary>Writes the canonical encoding of one value.</summary>
    internal static void WriteValue(IBufferWriter<byte> writer, StorageColumnKind kind, object? value)
    {
        if (value is null)
        {
            WriteTag(writer, 0);
            return;
        }

        switch (kind)
        {
            case StorageColumnKind.Guid:
            {
                WriteTag(writer, 1);
                var span = writer.GetSpan(16);
                ((Guid)value).TryWriteBytes(span);
                writer.Advance(16);
                break;
            }

            case StorageColumnKind.String:
            {
                WriteTag(writer, 2);
                var chars = ((string)value).AsSpan();
                WriteInt64(writer, chars.Length);
                var bytes = MemoryMarshal.AsBytes(chars);
                if (!BitConverter.IsLittleEndian)
                {
                    var tmp = new char[chars.Length];
                    for (var i = 0; i < chars.Length; i++)
                        tmp[i] = (char)BinaryPrimitives.ReverseEndianness(chars[i]);
                    bytes = MemoryMarshal.AsBytes(tmp.AsSpan());
                }

                writer.Write(bytes);
                break;
            }

            case StorageColumnKind.Boolean:
                WriteTag(writer, 3);
                WriteTag(writer, (bool)value ? (byte)1 : (byte)0);
                break;

            case StorageColumnKind.Int32:
            case StorageColumnKind.Int64:
                WriteTag(writer, 4);
                WriteInt64(writer, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;

            case StorageColumnKind.Double:
                WriteTag(writer, 5);
                WriteInt64(writer, BitConverter.DoubleToInt64Bits((double)value));
                break;

            case StorageColumnKind.DateTimeOffset:
                WriteTag(writer, 6);
                WriteInt64(writer, ValueNormalizer.ToPostgresPrecision((DateTimeOffset)value).UtcTicks);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static void WriteTag(IBufferWriter<byte> writer, byte tag)
    {
        writer.GetSpan(1)[0] = tag;
        writer.Advance(1);
    }

    private static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), value);
        writer.Advance(8);
    }
}
