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
            new MigrationStep(
                3,
                "engineering drafts templates secrets and diagnostic jobs",
                $"""
                CREATE TABLE {Schema}.protocol_secret (
                    secret_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    protected_value bytea NOT NULL,
                    created_at timestamp with time zone NOT NULL
                );
                CREATE INDEX protocol_secret_scope_idx
                    ON {Schema}.protocol_secret (scope_id, secret_id);

                CREATE TABLE {Schema}.staging_draft (
                    row_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    equipment_id uuid NOT NULL,
                    location_id uuid NOT NULL,
                    code text NOT NULL,
                    name text NOT NULL,
                    protocol text NOT NULL,
                    form_data jsonb NOT NULL,
                    secret_reference text NULL,
                    apply_action smallint NOT NULL CHECK (apply_action BETWEEN 1 AND 3),
                    update_authorized boolean NOT NULL DEFAULT false,
                    fingerprint character(64) NOT NULL,
                    version bigint NOT NULL CHECK (version > 0),
                    applied_at timestamp with time zone NULL,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL
                );
                CREATE INDEX staging_draft_scope_idx
                    ON {Schema}.staging_draft (scope_id, updated_at, row_id);

                CREATE TABLE {Schema}.staging_template (
                    template_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    name text NOT NULL,
                    protocol text NOT NULL,
                    form_data jsonb NOT NULL,
                    version bigint NOT NULL CHECK (version > 0),
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL,
                    CONSTRAINT staging_template_scope_name_key UNIQUE (scope_id, name)
                );

                CREATE TABLE {Schema}.diagnostic_job (
                    job_id uuid PRIMARY KEY,
                    row_id uuid NOT NULL,
                    scope_id uuid NOT NULL,
                    mode smallint NOT NULL CHECK (mode BETWEEN 1 AND 2),
                    status smallint NOT NULL CHECK (status BETWEEN 1 AND 6),
                    fingerprint character(64) NOT NULL,
                    manifest jsonb NOT NULL,
                    secret_reference text NULL,
                    available_at timestamp with time zone NOT NULL,
                    claimed_by text NULL,
                    lease_token uuid NULL,
                    lease_until timestamp with time zone NULL,
                    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
                    started_at timestamp with time zone NULL,
                    completed_at timestamp with time zone NULL,
                    outcome_code text NULL,
                    outcome_message text NULL,
                    result jsonb NULL,
                    created_at timestamp with time zone NOT NULL,
                    CONSTRAINT diagnostic_outcome_pair CHECK (
                        (outcome_code IS NULL) = (outcome_message IS NULL))
                );
                CREATE INDEX diagnostic_job_claim_idx
                    ON {Schema}.diagnostic_job (available_at, job_id)
                    WHERE completed_at IS NULL;
                CREATE INDEX diagnostic_job_scope_idx
                    ON {Schema}.diagnostic_job (scope_id, created_at, job_id);

                CREATE TABLE {Schema}.commissioning_audit (
                    audit_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    row_id uuid NULL,
                    job_id uuid NULL,
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    permission text NOT NULL,
                    action text NOT NULL,
                    resulting_version bigint NULL,
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX commissioning_audit_scope_idx
                    ON {Schema}.commissioning_audit (scope_id, changed_at);
                """),
        ]);
}
