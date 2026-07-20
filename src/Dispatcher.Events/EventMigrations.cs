using Dispatcher.Persistence;

namespace Dispatcher.Events;

public static class EventMigrations
{
    public const string Owner = "event_journal";
    public const string Schema = "event_journal";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "immutable event journal and separate occurrence projection",
                $"""
                CREATE TABLE {Schema}.scope_state (
                    scope_id uuid PRIMARY KEY,
                    next_event_position bigint NOT NULL DEFAULT 1 CHECK (next_event_position > 0),
                    next_projection_version bigint NOT NULL DEFAULT 1 CHECK (next_projection_version > 0)
                );
                CREATE TABLE {Schema}.journal_event (
                    scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                    event_position bigint NOT NULL CHECK (event_position > 0),
                    event_id uuid NOT NULL UNIQUE,
                    point_id uuid NOT NULL,
                    occurrence_id uuid NOT NULL,
                    source_condition_version bigint NOT NULL CHECK (source_condition_version > 0),
                    source_fingerprint character(64) NOT NULL,
                    kind smallint NOT NULL CHECK (kind IN (1, 2, 3)),
                    occurred_at timestamp with time zone NOT NULL,
                    accepted_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (scope_id, event_position),
                    CONSTRAINT journal_alarm_source_key UNIQUE
                        (scope_id, occurrence_id, source_condition_version)
                );
                CREATE INDEX journal_query_idx
                    ON {Schema}.journal_event (scope_id, occurred_at, point_id, kind, event_position);
                CREATE TABLE {Schema}.occurrence_projection (
                    scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                    occurrence_id uuid NOT NULL,
                    point_id uuid NOT NULL,
                    projection_version bigint NOT NULL CHECK (projection_version > 0),
                    condition_state smallint NOT NULL CHECK (condition_state IN (1, 3, 4)),
                    acknowledgement_state smallint NOT NULL CHECK (acknowledgement_state IN (1, 2)),
                    condition_version bigint NOT NULL CHECK (condition_version > 0),
                    acknowledgement_version bigint NOT NULL CHECK (acknowledgement_version > 0),
                    assignment_version bigint NOT NULL CHECK (assignment_version > 0),
                    shelving_version bigint NOT NULL CHECK (shelving_version > 0),
                    suppression_version bigint NOT NULL CHECK (suppression_version > 0),
                    fingerprint character(64) NOT NULL,
                    snapshot jsonb NOT NULL,
                    projected_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (scope_id, occurrence_id),
                    CONSTRAINT projection_scope_version_key UNIQUE (scope_id, projection_version)
                );
                CREATE INDEX projection_point_idx
                    ON {Schema}.occurrence_projection (scope_id, point_id, projection_version);
                CREATE TABLE {Schema}.projection_change (
                    scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                    projection_version bigint NOT NULL CHECK (projection_version > 0),
                    occurrence_id uuid NOT NULL,
                    point_id uuid NOT NULL,
                    snapshot jsonb NOT NULL,
                    projected_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (scope_id, projection_version)
                );
                CREATE FUNCTION {Schema}.reject_event_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'accepted Event Journal records are immutable';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_journal_event
                    BEFORE UPDATE OR DELETE ON {Schema}.journal_event
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.reject_event_mutation();
                CREATE TRIGGER protect_projection_change
                    BEFORE UPDATE OR DELETE ON {Schema}.projection_change
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.reject_event_mutation();
                """),
        ]);
}
