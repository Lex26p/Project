using Dispatcher.Semantics;

namespace Dispatcher.Core;

public readonly record struct RuntimeScopeId
{
    private readonly CanonicalId<RuntimeScopeId> value;

    private RuntimeScopeId(CanonicalId<RuntimeScopeId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static RuntimeScopeId From(Guid value) => new(CanonicalId.From<RuntimeScopeId>(value));
}

public readonly record struct SourceId
{
    private readonly CanonicalId<SourceId> value;

    private SourceId(CanonicalId<SourceId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static SourceId From(Guid value) => new(CanonicalId.From<SourceId>(value));
}

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
