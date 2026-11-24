using System.Collections;
using System.Data.Common;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.Internal;

/// <summary>
/// A forward-only <see cref="DbDataReader"/> over an in-memory batch, so <c>SqlBulkCopy</c> streams the
/// already-read rows without building a <c>DataTable</c> copy of them.
/// </summary>
internal sealed class RowBatchDataReader : DbDataReader
{
    private readonly StorageTable _table;
    private readonly IReadOnlyList<object?[]> _rows;
    private int _index = -1;

    public RowBatchDataReader(StorageTable table, IReadOnlyList<object?[]> rows)
    {
        _table = table;
        _rows = rows;
    }

    private object?[] Current => _rows[_index];

    /// <inheritdoc/>
    public override int FieldCount => _table.Columns.Length;

    /// <inheritdoc/>
    public override bool HasRows => _rows.Count > 0;

    /// <inheritdoc/>
    public override bool IsClosed => false;

    /// <inheritdoc/>
    public override int RecordsAffected => -1;

    /// <inheritdoc/>
    public override int Depth => 0;

    /// <inheritdoc/>
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc/>
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc/>
    public override bool Read() => ++_index < _rows.Count;

    /// <inheritdoc/>
    public override bool NextResult() => false;

    /// <inheritdoc/>
    public override string GetName(int ordinal) => _table.Columns[ordinal].SqlName;

    /// <inheritdoc/>
    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _table.Columns.Length; i++)
        {
            if (string.Equals(_table.Columns[i].SqlName, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new IndexOutOfRangeException(name);
    }

    /// <inheritdoc/>
    public override Type GetFieldType(int ordinal) => _table.Columns[ordinal].Kind switch
    {
        StorageColumnKind.Guid => typeof(Guid),
        StorageColumnKind.String => typeof(string),
        StorageColumnKind.Boolean => typeof(bool),
        StorageColumnKind.Int32 => typeof(int),
        StorageColumnKind.Int64 => typeof(long),
        StorageColumnKind.Double => typeof(double),
        StorageColumnKind.DateTimeOffset => typeof(DateTimeOffset),
        _ => typeof(object),
    };

    /// <inheritdoc/>
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    /// <inheritdoc/>
    public override object GetValue(int ordinal) => Current[ordinal] ?? DBNull.Value;

    /// <inheritdoc/>
    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++)
            values[i] = GetValue(i);
        return n;
    }

    /// <inheritdoc/>
    public override bool IsDBNull(int ordinal) => Current[ordinal] is null;

    /// <inheritdoc/>
    public override bool GetBoolean(int ordinal) => (bool)Current[ordinal]!;

    /// <inheritdoc/>
    public override byte GetByte(int ordinal) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override char GetChar(int ordinal) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override double GetDouble(int ordinal) => (double)Current[ordinal]!;

    /// <inheritdoc/>
    public override float GetFloat(int ordinal) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override Guid GetGuid(int ordinal) => (Guid)Current[ordinal]!;

    /// <inheritdoc/>
    public override short GetInt16(int ordinal) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override int GetInt32(int ordinal) => (int)Current[ordinal]!;

    /// <inheritdoc/>
    public override long GetInt64(int ordinal) => (long)Current[ordinal]!;

    /// <inheritdoc/>
    public override string GetString(int ordinal) => (string)Current[ordinal]!;

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
