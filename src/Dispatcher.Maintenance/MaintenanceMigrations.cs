using Dispatcher.Persistence;

namespace Dispatcher.Maintenance;

public static class MaintenanceMigrations
{
    public const string Owner = "maintenance";
    public const string Schema = "maintenance";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner, Schema, databaseRole,
        [new MigrationStep(1, "independent assets optional equipment links history commands and audit", $"""
        CREATE TABLE {Schema}.asset (
            asset_id uuid PRIMARY KEY,
            scope_id uuid NOT NULL,
            code text NOT NULL CHECK (length(trim(code)) BETWEEN 1 AND 100),
            name text NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 500),
            equipment_id uuid NULL,
            version bigint NOT NULL CHECK (version > 0),
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL,
            CONSTRAINT maintenance_asset_scope_code UNIQUE (scope_id, code)
        );
        CREATE INDEX maintenance_asset_equipment_idx ON {Schema}.asset (equipment_id) WHERE equipment_id IS NOT NULL;
        CREATE TABLE {Schema}.equipment_link_history (
            asset_id uuid NOT NULL REFERENCES {Schema}.asset(asset_id),
            asset_version bigint NOT NULL CHECK (asset_version > 0),
            action smallint NOT NULL CHECK (action IN (1, 2)),
            equipment_id uuid NULL,
            changed_at timestamp with time zone NOT NULL,
            PRIMARY KEY (asset_id, asset_version)
        );
        CREATE TABLE {Schema}.command_receipt (
            idempotency_key text PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 200),
            action text NOT NULL,
            fingerprint character(64) NOT NULL,
            result_snapshot jsonb NOT NULL,
            applied_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.mutation_audit (
            audit_id uuid PRIMARY KEY,
            asset_id uuid NOT NULL,
            scope_id uuid NOT NULL,
            session_id uuid NOT NULL,
            subject_id uuid NOT NULL,
            action text NOT NULL,
            resulting_version bigint NOT NULL CHECK (resulting_version > 0),
            changed_at timestamp with time zone NOT NULL
        );
        CREATE INDEX maintenance_audit_scope_idx ON {Schema}.mutation_audit (scope_id, changed_at, audit_id);
        """)]);
}
