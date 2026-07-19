using Dispatcher.Persistence;

namespace Dispatcher.Workspace;

public static class WorkspaceMigrations
{
    public const string Owner = "personal_workspace";
    public const string Schema = "personal_workspace";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner,
        Schema,
        databaseRole,
        [
            new MigrationStep(
                1,
                "accounts persons and personal workspace",
                $"""
                CREATE TABLE {Schema}.person (
                    person_id uuid PRIMARY KEY,
                    display_name text NOT NULL CHECK (length(trim(display_name)) > 0),
                    title text NULL,
                    availability smallint NOT NULL CHECK (availability BETWEEN 1 AND 3),
                    visibility smallint NOT NULL CHECK (visibility BETWEEN 1 AND 2)
                );
                CREATE TABLE {Schema}.account (
                    account_id uuid PRIMARY KEY,
                    subject_id uuid NOT NULL UNIQUE,
                    person_id uuid NOT NULL UNIQUE REFERENCES {Schema}.person(person_id)
                );
                CREATE TABLE {Schema}.account_membership (
                    account_id uuid NOT NULL REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
                    audience_kind smallint NOT NULL CHECK (audience_kind IN (2, 3)),
                    audience_key text NOT NULL CHECK (length(trim(audience_key)) > 0),
                    PRIMARY KEY (account_id, audience_kind, audience_key)
                );
                CREATE TABLE {Schema}.home_item (
                    item_id uuid PRIMARY KEY,
                    label text NOT NULL CHECK (length(trim(label)) > 0),
                    route text NOT NULL CHECK (route LIKE '/%'),
                    required_permission text NOT NULL,
                    audience_kind smallint NOT NULL CHECK (audience_kind BETWEEN 1 AND 3),
                    audience_key text NOT NULL CHECK (length(trim(audience_key)) > 0),
                    default_order integer NOT NULL
                );
                CREATE TABLE {Schema}.home_override (
                    account_id uuid NOT NULL REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
                    item_id uuid NOT NULL REFERENCES {Schema}.home_item(item_id) ON DELETE CASCADE,
                    hidden boolean NOT NULL,
                    sort_order integer NULL,
                    PRIMARY KEY (account_id, item_id)
                );
                CREATE TABLE {Schema}.preferences (
                    account_id uuid PRIMARY KEY REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
                    compact_navigation boolean NOT NULL,
                    start_route text NOT NULL CHECK (start_route LIKE '/%'),
                    version bigint NOT NULL CHECK (version > 0)
                );
                CREATE TABLE {Schema}.favorite (
                    account_id uuid NOT NULL REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
                    item_id uuid NOT NULL REFERENCES {Schema}.home_item(item_id) ON DELETE CASCADE,
                    created_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (account_id, item_id)
                );
                CREATE TABLE {Schema}.recent (
                    account_id uuid NOT NULL REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
                    item_id uuid NOT NULL REFERENCES {Schema}.home_item(item_id) ON DELETE CASCADE,
                    opened_at timestamp with time zone NOT NULL,
                    PRIMARY KEY (account_id, item_id)
                );
                CREATE TABLE {Schema}.preference_audit (
                    audit_id uuid PRIMARY KEY,
                    account_id uuid NOT NULL REFERENCES {Schema}.account(account_id),
                    session_id uuid NOT NULL,
                    subject_id uuid NOT NULL,
                    action text NOT NULL,
                    changed_at timestamp with time zone NOT NULL
                );
                CREATE INDEX person_display_name_idx ON {Schema}.person (lower(display_name));
                CREATE INDEX home_item_label_idx ON {Schema}.home_item (lower(label));
                CREATE INDEX recent_account_time_idx ON {Schema}.recent (account_id, opened_at DESC);
                """),
        ]);
}
