package config

import "os"

type Config struct{ DatabaseURL, JWTSecret, Port string }

func Load() Config {
	return Config{
		DatabaseURL: env("DATABASE_URL", "postgres://domusflow:domusflow@localhost:5432/domusflow?sslmode=disable"),
		JWTSecret:   env("JWT_SECRET", "change-me-in-production"),
		Port:        env("PORT", "8080"),
	}
}
func env(k, fallback string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return fallback
}
