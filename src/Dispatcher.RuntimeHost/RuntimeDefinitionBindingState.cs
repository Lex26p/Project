using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public sealed record RuntimeDefinitionBinding(
    Guid ConfigurationRevisionId,
    RevisionNumber AlarmDefinitionEpoch);

public sealed class RuntimeDefinitionBindingState
{
    private readonly object sync = new();
    private RuntimeDefinitionBinding current;

    public RuntimeDefinitionBindingState(
        Guid configurationRevisionId,
        RevisionNumber alarmDefinitionEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(configurationRevisionId, Guid.Empty);
        if (!alarmDefinitionEpoch.IsDefined)
        {
            throw new ArgumentOutOfRangeException(nameof(alarmDefinitionEpoch));
        }

        current = new RuntimeDefinitionBinding(configurationRevisionId, alarmDefinitionEpoch);
    }

    public RuntimeDefinitionBinding Read()
    {
        lock (sync)
        {
            return current;
        }
    }

    public void Switch(Guid configurationRevisionId, RevisionNumber alarmDefinitionEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(configurationRevisionId, Guid.Empty);
        if (!alarmDefinitionEpoch.IsDefined)
        {
            throw new ArgumentOutOfRangeException(nameof(alarmDefinitionEpoch));
        }

        lock (sync)
        {
            current = new RuntimeDefinitionBinding(configurationRevisionId, alarmDefinitionEpoch);
        }
    }
}
