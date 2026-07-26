using System.Collections.ObjectModel;

using Dispatcher.Administration;
using Dispatcher.Alarm;
using Dispatcher.Command;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Dashboards;
using Dispatcher.Equipment;
using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.History;
using Dispatcher.Identity;
using Dispatcher.Incidents;
using Dispatcher.Maintenance;
using Dispatcher.MyWork;
using Dispatcher.Notifications;
using Dispatcher.Platform;
using Dispatcher.Simulator;
using Dispatcher.Terminals;
using Dispatcher.Workspace;

namespace Dispatcher.DatabaseMigrator;

public static class MigrationCatalog
{
    public const int ExpectedPlanCount = 19;

    private const string ValidationRole = "migration_catalog_validation_role";

    private static readonly ReadOnlyCollection<MigrationPlanRegistration> OrderedRegistrations =
        CreateRegistrations();

    public static IReadOnlyList<MigrationPlanRegistration> Registrations => OrderedRegistrations;

    private static ReadOnlyCollection<MigrationPlanRegistration> CreateRegistrations()
    {
        MigrationPlanRegistration[] registrations =
        [
            new(AdministrationMigrations.Owner, AdministrationMigrations.Schema, AdministrationMigrations.CreatePlan),
            new(AlarmMigrations.Owner, AlarmMigrations.Schema, AlarmMigrations.CreatePlan),
            new(CommandMigrations.Owner, CommandMigrations.Schema, CommandMigrations.CreatePlan),
            new(ConfigurationMigrations.Owner, ConfigurationMigrations.Schema, ConfigurationMigrations.CreatePlan),
            new(CoreRuntimeMigrations.Owner, CoreRuntimeMigrations.Schema, CoreRuntimeMigrations.CreatePlan),
            new(DashboardMigrations.Owner, DashboardMigrations.Schema, DashboardMigrations.CreatePlan),
            new(EquipmentMigrations.Owner, EquipmentMigrations.Schema, EquipmentMigrations.CreatePlan),
            new(EventMigrations.Owner, EventMigrations.Schema, EventMigrations.CreatePlan),
            new(FacilityMigrations.Owner, FacilityMigrations.Schema, FacilityMigrations.CreatePlan),
            new(HistoryMigrations.Owner, HistoryMigrations.Schema, HistoryMigrations.CreatePlan),
            new(IdentityMigrations.Owner, IdentityMigrations.Schema, IdentityMigrations.CreatePlan),
            new(IncidentMigrations.Owner, IncidentMigrations.Schema, IncidentMigrations.CreatePlan),
            new(MaintenanceMigrations.Owner, MaintenanceMigrations.Schema, MaintenanceMigrations.CreatePlan),
            new(MyWorkMigrations.Owner, MyWorkMigrations.Schema, MyWorkMigrations.CreatePlan),
            new(NotificationMigrations.Owner, NotificationMigrations.Schema, NotificationMigrations.CreatePlan),
            new(PlatformMigrations.Owner, PlatformMigrations.Schema, PlatformMigrations.CreatePlan),
            new(SimulatorRuntimeMigrations.Owner, SimulatorRuntimeMigrations.Schema, SimulatorRuntimeMigrations.CreatePlan),
            new(TerminalMigrations.Owner, TerminalMigrations.Schema, TerminalMigrations.CreatePlan),
            new(WorkspaceMigrations.Owner, WorkspaceMigrations.Schema, WorkspaceMigrations.CreatePlan),
        ];

        Validate(registrations);
        return Array.AsReadOnly(registrations);
    }

    private static void Validate(MigrationPlanRegistration[] registrations)
    {
        if (registrations.Length != ExpectedPlanCount)
        {
            throw new InvalidOperationException(
                $"The production migration catalog must contain exactly {ExpectedPlanCount} plans.");
        }

        ValidateUnique(registrations.Select(static registration => registration.Owner), "owner");
        ValidateUnique(registrations.Select(static registration => registration.Schema), "schema");

        foreach (MigrationPlanRegistration registration in registrations)
        {
            var plan = registration.CreatePlan(ValidationRole);
            if (!string.Equals(plan.Owner, registration.Owner, StringComparison.Ordinal) ||
                !string.Equals(plan.Schema, registration.Schema, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migration registration '{registration.Owner}' does not match the plan produced by its factory.");
            }
        }
    }

    private static void ValidateUnique(IEnumerable<string> values, string valueKind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (!seen.Add(value))
            {
                throw new InvalidOperationException(
                    $"The production migration catalog contains duplicate {valueKind} '{value}'.");
            }
        }
    }
}
