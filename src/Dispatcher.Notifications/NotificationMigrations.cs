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
        ]);
}
