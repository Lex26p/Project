using System.Collections;

namespace Dispatcher.DatabaseMigrator;

public static class MigrationEnvironmentReader
{
    public static Dictionary<string, string?> ReadCurrentProcess()
    {
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name)
            {
                variables[name] = entry.Value as string;
            }
        }

        return variables;
    }
}
