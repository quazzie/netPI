using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

// --------------------------------------------------------------------------------
// PENDING: the Ideas collections are not in the tree yet — the coordinator sends the mapping (the Ideas plugin's new
// store, in the shape of its own comment block). When it arrives, MigrateIdeas.Run fills in exactly what this stub
// stands for: read every row of the old ideas_* tables from the attached old database, convert each to the document
// its new collection defines, and Put them into the netpi.ideas collections. The collection shapes then get the same
// comment block the other migrations carry (name, key, document fields, index fields).
//
// Until it does, Pending stays true: the old ideas_* tables are counted in the report as not migrated, and --apply
// refuses to run — applying would move a new database in that carries no ideas, and the old one (renamed aside) is
// the only place the backlog would live.
// --------------------------------------------------------------------------------
internal static class IdeasMigration
{
    public const string PluginId = "netpi.ideas";

    /// <summary>
    /// False once the mapping is in and Run migrates for real. A property, not a constant: const-folding would make the
    /// stub's body (and Apply's refusal) look unreachable.
    /// </summary>
    public static bool Pending => true;

    public static void Run(Database old, IStorage storage, List<string> notes)
    {
        if (!Pending) return;
        notes.Add("netpi.ideas: the collection mapping is pending (IdeasMigration stub) — the old ideas_* tables are NOT migrated");
    }
}
