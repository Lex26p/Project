namespace Dispatcher.Semantics;

public readonly record struct PointId
{
    private readonly CanonicalId<PointId> value;

    private PointId(CanonicalId<PointId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static PointId From(Guid value) => new(CanonicalId.From<PointId>(value));
}
