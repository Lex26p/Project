using Dispatcher.Persistence;

namespace Dispatcher.Identity;

public static class IdentityMigrations
{
    public const string Owner = "identity";
    public const string Schema = "identity";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner, Schema, databaseRole,
        [new MigrationStep(1, "local accounts sessions administration settings and diagnostics", $"""
        CREATE TABLE {Schema}.access_scope (
            scope_id uuid PRIMARY KEY,
            name text NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 200),
            parent_scope_id uuid NULL REFERENCES {Schema}.access_scope(scope_id),
            CONSTRAINT identity_scope_name UNIQUE (name),
            CONSTRAINT identity_scope_not_self CHECK (parent_scope_id IS NULL OR parent_scope_id <> scope_id)
        );
        CREATE TABLE {Schema}.role (
            role_id uuid PRIMARY KEY,
            name text NOT NULL UNIQUE CHECK (length(trim(name)) BETWEEN 1 AND 200),
            version bigint NOT NULL CHECK (version > 0),
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.role_permission (
            role_id uuid NOT NULL REFERENCES {Schema}.role(role_id) ON DELETE CASCADE,
            permission_code text NOT NULL,
            scope_id uuid NULL REFERENCES {Schema}.access_scope(scope_id),
            PRIMARY KEY (role_id, permission_code)
        );
        CREATE TABLE {Schema}.account (
            account_id uuid PRIMARY KEY,
            subject_id uuid NOT NULL UNIQUE,
            workspace_account_id uuid NULL UNIQUE,
            primary_scope_id uuid NULL REFERENCES {Schema}.access_scope(scope_id),
            user_name text NOT NULL,
            normalized_user_name text NOT NULL UNIQUE,
            password_salt bytea NOT NULL CHECK (octet_length(password_salt) = 16),
            password_hash bytea NOT NULL CHECK (octet_length(password_hash) = 32),
            password_iterations integer NOT NULL CHECK (password_iterations > 0),
            enabled boolean NOT NULL,
            failed_login_count integer NOT NULL CHECK (failed_login_count >= 0),
            locked_until timestamp with time zone NULL,
            authorization_version bigint NOT NULL CHECK (authorization_version > 0),
            version bigint NOT NULL CHECK (version > 0),
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.identity_group (
            group_id uuid PRIMARY KEY,
            name text NOT NULL UNIQUE CHECK (length(trim(name)) BETWEEN 1 AND 200),
            version bigint NOT NULL CHECK (version > 0)
        );
        CREATE TABLE {Schema}.account_role (
            account_id uuid NOT NULL REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
            role_id uuid NOT NULL REFERENCES {Schema}.role(role_id) ON DELETE CASCADE,
            PRIMARY KEY (account_id, role_id)
        );
        CREATE TABLE {Schema}.group_member (
            group_id uuid NOT NULL REFERENCES {Schema}.identity_group(group_id) ON DELETE CASCADE,
            account_id uuid NOT NULL REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
            PRIMARY KEY (group_id, account_id)
        );
        CREATE TABLE {Schema}.group_role (
            group_id uuid NOT NULL REFERENCES {Schema}.identity_group(group_id) ON DELETE CASCADE,
            role_id uuid NOT NULL REFERENCES {Schema}.role(role_id) ON DELETE CASCADE,
            PRIMARY KEY (group_id, role_id)
        );
        CREATE TABLE {Schema}.account_permission_override (
            account_id uuid NOT NULL REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
            permission_code text NOT NULL,
            allowed boolean NOT NULL,
            PRIMARY KEY (account_id, permission_code)
        );
        CREATE TABLE {Schema}.production_session (
            session_id uuid PRIMARY KEY,
            account_id uuid NOT NULL REFERENCES {Schema}.account(account_id),
            access_token_hash bytea NOT NULL UNIQUE CHECK (octet_length(access_token_hash) = 32),
            refresh_token_hash bytea NOT NULL UNIQUE CHECK (octet_length(refresh_token_hash) = 32),
            authorization_version bigint NOT NULL CHECK (authorization_version > 0),
            issued_at timestamp with time zone NOT NULL,
            expires_at timestamp with time zone NOT NULL,
            refresh_expires_at timestamp with time zone NOT NULL,
            refreshed_at timestamp with time zone NULL,
            revoked_at timestamp with time zone NULL,
            CONSTRAINT identity_session_expiry CHECK (expires_at > issued_at AND refresh_expires_at > expires_at)
        );
        CREATE INDEX identity_session_account_idx ON {Schema}.production_session (account_id, revoked_at, expires_at);
        CREATE TABLE {Schema}.setting_definition (
            setting_key text PRIMARY KEY CHECK (length(trim(setting_key)) BETWEEN 1 AND 200),
            global_value text NOT NULL CHECK (length(global_value) <= 4000)
        );
        CREATE TABLE {Schema}.scope_setting (
            scope_id uuid NOT NULL REFERENCES {Schema}.access_scope(scope_id) ON DELETE CASCADE,
            setting_key text NOT NULL REFERENCES {Schema}.setting_definition(setting_key) ON DELETE CASCADE,
            setting_value text NOT NULL CHECK (length(setting_value) <= 4000),
            PRIMARY KEY (scope_id, setting_key)
        );
        CREATE TABLE {Schema}.group_setting (
            group_id uuid NOT NULL REFERENCES {Schema}.identity_group(group_id) ON DELETE CASCADE,
            setting_key text NOT NULL REFERENCES {Schema}.setting_definition(setting_key) ON DELETE CASCADE,
            setting_value text NOT NULL CHECK (length(setting_value) <= 4000),
            PRIMARY KEY (group_id, setting_key)
        );
        CREATE TABLE {Schema}.account_setting (
            account_id uuid NOT NULL REFERENCES {Schema}.account(account_id) ON DELETE CASCADE,
            setting_key text NOT NULL REFERENCES {Schema}.setting_definition(setting_key) ON DELETE CASCADE,
            setting_value text NOT NULL CHECK (length(setting_value) <= 4000),
            PRIMARY KEY (account_id, setting_key)
        );
        CREATE TABLE {Schema}.integration_diagnostic (
            integration_kind smallint PRIMARY KEY CHECK (integration_kind = 1),
            status smallint NOT NULL CHECK (status IN (1, 2, 3)),
            summary text NOT NULL CHECK (length(summary) <= 1000),
            secret_configured boolean NOT NULL,
            checked_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.mutation_audit (
            audit_id uuid PRIMARY KEY,
            session_id uuid NULL,
            subject_id uuid NULL,
            action text NOT NULL,
            target_kind text NOT NULL,
            target_id uuid NOT NULL,
            changed_at timestamp with time zone NOT NULL
        );
        """)]);
}
