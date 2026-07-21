using Dispatcher.Persistence;

namespace Dispatcher.Incidents;

public static class IncidentMigrations
{
    public const string Owner = "incidents";
    public const string Schema = "incidents";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner, Schema, databaseRole,
        [new MigrationStep(1, "incident nucleus source links tasks commands and audit", $"""
        CREATE TABLE {Schema}.incident (
            incident_id uuid PRIMARY KEY,
            summary text NOT NULL CHECK (length(trim(summary)) BETWEEN 1 AND 500),
            coordinator_person_id uuid NOT NULL,
            version bigint NOT NULL CHECK (version > 0),
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.source_link (
            link_id uuid PRIMARY KEY,
            incident_id uuid NOT NULL REFERENCES {Schema}.incident(incident_id),
            event_id uuid NOT NULL,
            occurrence_id uuid NOT NULL,
            scope_id uuid NOT NULL,
            point_id uuid NOT NULL,
            route text NOT NULL CHECK (route LIKE '/%'),
            required_permissions text[] NOT NULL,
            CONSTRAINT source_link_event_once UNIQUE (incident_id, event_id)
        );
        CREATE TABLE {Schema}.task (
            task_id uuid PRIMARY KEY,
            incident_id uuid NOT NULL REFERENCES {Schema}.incident(incident_id),
            summary text NOT NULL CHECK (length(trim(summary)) BETWEEN 1 AND 500),
            assigned_person_id uuid NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 3),
            version bigint NOT NULL CHECK (version > 0),
            updated_at timestamp with time zone NOT NULL
        );
        CREATE INDEX task_assignee_idx ON {Schema}.task (assigned_person_id, updated_at DESC, task_id);
        CREATE TABLE {Schema}.command_receipt (
            idempotency_key text PRIMARY KEY CHECK (length(idempotency_key) BETWEEN 1 AND 200),
            action text NOT NULL,
            fingerprint character(64) NOT NULL,
            result_snapshot jsonb NOT NULL,
            applied_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.mutation_audit (
            audit_id uuid PRIMARY KEY,
            incident_id uuid NOT NULL,
            task_id uuid NULL,
            session_id uuid NOT NULL,
            subject_id uuid NOT NULL,
            action text NOT NULL,
            resulting_version bigint NOT NULL CHECK (resulting_version > 0),
            changed_at timestamp with time zone NOT NULL
        );
        CREATE INDEX incident_audit_idx ON {Schema}.mutation_audit (incident_id, changed_at, audit_id);
        """)]);
}
