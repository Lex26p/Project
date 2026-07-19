using Dispatcher.Persistence;

namespace Dispatcher.Facilities;

public static class FacilityMigrations
{
    public const string Owner = "facility_model";
    public const string Schema = "facility_model";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "locations and explicit relations",
                $"""
                CREATE TABLE {Schema}.location (
                    location_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    physical_parent_id uuid NULL REFERENCES {Schema}.location(location_id),
                    code text NOT NULL CHECK (length(trim(code)) > 0),
                    name text NOT NULL CHECK (length(trim(name)) > 0),
                    version bigint NOT NULL CHECK (version > 0),
                    CONSTRAINT location_scope_code_key UNIQUE (scope_id, code),
                    CONSTRAINT location_not_own_parent CHECK (physical_parent_id IS NULL OR physical_parent_id <> location_id)
                );
                CREATE INDEX location_parent_idx ON {Schema}.location (physical_parent_id);
                CREATE TABLE {Schema}.functional_relation (
                    source_location_id uuid NOT NULL REFERENCES {Schema}.location(location_id),
                    target_location_id uuid NOT NULL REFERENCES {Schema}.location(location_id),
                    PRIMARY KEY (source_location_id, target_location_id),
                    CONSTRAINT functional_relation_not_self CHECK (source_location_id <> target_location_id)
                );
                CREATE TABLE {Schema}.mutation_audit (
                    audit_id uuid PRIMARY KEY,
                    location_id uuid NOT NULL,
                    scope_id uuid NOT NULL,
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    permission text NOT NULL,
                    action text NOT NULL,
                    resulting_version bigint NOT NULL CHECK (resulting_version > 0),
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX facility_audit_scope_idx ON {Schema}.mutation_audit (scope_id, changed_at);
                """),
        ]);
}
