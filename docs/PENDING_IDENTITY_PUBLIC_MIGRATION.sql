CREATE TABLE IF NOT EXISTS public.__ef_migrations_history (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public.__ef_migrations_history WHERE "migration_id" = '20261023040204_BaselineExistingIdentityTables') THEN
    INSERT INTO public.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261023040204_BaselineExistingIdentityTables', '8.0.31');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public.__ef_migrations_history WHERE "migration_id" = '20261023040310_AddRefreshSessions') THEN
    CREATE TABLE public.refresh_sessions (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        branch_id uuid NOT NULL,
        token_hash character(64) NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        revoked_at_utc timestamp with time zone,
        replaced_by_session_id uuid,
        version uuid NOT NULL,
        CONSTRAINT pk_refresh_sessions PRIMARY KEY (id),
        CONSTRAINT fk_refresh_sessions_branches_branch_id FOREIGN KEY (branch_id) REFERENCES public.branches (id),
        CONSTRAINT fk_refresh_sessions_refresh_sessions_replaced_by_session_id FOREIGN KEY (replaced_by_session_id) REFERENCES public.refresh_sessions (id),
        CONSTRAINT fk_refresh_sessions_users_user_id FOREIGN KEY (user_id) REFERENCES public.users (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public.__ef_migrations_history WHERE "migration_id" = '20261023040310_AddRefreshSessions') THEN
    CREATE INDEX ix_refresh_sessions_branch_id ON public.refresh_sessions (branch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public.__ef_migrations_history WHERE "migration_id" = '20261023040310_AddRefreshSessions') THEN
    CREATE INDEX ix_refresh_sessions_replaced_by_session_id ON public.refresh_sessions (replaced_by_session_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public.__ef_migrations_history WHERE "migration_id" = '20261023040310_AddRefreshSessions') THEN
    CREATE UNIQUE INDEX ix_refresh_sessions_token_hash ON public.refresh_sessions (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public.__ef_migrations_history WHERE "migration_id" = '20261023040310_AddRefreshSessions') THEN
    CREATE INDEX ix_refresh_sessions_user_id_branch_id_expires_at_utc ON public.refresh_sessions (user_id, branch_id, expires_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM public.__ef_migrations_history WHERE "migration_id" = '20261023040310_AddRefreshSessions') THEN
    INSERT INTO public.__ef_migrations_history (migration_id, product_version)
    VALUES ('20261023040310_AddRefreshSessions', '8.0.31');
    END IF;
END $EF$;
COMMIT;

