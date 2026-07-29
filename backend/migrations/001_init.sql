CREATE EXTENSION IF NOT EXISTS pgcrypto;
DO $$ BEGIN CREATE TYPE user_role AS ENUM ('OWNER','EMPLOYEE','CHILD'); EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE TYPE task_status AS ENUM ('PLANNED','IN_PROGRESS','DONE','SKIPPED'); EXCEPTION WHEN duplicate_object THEN NULL; END $$;
CREATE TABLE IF NOT EXISTS households (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), name TEXT NOT NULL, country_code CHAR(2) NOT NULL DEFAULT 'AO', timezone TEXT NOT NULL DEFAULT 'Africa/Luanda', xp_bonus_threshold INT NOT NULL DEFAULT 1000, xp_dayoff_threshold INT NOT NULL DEFAULT 1500, created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS users (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL, role user_role NOT NULL, avatar TEXT, active BOOLEAN NOT NULL DEFAULT true, created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS holidays (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE, holiday_date DATE NOT NULL, name TEXT NOT NULL, country_code CHAR(2) NOT NULL, UNIQUE(household_id, holiday_date)
);
CREATE TABLE IF NOT EXISTS tasks (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE, assignee_id UUID NOT NULL REFERENCES users(id), created_by UUID NOT NULL REFERENCES users(id), title TEXT NOT NULL, description TEXT NOT NULL DEFAULT '', scheduled_date DATE NOT NULL, start_time TIME, estimated_minutes INT NOT NULL CHECK(estimated_minutes > 0), priority SMALLINT NOT NULL DEFAULT 2 CHECK(priority BETWEEN 1 AND 3), status task_status NOT NULL DEFAULT 'PLANNED', started_at TIMESTAMPTZ, completed_at TIMESTAMPTZ, xp_awarded INT NOT NULL DEFAULT 0, created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_tasks_household_date ON tasks(household_id, scheduled_date);
CREATE INDEX IF NOT EXISTS idx_tasks_assignee_date ON tasks(assignee_id, scheduled_date);
CREATE TABLE IF NOT EXISTS work_sessions (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE, user_id UUID NOT NULL REFERENCES users(id), work_date DATE NOT NULL, checked_in_at TIMESTAMPTZ NOT NULL DEFAULT now(), checked_out_at TIMESTAMPTZ, UNIQUE(user_id, work_date)
);
CREATE TABLE IF NOT EXISTS xp_ledger (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE, user_id UUID NOT NULL REFERENCES users(id), task_id UUID REFERENCES tasks(id), points INT NOT NULL, reason TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS rewards (
 id UUID PRIMARY KEY DEFAULT gen_random_uuid(), household_id UUID NOT NULL REFERENCES households(id) ON DELETE CASCADE, user_id UUID NOT NULL REFERENCES users(id), month CHAR(7) NOT NULL, reward_type TEXT NOT NULL CHECK(reward_type IN ('BONUS','DAY_OFF')), xp_cost INT NOT NULL, status TEXT NOT NULL DEFAULT 'AVAILABLE' CHECK(status IN ('AVAILABLE','CLAIMED','APPROVED','REJECTED')), created_at TIMESTAMPTZ NOT NULL DEFAULT now(), UNIQUE(user_id, month, reward_type)
);
