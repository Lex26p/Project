using Dispatcher.Persistence;

namespace Dispatcher.Command;

public static class CommandMigrations
{
    public const string Owner = "command";
    public const string Schema = "command";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner, Schema, databaseRole,
        [new MigrationStep(1, "simulator control lease safety and prepared intent", $"""
        CREATE TABLE {Schema}.scope_guard (
            scope_id uuid PRIMARY KEY,
            safety_version bigint NOT NULL CHECK (safety_version > 0),
            blocked boolean NOT NULL,
            reason_code text NOT NULL,
            updated_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.safety_fact_receipt (
            fact_id uuid PRIMARY KEY,
            fingerprint character(64) NOT NULL,
            scope_id uuid NOT NULL,
            accepted_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.control_lease (
            lease_id uuid PRIMARY KEY,
            scope_id uuid NOT NULL,
            session_id uuid NOT NULL,
            subject_id uuid NOT NULL,
            issued_at timestamp with time zone NOT NULL,
            expires_at timestamp with time zone NOT NULL,
            revoked_at timestamp with time zone NULL,
            revocation_reason text NULL,
            version bigint NOT NULL CHECK (version > 0),
            CHECK (expires_at > issued_at)
        );
        CREATE UNIQUE INDEX command_active_scope_lease_idx ON {Schema}.control_lease (scope_id)
            WHERE revoked_at IS NULL;
        CREATE TABLE {Schema}.used_step_up (
            attestation_id uuid PRIMARY KEY,
            lease_id uuid NOT NULL UNIQUE REFERENCES {Schema}.control_lease(lease_id),
            consumed_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.prepared_intent (
            intent_id uuid PRIMARY KEY,
            fingerprint character(64) NOT NULL,
            lease_id uuid NOT NULL REFERENCES {Schema}.control_lease(lease_id),
            scope_id uuid NOT NULL,
            point_id uuid NOT NULL,
            desired_value bigint NOT NULL,
            unit text NOT NULL,
            revision_id uuid NOT NULL,
            revision_number bigint NOT NULL CHECK (revision_number > 0),
            manifest_generation bigint NOT NULL CHECK (manifest_generation > 0),
            manifest_fingerprint character(64) NOT NULL,
            current_position bigint NOT NULL CHECK (current_position >= 0),
            current_value bigint NOT NULL,
            quality smallint NOT NULL,
            freshness smallint NOT NULL,
            safety_version bigint NOT NULL CHECK (safety_version > 0),
            prepared_at timestamp with time zone NOT NULL,
            expires_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.command_audit (
            audit_id uuid PRIMARY KEY,
            lease_id uuid NULL,
            intent_id uuid NULL,
            session_id uuid NOT NULL,
            subject_id uuid NOT NULL,
            action text NOT NULL,
            outcome text NOT NULL,
            changed_at timestamp with time zone NOT NULL
        );
        CREATE FUNCTION {Schema}.deny_prepared_intent_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'prepared command intent is immutable'; END $$;
        CREATE TRIGGER prepared_intent_immutable BEFORE UPDATE OR DELETE ON {Schema}.prepared_intent
            FOR EACH ROW EXECUTE FUNCTION {Schema}.deny_prepared_intent_mutation();
        """),
        new MigrationStep(2, "simulator command execution lifecycle and realtime feed", $"""
        ALTER TABLE {Schema}.command_audit ADD COLUMN execution_id uuid NULL;
        CREATE TABLE {Schema}.command_execution (
            execution_id uuid PRIMARY KEY,
            intent_id uuid NOT NULL UNIQUE REFERENCES {Schema}.prepared_intent(intent_id),
            request_fingerprint character(64) NOT NULL,
            lease_id uuid NOT NULL REFERENCES {Schema}.control_lease(lease_id),
            scope_id uuid NOT NULL,
            point_id uuid NOT NULL,
            session_id uuid NOT NULL,
            subject_id uuid NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 5),
            progress smallint NOT NULL CHECK (progress BETWEEN 0 AND 100),
            result_value bigint NULL,
            rejection_code text NULL,
            accepted_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL,
            completed_at timestamp with time zone NULL,
            version bigint NOT NULL CHECK (version > 0)
        );
        CREATE INDEX command_execution_session_scope_idx
            ON {Schema}.command_execution (session_id,scope_id,accepted_at,execution_id);
        CREATE TABLE {Schema}.simulator_execution_receipt (
            execution_id uuid PRIMARY KEY REFERENCES {Schema}.command_execution(execution_id),
            fingerprint character(64) NOT NULL,
            outcome smallint NOT NULL CHECK (outcome IN (3,4)),
            result_value bigint NULL,
            rejection_code text NULL,
            accepted_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.execution_transition (
            position bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            execution_id uuid NOT NULL REFERENCES {Schema}.command_execution(execution_id),
            scope_id uuid NOT NULL,
            point_id uuid NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 5),
            progress smallint NOT NULL CHECK (progress BETWEEN 0 AND 100),
            result_value bigint NULL,
            rejection_code text NULL,
            occurred_at timestamp with time zone NOT NULL,
            version bigint NOT NULL CHECK (version > 0)
        );
        CREATE INDEX command_transition_scope_position_idx
            ON {Schema}.execution_transition (scope_id,position);
        CREATE FUNCTION {Schema}.deny_command_fact_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'command execution fact is immutable'; END $$;
        CREATE TRIGGER simulator_execution_receipt_immutable BEFORE UPDATE OR DELETE
            ON {Schema}.simulator_execution_receipt FOR EACH ROW
            EXECUTE FUNCTION {Schema}.deny_command_fact_mutation();
        CREATE TRIGGER execution_transition_immutable BEFORE UPDATE OR DELETE
            ON {Schema}.execution_transition FOR EACH ROW
            EXECUTE FUNCTION {Schema}.deny_command_fact_mutation();
        """)]);
}
