using Dispatcher.Persistence;

namespace Dispatcher.Alarm;

public static class AlarmMigrations
{
    public const string Owner = "alarm_runtime";
    public const string Schema = "alarm_runtime";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "versioned alarm definitions and local occurrence evaluator",
                $"""
                CREATE TABLE {Schema}.scope_state (
                    scope_id uuid PRIMARY KEY,
                    active_epoch bigint NULL CHECK (active_epoch > 0),
                    last_evaluation_position bigint NOT NULL DEFAULT 0 CHECK (last_evaluation_position >= 0)
                );
                CREATE TABLE {Schema}.definition_set (
                    scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                    epoch bigint NOT NULL CHECK (epoch > 0),
                    fingerprint character(64) NOT NULL,
                    activated_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (scope_id, epoch)
                );
                CREATE TABLE {Schema}.definition (
                    scope_id uuid NOT NULL,
                    epoch bigint NOT NULL,
                    definition_id uuid NOT NULL,
                    point_id uuid NOT NULL,
                    name text NOT NULL,
                    direction smallint NOT NULL CHECK (direction IN (1, 2)),
                    threshold bigint NOT NULL,
                    hysteresis bigint NOT NULL CHECK (hysteresis >= 0),
                    raise_delay_ticks bigint NOT NULL CHECK (raise_delay_ticks >= 0),
                    clear_delay_ticks bigint NOT NULL CHECK (clear_delay_ticks >= 0),
                    enabled boolean NOT NULL,
                    PRIMARY KEY (scope_id, epoch, definition_id),
                    FOREIGN KEY (scope_id, epoch)
                        REFERENCES {Schema}.definition_set(scope_id, epoch)
                );
                CREATE INDEX definition_point_idx
                    ON {Schema}.definition (scope_id, epoch, point_id);
                CREATE TABLE {Schema}.occurrence (
                    occurrence_id uuid PRIMARY KEY,
                    scope_id uuid NOT NULL,
                    definition_epoch bigint NOT NULL,
                    definition_id uuid NOT NULL,
                    point_id uuid NOT NULL,
                    opened_at timestamp with time zone NOT NULL,
                    closed_at timestamp with time zone NULL,
                    condition_state smallint NOT NULL CHECK (condition_state IN (1, 3, 4)),
                    condition_pending_since timestamp with time zone NULL,
                    condition_active_since timestamp with time zone NULL,
                    condition_cleared_at timestamp with time zone NULL,
                    condition_version bigint NOT NULL CHECK (condition_version > 0),
                    acknowledgement_state smallint NOT NULL CHECK (acknowledgement_state IN (1, 2)),
                    acknowledged_by uuid NULL,
                    acknowledged_at timestamp with time zone NULL,
                    acknowledgement_version bigint NOT NULL CHECK (acknowledgement_version > 0),
                    assigned_to uuid NULL,
                    assigned_at timestamp with time zone NULL,
                    assignment_version bigint NOT NULL CHECK (assignment_version > 0),
                    shelved_until timestamp with time zone NULL,
                    shelving_reason text NULL,
                    shelving_version bigint NOT NULL CHECK (shelving_version > 0),
                    is_suppressed boolean NOT NULL,
                    suppression_reason text NULL,
                    suppression_version bigint NOT NULL CHECK (suppression_version > 0),
                    FOREIGN KEY (scope_id, definition_epoch, definition_id)
                        REFERENCES {Schema}.definition(scope_id, epoch, definition_id)
                );
                CREATE INDEX occurrence_scope_idx
                    ON {Schema}.occurrence (scope_id, opened_at, occurrence_id);
                CREATE UNIQUE INDEX occurrence_active_definition_idx
                    ON {Schema}.occurrence (scope_id, definition_epoch, definition_id)
                    WHERE closed_at IS NULL;
                CREATE TABLE {Schema}.evaluator_state (
                    scope_id uuid NOT NULL,
                    definition_epoch bigint NOT NULL,
                    definition_id uuid NOT NULL,
                    condition_state smallint NOT NULL CHECK (condition_state IN (1, 2, 3, 4)),
                    pending_since timestamp with time zone NULL,
                    current_occurrence_id uuid NULL REFERENCES {Schema}.occurrence(occurrence_id),
                    last_value bigint NULL,
                    last_current_position bigint NOT NULL DEFAULT 0 CHECK (last_current_position >= 0),
                    updated_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (scope_id, definition_epoch, definition_id),
                    FOREIGN KEY (scope_id, definition_epoch, definition_id)
                        REFERENCES {Schema}.definition(scope_id, epoch, definition_id)
                );
                CREATE FUNCTION {Schema}.reject_definition_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'activated alarm definitions are immutable';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_definition_set
                    BEFORE UPDATE OR DELETE ON {Schema}.definition_set
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.reject_definition_mutation();
                CREATE TRIGGER protect_definition
                    BEFORE UPDATE OR DELETE ON {Schema}.definition
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.reject_definition_mutation();
                """),
        ]);
}
