namespace Dispatcher.Web;

public sealed class EditorWorkflowState
{
    public EditorRevisionPayload? Revision { get; private set; }
    public bool IsUnsaved { get; private set; }
    public bool HasConflict { get; private set; }
    public bool IsValidated => Revision?.ValidatedAt is not null && !IsUnsaved && !HasConflict;

    public void Load(EditorRevisionPayload? revision)
    {
        Revision = revision;
        IsUnsaved = false;
        HasConflict = false;
    }

    public void MarkChanged()
    {
        IsUnsaved = true;
        HasConflict = false;
    }

    public void Saved(EditorRevisionPayload revision)
    {
        Revision = revision;
        IsUnsaved = false;
        HasConflict = false;
    }

    public void Validated(EditorRevisionPayload revision) => Saved(revision);

    public void Published(EditorRevisionPayload revision) => Saved(revision);

    public void MarkConflict() => HasConflict = true;
}
