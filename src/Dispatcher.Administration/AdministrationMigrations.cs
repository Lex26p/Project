using Dispatcher.Persistence;

namespace Dispatcher.Administration;

public static class AdministrationMigrations
{
    public const string Owner = "administration";
    public const string Schema = "administration";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [new MigrationStep(1, "immutable operational views and audit journal", $"""
            CREATE FUNCTION {Schema}.deny_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'administration facts are immutable'; END $$;

            CREATE TABLE {Schema}.health_fact (
                position bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                fact_id uuid NOT NULL UNIQUE,
                fingerprint character(64) NOT NULL,
                owner text NOT NULL,
                scope_id uuid NULL,
                required_permission text NOT NULL,
                facet smallint NOT NULL CHECK (facet BETWEEN 1 AND 6),
                state smallint NOT NULL CHECK (state BETWEEN 1 AND 4),
                reason_code text NOT NULL,
                observed_at timestamp with time zone NOT NULL
            );
            CREATE INDEX health_fact_current_idx ON {Schema}.health_fact (owner,scope_id,facet,position DESC);

            CREATE TABLE {Schema}.data_quality_fact (
                position bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                fact_id uuid NOT NULL UNIQUE,
                issue_id uuid NOT NULL,
                fingerprint character(64) NOT NULL,
                owner text NOT NULL,
                scope_id uuid NULL,
                required_permission text NOT NULL,
                kind smallint NOT NULL CHECK (kind BETWEEN 1 AND 5),
                severity smallint NOT NULL CHECK (severity BETWEEN 1 AND 3),
                state smallint NOT NULL CHECK (state BETWEEN 1 AND 2),
                summary text NOT NULL,
                source_reference text NOT NULL,
                observed_at timestamp with time zone NOT NULL
            );
            CREATE INDEX data_quality_current_idx ON {Schema}.data_quality_fact (issue_id,position DESC);

            CREATE TABLE {Schema}.audit_record (
                position bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                event_id uuid NOT NULL UNIQUE,
                fingerprint character(64) NOT NULL,
                owner text NOT NULL,
                scope_id uuid NULL,
                required_permission text NOT NULL,
                session_id uuid NULL,
                subject_id uuid NULL,
                principal_kind smallint NULL CHECK (principal_kind IS NULL OR principal_kind BETWEEN 1 AND 3),
                action text NOT NULL,
                target_kind text NOT NULL,
                target_id uuid NULL,
                outcome smallint NOT NULL CHECK (outcome BETWEEN 1 AND 3),
                summary text NOT NULL,
                occurred_at timestamp with time zone NOT NULL
            );
            CREATE INDEX audit_record_tail_idx ON {Schema}.audit_record (position);

            CREATE TRIGGER health_fact_immutable BEFORE UPDATE OR DELETE ON {Schema}.health_fact
                FOR EACH ROW EXECUTE FUNCTION {Schema}.deny_mutation();
            CREATE TRIGGER data_quality_fact_immutable BEFORE UPDATE OR DELETE ON {Schema}.data_quality_fact
                FOR EACH ROW EXECUTE FUNCTION {Schema}.deny_mutation();
            CREATE TRIGGER audit_record_immutable BEFORE UPDATE OR DELETE ON {Schema}.audit_record
                FOR EACH ROW EXECUTE FUNCTION {Schema}.deny_mutation();
            """)]);
}
