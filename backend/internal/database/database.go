package database

import (
	"context"
	"fmt"
	"os"
	"sort"

	"github.com/jackc/pgx/v5/pgxpool"
)

func Open(ctx context.Context, url string) (*pgxpool.Pool, error) {
	p, err := pgxpool.New(ctx, url)
	if err != nil {
		return nil, err
	}
	if err = p.Ping(ctx); err != nil {
		p.Close()
		return nil, err
	}
	return p, nil
}

func Migrate(ctx context.Context, p *pgxpool.Pool, dir string) error {
	if _, err := p.Exec(ctx, `CREATE TABLE IF NOT EXISTS schema_migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())`); err != nil {
		return err
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		return err
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Name() < entries[j].Name() })
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		var applied bool
		if err := p.QueryRow(ctx, `SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE name=$1)`, e.Name()).Scan(&applied); err != nil {
			return err
		}
		if applied {
			continue
		}
		b, err := os.ReadFile(dir + "/" + e.Name())
		if err != nil {
			return err
		}
		tx, err := p.Begin(ctx)
		if err != nil {
			return err
		}
		if _, err = tx.Exec(ctx, string(b)); err != nil {
			_ = tx.Rollback(ctx)
			return fmt.Errorf("migration %s: %w", e.Name(), err)
		}
		if _, err = tx.Exec(ctx, `INSERT INTO schema_migrations(name) VALUES($1)`, e.Name()); err != nil {
			_ = tx.Rollback(ctx)
			return err
		}
		if err = tx.Commit(ctx); err != nil {
			return err
		}
	}
	return nil
}
