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
        """),
        new MigrationStep(2, "request defect and approved work order lifecycle", $"""
        CREATE TABLE {Schema}.maintenance_request (
            request_id uuid PRIMARY KEY,
            asset_id uuid NOT NULL REFERENCES {Schema}.asset(asset_id),
            scope_id uuid NOT NULL,
            summary text NOT NULL CHECK (length(trim(summary)) BETWEEN 1 AND 500),
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 3),
            version bigint NOT NULL CHECK (version > 0),
            event_id uuid NULL UNIQUE,
            occurrence_id uuid NULL,
            source_runtime_scope_id uuid NULL,
            point_id uuid NULL,
            source_route text NULL CHECK (source_route IS NULL OR source_route LIKE '/%'),
            source_permissions text[] NULL,
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.defect (
            defect_id uuid PRIMARY KEY,
            asset_id uuid NOT NULL REFERENCES {Schema}.asset(asset_id),
            scope_id uuid NOT NULL,
            summary text NOT NULL CHECK (length(trim(summary)) BETWEEN 1 AND 500),
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 3),
            version bigint NOT NULL CHECK (version > 0),
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.work_order (
            work_order_id uuid PRIMARY KEY,
            asset_id uuid NOT NULL REFERENCES {Schema}.asset(asset_id),
            scope_id uuid NOT NULL,
            source_kind smallint NOT NULL CHECK (source_kind IN (1, 2)),
            source_id uuid NOT NULL,
            summary text NOT NULL CHECK (length(trim(summary)) BETWEEN 1 AND 500),
            assigned_person_id uuid NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 4),
            permit_required boolean NOT NULL,
            isolation_required boolean NOT NULL,
            safety_instructions text NULL CHECK (safety_instructions IS NULL OR length(safety_instructions) <= 1000),
            safety_acknowledged_at timestamp with time zone NULL,
            version bigint NOT NULL CHECK (version > 0),
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL,
            CONSTRAINT work_order_source_once UNIQUE (source_kind, source_id)
        );
        CREATE INDEX work_order_assignee_idx
            ON {Schema}.work_order (assigned_person_id, state, updated_at DESC, work_order_id);
        CREATE TABLE {Schema}.work_order_checklist (
            checklist_item_id uuid PRIMARY KEY,
            work_order_id uuid NOT NULL REFERENCES {Schema}.work_order(work_order_id),
            description text NOT NULL CHECK (length(trim(description)) BETWEEN 1 AND 500),
            mandatory boolean NOT NULL,
            completed_at timestamp with time zone NULL,
            completed_by uuid NULL,
            CONSTRAINT checklist_completion_pair CHECK (
                (completed_at IS NULL AND completed_by IS NULL) OR
                (completed_at IS NOT NULL AND completed_by IS NOT NULL))
        );
        CREATE TABLE {Schema}.work_command_receipt (
            idempotency_key text PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 200),
            action text NOT NULL,
            fingerprint character(64) NOT NULL,
            result_kind text NOT NULL,
            result_snapshot jsonb NOT NULL,
            applied_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.work_mutation_audit (
            audit_id uuid PRIMARY KEY,
            scope_id uuid NOT NULL,
            entity_kind text NOT NULL,
            entity_id uuid NOT NULL,
            session_id uuid NOT NULL,
            subject_id uuid NOT NULL,
            action text NOT NULL,
            resulting_version bigint NOT NULL CHECK (resulting_version > 0),
            changed_at timestamp with time zone NOT NULL
        );
        CREATE INDEX work_audit_entity_idx
            ON {Schema}.work_mutation_audit (entity_kind, entity_id, changed_at, audit_id);
        """)]);
}
