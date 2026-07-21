using Dispatcher.Persistence;

namespace Dispatcher.Terminals;

public static class TerminalMigrations
{
    public const string Owner = "terminals";
    public const string Schema = "terminals";

    public static ModuleMigrationPlan CreatePlan(string databaseRole) => new(
        Owner, Schema, databaseRole,
        [new MigrationStep(1, "terminal enrollment identity fleet profile content and presence", $"""
        CREATE TABLE {Schema}.profile (
            profile_id uuid PRIMARY KEY,
            name text NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 200),
            content_kind smallint NULL CHECK (content_kind IS NULL OR content_kind IN (1, 2)),
            content_id uuid NULL,
            version bigint NOT NULL CHECK (version > 0),
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL,
            CONSTRAINT profile_content_pair CHECK (
                (content_kind IS NULL AND content_id IS NULL) OR
                (content_kind IS NOT NULL AND content_id IS NOT NULL))
        );
        CREATE TABLE {Schema}.terminal (
            terminal_id uuid PRIMARY KEY,
            label text NOT NULL CHECK (length(trim(label)) BETWEEN 1 AND 200),
            state smallint NOT NULL CHECK (state IN (1, 2, 3, 4)),
            profile_id uuid NULL REFERENCES {Schema}.profile(profile_id),
            version bigint NOT NULL CHECK (version > 0),
            last_seen_at timestamp with time zone NULL,
            created_at timestamp with time zone NOT NULL,
            updated_at timestamp with time zone NOT NULL
        );
        CREATE TABLE {Schema}.enrollment (
            enrollment_id uuid PRIMARY KEY,
            terminal_id uuid NOT NULL UNIQUE REFERENCES {Schema}.terminal(terminal_id),
            challenge_hash bytea NOT NULL CHECK (octet_length(challenge_hash) = 32),
            state smallint NOT NULL CHECK (state IN (1, 2, 3, 4)),
            expires_at timestamp with time zone NOT NULL,
            approved_session_id uuid NULL,
            approved_subject_id uuid NULL,
            approved_at timestamp with time zone NULL,
            consumed_at timestamp with time zone NULL,
            created_at timestamp with time zone NOT NULL,
            CONSTRAINT enrollment_approval_group CHECK (
                (approved_at IS NULL AND approved_session_id IS NULL AND approved_subject_id IS NULL) OR
                (approved_at IS NOT NULL AND approved_session_id IS NOT NULL AND approved_subject_id IS NOT NULL))
        );
        CREATE TABLE {Schema}.device_identity (
            device_identity_id uuid PRIMARY KEY,
            terminal_id uuid NOT NULL UNIQUE REFERENCES {Schema}.terminal(terminal_id),
            credential_hash bytea NOT NULL CHECK (octet_length(credential_hash) = 32),
            issued_at timestamp with time zone NOT NULL,
            expires_at timestamp with time zone NOT NULL,
            revoked_at timestamp with time zone NULL
        );
        CREATE UNIQUE INDEX terminal_credential_hash_once ON {Schema}.device_identity (credential_hash);
        CREATE TABLE {Schema}.mutation_audit (
            audit_id uuid PRIMARY KEY,
            terminal_id uuid NULL,
            profile_id uuid NULL,
            session_id uuid NOT NULL,
            subject_id uuid NOT NULL,
            action text NOT NULL,
            resulting_version bigint NOT NULL CHECK (resulting_version > 0),
            changed_at timestamp with time zone NOT NULL,
            CONSTRAINT terminal_audit_target CHECK (terminal_id IS NOT NULL OR profile_id IS NOT NULL)
        );
        CREATE INDEX terminal_fleet_idx ON {Schema}.terminal (state, label, terminal_id);
        """)]);
}
