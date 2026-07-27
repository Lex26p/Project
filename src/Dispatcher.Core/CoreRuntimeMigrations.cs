using System.Text.RegularExpressions;
using Dispatcher.Persistence;

namespace Dispatcher.Core;

public static partial class CoreRuntimeMigrations
{
    public const string Owner = "core_runtime";
    public const string Schema = "core_runtime";
    public const string PublishedReadRoleKey = "core_runtime_published_read";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) =>
        CreatePlan(databaseRole, databaseRole);

    public static ModuleMigrationPlan CreatePlan(
        string databaseRole,
        string publishedReadRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedReadRole);
        if (!RolePattern().IsMatch(publishedReadRole))
        {
            throw new ArgumentException(
                "PostgreSQL identifiers must start with a lowercase letter and contain only lowercase letters, digits or underscores.",
                nameof(publishedReadRole));
        }

        var quotedPublishedReadRole = $"\"{publishedReadRole}\"";
        var publishedReadBoundarySql = string.Equals(
            databaseRole,
            publishedReadRole,
            StringComparison.Ordinal)
            ? "SELECT 1;"
            : $"""
              REVOKE ALL PRIVILEGES ON SCHEMA {Schema}
                  FROM PUBLIC;
              REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA {Schema}
                  FROM PUBLIC;
              REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA {Schema}
                  FROM {quotedPublishedReadRole};
              GRANT USAGE ON SCHEMA {Schema}
                  TO {quotedPublishedReadRole};
              GRANT SELECT ON
                  {Schema}.published_scope,
                  {Schema}.published_current,
                  {Schema}.published_delta
                  TO {quotedPublishedReadRole};
              """;
        return new ModuleMigrationPlan(
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
                new MigrationStep(
                    2,
                    "durable source session generation allocation",
                    $"""
                    CREATE TABLE {Schema}.source_session_generation (
                        scope_id uuid NOT NULL REFERENCES {Schema}.scope_state(scope_id),
                        source_id uuid NOT NULL,
                        last_generation bigint NOT NULL CHECK (last_generation > 0),
                        allocated_at timestamp with time zone NOT NULL,
                        PRIMARY KEY (scope_id, source_id)
                    );
                    """),
                new MigrationStep(
                    3,
                    "durable processing delivery and published current contract",
                    $"""
                    CREATE TABLE {Schema}.processing_delivery (
                        scope_id uuid NOT NULL,
                        obligation_position bigint NOT NULL CHECK (obligation_position > 0),
                        fact_class smallint NOT NULL CHECK (fact_class IN (2, 3)),
                        stage smallint NOT NULL DEFAULT 1 CHECK (stage IN (1, 2, 3)),
                        post_cut_acceptance jsonb NULL,
                        gap_reason text NULL,
                        definition_epoch uuid NULL,
                        history_state smallint NOT NULL DEFAULT 1 CHECK (history_state IN (1, 2)),
                        alarm_state smallint NOT NULL DEFAULT 1 CHECK (alarm_state IN (1, 2)),
                        event_state smallint NOT NULL DEFAULT 1 CHECK (event_state IN (1, 2)),
                        last_error_code text NULL CHECK (
                            last_error_code IS NULL OR
                            (length(last_error_code) BETWEEN 1 AND 200 AND last_error_code = btrim(last_error_code))),
                        last_error_at timestamp with time zone NULL,
                        created_at timestamp with time zone NOT NULL,
                        updated_at timestamp with time zone NOT NULL,
                        downstream_completed_at timestamp with time zone NULL,
                        published_at timestamp with time zone NULL,
                        PRIMARY KEY (scope_id, obligation_position),
                        FOREIGN KEY (scope_id, obligation_position)
                            REFERENCES {Schema}.source_obligation(scope_id, obligation_position),
                        CONSTRAINT processing_delivery_payload CHECK (
                            (fact_class = 2 AND post_cut_acceptance IS NOT NULL AND gap_reason IS NULL) OR
                            (fact_class = 3 AND post_cut_acceptance IS NULL AND gap_reason IS NOT NULL)),
                        CONSTRAINT processing_delivery_error_pair CHECK (
                            (last_error_code IS NULL) = (last_error_at IS NULL)),
                        CONSTRAINT processing_delivery_stage CHECK (
                            (stage = 1 AND downstream_completed_at IS NULL AND published_at IS NULL) OR
                            (stage = 2 AND history_state = 2 AND alarm_state = 2 AND event_state = 2 AND
                             downstream_completed_at IS NOT NULL AND published_at IS NULL) OR
                            (stage = 3 AND history_state = 2 AND alarm_state = 2 AND event_state = 2 AND
                             downstream_completed_at IS NOT NULL AND published_at IS NOT NULL))
                    );
                    CREATE UNIQUE INDEX processing_delivery_one_unfinished_idx
                        ON {Schema}.processing_delivery (scope_id)
                        WHERE stage < 3;

                    CREATE TABLE {Schema}.published_scope (
                        scope_id uuid PRIMARY KEY REFERENCES {Schema}.scope_state(scope_id),
                        completed_obligation_position bigint NOT NULL DEFAULT 0
                            CHECK (completed_obligation_position >= 0),
                        current_position bigint NOT NULL DEFAULT 0 CHECK (current_position >= 0),
                        earliest_delta_position bigint NOT NULL DEFAULT 1
                            CHECK (earliest_delta_position > 0),
                        protected_continuity boolean NOT NULL DEFAULT true,
                        ready boolean NOT NULL DEFAULT false,
                        degradation_reason_code text NULL CHECK (
                            degradation_reason_code IS NULL OR
                            (length(degradation_reason_code) BETWEEN 1 AND 200 AND
                             degradation_reason_code = btrim(degradation_reason_code))),
                        heartbeat_at timestamp with time zone NOT NULL,
                        published_at timestamp with time zone NULL,
                        CONSTRAINT published_scope_cursor_window CHECK (
                            earliest_delta_position <= current_position + 1)
                    );

                    CREATE TABLE {Schema}.published_current (
                        scope_id uuid NOT NULL REFERENCES {Schema}.published_scope(scope_id),
                        point_id uuid NOT NULL,
                        source_id uuid NOT NULL,
                        binding_generation bigint NOT NULL CHECK (binding_generation > 0),
                        session_generation bigint NOT NULL CHECK (session_generation > 0),
                        source_position bigint NOT NULL CHECK (source_position > 0),
                        current_position bigint NOT NULL CHECK (current_position > 0),
                        value bigint NOT NULL,
                        unit text NOT NULL CHECK (length(unit) BETWEEN 1 AND 100),
                        quality smallint NOT NULL,
                        freshness smallint NOT NULL,
                        source_timestamp timestamp with time zone NOT NULL,
                        receive_timestamp timestamp with time zone NOT NULL,
                        processed_timestamp timestamp with time zone NOT NULL,
                        PRIMARY KEY (scope_id, point_id),
                        UNIQUE (scope_id, current_position)
                    );

                    CREATE TABLE {Schema}.published_delta (
                        scope_id uuid NOT NULL REFERENCES {Schema}.published_scope(scope_id),
                        current_position bigint NOT NULL CHECK (current_position > 0),
                        point_id uuid NOT NULL,
                        source_id uuid NOT NULL,
                        binding_generation bigint NOT NULL CHECK (binding_generation > 0),
                        session_generation bigint NOT NULL CHECK (session_generation > 0),
                        source_position bigint NOT NULL CHECK (source_position > 0),
                        value bigint NOT NULL,
                        unit text NOT NULL CHECK (length(unit) BETWEEN 1 AND 100),
                        quality smallint NOT NULL,
                        freshness smallint NOT NULL,
                        source_timestamp timestamp with time zone NOT NULL,
                        receive_timestamp timestamp with time zone NOT NULL,
                        processed_timestamp timestamp with time zone NOT NULL,
                        PRIMARY KEY (scope_id, current_position)
                    );
                    CREATE INDEX published_delta_point_idx
                        ON {Schema}.published_delta (scope_id, point_id, current_position);
                    """),
                new MigrationStep(
                    4,
                    "published current read role boundary",
                    publishedReadBoundarySql),
            ]);
    }

    [GeneratedRegex(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
