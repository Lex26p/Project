using Dispatcher.Persistence;

namespace Dispatcher.Core;

public static class CoreRuntimeMigrations
{
    public const string Owner = "core_runtime";
    public const string Schema = "core_runtime";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "protected source obligations and rebuildable checkpoints",
                $"""
                CREATE TABLE {Schema}.scope_state (
                    scope_id uuid PRIMARY KEY,
                    next_obligation_position bigint NOT NULL DEFAULT 1 CHECK (next_obligation_position > 0),
                    checkpoint_obligation_position bigint NOT NULL DEFAULT 0 CHECK (checkpoint_obligation_position >= 0),
                    protected_continuity boolean NOT NULL DEFAULT true,
                    checkpoint jsonb NULL,
                    checkpointed_at timestamp with time zone NULL
                );
                CREATE TABLE {Schema}.source_obligation (
                    scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                    obligation_position bigint NOT NULL CHECK (obligation_position > 0),
                    fact_class smallint NOT NULL CHECK (fact_class IN (2, 3)),
                    protection smallint NOT NULL DEFAULT 2 CHECK (protection = 2),
                    source_id uuid NOT NULL,
                    binding_generation bigint NOT NULL CHECK (binding_generation > 0),
                    session_generation bigint NOT NULL CHECK (session_generation > 0),
                    schedule_sequence bigint NOT NULL CHECK (schedule_sequence > 0),
                    first_source_position bigint NULL CHECK (first_source_position > 0),
                    last_source_position bigint NULL CHECK (last_source_position >= first_source_position),
                    payload jsonb NULL,
                    gap_reason text NULL,
                    recorded_at timestamp with time zone NOT NULL,
                    checkpointed_at timestamp with time zone NULL,
                    PRIMARY KEY (scope_id, obligation_position),
                    CONSTRAINT source_obligation_payload CHECK (
                        (fact_class = 2 AND payload IS NOT NULL AND gap_reason IS NULL) OR
                        (fact_class = 3 AND payload IS NULL AND gap_reason IS NOT NULL))
                );
                CREATE INDEX source_obligation_pending_idx
                    ON {Schema}.source_obligation (scope_id, obligation_position)
                    WHERE checkpointed_at IS NULL;
                CREATE FUNCTION {Schema}.protect_source_obligation() RETURNS trigger AS $$
                BEGIN
                    IF NEW.scope_id IS DISTINCT FROM OLD.scope_id OR
                       NEW.obligation_position IS DISTINCT FROM OLD.obligation_position OR
                       NEW.fact_class IS DISTINCT FROM OLD.fact_class OR
                       NEW.protection IS DISTINCT FROM OLD.protection OR
                       NEW.source_id IS DISTINCT FROM OLD.source_id OR
                       NEW.binding_generation IS DISTINCT FROM OLD.binding_generation OR
                       NEW.session_generation IS DISTINCT FROM OLD.session_generation OR
                       NEW.schedule_sequence IS DISTINCT FROM OLD.schedule_sequence OR
                       NEW.first_source_position IS DISTINCT FROM OLD.first_source_position OR
                       NEW.last_source_position IS DISTINCT FROM OLD.last_source_position OR
                       NEW.payload IS DISTINCT FROM OLD.payload OR
                       NEW.gap_reason IS DISTINCT FROM OLD.gap_reason OR
                       NEW.recorded_at IS DISTINCT FROM OLD.recorded_at THEN
                        RAISE EXCEPTION 'protected source obligation is immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_source_obligation
                    BEFORE UPDATE ON {Schema}.source_obligation
                    FOR EACH ROW EXECUTE FUNCTION {Schema}.protect_source_obligation();
                """),
        ]);
}
