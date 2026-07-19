using Dispatcher.Persistence;

namespace Dispatcher.Simulator;

public static class SimulatorRuntimeMigrations
{
    public const string Owner = "simulator_runtime";
    public const string Schema = "simulator_runtime";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "immutable simulator manifests and active scope",
                $"""
                CREATE TABLE {Schema}.scope_state (
                    scope_id uuid PRIMARY KEY,
                    last_received_revision_number bigint NOT NULL DEFAULT 0 CHECK (last_received_revision_number >= 0),
                    active_revision_id uuid NULL,
                    active_generation bigint NOT NULL DEFAULT 0 CHECK (active_generation >= 0)
                );
                CREATE TABLE {Schema}.manifest (
                    revision_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                    revision_number bigint NOT NULL CHECK (revision_number > 0),
                    source_revision_id uuid NULL,
                    manifest text NOT NULL CHECK (jsonb_typeof(manifest::jsonb) = 'object'),
                    manifest_fingerprint character(64) NOT NULL,
                    dependency_fingerprint character(64) NOT NULL,
                    status smallint NOT NULL CHECK (status BETWEEN 1 AND 4),
                    rejection_code text NULL,
                    rejection_message text NULL,
                    received_at timestamp with time zone NOT NULL,
                    validated_at timestamp with time zone NULL,
                    activated_at timestamp with time zone NULL,
                    CONSTRAINT simulator_manifest_scope_number_key UNIQUE (scope_id, revision_number),
                    CONSTRAINT simulator_rejection_pair CHECK ((rejection_code IS NULL) = (rejection_message IS NULL))
                );
                ALTER TABLE {Schema}.scope_state ADD CONSTRAINT simulator_active_revision_fk
                    FOREIGN KEY (active_revision_id) REFERENCES {Schema}.manifest(revision_id);
                CREATE TABLE {Schema}.activation_audit (
                    audit_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    revision_id uuid NOT NULL,
                    worker_id text NOT NULL,
                    action text NOT NULL,
                    active_generation bigint NOT NULL CHECK (active_generation >= 0),
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX simulator_activation_audit_idx ON {Schema}.activation_audit (scope_id, changed_at);
                CREATE FUNCTION {Schema}.protect_manifest_content() RETURNS trigger AS $$
                BEGIN
                    IF NEW.manifest IS DISTINCT FROM OLD.manifest OR
                       NEW.manifest_fingerprint IS DISTINCT FROM OLD.manifest_fingerprint OR
                       NEW.dependency_fingerprint IS DISTINCT FROM OLD.dependency_fingerprint OR
                       NEW.scope_id IS DISTINCT FROM OLD.scope_id OR
                       NEW.revision_number IS DISTINCT FROM OLD.revision_number THEN
                        RAISE EXCEPTION 'received simulator manifest is immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_manifest_content
                    BEFORE UPDATE ON {Schema}.manifest
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.protect_manifest_content();
                """),
        ]);
}
