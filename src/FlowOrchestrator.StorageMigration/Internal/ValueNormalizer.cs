using System.Globalization;
using FlowOrchestrator.StorageMigration.Schema;

namespace FlowOrchestrator.StorageMigration.Internal;

/// <summary>
/// Converts a value read from one backend into what the other backend stores, and rejects values the target
/// cannot hold — before the bulk load, so the error names the row and column instead of a COPY offset.
/// </summary>
/// <remarks>
/// <para><b>Timestamps.</b> SQL Server <c>DATETIMEOFFSET</c> keeps 100 ns ticks and an offset; PostgreSQL
/// <c>TIMESTAMPTZ</c> keeps microseconds in UTC. Toward PostgreSQL the value is converted to UTC and
/// truncated (not rounded) to the microsecond, explicitly here rather than inside Npgsql, so the result is
/// defined by this class. Toward SQL Server the value is written as read (PostgreSQL values are already UTC,
/// SQL Server values keep their offset).</para>
/// <para><b>Text.</b> PostgreSQL <c>TEXT</c> cannot hold U+0000, and its UTF-8 encoding cannot hold a lone
/// UTF-16 surrogate (SQL Server <c>NVARCHAR</c> stores both). <c>VARCHAR(n)</c> counts code points on
/// PostgreSQL but UTF-16 code units on SQL Server, so a value with characters outside the BMP can fit the
/// PostgreSQL column and overflow the SQL Server one.</para>
/// <para>Cost: one vectorized scan per string for the two rare characters; the per-character walk only runs
/// on strings that contain surrogates.</para>
/// </remarks>
internal static class ValueNormalizer
{
    /// <summary>Returns <paramref name="value"/> truncated to a whole microsecond and converted to UTC.</summary>
    public static DateTimeOffset ToPostgresPrecision(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }

    /// <summary>
    /// Prepares <paramref name="value"/> of <paramref name="column"/> for <paramref name="target"/>.
    /// </summary>
    /// <param name="target">The backend the value is written to.</param>
    /// <param name="table">The table, for the error message.</param>
    /// <param name="column">The column.</param>
    /// <param name="value">The value as read (<see langword="null"/> for SQL NULL).</param>
    /// <param name="row">The whole row, to name its key in an error.</param>
    /// <returns>The value to write.</returns>
    /// <exception cref="FlowStorageCopyException">Thrown when the target cannot store the value.</exception>
    public static object? PrepareForTarget(FlowStorageProvider target, StorageTable table, StorageColumn column, object? value, object?[] row)
    {
        switch (value)
        {
            case null:
                return null;
            case DateTimeOffset dto when target == FlowStorageProvider.PostgreSql:
                return ToPostgresPrecision(dto);
            case string s:
                var problem = target == FlowStorageProvider.PostgreSql
                    ? FindPostgresTextProblem(s, column.MaxLength)
                    : FindSqlServerTextProblem(s, column.MaxLength);
                if (problem is not null)
                {
                    throw new FlowStorageCopyException(
                        table.SqlName,
                        $"{table.SqlName} row {DescribeKey(table, row)}, column {column.SqlName}: {problem}");
                }

                return s;
            default:
                return value;
        }
    }

    /// <summary>Returns why PostgreSQL cannot store <paramref name="value"/>, or <see langword="null"/> when it can.</summary>
    internal static string? FindPostgresTextProblem(string value, int? maxLength)
    {
        var span = value.AsSpan();
        if (span.Contains('\0'))
            return "the value contains U+0000 (NUL), which PostgreSQL text columns cannot store. Remove it on the source and run the copy again.";

        var firstSurrogate = span.IndexOfAnyInRange('\uD800', '\uDFFF');
        var codePoints = span.Length;
        if (firstSurrogate >= 0)
        {
            for (var i = firstSurrogate; i < span.Length; i++)
            {
                var c = span[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < span.Length && char.IsLowSurrogate(span[i + 1]))
                    {
                        i++;
                        codePoints--;
                        continue;
                    }

                    return string.Create(CultureInfo.InvariantCulture, $"the value has an unpaired UTF-16 surrogate at index {i}, which has no UTF-8 encoding. Fix it on the source and run the copy again.");
                }

                if (char.IsLowSurrogate(c))
                    return string.Create(CultureInfo.InvariantCulture, $"the value has an unpaired UTF-16 surrogate at index {i}, which has no UTF-8 encoding. Fix it on the source and run the copy again.");
            }
        }

        if (maxLength is { } max && codePoints > max)
            return string.Create(CultureInfo.InvariantCulture, $"the value has {codePoints} characters; the PostgreSQL column holds {max}.");
        return null;
    }

    /// <summary>Returns why SQL Server cannot store <paramref name="value"/>, or <see langword="null"/> when it can.</summary>
    internal static string? FindSqlServerTextProblem(string value, int? maxLength)
    {
        if (maxLength is { } max && value.Length > max)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"the value is {value.Length} UTF-16 code units long; the SQL Server column NVARCHAR({max}) holds {max} (characters outside the Basic Multilingual Plane count twice on SQL Server).");
        }

        return null;
    }

    /// <summary>Formats the key of <paramref name="row"/> as <c>(Col=value, ...)</c>.</summary>
    internal static string DescribeKey(StorageTable table, object?[] row)
    {
        var parts = new string[table.KeyOrdinals.Length];
        for (var k = 0; k < parts.Length; k++)
        {
            var ordinal = table.KeyOrdinals[k];
            parts[k] = table.Columns[ordinal].SqlName + "=" + Convert.ToString(row[ordinal], CultureInfo.InvariantCulture);
        }

        return "(" + string.Join(", ", parts) + ")";
    }
}
