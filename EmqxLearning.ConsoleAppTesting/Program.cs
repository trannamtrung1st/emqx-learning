using System.Diagnostics;
using Dapper;
using EmqxLearning.Shared.Models;
using Npgsql;

ThreadPool.SetMinThreads(512, 512);
const int DeviceCount = 1000;
const int MetricCount = 5;
const int UpsertCount = 50;
using var dataSource = NpgsqlDataSource.Create("User ID=postgres;Password=zaQ@123456!;Host=localhost;Port=5432;Database=device;Pooling=true;Maximum Pool Size=20;");

Console.WriteLine($"\nTotal records: {DeviceCount * MetricCount}");

await WarmUp(dataSource);

await MeasureTime($"{nameof(NormalUpsert)} (1 loop)", async () =>
{
    var series = GenerateSeries();
    await NormalUpsert(dataSource, series, default);
});

await MeasureTime($"{nameof(NormalUpsert)} ({UpsertCount} loops)", async () =>
{
    for (int i = 0; i < UpsertCount; i++)
    {
        var series = GenerateSeries();
        await NormalUpsert(dataSource, series, default);
    }
});

await MeasureTime($"{nameof(TempTableUpsert)} (1 loop)", async () =>
{
    var series = GenerateSeries();
    await TempTableUpsert(dataSource, series);
});

await MeasureTime($"{nameof(TempTableUpsert)} ({UpsertCount} loops)", async () =>
{
    for (int i = 0; i < UpsertCount; i++)
    {
        var series = GenerateSeries();
        await TempTableUpsert(dataSource, series);
    }
});


await MeasureTime($"{nameof(NormalUpsert)} ({UpsertCount} parallel)", async () =>
{
    await ParallelLoop(UpsertCount, async () =>
    {
        var series = GenerateSeries();
        await NormalUpsert(dataSource, series, default);
    });
});

await MeasureTime($"{nameof(TempTableUpsert)} ({UpsertCount} parallel)", async () =>
{
    await ParallelLoop(UpsertCount, async () =>
    {
        var series = GenerateSeries();
        await TempTableUpsert(dataSource, series);
    });
});

// =========
IEnumerable<ReadIngestionMessage> GenerateSeries()
{
    var series = new List<ReadIngestionMessage>();
    for (int i = 0; i < DeviceCount; i++)
    {
        var dict = new Dictionary<string, object>();
        dict["deviceId"] = $"device-{i}";
        dict["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int m = 0; m < MetricCount; m++)
            dict[$"numeric_{i}_{m}"] = Random.Shared.NextDouble();
        series.Add(new() { RawData = dict });
    }
    return series;
}

async Task NormalUpsert(NpgsqlDataSource dataSource, IEnumerable<ReadIngestionMessage> messages, CancellationToken cancellationToken)
{
    await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
    var sql = @$"INSERT INTO device_metric_series(_ts, device_id, metric_key, value, retention_days)
        VALUES(@_ts, @device_id, @metric_key, @value, @retention_days)
        ON CONFLICT(device_id, metric_key)
        DO UPDATE SET value = @value, _ts = @_ts;";

    var records = messages.SelectMany(m =>
    {
        var data = m.RawData;
        var deviceId = data["deviceId"].ToString();
        data.Remove("timestamp");
        data.Remove("deviceId");
        var values = new List<object>();
        foreach (var kvp in data)
        {
            values.Add(new
            {
                _ts = DateTime.Now,
                device_id = deviceId,
                metric_key = kvp.Key,
                value = kvp.Value,
                retention_days = 90
            });
        }
        return values;
    });

    await connection.ExecuteAsync(sql, records);
}

const string SeriesTable = "device_metric_series";
const string SeriesColumns = "_ts, device_id, metric_key, value, retention_days";
async ValueTask TempTableUpsert(NpgsqlDataSource dataSource, IEnumerable<ReadIngestionMessage> messages)
{
    using var dbConnection = await dataSource.OpenConnectionAsync();
    Func<NpgsqlBinaryImporter, Task> writeToSTDIN = async (NpgsqlBinaryImporter writer) =>
    {
        foreach (var message in messages)
        {
            var data = message.RawData;
            var deviceId = data["deviceId"].ToString();
            data.Remove("timestamp");
            data.Remove("deviceId");
            var values = new List<object>();
            foreach (var kvp in message.RawData)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(DateTime.Now, NpgsqlTypes.NpgsqlDbType.Timestamp);
                await writer.WriteAsync(deviceId);
                await writer.WriteAsync(kvp.Key);
                await writer.WriteAsync(kvp.Value, NpgsqlTypes.NpgsqlDbType.Numeric);
                await writer.WriteAsync(90);
            }
        }
    };

    (var affected, var err) = await BulkUpsertAsync(dbConnection,
        targetTableName: SeriesTable,
        targetTableFields: $"({SeriesColumns})",
        onConflictFields: "(device_id, metric_key)",
        onConflictAction: $"UPDATE SET _ts = EXCLUDED._ts, value = EXCLUDED.value WHERE {SeriesTable}._ts < EXCLUDED._ts",
        writeToSTDIN
        );
}


async Task WarmUp(NpgsqlDataSource dataSource)
{
    await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
    await connection.ExecuteAsync("SELECT count(*) FROM device_metric_series");
}

async Task MeasureTime(string name, Func<Task> func)
{
    var sw = Stopwatch.StartNew();
    await func();
    sw.Stop();
    Console.WriteLine($"{name} took {sw.ElapsedMilliseconds} ms");
}

async Task<(int rowAffects, string errMessage)> BulkUpsertAsync(NpgsqlConnection dbConnection,
    string targetTableName,
    string targetTableFields,
    string onConflictFields,
    string onConflictAction,
    Func<NpgsqlBinaryImporter, Task> writeDataToSTDINAsync)
{
    var fields = targetTableFields.Replace("(", "").Replace(")", "");
    var temptableName = $"tmp_table_{Guid.NewGuid().ToString().Replace("-", "")}";
    var createTempTableSql = $"CREATE TEMP TABLE {temptableName} AS SELECT {fields} FROM {targetTableName} WITH NO DATA;";
    await using var cmd1 = new NpgsqlCommand(createTempTableSql, dbConnection);
    _ = await cmd1.ExecuteNonQueryAsync();
    var copyBinaryCmd = $"COPY {temptableName}{targetTableFields} FROM STDIN (FORMAT BINARY)";
    using (var writer = dbConnection.BeginBinaryImport(copyBinaryCmd))
    {
        await writeDataToSTDINAsync(writer);
        await writer.CompleteAsync();
    }
    var insertSql = $"INSERT INTO {targetTableName}{targetTableFields} SELECT {fields} FROM {temptableName} ON CONFLICT {onConflictFields} DO {onConflictAction};";
    await using var cmd2 = new NpgsqlCommand(insertSql, dbConnection);
    var rowAffects = await cmd2.ExecuteNonQueryAsync();
    return (rowAffects, string.Empty);
}

async Task ParallelLoop(int n, Func<Task> task)
{
    var waits = new List<TaskCompletionSource>();
    for (int i = 0; i < n; i++)
    {
        var tcs = new TaskCompletionSource();
        waits.Add(tcs);
        _ = Task.Run(function: async () =>
        {
            try
            {
                await task();
                tcs.SetResult();
            }
            catch (Exception ex) { tcs.SetException(ex); }
        });
    }
    await Task.WhenAll(waits.Select(w => w.Task));
}
