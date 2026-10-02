using Microsoft.Extensions.Logging.Abstractions;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

/// <summary>
/// The one-off migration of an old-format NetPI home to the storage port's shape (docs/plans/2026-10-02-replaceable-parts.md,
/// "Cutover and rollback"). It never modifies the old database: the new one is built in <c>&lt;home&gt;/.migrating/netpi.db</c>,
/// verified against the old (row counts, the messages hash, the bindings, the ledger's money), and moved into place only by
/// <c>--apply</c>, which renames the old one to <c>netpi.db.pre-storage-port*</c> first and says how to roll back.
/// <para>
/// Usage: <c>dotnet run --project scripts/migrations/001-storage-port -- --home &lt;dir&gt;</c> (dry run)
/// <br/>          <c>… -- --home &lt;dir&gt; --apply</c>
/// <br/>          <c>… -- --selftest</c>
/// Exit codes: 0 verified (and applied when asked), 1 verification failed, 2 a precondition.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (MigrationAbortedException ex)
        {
            Console.Error.WriteLine("aborted: " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("migration failed: " + ex.Message);
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        string? home = null;
        var apply = false;
        var selftest = false;
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--home": home = args.Length > ++i ? args[i] : null; break;
                case "--apply": apply = true; break;
                case "--selftest": selftest = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default: PrintUsage(); return 2;
            }
        }
        if (help) { PrintUsage(); return 0; }
        if (selftest) return SelfTest.Run();
        if (home is null) { PrintUsage(); return 2; }
        return Run(home, apply);
    }

    /// <summary>The migration of one home: dry run by default, verified before anything is said to be done.</summary>
    public static int Run(string home, bool apply)
    {
        home = Path.GetFullPath(home);
        if (!Directory.Exists(home)) { Console.Error.WriteLine($"the home {home} does not exist"); return 2; }
        var oldDb = Path.Combine(home, "netpi.db");
        if (!File.Exists(oldDb)) { Console.Error.WriteLine($"no database at {oldDb}"); return 2; }
        var wal = oldDb + "-wal";
        if (File.Exists(wal) && new FileInfo(wal).Length > 0)
        {
            // A WAL with content is either still open by a running NetPI or the residue of a crash: closing our ATTACH
            // would checkpoint it into the old file. Start NetPI once so it closes the database cleanly, then re-run.
            Console.Error.WriteLine($"the old database's -wal has content ({oldDb}-wal): a NetPI is running on this home " +
                                    "or it did not close cleanly. Start NetPI once and let it exit, then re-run.");
            return 2;
        }
        var migrating = Path.Combine(home, ".migrating");
        var newDb = Path.Combine(migrating, "netpi.db");

        Console.WriteLine("storage migration 001 (the old netpi.db -> the storage port's shape)");
        Console.WriteLine($"  old: {oldDb} (read only)");
        Console.WriteLine($"  new: {newDb}{(apply ? "  (--apply: verified, then moved into place)" : "  (dry run: left for inspection)")}");

        var notes = new List<string>();
        CleanScratch(migrating);
        var options = new StorageOpenOptions { Home = migrating, Logger = NullLogger.Instance, Settings = MigrationSettings.For(home) };

        // 1. The new schema, created by the provider itself (and refused when the scratch already holds a foreign one).
        using (new SqliteStorageProvider().Open(options)) { }

        // 2+3. One connection to the new database carries the whole migration: the old one is ATTACHed to it as "old"
        // (so the core copy and the plugin reads alike query "old.…"), and the plugin collections are written through
        // the provider's own port on the same file. The ATTACH is read-only in effect: nothing ever writes through old.
        using (var db = new Database(newDb, null))
        using (var storage = new SqliteStorageProvider().Open(options))
        {
            db.Execute("ATTACH DATABASE '" + oldDb.Replace("'", "''") + "' AS old");
            try
            {
                CoreCopy.CheckOldShape(db);
                CoreCopy.Run(db, notes);
                WorkspacesMigration.Run(db, storage, notes);
                ContextMigration.Run(db, storage, notes);
                AgentsMigration.Run(db, storage, notes);
                RuntimeMigration.Run(db, storage, notes);
                IdeasMigration.Run(db, storage, notes);
            }
            finally { db.Execute("DETACH DATABASE old"); }
        }

        // 4. Verify, and print the report.
        var result = Verify.Run(oldDb, newDb, home, notes);
        PrintReport(result);
        if (!result.Passed)
        {
            Console.WriteLine($"verification FAILED: {result.Problems.Count} problem{(result.Problems.Count == 1 ? "" : "s")} — nothing applied.");
            Console.WriteLine($"the new database is left at {newDb} for inspection.");
            return 1;
        }
        Console.WriteLine("verification passed.");
        if (!apply)
        {
            Console.WriteLine($"dry run: {newDb} is verified and left in place. Re-run with --apply to install it " +
                               "(the old database is renamed to netpi.db.pre-storage-port* first).");
            return 0;
        }
        return Apply.Run(home, newDb);
    }

    /// <summary>The scratch files of a previous run of this tool (and only those): the .migrating folder is the tool's own.</summary>
    private static void CleanScratch(string migrating)
    {
        Directory.CreateDirectory(migrating);
        foreach (var name in new[] { "netpi.db", "netpi.db-wal", "netpi.db-shm" })
        {
            var path = Path.Combine(migrating, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void PrintReport(Verify.Result r)
    {
        Console.WriteLine();
        Console.WriteLine("  " + Align("store", 46) + Align("old", 10) + Align("new", 10) + "state");
        foreach (var c in r.Checks)
        {
            var state = c.Ok
                ? (c.Note.StartsWith("PENDING", StringComparison.Ordinal) ? c.Note : "ok")
                : "MISMATCH" + (c.Note.Length > 0 ? " (" + c.Note + ")" : "");
            Console.WriteLine("  " + Align(c.Name, 46) + Align(c.Old, 10) + Align(c.New, 10) + state);
        }
        foreach (var n in r.Notes) Console.WriteLine("  note: " + n);
        if (r.Problems.Count > 0)
        {
            Console.WriteLine("  problems:");
            foreach (var p in r.Problems) Console.WriteLine("    - " + p);
        }
        Console.WriteLine();
    }

    private static string Align(string s, int width) => s.Length <= width ? s.PadRight(width) : s[..(width - 1)] + "…";

    private static void PrintUsage()
    {
        Console.WriteLine(
            "The one-off migration of an old-format NetPI home to the storage port's shape. It never modifies the old database; " +
            "it builds the new one in <home>/.migrating, verifies it against the old, and (with --apply) moves it into place.\n" +
            "\n" +
            "  dotnet run --project scripts/migrations/001-storage-port -- --home <dir>            dry run (default)\n" +
            "  dotnet run --project scripts/migrations/001-storage-port -- --home <dir> --apply    apply, after a passing dry run\n" +
            "  dotnet run --project scripts/migrations/001-storage-port -- --selftest              build an old-format database in a temp dir and migrate it\n" +
            "\n" +
            "The old database must be closed (a dry run only reads it; --apply takes the home lock and refuses while a NetPI runs on it). " +
            "Rollback after an apply: see the printed steps. Exit codes: 0 verified, 1 verification failed, 2 a precondition.");
    }
}
