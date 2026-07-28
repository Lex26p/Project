using Dispatcher.Persistence;

namespace Dispatcher.Configuration;

public static class ConfigurationMigrations
{
    public const string Owner = "configuration_release";
    public const string Schema = "configuration_release";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "scoped configuration revisions and release jobs",
                $"""
                CREATE TABLE {Schema}.scope_state (
                    scope_id uuid PRIMARY KEY,
                    next_revision_number bigint NOT NULL CHECK (next_revision_number > 0),
                    draft_revision_id uuid NULL,
                    published_revision_id uuid NULL,
                    distributed_revision_id uuid NULL,
                    activated_revision_id uuid NULL,
                    version bigint NOT NULL CHECK (version > 0)
                );
                CREATE TABLE {Schema}.revision (
                    revision_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                    revision_number bigint NOT NULL CHECK (revision_number > 0),
                    source_revision_id uuid NULL REFERENCES {Schema}.revision(revision_id),
                    manifest jsonb NOT NULL,
                    manifest_fingerprint character(64) NOT NULL,
                    dependencies jsonb NOT NULL,
                    dependency_fingerprint character(64) NOT NULL,
                    validated_manifest_fingerprint character(64) NULL,
                    validated_dependency_fingerprint character(64) NULL,
                    version bigint NOT NULL CHECK (version > 0),
                    saved_at timestamp with time zone NOT NULL,
                    validated_at timestamp with time zone NULL,
                    published_at timestamp with time zone NULL,
                    distributed_at timestamp with time zone NULL,
                    activated_at timestamp with time zone NULL,
                    CONSTRAINT revision_scope_number_key UNIQUE (scope_id, revision_number),
                    CONSTRAINT revision_validation_pair CHECK (
                        (validated_manifest_fingerprint IS NULL) = (validated_dependency_fingerprint IS NULL))
                );
                ALTER TABLE {Schema}.scope_state
                    ADD CONSTRAINT scope_draft_fk FOREIGN KEY (draft_revision_id) REFERENCES {Schema}.revision(revision_id),
                    ADD CONSTRAINT scope_published_fk FOREIGN KEY (published_revision_id) REFERENCES {Schema}.revision(revision_id),
                    ADD CONSTRAINT scope_distributed_fk FOREIGN KEY (distributed_revision_id) REFERENCES {Schema}.revision(revision_id),
                    ADD CONSTRAINT scope_activated_fk FOREIGN KEY (activated_revision_id) REFERENCES {Schema}.revision(revision_id);
                CREATE TABLE {Schema}.distribution_job (
                    job_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    revision_id uuid NOT NULL REFERENCES {Schema}.revision(revision_id),
                    revision_fingerprint character(64) NOT NULL,
                    available_at timestamp with time zone NOT NULL,
                    claimed_by text NULL,
                    lease_until timestamp with time zone NULL,
                    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
                    completed_at timestamp with time zone NULL,
                    CONSTRAINT distribution_revision_key UNIQUE (revision_id)
                );
                CREATE INDEX distribution_claim_idx ON {Schema}.distribution_job (available_at, job_id)
                    WHERE completed_at IS NULL;
                CREATE TABLE {Schema}.mutation_audit (
                    audit_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    revision_id uuid NOT NULL,
                    job_id uuid NULL,
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    permission text NOT NULL,
                    action text NOT NULL,
                    resulting_version bigint NOT NULL CHECK (resulting_version > 0),
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX configuration_audit_scope_idx ON {Schema}.mutation_audit (scope_id, changed_at);
                CREATE FUNCTION {Schema}.protect_published_revision() RETURNS trigger AS $$
                BEGIN
                    IF OLD.published_at IS NOT NULL AND
                       (NEW.manifest IS DISTINCT FROM OLD.manifest OR
                        NEW.dependencies IS DISTINCT FROM OLD.dependencies OR
                        NEW.manifest_fingerprint IS DISTINCT FROM OLD.manifest_fingerprint OR
                        NEW.dependency_fingerprint IS DISTINCT FROM OLD.dependency_fingerprint) THEN
                        RAISE EXCEPTION 'published configuration content is immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_published_revision
                    BEFORE UPDATE ON {Schema}.revision
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.protect_published_revision();
                """),
            new MigrationStep(
                2,
                "initial equipment configuration obligations",
                $"""
                CREATE TABLE {Schema}.initial_configuration_obligation (
                    obligation_id uuid PRIMARY KEY,
                    request_fingerprint character(64) NOT NULL,
                    scope_id uuid NOT NULL,
                    equipment_id uuid NOT NULL,
                    protocol text NOT NULL,
                    form_data jsonb NOT NULL,
                    protected_secret bytea NULL,
                    accepted_at timestamp with time zone NOT NULL
                );
                CREATE INDEX initial_configuration_scope_idx
                    ON {Schema}.initial_configuration_obligation (scope_id, equipment_id);
                CREATE TABLE {Schema}.obligation_audit (
                    audit_id uuid PRIMARY KEY,
                    obligation_id uuid NOT NULL,
                    scope_id uuid NOT NULL,
                    equipment_id uuid NOT NULL,
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    permission text NOT NULL,
                    action text NOT NULL,
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX obligation_audit_scope_idx ON {Schema}.obligation_audit (scope_id, changed_at);
                """),
            new MigrationStep(
                3,
                "workload deployment prepare switch acknowledge",
                $"""
                ALTER TABLE {Schema}.distribution_job
                    ADD COLUMN lease_token uuid NULL,
                    ADD COLUMN prepared_at timestamp with time zone NULL,
                    ADD COLUMN switched_at timestamp with time zone NULL,
                    ADD COLUMN acknowledged_at timestamp with time zone NULL,
                    ADD COLUMN runtime_generation bigint NULL CHECK (runtime_generation > 0),
                    ADD COLUMN alarm_definition_epoch bigint NULL CHECK (alarm_definition_epoch > 0),
                    ADD COLUMN outcome_code text NULL,
                    ADD COLUMN outcome_message text NULL,
                    ADD CONSTRAINT distribution_outcome_pair CHECK (
                        (outcome_code IS NULL) = (outcome_message IS NULL)),
                    ADD CONSTRAINT distribution_switch_generation CHECK (
                        switched_at IS NULL OR
                        (runtime_generation IS NOT NULL AND alarm_definition_epoch IS NOT NULL)),
                    ADD CONSTRAINT distribution_acknowledged_completed CHECK (
                        acknowledged_at IS NULL OR completed_at IS NOT NULL);
                ALTER TABLE {Schema}.scope_state
                    ADD COLUMN pending_activation_revision_id uuid NULL,
                    ADD CONSTRAINT scope_pending_activation_fk
                        FOREIGN KEY (pending_activation_revision_id)
                        REFERENCES {Schema}.revision(revision_id);
                CREATE TABLE {Schema}.workload_deployment_event (
                    event_id uuid PRIMARY KEY,
                    job_id uuid NOT NULL REFERENCES {Schema}.distribution_job(job_id),
                    scope_id uuid NOT NULL,
                    revision_id uuid NOT NULL,
                    worker_id text NOT NULL,
                    action text NOT NULL,
                    outcome_code text NULL,
                    outcome_message text NULL,
                    runtime_generation bigint NULL,
                    alarm_definition_epoch bigint NULL,
                    changed_at timestamp with time zone NOT NULL,
                    CONSTRAINT workload_event_outcome_pair CHECK (
                        (outcome_code IS NULL) = (outcome_message IS NULL))
                );
                CREATE INDEX workload_deployment_event_scope_idx
                    ON {Schema}.workload_deployment_event (scope_id, changed_at);
                """),
        ]);
}
