using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Text.Json;

string dbPath = Path.Combine("listenarr.api", "config", "database", "listenarr.db");
// If first arg is a path, use it; otherwise default above. Support invocation: 'dotnet run --project tools/dbscan -- audiobook-raw 8 10'
if (args.Length > 0 && File.Exists(args[0])) dbPath = args[0];

// Parse optional command mode
var mode = args.Length > 0 && !File.Exists(args[0]) ? args[0] : null;
var modeArgs = args.Skip(mode != null ? 1 : 0).ToArray();

if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"Database not found: {dbPath}");
    return 2;
}

var checks = new List<(string table, string key, string[] cols)>
{
    ("QualityProfiles", "Id", new[] { "Qualities", "PreferredFormats", "PreferredLanguages", "MustContain", "MustNotContain", "CustomGroupNames" }),
    ("Downloads", "Id", new[] { "Metadata" }),
    ("DownloadProcessingJobs", "Id", new[] { "JobData" }),
    ("ApiConfigurations", "Id", new[] { "HeadersJson", "ParametersJson" }),
    ("Audiobooks", "Id", new[] { "Authors", "Genres", "Tags", "Narrators", "AuthorAsins" })
};

var problems = new Dictionary<string, List<object>>();

// Ensure native SQLite provider is initialized for Microsoft.Data.Sqlite
Batteries_V2.Init();
using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

foreach (var (table, key, cols) in checks)
{
    var list = new List<object>();
    foreach (var col in cols)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {key}, {col} FROM {table}";
        try
        {
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var id = rdr[key];
                var raw = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                var sample = raw ?? string.Empty;
                // Quick heuristic
                bool looksLikeJson = true;
                if (string.IsNullOrWhiteSpace(raw)) looksLikeJson = true;
                else
                {
                    var t = raw.TrimStart();
                    if (t.Length == 0) looksLikeJson = true;
                    else
                    {
                        var f = t[0];
                        if (!(f == '{' || f == '[' || f == '"' || f == 't' || f == 'f' || f == 'n' || f == '-' || char.IsDigit(f))) looksLikeJson = false;
                    }
                }

                        if (!looksLikeJson)
                        {
                            list.Add(new { Table = table + "." + col, Id = id, Issue = "NotJson", Sample = sample.Length > 200 ? sample.Substring(0,200) : sample });
                            continue;
                        }

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        var root = doc.RootElement;
                        // Heuristics
                        if (table == "QualityProfiles" && col == "Qualities")
                        {
                                if (root.ValueKind != JsonValueKind.Array)
                                {
                                    list.Add(new { Table = table + "." + col, Id = id, Issue = "ExpectedArray", Sample = sample.Length > 200 ? sample.Substring(0,200) : sample });
                                }
                            else
                            {
                                var first = root.EnumerateArray().FirstOrDefault();
                                    if (!first.Equals(default(JsonElement)) && first.ValueKind != JsonValueKind.Object)
                                    {
                                        list.Add(new { Table = table + "." + col, Id = id, Issue = "ArrayNotObjects", Sample = sample.Length > 200 ? sample.Substring(0,200) : sample });
                                    }
                            }
                        }
                        else if (table == "Downloads" && col == "Metadata")
                        {
                                if (root.ValueKind != JsonValueKind.Object)
                                {
                                    list.Add(new { Table = table + "." + col, Id = id, Issue = "ExpectedObject", Sample = sample.Length > 200 ? sample.Substring(0,200) : sample });
                                }
                        }
                    }
                        catch (JsonException je)
                        {
                            list.Add(new { Table = table + "." + col, Id = id, Issue = "ParseError", Sample = sample.Length > 200 ? sample.Substring(0,200) : sample, Error = je.Message });
                        }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            list.Add(new { Table = table + "." + col, Id = "<query-failed>", Issue = "QueryError", Sample = ex.Message });
        }
    }
    problems[table] = list;
}
var options = new JsonSerializerOptions { WriteIndented = true };

// If mode is 'audiobook-raw' and ids provided, dump raw columns for those ids and exit
if (string.Equals(mode, "audiobook-raw", StringComparison.OrdinalIgnoreCase) && modeArgs.Length > 0)
{
    using var conn2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
    conn2.Open();
    var idList = modeArgs.SelectMany(a => a.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)).Select(s => { int.TryParse(s, out var v); return v; }).Where(i => i != 0).ToList();
        var outList = new Dictionary<int, Dictionary<string, object?>?>();
    foreach (var id in idList)
    {
        var cmd = conn2.CreateCommand();
        cmd.CommandText = "SELECT * FROM Audiobooks WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var rdr = cmd.ExecuteReader();
        if (rdr.Read())
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < rdr.FieldCount; i++) dict[rdr.GetName(i)] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
            outList[id] = dict;
        }
        else outList[id] = null;
    }

    Console.WriteLine(JsonSerializer.Serialize(outList, options));
    return 0;
}

Console.WriteLine(JsonSerializer.Serialize(problems, options));
return 0;
