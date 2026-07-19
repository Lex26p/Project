using Dispatcher.Persistence;

namespace Dispatcher.History;

public static class HistoryMigrations
{
    public const string Owner = "history";
    public const string Schema = "history";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "independent history sample and gap acceptance",
                $"""
                CREATE TABLE {Schema}.scope_state (
                    scope_id uuid PRIMARY KEY,
                    next_stream_position bigint NOT NULL DEFAULT 1 CHECK (next_stream_position > 0),
                    checkpoint_runtime_fact_position bigint NOT NULL DEFAULT 0 CHECK (checkpoint_runtime_fact_position >= 0),
                    checkpoint_stream_position bigint NOT NULL DEFAULT 0 CHECK (checkpoint_stream_position >= 0)
                );
                CREATE TABLE {Schema}.ingest_batch (
                    scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                    runtime_fact_position bigint NOT NULL CHECK (runtime_fact_position > 0),
                    fact_class smallint NOT NULL CHECK (fact_class IN (2, 3)),
                    fingerprint character(64) NOT NULL,
                    first_stream_position bigint NOT NULL CHECK (first_stream_position > 0),
                    last_stream_position bigint NOT NULL CHECK (last_stream_position >= first_stream_position),
                    accepted_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (scope_id, runtime_fact_position)
                );
                CREATE TABLE {Schema}.sample (
                    scope_id uuid NOT NULL,
                    history_stream_position bigint NOT NULL CHECK (history_stream_position > 0),
                    runtime_fact_position bigint NOT NULL,
                    source_id uuid NOT NULL,
                    point_id uuid NOT NULL,
                    source_position bigint NOT NULL CHECK (source_position > 0),
                    value bigint NOT NULL,
                    unit text NOT NULL,
                    quality smallint NOT NULL,
                    freshness smallint NOT NULL,
                    source_timestamp timestamp with time zone NOT NULL,
                    accepted_at timestamp with time zone NOT NULL,
                    is_late boolean NOT NULL,
                    is_out_of_order boolean NOT NULL,
                    PRIMARY KEY (scope_id, history_stream_position),
                    FOREIGN KEY (scope_id, runtime_fact_position)
                        REFERENCES {Schema}.ingest_batch(scope_id, runtime_fact_position),
                    CONSTRAINT sample_source_identity UNIQUE
                        (scope_id, runtime_fact_position, source_id, source_position)
                );
                CREATE INDEX sample_provenance_idx
                    ON {Schema}.sample (scope_id, source_id, point_id, source_position);
                CREATE TABLE {Schema}.gap (
                    scope_id uuid NOT NULL,
                    history_stream_position bigint NOT NULL CHECK (history_stream_position > 0),
                    runtime_fact_position bigint NOT NULL,
                    source_id uuid NOT NULL,
                    first_source_position bigint NOT NULL CHECK (first_source_position > 0),
                    last_source_position bigint NOT NULL CHECK (last_source_position >= first_source_position),
                    reason text NOT NULL,
                    accepted_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (scope_id, history_stream_position),
                    FOREIGN KEY (scope_id, runtime_fact_position)
                        REFERENCES {Schema}.ingest_batch(scope_id, runtime_fact_position)
                );
                CREATE TABLE {Schema}.source_watermark (
                    scope_id uuid NOT NULL,
                    source_id uuid NOT NULL,
                    point_id uuid NOT NULL,
                    max_source_position bigint NOT NULL CHECK (max_source_position > 0),
                    max_source_timestamp timestamp with time zone NOT NULL,
                    last_runtime_fact_position bigint NOT NULL CHECK (last_runtime_fact_position > 0),
                    PRIMARY KEY (scope_id, source_id, point_id)
                );
                CREATE FUNCTION {Schema}.reject_history_record_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'accepted history records are immutable';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_ingest_batch
                    BEFORE UPDATE OR DELETE ON {Schema}.ingest_batch
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.reject_history_record_mutation();
                CREATE TRIGGER protect_sample
                    BEFORE UPDATE OR DELETE ON {Schema}.sample
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.reject_history_record_mutation();
                CREATE TRIGGER protect_gap
                    BEFORE UPDATE OR DELETE ON {Schema}.gap
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.reject_history_record_mutation();
                """),
            new MigrationStep(
                2,
                "range query and explicit retention policy",
                $"""
                CREATE INDEX sample_range_idx
                    ON {Schema}.sample
                    (scope_id, source_id, point_id, source_timestamp, history_stream_position);
                CREATE INDEX gap_range_idx
                    ON {Schema}.gap
                    (scope_id, source_id, accepted_at, history_stream_position);
                CREATE TABLE {Schema}.retention_policy (
                    scope_id uuid PRIMARY KEY REFERENCES {Schema}.scope_state(scope_id),
                    policy_version integer NOT NULL CHECK (policy_version > 0),
                    delete_before timestamp with time zone NOT NULL,
                    through_stream_position bigint NOT NULL CHECK (through_stream_position >= 0),
                    applied_at timestamp with time zone NOT NULL
                );
                CREATE OR REPLACE FUNCTION {Schema}.reject_history_record_mutation() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' AND
                       current_setting('dispatcher.history_retention', true) = 'enabled' THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'accepted history records are immutable';
                END;
                $$ LANGUAGE plpgsql;
                """),
        ]);
}
