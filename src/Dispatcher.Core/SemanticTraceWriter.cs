using System.Globalization;
using System.Text;

namespace Dispatcher.Core;

public static class SemanticTraceWriter
{
    public static string Write(IEnumerable<CurrentEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var trace = new StringBuilder();
        foreach (var entry in entries.OrderBy(item => item.CurrentPosition.Value))
        {
            trace.Append("current=").Append(entry.CurrentPosition.Value.ToString(CultureInfo.InvariantCulture));
            trace.Append(";sourcePosition=").Append(entry.SourcePosition.Value.ToString(CultureInfo.InvariantCulture));
            trace.Append(";scope=").Append(entry.ScopeId.Value.ToString("D"));
            trace.Append(";source=").Append(entry.SourceId.Value.ToString("D"));
            trace.Append(";point=").Append(entry.PointId.Value.ToString("D"));
            trace.Append(";binding=").Append(entry.BindingGeneration.Value.ToString(CultureInfo.InvariantCulture));
            trace.Append(";session=").Append(entry.SessionGeneration.Value.ToString(CultureInfo.InvariantCulture));
            trace.Append(";value=").Append(entry.Value.Value.ToString(CultureInfo.InvariantCulture));
            trace.Append(";unit=").Append(entry.Unit.Symbol);
            trace.Append(";quality=").Append(entry.Quality);
            trace.Append(";freshness=").Append(entry.Freshness);
            trace.Append(";sourceTime=").Append(entry.SourceTimestamp.Value.ToString("O", CultureInfo.InvariantCulture));
            trace.Append(";receiveTime=").Append(entry.ReceiveTimestamp.Value.ToString("O", CultureInfo.InvariantCulture));
            trace.Append(";processedTime=").Append(entry.ProcessedTimestamp.Value.ToString("O", CultureInfo.InvariantCulture));
            trace.Append(";monotonic=").Append(entry.ProcessedMonotonicTimestamp.Value.ToString(CultureInfo.InvariantCulture));
            trace.Append('\n');
        }

        return trace.ToString();
    }
}
