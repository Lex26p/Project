using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Dispatcher.Platform;

internal static class PlatformDiagnostics
{
    public const string SourceName = "Dispatcher.Platform";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);
    public static readonly Counter<long> Admissions = Meter.CreateCounter<long>("dispatcher.platform.admissions");
    public static readonly Counter<long> AdmissionReplays = Meter.CreateCounter<long>("dispatcher.platform.admission_replays");
    public static readonly Counter<long> AdmissionConflicts = Meter.CreateCounter<long>("dispatcher.platform.admission_conflicts");
    public static readonly Counter<long> JobsEnqueued = Meter.CreateCounter<long>("dispatcher.platform.jobs_enqueued");
    public static readonly Counter<long> JobsClaimed = Meter.CreateCounter<long>("dispatcher.platform.jobs_claimed");
}
