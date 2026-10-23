-- =====================================================================
-- NEXO: схема базы данных PostgreSQL 16
-- Документ-источник: RU.00000000.00001-01 13 02 (Описание модели данных)
-- Файл сгенерирован из model/schema_model.py. Вручную не редактировать.
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS citext;
CREATE SCHEMA IF NOT EXISTS nexo;
SET search_path = nexo, public;

-- ---------- Перечисляемые типы ----------
CREATE TYPE user_role AS ENUM ('USER', 'MODERATOR', 'ADMIN');
CREATE TYPE account_state AS ENUM ('ACTIVE', 'SUSPENDED', 'DELETED');
CREATE TYPE visibility AS ENUM ('EVERYONE', 'CONTACTS', 'NOBODY');
CREATE TYPE audience AS ENUM ('EVERYONE', 'CONTACTS');
CREATE TYPE device_platform AS ENUM ('WINDOWS', 'MACOS', 'LINUX', 'ANDROID', 'IOS', 'WEB');
CREATE TYPE push_provider AS ENUM ('NONE', 'FCM', 'RUSTORE', 'APNS', 'WEBPUSH');
CREATE TYPE session_revoke_reason AS ENUM ('LOGOUT', 'ROTATED', 'REUSE_DETECTED', 'DEVICE_REVOKED', 'PASSWORD_CHANGED', 'ADMIN');
CREATE TYPE auth_event_kind AS ENUM ('LOGIN_OK', 'LOGIN_FAILED', 'LOGIN_APPROVAL_REQUESTED', 'LOGIN_APPROVED', 'LOGIN_DENIED', 'DEVICE_REVOKED', 'LOGOUT', 'PASSWORD_CHANGED', 'RECOVERY_KEY_USED', 'RECOVERY_KEY_REGENERATED', 'TOTP_ENABLED', 'TOTP_DISABLED', 'ACCOUNT_LOCKED');
CREATE TYPE approval_status AS ENUM ('PENDING', 'APPROVED', 'DENIED', 'EXPIRED');
CREATE TYPE contact_status AS ENUM ('PENDING', 'ACCEPTED', 'DECLINED');
CREATE TYPE chat_kind AS ENUM ('DIRECT', 'GROUP', 'CHANNEL', 'SECRET');
CREATE TYPE member_role AS ENUM ('OWNER', 'ADMIN', 'MEMBER', 'SUBSCRIBER');
CREATE TYPE secret_state AS ENUM ('PENDING', 'ACTIVE', 'CLOSED');
CREATE TYPE message_kind AS ENUM ('TEXT', 'MEDIA', 'VOICE', 'VIDEO_NOTE', 'SYSTEM', 'SECRET');
CREATE TYPE update_kind AS ENUM ('MESSAGE_NEW', 'MESSAGE_EDITED', 'MESSAGE_DELETED', 'READ_STATE', 'CHAT_UPDATED', 'MEMBER_UPDATED', 'FOLDER_UPDATED', 'DRAFT_UPDATED', 'SETTINGS_UPDATED', 'DEVICE_UPDATED', 'FILE_READY', 'CONTACT_UPDATED', 'REACTION_UPDATED');
CREATE TYPE storage_tier AS ENUM ('HOT', 'COLD', 'RESTORING');
CREATE TYPE compression_kind AS ENUM ('NONE', 'ZSTD');
CREATE TYPE media_kind AS ENUM ('IMAGE', 'VIDEO', 'AUDIO', 'VOICE', 'VIDEO_NOTE', 'FILE', 'THEME', 'AVATAR');
CREATE TYPE file_state AS ENUM ('UPLOADING', 'PROCESSING', 'READY', 'FAILED', 'DELETED');
CREATE TYPE variant_kind AS ENUM ('THUMBNAIL', 'PREVIEW', 'OPTIMIZED', 'WAVEFORM');
CREATE TYPE upload_state AS ENUM ('OPEN', 'COMPLETING', 'COMPLETED', 'ABORTED', 'EXPIRED');
CREATE TYPE call_kind AS ENUM ('AUDIO', 'VIDEO');
CREATE TYPE call_state AS ENUM ('RINGING', 'ACTIVE', 'ENDED', 'MISSED', 'DECLINED', 'FAILED');
CREATE TYPE status_kind AS ENUM ('AUTO', 'DND', 'AWAY', 'BUSY');
CREATE TYPE report_target AS ENUM ('USER', 'CHAT', 'MESSAGE', 'FILE');
CREATE TYPE report_reason AS ENUM ('SPAM', 'ILLEGAL', 'ABUSE', 'VIOLENCE', 'FRAUD', 'OTHER');
CREATE TYPE report_status AS ENUM ('OPEN', 'IN_REVIEW', 'ACTIONED', 'REJECTED');
CREATE TYPE sanction_kind AS ENUM ('WARNING', 'MUTE', 'SUSPEND', 'BAN');
CREATE TYPE term_category AS ENUM ('PROFANITY', 'EXTREMISM', 'CUSTOM');
CREATE TYPE legal_status AS ENUM ('RECEIVED', 'IN_PROGRESS', 'COMPLETED', 'REJECTED');
CREATE TYPE legal_doc_kind AS ENUM ('TERMS', 'PRIVACY', 'PD_CONSENT', 'COMMUNITY_RULES');
CREATE TYPE job_state AS ENUM ('QUEUED', 'RUNNING', 'DONE', 'FAILED', 'DEAD');

-- ---------- Таблицы ----------
-- Учётные записи, устройства и сессии
CREATE TABLE users (
    id uuid NOT NULL,
    username citext NOT NULL UNIQUE CHECK (username ~ '^[a-z][a-z0-9_]{2,31}$'),
    display_name varchar(64) NOT NULL,
    bio varchar(300),
    avatar_file_id uuid,
    role user_role NOT NULL DEFAULT 'USER',
    state account_state NOT NULL DEFAULT 'ACTIVE',
    locale varchar(10) NOT NULL DEFAULT 'ru',
    kdf_salt bytea NOT NULL,
    kdf_params jsonb NOT NULL,
    auth_verifier text NOT NULL,
    recovery_verifier text NOT NULL,
    recovery_set_at timestamptz NOT NULL DEFAULT now(),
    totp_secret_enc bytea,
    totp_enabled boolean NOT NULL DEFAULT false,
    failed_logins integer NOT NULL DEFAULT 0,
    locked_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    PRIMARY KEY (id)
);

-- Учётные записи, устройства и сессии
CREATE TABLE privacy_settings (
    user_id uuid NOT NULL,
    searchable boolean NOT NULL DEFAULT true,
    last_seen_vis visibility NOT NULL DEFAULT 'CONTACTS',
    status_vis visibility NOT NULL DEFAULT 'CONTACTS',
    avatar_vis visibility NOT NULL DEFAULT 'EVERYONE',
    bio_vis visibility NOT NULL DEFAULT 'EVERYONE',
    message_from audience NOT NULL DEFAULT 'EVERYONE',
    group_invite_from audience NOT NULL DEFAULT 'CONTACTS',
    call_from audience NOT NULL DEFAULT 'CONTACTS',
    read_receipts boolean NOT NULL DEFAULT true,
    typing_indicator boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id)
);

-- Учётные записи, устройства и сессии
CREATE TABLE devices (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    name varchar(80) NOT NULL,
    platform device_platform NOT NULL,
    app_version varchar(32),
    os_version varchar(64),
    device_pubkey bytea NOT NULL,
    push_provider push_provider NOT NULL DEFAULT 'NONE',
    push_token_enc bytea,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz,
    last_ip inet,
    revoked_at timestamptz,
    PRIMARY KEY (id)
);

-- Учётные записи, устройства и сессии
CREATE TABLE sessions (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    device_id uuid NOT NULL,
    family_id uuid NOT NULL,
    refresh_hash bytea NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    rotated_at timestamptz,
    revoked_at timestamptz,
    revoke_reason session_revoke_reason,
    PRIMARY KEY (id)
);

-- Учётные записи, устройства и сессии
CREATE TABLE auth_events (
    id bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    user_id uuid NOT NULL,
    device_id uuid,
    kind auth_event_kind NOT NULL,
    ip inet,
    user_agent varchar(300),
    details jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- Учётные записи, устройства и сессии
CREATE TABLE login_approvals (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    requester_pubkey bytea NOT NULL,
    requester_name varchar(80) NOT NULL,
    requester_platform device_platform NOT NULL,
    requester_ip inet,
    code varchar(8) NOT NULL,
    status approval_status NOT NULL DEFAULT 'PENDING',
    approver_device_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    resolved_at timestamptz,
    PRIMARY KEY (id)
);

-- Учётные записи, устройства и сессии
CREATE TABLE backup_codes (
    id bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    user_id uuid NOT NULL,
    code_hash text NOT NULL,
    used_at timestamptz,
    PRIMARY KEY (id)
);

-- Учётные записи, устройства и сессии
CREATE TABLE consent_records (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    document_kind legal_doc_kind NOT NULL,
    version varchar(16) NOT NULL,
    accepted_at timestamptz NOT NULL DEFAULT now(),
    ip inet,
    user_agent varchar(300),
    device_id uuid,
    PRIMARY KEY (id)
);

-- Учётные записи, устройства и сессии
CREATE TABLE user_settings (
    user_id uuid NOT NULL,
    key varchar(64) NOT NULL,
    value jsonb NOT NULL,
    version bigint NOT NULL DEFAULT 1,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, key)
);

-- Учётные записи, устройства и сессии
CREATE TABLE user_statuses (
    user_id uuid NOT NULL,
    kind status_kind NOT NULL DEFAULT 'AUTO',
    custom_text varchar(70),
    custom_emoji varchar(16),
    expires_at timestamptz,
    invisible boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id)
);

-- Контакты, приватность и ключи
CREATE TABLE contact_links (
    id uuid NOT NULL,
    requester_id uuid NOT NULL,
    addressee_id uuid NOT NULL,
    status contact_status NOT NULL DEFAULT 'PENDING',
    created_at timestamptz NOT NULL DEFAULT now(),
    responded_at timestamptz,
    PRIMARY KEY (id),
    CONSTRAINT ck_contact_links_1 CHECK (requester_id <> addressee_id)
);

-- Контакты, приватность и ключи
CREATE TABLE user_blocks (
    blocker_id uuid NOT NULL,
    blocked_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (blocker_id, blocked_id),
    CONSTRAINT ck_user_blocks_1 CHECK (blocker_id <> blocked_id)
);

-- Контакты, приватность и ключи
CREATE TABLE device_key_bundles (
    device_id uuid NOT NULL,
    identity_pub bytea NOT NULL,
    spk_id integer NOT NULL,
    spk_pub bytea NOT NULL,
    spk_sig bytea NOT NULL,
    spk_created_at timestamptz NOT NULL DEFAULT now(),
    kem_pub bytea,
    kem_sig bytea,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (device_id)
);

-- Контакты, приватность и ключи
CREATE TABLE one_time_prekeys (
    device_id uuid NOT NULL,
    key_id integer NOT NULL,
    pub bytea NOT NULL,
    consumed_at timestamptz,
    consumed_by uuid,
    PRIMARY KEY (device_id, key_id)
);

-- Чаты, участники и организация списка
CREATE TABLE chats (
    id uuid NOT NULL,
    kind chat_kind NOT NULL,
    title varchar(128),
    description varchar(500),
    avatar_file_id uuid,
    public_handle citext UNIQUE,
    owner_id uuid,
    default_ttl_seconds integer CHECK (default_ttl_seconds IS NULL OR default_ttl_seconds > 0),
    slow_mode_seconds integer,
    top_seq bigint NOT NULL DEFAULT 0,
    member_count integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    PRIMARY KEY (id)
);

-- Чаты, участники и организация списка
CREATE TABLE direct_pairs (
    chat_id uuid NOT NULL,
    user_low uuid NOT NULL,
    user_high uuid NOT NULL,
    PRIMARY KEY (chat_id),
    CONSTRAINT ux_direct_pair UNIQUE (user_low, user_high),
    CONSTRAINT ck_direct_pairs_1 CHECK (user_low < user_high)
);

-- Чаты, участники и организация списка
CREATE TABLE chat_members (
    chat_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role member_role NOT NULL DEFAULT 'MEMBER',
    permissions bigint NOT NULL DEFAULT 0,
    joined_at timestamptz NOT NULL DEFAULT now(),
    left_at timestamptz,
    invited_by uuid,
    muted_until timestamptz,
    pinned_position smallint,
    archived boolean NOT NULL DEFAULT false,
    last_read_seq bigint NOT NULL DEFAULT 0,
    last_delivered_seq bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (chat_id, user_id)
);

-- Чаты, участники и организация списка
CREATE TABLE invite_links (
    id uuid NOT NULL,
    chat_id uuid NOT NULL,
    token_hash bytea NOT NULL UNIQUE,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz,
    max_uses integer,
    uses integer NOT NULL DEFAULT 0,
    requires_approval boolean NOT NULL DEFAULT false,
    revoked_at timestamptz,
    PRIMARY KEY (id)
);

-- Чаты, участники и организация списка
CREATE TABLE chat_folders (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    name varchar(32) NOT NULL,
    icon varchar(32),
    position smallint NOT NULL,
    filter_flags integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- Чаты, участники и организация списка
CREATE TABLE folder_chats (
    folder_id uuid NOT NULL,
    chat_id uuid NOT NULL,
    position smallint,
    PRIMARY KEY (folder_id, chat_id)
);

-- Чаты, участники и организация списка
CREATE TABLE drafts (
    user_id uuid NOT NULL,
    chat_id uuid NOT NULL,
    body text NOT NULL,
    reply_to_id uuid,
    version bigint NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, chat_id)
);

-- Чаты, участники и организация списка
CREATE TABLE secret_chats (
    chat_id uuid NOT NULL,
    initiator_user_id uuid NOT NULL,
    initiator_device_id uuid NOT NULL,
    peer_user_id uuid NOT NULL,
    peer_device_id uuid NOT NULL,
    state secret_state NOT NULL DEFAULT 'PENDING',
    x3dh_init bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    closed_at timestamptz,
    PRIMARY KEY (chat_id)
);

-- Сообщения и журнал синхронизации
CREATE TABLE messages (
    id uuid NOT NULL,
    chat_id uuid NOT NULL,
    seq bigint NOT NULL,
    sender_id uuid NOT NULL,
    sender_device_id uuid,
    client_msg_id uuid NOT NULL,
    kind message_kind NOT NULL,
    body text CHECK (body IS NULL OR char_length(body) <= 4096),
    entities jsonb,
    ciphertext bytea,
    reply_to_id uuid,
    forward_origin jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    edited_at timestamptz,
    deleted_at timestamptz,
    ttl_seconds integer,
    first_read_at timestamptz,
    expires_at timestamptz,
    pinned_at timestamptz,
    PRIMARY KEY (id),
    CONSTRAINT ux_messages_seq UNIQUE (chat_id, seq),
    CONSTRAINT ux_messages_client UNIQUE (chat_id, sender_id, client_msg_id),
    CONSTRAINT ck_messages_1 CHECK ((kind = 'SECRET') = (ciphertext IS NOT NULL)),
    CONSTRAINT ck_messages_2 CHECK (kind <> 'SECRET' OR body IS NULL)
);

-- Сообщения и журнал синхронизации
CREATE TABLE message_attachments (
    message_id uuid NOT NULL,
    ordinal smallint NOT NULL,
    file_id uuid NOT NULL,
    caption varchar(1024),
    PRIMARY KEY (message_id, ordinal)
);

-- Сообщения и журнал синхронизации
CREATE TABLE message_reactions (
    message_id uuid NOT NULL,
    user_id uuid NOT NULL,
    emoji varchar(16) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (message_id, user_id, emoji)
);

-- Сообщения и журнал синхронизации
CREATE TABLE user_update_counters (
    user_id uuid NOT NULL,
    last_seq bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id)
);

-- Сообщения и журнал синхронизации
CREATE TABLE user_updates (
    user_id uuid NOT NULL,
    update_seq bigint NOT NULL,
    kind update_kind NOT NULL,
    payload bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, update_seq)
);

-- Медиафайлы и хранилище
CREATE TABLE media_blobs (
    id uuid NOT NULL,
    sha256 bytea NOT NULL,
    size_bytes bigint NOT NULL,
    stored_size_bytes bigint,
    compression compression_kind NOT NULL DEFAULT 'NONE',
    is_encrypted boolean NOT NULL DEFAULT false,
    storage_tier storage_tier NOT NULL DEFAULT 'HOT',
    bucket varchar(63) NOT NULL,
    storage_key text NOT NULL,
    refcount integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_accessed_at timestamptz NOT NULL DEFAULT now(),
    tier_changed_at timestamptz,
    restore_requested_at timestamptz,
    PRIMARY KEY (id),
    CONSTRAINT ux_blobs_key UNIQUE (bucket, storage_key)
);

-- Медиафайлы и хранилище
CREATE TABLE media_files (
    id uuid NOT NULL,
    owner_id uuid NOT NULL,
    blob_id uuid,
    kind media_kind NOT NULL,
    file_name varchar(255),
    mime varchar(127),
    size_bytes bigint NOT NULL,
    width integer,
    height integer,
    duration_ms integer,
    is_encrypted boolean NOT NULL DEFAULT false,
    state file_state NOT NULL DEFAULT 'UPLOADING',
    failure_reason varchar(200),
    created_at timestamptz NOT NULL DEFAULT now(),
    ready_at timestamptz,
    deleted_at timestamptz,
    PRIMARY KEY (id)
);

-- Медиафайлы и хранилище
CREATE TABLE media_variants (
    file_id uuid NOT NULL,
    variant variant_kind NOT NULL,
    blob_id uuid NOT NULL,
    width integer,
    height integer,
    PRIMARY KEY (file_id, variant)
);

-- Медиафайлы и хранилище
CREATE TABLE upload_sessions (
    id uuid NOT NULL,
    owner_id uuid NOT NULL,
    file_id uuid NOT NULL,
    declared_size bigint NOT NULL,
    part_size integer NOT NULL,
    parts_total integer NOT NULL,
    parts_completed integer[] NOT NULL DEFAULT '{}',
    s3_upload_id varchar(255),
    state upload_state NOT NULL DEFAULT 'OPEN',
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- Медиафайлы и хранилище
CREATE TABLE user_quotas (
    user_id uuid NOT NULL,
    used_bytes bigint NOT NULL DEFAULT 0,
    limit_bytes bigint NOT NULL DEFAULT 5368709120,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id)
);

-- Звонки
CREATE TABLE calls (
    id uuid NOT NULL,
    chat_id uuid NOT NULL,
    initiator_id uuid NOT NULL,
    kind call_kind NOT NULL,
    state call_state NOT NULL DEFAULT 'RINGING',
    sfu_room varchar(128) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    answered_at timestamptz,
    ended_at timestamptz,
    end_reason varchar(32),
    PRIMARY KEY (id)
);

-- Звонки
CREATE TABLE call_participants (
    call_id uuid NOT NULL,
    user_id uuid NOT NULL,
    device_id uuid NOT NULL,
    joined_at timestamptz NOT NULL DEFAULT now(),
    left_at timestamptz,
    video_enabled boolean NOT NULL DEFAULT false,
    screen_sharing boolean NOT NULL DEFAULT false,
    PRIMARY KEY (call_id, user_id, device_id)
);

-- Модерация, аудит и фоновые задания
CREATE TABLE reports (
    id uuid NOT NULL,
    reporter_id uuid NOT NULL,
    target_kind report_target NOT NULL,
    target_id uuid NOT NULL,
    reason report_reason NOT NULL,
    details varchar(1000),
    evidence jsonb,
    status report_status NOT NULL DEFAULT 'OPEN',
    created_at timestamptz NOT NULL DEFAULT now(),
    resolved_by uuid,
    resolved_at timestamptz,
    resolution_note varchar(1000),
    PRIMARY KEY (id)
);

-- Модерация, аудит и фоновые задания
CREATE TABLE sanctions (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    kind sanction_kind NOT NULL,
    reason text NOT NULL,
    report_id uuid,
    issued_by uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz,
    revoked_at timestamptz,
    PRIMARY KEY (id)
);

-- Модерация, аудит и фоновые задания
CREATE TABLE moderation_terms (
    id uuid NOT NULL,
    category term_category NOT NULL,
    pattern text NOT NULL,
    is_regex boolean NOT NULL DEFAULT false,
    active boolean NOT NULL DEFAULT true,
    created_by uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    CONSTRAINT ux_terms UNIQUE (category, pattern)
);

-- Модерация, аудит и фоновые задания
CREATE TABLE legal_requests (
    id uuid NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    authority varchar(200) NOT NULL,
    basis text NOT NULL,
    scope text NOT NULL,
    status legal_status NOT NULL DEFAULT 'RECEIVED',
    handled_by uuid,
    notes text,
    completed_at timestamptz,
    PRIMARY KEY (id)
);

-- Модерация, аудит и фоновые задания
CREATE TABLE audit_log (
    id bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    actor_id uuid,
    action varchar(64) NOT NULL,
    target_kind varchar(32),
    target_id uuid,
    ip inet,
    details jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- Модерация, аудит и фоновые задания
CREATE TABLE background_jobs (
    id uuid NOT NULL,
    kind varchar(48) NOT NULL,
    payload jsonb NOT NULL,
    state job_state NOT NULL DEFAULT 'QUEUED',
    run_at timestamptz NOT NULL DEFAULT now(),
    attempts integer NOT NULL DEFAULT 0,
    locked_by varchar(64),
    locked_until timestamptz,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- ---------- Внешние ключи ----------
ALTER TABLE users ADD CONSTRAINT fk_users_avatar_file_id FOREIGN KEY (avatar_file_id) REFERENCES media_files (id) ON DELETE SET NULL;
ALTER TABLE privacy_settings ADD CONSTRAINT fk_privacy_settings_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE devices ADD CONSTRAINT fk_devices_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE sessions ADD CONSTRAINT fk_sessions_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE sessions ADD CONSTRAINT fk_sessions_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE NO ACTION;
ALTER TABLE auth_events ADD CONSTRAINT fk_auth_events_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE auth_events ADD CONSTRAINT fk_auth_events_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE SET NULL;
ALTER TABLE login_approvals ADD CONSTRAINT fk_login_approvals_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE login_approvals ADD CONSTRAINT fk_login_approvals_approver_device_id FOREIGN KEY (approver_device_id) REFERENCES devices (id) ON DELETE SET NULL;
ALTER TABLE backup_codes ADD CONSTRAINT fk_backup_codes_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE consent_records ADD CONSTRAINT fk_consent_records_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE consent_records ADD CONSTRAINT fk_consent_records_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE SET NULL;
ALTER TABLE user_settings ADD CONSTRAINT fk_user_settings_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE user_statuses ADD CONSTRAINT fk_user_statuses_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE contact_links ADD CONSTRAINT fk_contact_links_requester_id FOREIGN KEY (requester_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE contact_links ADD CONSTRAINT fk_contact_links_addressee_id FOREIGN KEY (addressee_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE user_blocks ADD CONSTRAINT fk_user_blocks_blocker_id FOREIGN KEY (blocker_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE user_blocks ADD CONSTRAINT fk_user_blocks_blocked_id FOREIGN KEY (blocked_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE device_key_bundles ADD CONSTRAINT fk_device_key_bundles_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE;
ALTER TABLE one_time_prekeys ADD CONSTRAINT fk_one_time_prekeys_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE;
ALTER TABLE one_time_prekeys ADD CONSTRAINT fk_one_time_prekeys_consumed_by FOREIGN KEY (consumed_by) REFERENCES users (id) ON DELETE SET NULL;
ALTER TABLE chats ADD CONSTRAINT fk_chats_avatar_file_id FOREIGN KEY (avatar_file_id) REFERENCES media_files (id) ON DELETE SET NULL;
ALTER TABLE chats ADD CONSTRAINT fk_chats_owner_id FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE SET NULL;
ALTER TABLE direct_pairs ADD CONSTRAINT fk_direct_pairs_chat_id FOREIGN KEY (chat_id) REFERENCES chats (id) ON DELETE CASCADE;
ALTER TABLE direct_pairs ADD CONSTRAINT fk_direct_pairs_user_low FOREIGN KEY (user_low) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE direct_pairs ADD CONSTRAINT fk_direct_pairs_user_high FOREIGN KEY (user_high) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE chat_members ADD CONSTRAINT fk_chat_members_chat_id FOREIGN KEY (chat_id) REFERENCES chats (id) ON DELETE CASCADE;
ALTER TABLE chat_members ADD CONSTRAINT fk_chat_members_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE chat_members ADD CONSTRAINT fk_chat_members_invited_by FOREIGN KEY (invited_by) REFERENCES users (id) ON DELETE SET NULL;
ALTER TABLE invite_links ADD CONSTRAINT fk_invite_links_chat_id FOREIGN KEY (chat_id) REFERENCES chats (id) ON DELETE NO ACTION;
ALTER TABLE invite_links ADD CONSTRAINT fk_invite_links_created_by FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE chat_folders ADD CONSTRAINT fk_chat_folders_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE folder_chats ADD CONSTRAINT fk_folder_chats_folder_id FOREIGN KEY (folder_id) REFERENCES chat_folders (id) ON DELETE CASCADE;
ALTER TABLE folder_chats ADD CONSTRAINT fk_folder_chats_chat_id FOREIGN KEY (chat_id) REFERENCES chats (id) ON DELETE CASCADE;
ALTER TABLE drafts ADD CONSTRAINT fk_drafts_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE drafts ADD CONSTRAINT fk_drafts_chat_id FOREIGN KEY (chat_id) REFERENCES chats (id) ON DELETE CASCADE;
ALTER TABLE drafts ADD CONSTRAINT fk_drafts_reply_to_id FOREIGN KEY (reply_to_id) REFERENCES messages (id) ON DELETE SET NULL;
ALTER TABLE secret_chats ADD CONSTRAINT fk_secret_chats_chat_id FOREIGN KEY (chat_id) REFERENCES chats (id) ON DELETE CASCADE;
ALTER TABLE secret_chats ADD CONSTRAINT fk_secret_chats_initiator_user_id FOREIGN KEY (initiator_user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE secret_chats ADD CONSTRAINT fk_secret_chats_initiator_device_id FOREIGN KEY (initiator_device_id) REFERENCES devices (id) ON DELETE NO ACTION;
ALTER TABLE secret_chats ADD CONSTRAINT fk_secret_chats_peer_user_id FOREIGN KEY (peer_user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE secret_chats ADD CONSTRAINT fk_secret_chats_peer_device_id FOREIGN KEY (peer_device_id) REFERENCES devices (id) ON DELETE NO ACTION;
ALTER TABLE messages ADD CONSTRAINT fk_messages_chat_id FOREIGN KEY (chat_id) REFERENCES chats (id) ON DELETE NO ACTION;
ALTER TABLE messages ADD CONSTRAINT fk_messages_sender_id FOREIGN KEY (sender_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE messages ADD CONSTRAINT fk_messages_sender_device_id FOREIGN KEY (sender_device_id) REFERENCES devices (id) ON DELETE SET NULL;
ALTER TABLE messages ADD CONSTRAINT fk_messages_reply_to_id FOREIGN KEY (reply_to_id) REFERENCES messages (id) ON DELETE SET NULL;
ALTER TABLE message_attachments ADD CONSTRAINT fk_message_attachments_message_id FOREIGN KEY (message_id) REFERENCES messages (id) ON DELETE CASCADE;
ALTER TABLE message_attachments ADD CONSTRAINT fk_message_attachments_file_id FOREIGN KEY (file_id) REFERENCES media_files (id) ON DELETE NO ACTION;
ALTER TABLE message_reactions ADD CONSTRAINT fk_message_reactions_message_id FOREIGN KEY (message_id) REFERENCES messages (id) ON DELETE CASCADE;
ALTER TABLE message_reactions ADD CONSTRAINT fk_message_reactions_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE user_update_counters ADD CONSTRAINT fk_user_update_counters_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE user_updates ADD CONSTRAINT fk_user_updates_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE media_files ADD CONSTRAINT fk_media_files_owner_id FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE media_files ADD CONSTRAINT fk_media_files_blob_id FOREIGN KEY (blob_id) REFERENCES media_blobs (id) ON DELETE SET NULL;
ALTER TABLE media_variants ADD CONSTRAINT fk_media_variants_file_id FOREIGN KEY (file_id) REFERENCES media_files (id) ON DELETE CASCADE;
ALTER TABLE media_variants ADD CONSTRAINT fk_media_variants_blob_id FOREIGN KEY (blob_id) REFERENCES media_blobs (id) ON DELETE NO ACTION;
ALTER TABLE upload_sessions ADD CONSTRAINT fk_upload_sessions_owner_id FOREIGN KEY (owner_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE upload_sessions ADD CONSTRAINT fk_upload_sessions_file_id FOREIGN KEY (file_id) REFERENCES media_files (id) ON DELETE NO ACTION;
ALTER TABLE user_quotas ADD CONSTRAINT fk_user_quotas_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE calls ADD CONSTRAINT fk_calls_chat_id FOREIGN KEY (chat_id) REFERENCES chats (id) ON DELETE NO ACTION;
ALTER TABLE calls ADD CONSTRAINT fk_calls_initiator_id FOREIGN KEY (initiator_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE call_participants ADD CONSTRAINT fk_call_participants_call_id FOREIGN KEY (call_id) REFERENCES calls (id) ON DELETE CASCADE;
ALTER TABLE call_participants ADD CONSTRAINT fk_call_participants_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
ALTER TABLE call_participants ADD CONSTRAINT fk_call_participants_device_id FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE;
ALTER TABLE reports ADD CONSTRAINT fk_reports_reporter_id FOREIGN KEY (reporter_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE reports ADD CONSTRAINT fk_reports_resolved_by FOREIGN KEY (resolved_by) REFERENCES users (id) ON DELETE SET NULL;
ALTER TABLE sanctions ADD CONSTRAINT fk_sanctions_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE NO ACTION;
ALTER TABLE sanctions ADD CONSTRAINT fk_sanctions_report_id FOREIGN KEY (report_id) REFERENCES reports (id) ON DELETE SET NULL;
ALTER TABLE sanctions ADD CONSTRAINT fk_sanctions_issued_by FOREIGN KEY (issued_by) REFERENCES users (id) ON DELETE SET NULL;
ALTER TABLE moderation_terms ADD CONSTRAINT fk_moderation_terms_created_by FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE SET NULL;
ALTER TABLE legal_requests ADD CONSTRAINT fk_legal_requests_handled_by FOREIGN KEY (handled_by) REFERENCES users (id) ON DELETE SET NULL;
ALTER TABLE audit_log ADD CONSTRAINT fk_audit_log_actor_id FOREIGN KEY (actor_id) REFERENCES users (id) ON DELETE SET NULL;

-- ---------- Индексы ----------
CREATE INDEX ix_devices_user ON devices (user_id) WHERE revoked_at IS NULL;
CREATE INDEX ix_sessions_family ON sessions (family_id);
CREATE INDEX ix_auth_events_user ON auth_events (user_id, created_at DESC);
CREATE INDEX ix_login_approvals_user ON login_approvals (user_id, status);
CREATE INDEX ix_consent_user ON consent_records (user_id, document_kind, accepted_at DESC);
CREATE UNIQUE INDEX ux_contact_pair ON contact_links (LEAST(requester_id, addressee_id), GREATEST(requester_id, addressee_id));
CREATE INDEX ix_otpk_free ON one_time_prekeys (device_id) WHERE consumed_at IS NULL;
CREATE INDEX ix_members_user ON chat_members (user_id) WHERE left_at IS NULL;
CREATE INDEX ix_folders_user ON chat_folders (user_id, position);
CREATE INDEX ix_messages_expires ON messages (expires_at) WHERE expires_at IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX ix_messages_fts ON messages USING gin ((to_tsvector('russian', coalesce(body, '')))) WHERE kind <> 'SECRET';
CREATE INDEX ix_updates_created ON user_updates (created_at);
CREATE INDEX ix_blobs_sha ON media_blobs (sha256) WHERE is_encrypted = false;
CREATE INDEX ix_blobs_cold_candidates ON media_blobs (last_accessed_at) WHERE storage_tier = 'HOT';
CREATE INDEX ix_files_owner ON media_files (owner_id, created_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX ix_calls_chat ON calls (chat_id, created_at DESC);
CREATE INDEX ix_reports_status ON reports (status, created_at);
CREATE INDEX ix_audit_actor ON audit_log (actor_id, created_at DESC);
CREATE INDEX ix_jobs_ready ON background_jobs (run_at) WHERE state = 'QUEUED';

-- ---------- Комментарии ----------
COMMENT ON TABLE users IS 'Учётная запись пользователя.';
COMMENT ON COLUMN users.id IS 'Идентификатор (UUIDv7)';
COMMENT ON COLUMN users.username IS 'Уникальный ник без символа @ (3–32 символа: a–z, 0–9, _)';
COMMENT ON COLUMN users.display_name IS 'Отображаемое имя';
COMMENT ON COLUMN users.bio IS 'О себе';
COMMENT ON COLUMN users.avatar_file_id IS 'Аватар';
COMMENT ON COLUMN users.role IS 'Роль в системе';
COMMENT ON COLUMN users.state IS 'Состояние учётной записи';
COMMENT ON COLUMN users.locale IS 'Язык интерфейса';
COMMENT ON COLUMN users.kdf_salt IS 'Публичная соль KDF клиента (16 байт)';
COMMENT ON COLUMN users.kdf_params IS 'Параметры Argon2id клиента (m, t, p, версия)';
COMMENT ON COLUMN users.auth_verifier IS 'Верификатор пароля: Argon2id(AuthKey) в формате PHC';
COMMENT ON COLUMN users.recovery_verifier IS 'Верификатор ключа восстановления в формате PHC';
COMMENT ON COLUMN users.recovery_set_at IS 'Дата выпуска ключа восстановления';
COMMENT ON COLUMN users.totp_secret_enc IS 'Секрет TOTP, зашифрованный ключом сервера (AES-256-GCM)';
COMMENT ON COLUMN users.totp_enabled IS 'Включена ли двухфакторная защита';
COMMENT ON COLUMN users.failed_logins IS 'Счётчик неудачных входов подряд';
COMMENT ON COLUMN users.locked_until IS 'Временная блокировка входа до';
COMMENT ON COLUMN users.created_at IS 'Дата регистрации';
COMMENT ON COLUMN users.updated_at IS 'Дата изменения';
COMMENT ON COLUMN users.deleted_at IS 'Дата удаления учётной записи';
COMMENT ON TABLE privacy_settings IS 'Настройки приватности пользователя.';
COMMENT ON COLUMN privacy_settings.user_id IS 'Пользователь';
COMMENT ON COLUMN privacy_settings.searchable IS 'Можно ли найти по нику';
COMMENT ON COLUMN privacy_settings.last_seen_vis IS 'Кто видит время последнего визита';
COMMENT ON COLUMN privacy_settings.status_vis IS 'Кто видит статус';
COMMENT ON COLUMN privacy_settings.avatar_vis IS 'Кто видит аватар';
COMMENT ON COLUMN privacy_settings.bio_vis IS 'Кто видит раздел «О себе»';
COMMENT ON COLUMN privacy_settings.message_from IS 'Кто может писать первым';
COMMENT ON COLUMN privacy_settings.group_invite_from IS 'Кто может добавлять в группы';
COMMENT ON COLUMN privacy_settings.call_from IS 'Кто может звонить';
COMMENT ON COLUMN privacy_settings.read_receipts IS 'Отправлять ли отметки о прочтении';
COMMENT ON COLUMN privacy_settings.typing_indicator IS 'Показывать ли «печатает…»';
COMMENT ON COLUMN privacy_settings.updated_at IS 'Дата изменения';
COMMENT ON TABLE devices IS 'Зарегистрированное устройство (клиентская установка).';
COMMENT ON COLUMN devices.id IS 'Идентификатор устройства (UUIDv7)';
COMMENT ON COLUMN devices.user_id IS 'Владелец';
COMMENT ON COLUMN devices.name IS 'Имя устройства';
COMMENT ON COLUMN devices.platform IS 'Платформа';
COMMENT ON COLUMN devices.app_version IS 'Версия приложения';
COMMENT ON COLUMN devices.os_version IS 'Версия ОС';
COMMENT ON COLUMN devices.device_pubkey IS 'Открытый ключ устройства Ed25519 (32 байта)';
COMMENT ON COLUMN devices.push_provider IS 'Провайдер push-уведомлений';
COMMENT ON COLUMN devices.push_token_enc IS 'Push-токен, зашифрованный ключом сервера';
COMMENT ON COLUMN devices.created_at IS 'Дата регистрации';
COMMENT ON COLUMN devices.last_seen_at IS 'Последняя активность';
COMMENT ON COLUMN devices.last_ip IS 'Последний IP-адрес';
COMMENT ON COLUMN devices.revoked_at IS 'Дата отзыва устройства';
COMMENT ON TABLE sessions IS 'Сессия: цепочка ротируемых refresh-токенов устройства.';
COMMENT ON COLUMN sessions.id IS 'Идентификатор сессии';
COMMENT ON COLUMN sessions.user_id IS 'Пользователь';
COMMENT ON COLUMN sessions.device_id IS 'Устройство';
COMMENT ON COLUMN sessions.family_id IS 'Семейство токенов (для обнаружения повторного использования)';
COMMENT ON COLUMN sessions.refresh_hash IS 'SHA-256 refresh-токена';
COMMENT ON COLUMN sessions.created_at IS 'Дата выдачи';
COMMENT ON COLUMN sessions.expires_at IS 'Срок действия';
COMMENT ON COLUMN sessions.rotated_at IS 'Дата ротации';
COMMENT ON COLUMN sessions.revoked_at IS 'Дата отзыва';
COMMENT ON COLUMN sessions.revoke_reason IS 'Причина отзыва';
COMMENT ON TABLE auth_events IS 'Журнал событий входа и безопасности (виден пользователю).';
COMMENT ON COLUMN auth_events.id IS 'Идентификатор';
COMMENT ON COLUMN auth_events.user_id IS 'Пользователь';
COMMENT ON COLUMN auth_events.device_id IS 'Устройство';
COMMENT ON COLUMN auth_events.kind IS 'Тип события';
COMMENT ON COLUMN auth_events.ip IS 'IP-адрес';
COMMENT ON COLUMN auth_events.user_agent IS 'Клиентское приложение';
COMMENT ON COLUMN auth_events.details IS 'Дополнительные сведения';
COMMENT ON COLUMN auth_events.created_at IS 'Время события';
COMMENT ON TABLE login_approvals IS 'Запрос подтверждения входа с нового устройства.';
COMMENT ON COLUMN login_approvals.id IS 'Идентификатор запроса';
COMMENT ON COLUMN login_approvals.user_id IS 'Пользователь';
COMMENT ON COLUMN login_approvals.requester_pubkey IS 'Открытый ключ запрашивающего устройства';
COMMENT ON COLUMN login_approvals.requester_name IS 'Имя запрашивающего устройства';
COMMENT ON COLUMN login_approvals.requester_platform IS 'Платформа';
COMMENT ON COLUMN login_approvals.requester_ip IS 'IP-адрес запроса';
COMMENT ON COLUMN login_approvals.code IS 'Короткий код для сверки на обоих устройствах';
COMMENT ON COLUMN login_approvals.status IS 'Состояние';
COMMENT ON COLUMN login_approvals.approver_device_id IS 'Устройство, принявшее решение';
COMMENT ON COLUMN login_approvals.created_at IS 'Время запроса';
COMMENT ON COLUMN login_approvals.expires_at IS 'Срок действия запроса';
COMMENT ON COLUMN login_approvals.resolved_at IS 'Время решения';
COMMENT ON TABLE backup_codes IS 'Резервные коды для двухфакторной защиты.';
COMMENT ON COLUMN backup_codes.id IS 'Идентификатор';
COMMENT ON COLUMN backup_codes.user_id IS 'Пользователь';
COMMENT ON COLUMN backup_codes.code_hash IS 'Хеш кода (Argon2id)';
COMMENT ON COLUMN backup_codes.used_at IS 'Время использования';
COMMENT ON TABLE consent_records IS 'Фиксация принятия юридических документов и согласий.';
COMMENT ON COLUMN consent_records.id IS 'Идентификатор';
COMMENT ON COLUMN consent_records.user_id IS 'Пользователь';
COMMENT ON COLUMN consent_records.document_kind IS 'Документ';
COMMENT ON COLUMN consent_records.version IS 'Версия документа';
COMMENT ON COLUMN consent_records.accepted_at IS 'Время принятия';
COMMENT ON COLUMN consent_records.ip IS 'IP-адрес';
COMMENT ON COLUMN consent_records.user_agent IS 'Клиентское приложение';
COMMENT ON COLUMN consent_records.device_id IS 'Устройство';
COMMENT ON TABLE user_settings IS 'Синхронизируемые настройки (темы, горячие клавиши, оформление).';
COMMENT ON COLUMN user_settings.user_id IS 'Пользователь';
COMMENT ON COLUMN user_settings.key IS 'Ключ настройки';
COMMENT ON COLUMN user_settings.value IS 'Значение';
COMMENT ON COLUMN user_settings.version IS 'Версия (для разрешения конфликтов)';
COMMENT ON COLUMN user_settings.updated_at IS 'Дата изменения';
COMMENT ON TABLE user_statuses IS 'Ручной статус пользователя (присутствие хранится в Redis).';
COMMENT ON COLUMN user_statuses.user_id IS 'Пользователь';
COMMENT ON COLUMN user_statuses.kind IS 'Вид статуса';
COMMENT ON COLUMN user_statuses.custom_text IS 'Собственный текст (проходит фильтр)';
COMMENT ON COLUMN user_statuses.custom_emoji IS 'Эмодзи статуса';
COMMENT ON COLUMN user_statuses.expires_at IS 'Автоматический сброс в указанное время';
COMMENT ON COLUMN user_statuses.invisible IS 'Не показывать присутствие';
COMMENT ON COLUMN user_statuses.updated_at IS 'Дата изменения';
COMMENT ON TABLE contact_links IS 'Связь «контакт» между пользователями (запрос и подтверждение).';
COMMENT ON COLUMN contact_links.id IS 'Идентификатор';
COMMENT ON COLUMN contact_links.requester_id IS 'Кто отправил запрос';
COMMENT ON COLUMN contact_links.addressee_id IS 'Кому адресован запрос';
COMMENT ON COLUMN contact_links.status IS 'Состояние';
COMMENT ON COLUMN contact_links.created_at IS 'Время запроса';
COMMENT ON COLUMN contact_links.responded_at IS 'Время ответа';
COMMENT ON TABLE user_blocks IS 'Блокировка пользователя пользователем.';
COMMENT ON COLUMN user_blocks.blocker_id IS 'Кто заблокировал';
COMMENT ON COLUMN user_blocks.blocked_id IS 'Кого заблокировали';
COMMENT ON COLUMN user_blocks.created_at IS 'Время блокировки';
COMMENT ON TABLE device_key_bundles IS 'Публичные ключи устройства для секретных чатов (X3DH).';
COMMENT ON COLUMN device_key_bundles.device_id IS 'Устройство';
COMMENT ON COLUMN device_key_bundles.identity_pub IS 'Долговременный ключ идентичности Ed25519/X25519 (32 байта)';
COMMENT ON COLUMN device_key_bundles.spk_id IS 'Идентификатор подписанного предключа';
COMMENT ON COLUMN device_key_bundles.spk_pub IS 'Подписанный предключ X25519';
COMMENT ON COLUMN device_key_bundles.spk_sig IS 'Подпись предключа ключом идентичности (64 байта)';
COMMENT ON COLUMN device_key_bundles.spk_created_at IS 'Дата создания предключа';
COMMENT ON COLUMN device_key_bundles.kem_pub IS 'Открытый ключ ML-KEM-768 (необязательно, режим PQXDH)';
COMMENT ON COLUMN device_key_bundles.kem_sig IS 'Подпись ключа ML-KEM';
COMMENT ON COLUMN device_key_bundles.updated_at IS 'Дата изменения';
COMMENT ON TABLE one_time_prekeys IS 'Одноразовые предключи устройства.';
COMMENT ON COLUMN one_time_prekeys.device_id IS 'Устройство';
COMMENT ON COLUMN one_time_prekeys.key_id IS 'Идентификатор ключа';
COMMENT ON COLUMN one_time_prekeys.pub IS 'Открытый ключ X25519';
COMMENT ON COLUMN one_time_prekeys.consumed_at IS 'Время использования';
COMMENT ON COLUMN one_time_prekeys.consumed_by IS 'Кто использовал';
COMMENT ON TABLE chats IS 'Чат любого вида: личный, группа, канал, секретный.';
COMMENT ON COLUMN chats.id IS 'Идентификатор (UUIDv7)';
COMMENT ON COLUMN chats.kind IS 'Вид чата';
COMMENT ON COLUMN chats.title IS 'Название (группа, канал)';
COMMENT ON COLUMN chats.description IS 'Описание';
COMMENT ON COLUMN chats.avatar_file_id IS 'Аватар чата';
COMMENT ON COLUMN chats.public_handle IS 'Публичный ник группы или канала';
COMMENT ON COLUMN chats.owner_id IS 'Владелец';
COMMENT ON COLUMN chats.default_ttl_seconds IS 'Срок жизни сообщений по умолчанию';
COMMENT ON COLUMN chats.slow_mode_seconds IS 'Минимальный интервал между сообщениями участника';
COMMENT ON COLUMN chats.top_seq IS 'Последний выданный порядковый номер сообщения';
COMMENT ON COLUMN chats.member_count IS 'Число участников';
COMMENT ON COLUMN chats.created_at IS 'Дата создания';
COMMENT ON COLUMN chats.updated_at IS 'Дата изменения';
COMMENT ON COLUMN chats.deleted_at IS 'Дата удаления';
COMMENT ON TABLE direct_pairs IS 'Гарантия единственности личного чата для пары пользователей.';
COMMENT ON COLUMN direct_pairs.chat_id IS 'Чат';
COMMENT ON COLUMN direct_pairs.user_low IS 'Пользователь с меньшим UUID';
COMMENT ON COLUMN direct_pairs.user_high IS 'Пользователь с большим UUID';
COMMENT ON TABLE chat_members IS 'Членство пользователя в чате и его личное состояние чата.';
COMMENT ON COLUMN chat_members.chat_id IS 'Чат';
COMMENT ON COLUMN chat_members.user_id IS 'Пользователь';
COMMENT ON COLUMN chat_members.role IS 'Роль';
COMMENT ON COLUMN chat_members.permissions IS 'Битовая маска прав';
COMMENT ON COLUMN chat_members.joined_at IS 'Дата вступления';
COMMENT ON COLUMN chat_members.left_at IS 'Дата выхода';
COMMENT ON COLUMN chat_members.invited_by IS 'Кто пригласил';
COMMENT ON COLUMN chat_members.muted_until IS 'Уведомления отключены до';
COMMENT ON COLUMN chat_members.pinned_position IS 'Позиция закрепления в списке чатов';
COMMENT ON COLUMN chat_members.archived IS 'Чат в архиве';
COMMENT ON COLUMN chat_members.last_read_seq IS 'Курсор прочтения (seq)';
COMMENT ON COLUMN chat_members.last_delivered_seq IS 'Курсор доставки (seq)';
COMMENT ON TABLE invite_links IS 'Пригласительные ссылки.';
COMMENT ON COLUMN invite_links.id IS 'Идентификатор';
COMMENT ON COLUMN invite_links.chat_id IS 'Чат';
COMMENT ON COLUMN invite_links.token_hash IS 'SHA-256 токена ссылки';
COMMENT ON COLUMN invite_links.created_by IS 'Автор';
COMMENT ON COLUMN invite_links.created_at IS 'Дата создания';
COMMENT ON COLUMN invite_links.expires_at IS 'Срок действия';
COMMENT ON COLUMN invite_links.max_uses IS 'Максимум использований';
COMMENT ON COLUMN invite_links.uses IS 'Использовано';
COMMENT ON COLUMN invite_links.requires_approval IS 'Требуется одобрение администратором';
COMMENT ON COLUMN invite_links.revoked_at IS 'Дата отзыва';
COMMENT ON TABLE chat_folders IS 'Пользовательская папка списка чатов.';
COMMENT ON COLUMN chat_folders.id IS 'Идентификатор';
COMMENT ON COLUMN chat_folders.user_id IS 'Владелец папки';
COMMENT ON COLUMN chat_folders.name IS 'Название';
COMMENT ON COLUMN chat_folders.icon IS 'Значок';
COMMENT ON COLUMN chat_folders.position IS 'Порядок папки';
COMMENT ON COLUMN chat_folders.filter_flags IS 'Маска фильтров: 1 непрочитанные, 2 личные, 4 группы, 8 каналы, 16 закреплённые';
COMMENT ON COLUMN chat_folders.created_at IS 'Дата создания';
COMMENT ON TABLE folder_chats IS 'Связь «папка — чат» (чат может быть в нескольких папках).';
COMMENT ON COLUMN folder_chats.folder_id IS 'Папка';
COMMENT ON COLUMN folder_chats.chat_id IS 'Чат';
COMMENT ON COLUMN folder_chats.position IS 'Позиция чата в папке';
COMMENT ON TABLE drafts IS 'Синхронизируемые черновики.';
COMMENT ON COLUMN drafts.user_id IS 'Пользователь';
COMMENT ON COLUMN drafts.chat_id IS 'Чат';
COMMENT ON COLUMN drafts.body IS 'Текст черновика';
COMMENT ON COLUMN drafts.reply_to_id IS 'Сообщение, на которое отвечает черновик';
COMMENT ON COLUMN drafts.version IS 'Версия (больше — новее)';
COMMENT ON COLUMN drafts.updated_at IS 'Дата изменения';
COMMENT ON TABLE secret_chats IS 'Параметры секретного чата, привязанного к паре устройств.';
COMMENT ON COLUMN secret_chats.chat_id IS 'Чат';
COMMENT ON COLUMN secret_chats.initiator_user_id IS 'Инициатор';
COMMENT ON COLUMN secret_chats.initiator_device_id IS 'Устройство инициатора';
COMMENT ON COLUMN secret_chats.peer_user_id IS 'Собеседник';
COMMENT ON COLUMN secret_chats.peer_device_id IS 'Устройство собеседника';
COMMENT ON COLUMN secret_chats.state IS 'Состояние';
COMMENT ON COLUMN secret_chats.x3dh_init IS 'Начальное сообщение X3DH для устройства собеседника';
COMMENT ON COLUMN secret_chats.created_at IS 'Дата создания';
COMMENT ON COLUMN secret_chats.closed_at IS 'Дата закрытия';
COMMENT ON TABLE messages IS 'Сообщение. Для секретных чатов хранится только шифртекст.';
COMMENT ON COLUMN messages.id IS 'Идентификатор (UUIDv7)';
COMMENT ON COLUMN messages.chat_id IS 'Чат';
COMMENT ON COLUMN messages.seq IS 'Порядковый номер в чате (без пропусков)';
COMMENT ON COLUMN messages.sender_id IS 'Отправитель';
COMMENT ON COLUMN messages.sender_device_id IS 'Устройство отправителя';
COMMENT ON COLUMN messages.client_msg_id IS 'Идемпотентный идентификатор клиента';
COMMENT ON COLUMN messages.kind IS 'Вид сообщения';
COMMENT ON COLUMN messages.body IS 'Текст (не более 4096 символов; NULL для секретных)';
COMMENT ON COLUMN messages.entities IS 'Разметка текста (жирный, ссылки, упоминания)';
COMMENT ON COLUMN messages.ciphertext IS 'Шифртекст E2EE (только kind = SECRET)';
COMMENT ON COLUMN messages.reply_to_id IS 'Ответ на сообщение';
COMMENT ON COLUMN messages.forward_origin IS 'Происхождение пересланного сообщения';
COMMENT ON COLUMN messages.created_at IS 'Время приёма сервером';
COMMENT ON COLUMN messages.edited_at IS 'Время изменения';
COMMENT ON COLUMN messages.deleted_at IS 'Время удаления (надгробие)';
COMMENT ON COLUMN messages.ttl_seconds IS 'Срок жизни после прочтения';
COMMENT ON COLUMN messages.first_read_at IS 'Время первого прочтения получателем';
COMMENT ON COLUMN messages.expires_at IS 'Момент автоматического удаления';
COMMENT ON COLUMN messages.pinned_at IS 'Время закрепления';
COMMENT ON TABLE message_attachments IS 'Вложения сообщения.';
COMMENT ON COLUMN message_attachments.message_id IS 'Сообщение';
COMMENT ON COLUMN message_attachments.ordinal IS 'Порядковый номер вложения';
COMMENT ON COLUMN message_attachments.file_id IS 'Файл';
COMMENT ON COLUMN message_attachments.caption IS 'Подпись';
COMMENT ON TABLE message_reactions IS 'Реакции на сообщения.';
COMMENT ON COLUMN message_reactions.message_id IS 'Сообщение';
COMMENT ON COLUMN message_reactions.user_id IS 'Пользователь';
COMMENT ON COLUMN message_reactions.emoji IS 'Эмодзи';
COMMENT ON COLUMN message_reactions.created_at IS 'Время реакции';
COMMENT ON TABLE user_update_counters IS 'Счётчик порядковых номеров журнала обновлений пользователя.';
COMMENT ON COLUMN user_update_counters.user_id IS 'Пользователь';
COMMENT ON COLUMN user_update_counters.last_seq IS 'Последний выданный update_seq';
COMMENT ON TABLE user_updates IS 'Журнал обновлений пользователя для синхронизации устройств (хранится 30 суток).';
COMMENT ON COLUMN user_updates.user_id IS 'Пользователь';
COMMENT ON COLUMN user_updates.update_seq IS 'Порядковый номер обновления';
COMMENT ON COLUMN user_updates.kind IS 'Тип обновления';
COMMENT ON COLUMN user_updates.payload IS 'Сериализованное сообщение Protocol Buffers';
COMMENT ON COLUMN user_updates.created_at IS 'Время создания';
COMMENT ON TABLE media_blobs IS 'Физический объект в хранилище (один на уникальное содержимое).';
COMMENT ON COLUMN media_blobs.id IS 'Идентификатор';
COMMENT ON COLUMN media_blobs.sha256 IS 'SHA-256 содержимого (32 байта)';
COMMENT ON COLUMN media_blobs.size_bytes IS 'Размер исходных данных';
COMMENT ON COLUMN media_blobs.stored_size_bytes IS 'Размер в хранилище после сжатия';
COMMENT ON COLUMN media_blobs.compression IS 'Способ сжатия на стороне хранилища';
COMMENT ON COLUMN media_blobs.is_encrypted IS 'Содержимое зашифровано клиентом (E2EE)';
COMMENT ON COLUMN media_blobs.storage_tier IS 'Уровень хранения';
COMMENT ON COLUMN media_blobs.bucket IS 'Бакет S3';
COMMENT ON COLUMN media_blobs.storage_key IS 'Ключ объекта';
COMMENT ON COLUMN media_blobs.refcount IS 'Число ссылающихся файлов';
COMMENT ON COLUMN media_blobs.created_at IS 'Дата создания';
COMMENT ON COLUMN media_blobs.last_accessed_at IS 'Последнее обращение';
COMMENT ON COLUMN media_blobs.tier_changed_at IS 'Время смены уровня хранения';
COMMENT ON COLUMN media_blobs.restore_requested_at IS 'Время запроса восстановления';
COMMENT ON TABLE media_files IS 'Логический файл пользователя (метаданные, права, состояние).';
COMMENT ON COLUMN media_files.id IS 'Идентификатор (UUIDv7)';
COMMENT ON COLUMN media_files.owner_id IS 'Загрузивший пользователь';
COMMENT ON COLUMN media_files.blob_id IS 'Физический объект';
COMMENT ON COLUMN media_files.kind IS 'Вид файла';
COMMENT ON COLUMN media_files.file_name IS 'Имя файла';
COMMENT ON COLUMN media_files.mime IS 'MIME-тип';
COMMENT ON COLUMN media_files.size_bytes IS 'Объявленный размер';
COMMENT ON COLUMN media_files.width IS 'Ширина, px';
COMMENT ON COLUMN media_files.height IS 'Высота, px';
COMMENT ON COLUMN media_files.duration_ms IS 'Длительность, мс';
COMMENT ON COLUMN media_files.is_encrypted IS 'Зашифрован клиентом (E2EE)';
COMMENT ON COLUMN media_files.state IS 'Состояние';
COMMENT ON COLUMN media_files.failure_reason IS 'Причина ошибки';
COMMENT ON COLUMN media_files.created_at IS 'Дата создания';
COMMENT ON COLUMN media_files.ready_at IS 'Дата готовности';
COMMENT ON COLUMN media_files.deleted_at IS 'Дата удаления';
COMMENT ON TABLE media_variants IS 'Производные представления файла (миниатюра, превью, оптимизированная копия).';
COMMENT ON COLUMN media_variants.file_id IS 'Файл';
COMMENT ON COLUMN media_variants.variant IS 'Вид представления';
COMMENT ON COLUMN media_variants.blob_id IS 'Физический объект';
COMMENT ON COLUMN media_variants.width IS 'Ширина, px';
COMMENT ON COLUMN media_variants.height IS 'Высота, px';
COMMENT ON TABLE upload_sessions IS 'Сессия возобновляемой многочастной загрузки.';
COMMENT ON COLUMN upload_sessions.id IS 'Идентификатор';
COMMENT ON COLUMN upload_sessions.owner_id IS 'Загружающий пользователь';
COMMENT ON COLUMN upload_sessions.file_id IS 'Создаваемый файл';
COMMENT ON COLUMN upload_sessions.declared_size IS 'Объявленный размер';
COMMENT ON COLUMN upload_sessions.part_size IS 'Размер части, байт';
COMMENT ON COLUMN upload_sessions.parts_total IS 'Число частей';
COMMENT ON COLUMN upload_sessions.parts_completed IS 'Номера принятых частей';
COMMENT ON COLUMN upload_sessions.s3_upload_id IS 'Идентификатор multipart-загрузки S3';
COMMENT ON COLUMN upload_sessions.state IS 'Состояние';
COMMENT ON COLUMN upload_sessions.created_at IS 'Дата создания';
COMMENT ON COLUMN upload_sessions.expires_at IS 'Срок действия';
COMMENT ON COLUMN upload_sessions.updated_at IS 'Дата изменения';
COMMENT ON TABLE user_quotas IS 'Квота хранилища пользователя.';
COMMENT ON COLUMN user_quotas.user_id IS 'Пользователь';
COMMENT ON COLUMN user_quotas.used_bytes IS 'Использовано';
COMMENT ON COLUMN user_quotas.limit_bytes IS 'Лимит';
COMMENT ON COLUMN user_quotas.updated_at IS 'Дата изменения';
COMMENT ON TABLE calls IS 'Звонок (аудио или видео, личный или групповой).';
COMMENT ON COLUMN calls.id IS 'Идентификатор';
COMMENT ON COLUMN calls.chat_id IS 'Чат, в котором идёт звонок';
COMMENT ON COLUMN calls.initiator_id IS 'Инициатор';
COMMENT ON COLUMN calls.kind IS 'Вид звонка';
COMMENT ON COLUMN calls.state IS 'Состояние';
COMMENT ON COLUMN calls.sfu_room IS 'Имя комнаты на медиасервере';
COMMENT ON COLUMN calls.created_at IS 'Время начала вызова';
COMMENT ON COLUMN calls.answered_at IS 'Время ответа';
COMMENT ON COLUMN calls.ended_at IS 'Время завершения';
COMMENT ON COLUMN calls.end_reason IS 'Причина завершения';
COMMENT ON TABLE call_participants IS 'Участники звонка.';
COMMENT ON COLUMN call_participants.call_id IS 'Звонок';
COMMENT ON COLUMN call_participants.user_id IS 'Пользователь';
COMMENT ON COLUMN call_participants.device_id IS 'Устройство';
COMMENT ON COLUMN call_participants.joined_at IS 'Время входа';
COMMENT ON COLUMN call_participants.left_at IS 'Время выхода';
COMMENT ON COLUMN call_participants.video_enabled IS 'Передаёт видео';
COMMENT ON COLUMN call_participants.screen_sharing IS 'Демонстрирует экран';
COMMENT ON TABLE reports IS 'Жалобы пользователей.';
COMMENT ON COLUMN reports.id IS 'Идентификатор';
COMMENT ON COLUMN reports.reporter_id IS 'Автор жалобы';
COMMENT ON COLUMN reports.target_kind IS 'Объект жалобы';
COMMENT ON COLUMN reports.target_id IS 'Идентификатор объекта';
COMMENT ON COLUMN reports.reason IS 'Причина';
COMMENT ON COLUMN reports.details IS 'Пояснение автора';
COMMENT ON COLUMN reports.evidence IS 'Приложенные автором материалы (для E2EE — расшифрованный фрагмент)';
COMMENT ON COLUMN reports.status IS 'Состояние';
COMMENT ON COLUMN reports.created_at IS 'Дата подачи';
COMMENT ON COLUMN reports.resolved_by IS 'Модератор';
COMMENT ON COLUMN reports.resolved_at IS 'Дата решения';
COMMENT ON COLUMN reports.resolution_note IS 'Комментарий модератора';
COMMENT ON TABLE sanctions IS 'Меры воздействия к учётным записям.';
COMMENT ON COLUMN sanctions.id IS 'Идентификатор';
COMMENT ON COLUMN sanctions.user_id IS 'Пользователь';
COMMENT ON COLUMN sanctions.kind IS 'Вид меры';
COMMENT ON COLUMN sanctions.reason IS 'Основание';
COMMENT ON COLUMN sanctions.report_id IS 'Жалоба-основание';
COMMENT ON COLUMN sanctions.issued_by IS 'Кто применил';
COMMENT ON COLUMN sanctions.created_at IS 'Дата применения';
COMMENT ON COLUMN sanctions.expires_at IS 'Срок действия';
COMMENT ON COLUMN sanctions.revoked_at IS 'Дата снятия';
COMMENT ON TABLE moderation_terms IS 'Словарь фильтра текста статусов.';
COMMENT ON COLUMN moderation_terms.id IS 'Идентификатор';
COMMENT ON COLUMN moderation_terms.category IS 'Категория';
COMMENT ON COLUMN moderation_terms.pattern IS 'Шаблон (нормализованная основа слова или регулярное выражение)';
COMMENT ON COLUMN moderation_terms.is_regex IS 'Шаблон — регулярное выражение';
COMMENT ON COLUMN moderation_terms.active IS 'Применяется';
COMMENT ON COLUMN moderation_terms.created_by IS 'Автор записи';
COMMENT ON COLUMN moderation_terms.created_at IS 'Дата создания';
COMMENT ON TABLE legal_requests IS 'Реестр обращений уполномоченных органов.';
COMMENT ON COLUMN legal_requests.id IS 'Идентификатор';
COMMENT ON COLUMN legal_requests.received_at IS 'Дата получения';
COMMENT ON COLUMN legal_requests.authority IS 'Орган';
COMMENT ON COLUMN legal_requests.basis IS 'Правовое основание';
COMMENT ON COLUMN legal_requests.scope IS 'Запрошенные сведения или действия';
COMMENT ON COLUMN legal_requests.status IS 'Состояние';
COMMENT ON COLUMN legal_requests.handled_by IS 'Ответственный';
COMMENT ON COLUMN legal_requests.notes IS 'Примечания';
COMMENT ON COLUMN legal_requests.completed_at IS 'Дата исполнения';
COMMENT ON TABLE audit_log IS 'Журнал действий администраторов и модераторов.';
COMMENT ON COLUMN audit_log.id IS 'Идентификатор';
COMMENT ON COLUMN audit_log.actor_id IS 'Кто выполнил';
COMMENT ON COLUMN audit_log.action IS 'Действие';
COMMENT ON COLUMN audit_log.target_kind IS 'Вид объекта';
COMMENT ON COLUMN audit_log.target_id IS 'Идентификатор объекта';
COMMENT ON COLUMN audit_log.ip IS 'IP-адрес';
COMMENT ON COLUMN audit_log.details IS 'Подробности';
COMMENT ON COLUMN audit_log.created_at IS 'Время';
COMMENT ON TABLE background_jobs IS 'Очередь фоновых заданий (transactional outbox).';
COMMENT ON COLUMN background_jobs.id IS 'Идентификатор';
COMMENT ON COLUMN background_jobs.kind IS 'Тип задания (transcode, tier_cold, tier_restore, purge_expired, push_send …)';
COMMENT ON COLUMN background_jobs.payload IS 'Параметры';
COMMENT ON COLUMN background_jobs.state IS 'Состояние';
COMMENT ON COLUMN background_jobs.run_at IS 'Не выполнять ранее';
COMMENT ON COLUMN background_jobs.attempts IS 'Число попыток';
COMMENT ON COLUMN background_jobs.locked_by IS 'Исполнитель';
COMMENT ON COLUMN background_jobs.locked_until IS 'Захват до';
COMMENT ON COLUMN background_jobs.last_error IS 'Последняя ошибка';
COMMENT ON COLUMN background_jobs.created_at IS 'Дата создания';

-- Примечание по масштабированию: таблицы messages и user_updates при росте нагрузки
-- переводятся на секционирование по HASH(chat_id) и HASH(user_id) соответственно
-- (см. пояснительную записку, раздел «Масштабирование»).
