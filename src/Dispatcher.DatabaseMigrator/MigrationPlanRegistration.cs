using Dispatcher.Persistence;

namespace Dispatcher.DatabaseMigrator;

public sealed class MigrationPlanRegistration
{
    private readonly Func<string, ModuleMigrationPlan> planFactory;

    public MigrationPlanRegistration(
        string owner,
        string schema,
        Func<string, ModuleMigrationPlan> planFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(planFactory);

        Owner = owner;
        Schema = schema;
        this.planFactory = planFactory;
    }

    public string Owner { get; }

    public string Schema { get; }

    public ModuleMigrationPlan CreatePlan(string databaseRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        return planFactory(databaseRole);
    }
}
