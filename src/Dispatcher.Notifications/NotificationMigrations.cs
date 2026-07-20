using Dispatcher.Persistence;

namespace Dispatcher.Notifications;

public static class NotificationMigrations
{
    public const string Owner = "notifications";
    public const string Schema = "notifications";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "notification policy personal settings subscriptions and inbox",
                $"""
                CREATE TABLE {Schema}.policy (
                    scope_id uuid PRIMARY KEY,
                    policy_id uuid NOT NULL UNIQUE,
                    revision bigint NOT NULL CHECK (revision > 0),
                    fingerprint character(64) NOT NULL,
                    snapshot jsonb NOT NULL,
                    updated_at timestamp with time zone NOT NULL
                );
                CREATE TABLE {Schema}.personal_setting (
                    person_id uuid PRIMARY KEY,
                    version bigint NOT NULL CHECK (version > 0),
                    fingerprint character(64) NOT NULL,
                    snapshot jsonb NOT NULL,
                    updated_at timestamp with time zone NOT NULL
                );
                CREATE TABLE {Schema}.subscription (
                    subscription_id uuid PRIMARY KEY,
                    person_id uuid NOT NULL,
                    scope_id uuid NOT NULL,
                    version bigint NOT NULL CHECK (version > 0),
                    fingerprint character(64) NOT NULL,
                    snapshot jsonb NOT NULL,
                    updated_at timestamp with time zone NOT NULL
                );
                CREATE INDEX subscription_scope_person_idx
                    ON {Schema}.subscription (scope_id, person_id, subscription_id);
                CREATE TABLE {Schema}.inbox_item (
                    recipient_id uuid NOT NULL,
                    item_id uuid NOT NULL UNIQUE,
                    event_id uuid NOT NULL,
                    version bigint NOT NULL CHECK (version > 0),
                    read_at timestamp with time zone NULL,
                    fingerprint character(64) NOT NULL,
                    snapshot jsonb NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (recipient_id, item_id),
                    CONSTRAINT inbox_event_once UNIQUE (recipient_id, event_id)
                );
                CREATE INDEX inbox_recipient_created_idx
                    ON {Schema}.inbox_item (recipient_id, created_at DESC, item_id);
                """),
            new MigrationStep(
                2,
                "smtp delivery obligations attempts and inbox counters",
                $"""
                CREATE TABLE {Schema}.delivery_obligation (
                    delivery_id uuid PRIMARY KEY,
                    event_id uuid NOT NULL,
                    recipient_id uuid NOT NULL,
                    covered_person_id uuid NULL,
                    mandatory boolean NOT NULL,
                    channel smallint NOT NULL CHECK (channel = 2),
                    endpoint text NOT NULL CHECK (length(endpoint) BETWEEN 1 AND 320),
                    subject text NOT NULL CHECK (length(subject) BETWEEN 1 AND 200),
                    body text NOT NULL CHECK (length(body) BETWEEN 1 AND 4000),
                    status smallint NOT NULL CHECK (status BETWEEN 1 AND 6),
                    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count BETWEEN 0 AND 10),
                    next_attempt_at timestamp with time zone NOT NULL,
                    active_attempt_id uuid NULL,
                    lease_owner text NULL,
                    lease_until timestamp with time zone NULL,
                    version bigint NOT NULL CHECK (version > 0),
                    fingerprint character(64) NOT NULL,
                    created_at timestamp with time zone NOT NULL,
                    completed_at timestamp with time zone NULL,
                    CONSTRAINT delivery_event_recipient_key UNIQUE (event_id, recipient_id, channel)
                );
                CREATE INDEX delivery_claim_idx
                    ON {Schema}.delivery_obligation (status, next_attempt_at, created_at, delivery_id);
                CREATE TABLE {Schema}.provider_attempt (
                    attempt_id uuid PRIMARY KEY,
                    delivery_id uuid NOT NULL REFERENCES {Schema}.delivery_obligation(delivery_id),
                    attempt_number integer NOT NULL CHECK (attempt_number BETWEEN 1 AND 10),
                    outcome smallint NOT NULL CHECK (outcome BETWEEN 1 AND 4),
                    outcome_code text NULL CHECK (outcome_code IS NULL OR length(outcome_code) <= 100),
                    provider_receipt text NULL CHECK (provider_receipt IS NULL OR length(provider_receipt) <= 500),
                    started_at timestamp with time zone NOT NULL,
                    completed_at timestamp with time zone NULL,
                    CONSTRAINT provider_attempt_number_key UNIQUE (delivery_id, attempt_number)
                );
                CREATE TABLE {Schema}.inbox_counter_state (
                    person_id uuid PRIMARY KEY,
                    cursor bigint NOT NULL DEFAULT 0 CHECK (cursor >= 0),
                    total_count bigint NOT NULL DEFAULT 0 CHECK (total_count >= 0),
                    unread_count bigint NOT NULL DEFAULT 0 CHECK (unread_count >= 0)
                );
                CREATE TABLE {Schema}.inbox_counter_change (
                    person_id uuid NOT NULL REFERENCES {Schema}.inbox_counter_state(person_id),
                    cursor bigint NOT NULL CHECK (cursor > 0),
                    total_count bigint NOT NULL CHECK (total_count >= 0),
                    unread_count bigint NOT NULL CHECK (unread_count >= 0),
                    changed_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (person_id, cursor)
                );
                """),
        ]);
}
