using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Parameters;
using RollGrinder.Core.Profiles;

namespace RollGrinder.Data;

/// <summary>
/// 多段辊形的存取。辊形库（roll_profile_*）与作业快照（job_profile_*）两处表结构一样，
/// 只有属主列名不同，所以读写只写一份，表名当参数传。
/// </summary>
internal sealed record ProfileSegmentTables(string SegmentTable, string ParameterTable, string OwnerColumn)
{
    /// <summary>辊形库里那条可复用的辊形。</summary>
    public static ProfileSegmentTables Library { get; } =
        new("roll_profile_segment", "roll_profile_segment_parameter", "profile_id");

    /// <summary>作业调出辊形时复制的那份快照。</summary>
    public static ProfileSegmentTables JobSnapshot { get; } =
        new("job_profile_segment", "job_profile_segment_parameter", "job_id");
}

internal static class ProfileSegmentMapping
{
    /// <summary>整体替换某个属主名下的全部段。先删后写，免得改少了段还留着旧的。</summary>
    public static async Task WriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfileSegmentTables tables,
        string ownerId,
        CompositeRollProfile profile,
        CancellationToken cancellationToken)
    {
        await DeleteAsync(connection, transaction, tables, ownerId, cancellationToken).ConfigureAwait(false);

        foreach (RollProfileSegment segment in profile.Segments)
        {
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    $"""
                     INSERT INTO {tables.SegmentTable}
                         ({tables.OwnerColumn}, segment_order, profile_type_key, from_mm, to_mm, is_mirrored)
                     VALUES ($owner, $order, $type, $from, $to, $mirrored);
                     """;
                SqlMapping.AddParameter(command, "$owner", ownerId);
                SqlMapping.AddParameter(command, "$order", segment.Order);
                SqlMapping.AddParameter(command, "$type", segment.ProfileTypeKey);
                SqlMapping.AddParameter(command, "$from", segment.FromMm);
                SqlMapping.AddParameter(command, "$to", segment.ToMm);
                SqlMapping.AddParameter(command, "$mirrored", segment.IsMirrored ? 1 : 0);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (KeyValuePair<string, ParameterValue> pair in segment.Parameters.ToOrderedPairs())
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"""
                     INSERT INTO {tables.ParameterTable}
                         ({tables.OwnerColumn}, segment_order, parameter_key, value_kind, value_text)
                     VALUES ($owner, $order, $key, $kind, $value);
                     """;
                SqlMapping.AddParameter(command, "$owner", ownerId);
                SqlMapping.AddParameter(command, "$order", segment.Order);
                SqlMapping.AddParameter(command, "$key", pair.Key);
                SqlMapping.AddParameter(command, "$kind", (int)pair.Value.Kind);
                SqlMapping.AddParameter(command, "$value", pair.Value.ToInvariantString());
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 读回某个属主名下的多段辊形。一段都没有时返回 null——
    /// 对旧库里的作业，调用方据此回落到单曲线的老表示。
    /// </summary>
    public static async Task<CompositeRollProfile?> ReadAsync(
        SqliteConnection connection,
        ProfileSegmentTables tables,
        string ownerId,
        CancellationToken cancellationToken)
    {
        Dictionary<int, List<KeyValuePair<string, ParameterValue>>> parameters =
            await ReadParametersAsync(connection, tables, ownerId, cancellationToken).ConfigureAwait(false);

        var segments = new List<RollProfileSegment>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                 SELECT segment_order, profile_type_key, from_mm, to_mm, is_mirrored
                 FROM {tables.SegmentTable}
                 WHERE {tables.OwnerColumn} = $owner
                 ORDER BY segment_order;
                 """;
            SqlMapping.AddParameter(command, "$owner", ownerId);

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int order = reader.GetInt32(0);
                segments.Add(new RollProfileSegment(
                    order,
                    reader.GetString(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    parameters.TryGetValue(order, out List<KeyValuePair<string, ParameterValue>>? pairs)
                        ? new ParameterSet(pairs)
                        : ParameterSet.Empty,
                    reader.GetInt64(4) != 0));
            }
        }

        return segments.Count == 0 ? null : new CompositeRollProfile(segments);
    }

    /// <summary>删掉某个属主名下的全部段。外键是级联的，这里显式删是为了不依赖 PRAGMA 的开关状态。</summary>
    public static async Task DeleteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProfileSegmentTables tables,
        string ownerId,
        CancellationToken cancellationToken)
    {
        foreach (string table in new[] { tables.ParameterTable, tables.SegmentTable })
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE {tables.OwnerColumn} = $owner;";
            SqlMapping.AddParameter(command, "$owner", ownerId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<int, List<KeyValuePair<string, ParameterValue>>>> ReadParametersAsync(
        SqliteConnection connection,
        ProfileSegmentTables tables,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var bySegment = new Dictionary<int, List<KeyValuePair<string, ParameterValue>>>();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT segment_order, parameter_key, value_kind, value_text
             FROM {tables.ParameterTable}
             WHERE {tables.OwnerColumn} = $owner
             ORDER BY segment_order, parameter_key;
             """;
        SqlMapping.AddParameter(command, "$owner", ownerId);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int order = reader.GetInt32(0);
            if (!bySegment.TryGetValue(order, out List<KeyValuePair<string, ParameterValue>>? list))
            {
                list = new List<KeyValuePair<string, ParameterValue>>();
                bySegment[order] = list;
            }

            list.Add(new KeyValuePair<string, ParameterValue>(
                reader.GetString(1),
                ParameterValue.Parse((ParameterValueKind)reader.GetInt32(2), reader.GetString(3))));
        }

        return bySegment;
    }
}
