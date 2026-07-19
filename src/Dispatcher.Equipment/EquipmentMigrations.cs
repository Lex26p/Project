using Dispatcher.Persistence;

namespace Dispatcher.Equipment;

public static class EquipmentMigrations
{
    public const string Owner = "equipment_registry";
    public const string Schema = "equipment_registry";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "equipment and owned point definitions",
                $"""
                CREATE TABLE {Schema}.equipment (
                    equipment_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    location_id uuid NOT NULL,
                    code text NOT NULL CHECK (length(trim(code)) > 0),
                    name text NOT NULL CHECK (length(trim(name)) > 0),
                    version bigint NOT NULL CHECK (version > 0),
                    CONSTRAINT equipment_scope_code_key UNIQUE (scope_id, code)
                );
                CREATE INDEX equipment_location_idx ON {Schema}.equipment (scope_id, location_id);
                CREATE TABLE {Schema}.point_definition (
                    point_id uuid PRIMARY KEY,
                    equipment_id uuid NOT NULL REFERENCES {Schema}.equipment(equipment_id),
                    code text NOT NULL CHECK (length(trim(code)) > 0),
                    name text NOT NULL CHECK (length(trim(name)) > 0),
                    unit_symbol text NOT NULL CHECK (length(trim(unit_symbol)) > 0),
                    version bigint NOT NULL CHECK (version > 0),
                    CONSTRAINT point_equipment_code_key UNIQUE (equipment_id, code)
                );
                CREATE TABLE {Schema}.mutation_audit (
                    audit_id uuid PRIMARY KEY,
                    equipment_id uuid NOT NULL,
                    point_id uuid NULL,
                    scope_id uuid NOT NULL,
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    permission text NOT NULL,
                    action text NOT NULL,
                    resulting_version bigint NOT NULL CHECK (resulting_version > 0),
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX equipment_audit_scope_idx ON {Schema}.mutation_audit (scope_id, changed_at);
                """),
            new MigrationStep(
                2,
                "recoverable equipment staging rows",
                $"""
                CREATE TABLE {Schema}.staging_row (
                    row_id uuid PRIMARY KEY,
                    request_fingerprint character(64) NOT NULL,
                    scope_id uuid NOT NULL,
                    equipment_id uuid NOT NULL,
                    location_id uuid NOT NULL,
                    code text NOT NULL,
                    name text NOT NULL,
                    protocol text NOT NULL,
                    form_data jsonb NOT NULL,
                    protected_secret bytea NULL,
                    state smallint NOT NULL CHECK (state BETWEEN 1 AND 3),
                    version bigint NOT NULL CHECK (version > 0),
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL
                );
                CREATE INDEX staging_scope_state_idx ON {Schema}.staging_row (scope_id, state, row_id);
                CREATE TABLE {Schema}.staging_audit (
                    audit_id uuid PRIMARY KEY,
                    row_id uuid NOT NULL,
                    scope_id uuid NOT NULL,
                    equipment_id uuid NOT NULL,
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    permission text NOT NULL,
                    action text NOT NULL,
                    resulting_version bigint NOT NULL CHECK (resulting_version > 0),
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX staging_audit_scope_idx ON {Schema}.staging_audit (scope_id, changed_at);
                """),
        ]);
}
