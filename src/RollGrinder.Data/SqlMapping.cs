using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RollGrinder.Core.Parameters;

namespace RollGrinder.Data;

/// <summary>
/// 轻量手写映射的公共部分：时间与参数的存取格式。
/// 时间一律以 ISO 8601 往返格式存 UTC 文本，避免 SQLite 的时区歧义。
/// </summary>
internal static class SqlMapping
{
    /// <summary>辊形参数在 job_parameter 里的 step_order 取值。</summary>
    public const int ProfileParameterStepOrder = 0;

    /// <summary>
    /// 程序步骤开关在 job_parameter 里的 step_order 取值。
    /// 工序从 1 起、辊形占 0，所以 -1 是空着的——用它省掉一次建表迁移。
    /// </summary>
    public const int ProgramOptionStepOrder = -1;

    public static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ToTimestamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ToTimestampOrNull(object? value) =>
        value is string text ? ToTimestamp(text) : null;

    public static void AddParameter(SqliteCommand command, string name, object? value)
    {
        SqliteParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    public static async Task WriteParametersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        int stepOrder,
        ParameterSet parameters,
        CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string, ParameterValue> pair in parameters.ToOrderedPairs())
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO job_parameter (job_id, step_order, parameter_key, value_kind, value_text)
                VALUES ($job, $step, $key, $kind, $value);
                """;
            AddParameter(command, "$job", jobId);
            AddParameter(command, "$step", stepOrder);
            AddParameter(command, "$key", pair.Key);
            AddParameter(command, "$kind", (int)pair.Value.Kind);
            AddParameter(command, "$value", pair.Value.ToInvariantString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<Dictionary<int, ParameterSet>> ReadParametersAsync(
        SqliteConnection connection,
        string jobId,
        CancellationToken cancellationToken)
    {
        var byStep = new Dictionary<int, List<KeyValuePair<string, ParameterValue>>>();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT step_order, parameter_key, value_kind, value_text
            FROM job_parameter
            WHERE job_id = $job
            ORDER BY step_order, parameter_key;
            """;
        AddParameter(command, "$job", jobId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int stepOrder = reader.GetInt32(0);
            string key = reader.GetString(1);
            var kind = (ParameterValueKind)reader.GetInt32(2);
            ParameterValue value = ParameterValue.Parse(kind, reader.GetString(3));

            if (!byStep.TryGetValue(stepOrder, out List<KeyValuePair<string, ParameterValue>>? list))
            {
                list = new List<KeyValuePair<string, ParameterValue>>();
                byStep[stepOrder] = list;
            }

            list.Add(new KeyValuePair<string, ParameterValue>(key, value));
        }

        var result = new Dictionary<int, ParameterSet>(byStep.Count);
        foreach (KeyValuePair<int, List<KeyValuePair<string, ParameterValue>>> pair in byStep)
        {
            result[pair.Key] = new ParameterSet(pair.Value);
        }

        return result;
    }
}
