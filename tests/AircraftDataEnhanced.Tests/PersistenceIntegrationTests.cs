// SPDX-License-Identifier: MIT
namespace SDRSharp.AircraftDataEnhanced;

internal static class PersistenceIntegrationTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "AircraftDataEnhanced.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "history.sqlite3");
            var jsonlPath = Path.Combine(root, "messages.jsonl");
            using var database = new LocalHistoryDatabase(256, dbPath);
            using var exporter = new JsonlExporter(256);
            exporter.Enable(jsonlPath);

            WaitUntil(() => database.Ready, "SQLite did not become ready.");
            for (var index = 0; index < 128; index++)
            {
                var message = CreateMessage(index);
                if (!database.TryEnqueue(message))
                    throw new InvalidOperationException("SQLite rejected a verified message.");
                if (!exporter.TryWrite(message))
                    throw new InvalidOperationException("JSONL rejected a record.");
            }

            WaitUntil(() => database.StatusSnapshot().PendingWrites == 0, "SQLite did not drain.");
            WaitUntil(() => exporter.StatusSnapshot().PendingWrites == 0, "JSONL did not drain.");
            var rows = database.QueryMessagesAsync(new LocalHistoryQuery(string.Empty, null, null, 500)).GetAwaiter().GetResult();
            if (rows.Count != 128) throw new InvalidOperationException($"Expected 128 SQLite rows, got {rows.Count}.");
            exporter.Disable();
            if (File.ReadLines(jsonlPath).Count() != 128) throw new InvalidOperationException("JSONL record count changed.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    internal static Vdl2Message CreateMessage(long index) => new(
        DateTimeOffset.UtcNow.AddMilliseconds(index), "AVLC", "Air → Ground", "ABCDEF",
        "N12345", $"TST{index % 1000:000}", "ABCDEF", "GROUND", "H1",
        $"P2 SYNTHETIC MESSAGE {index}", 136.975, -42.0, true,
        $"{{\"sequence\":{index}}}", AcarsMessageNumber: $"{index % 1000:000}",
        AcarsMessageSequence: ((char)('A' + index % 26)).ToString(), AcarsCrcValid: true);

    internal static void WaitUntil(Func<bool> condition, string message, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(20);
        }
        throw new InvalidOperationException(message);
    }
}
