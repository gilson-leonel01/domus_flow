package main

import (
	"context"
	"domusflow/backend/internal/config"
	"domusflow/backend/internal/database"
	"domusflow/backend/internal/httpapi"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

func main() {
	cfg := config.Load()
	ctx := context.Background()
	db, err := database.Open(ctx, cfg.DatabaseURL)
	if err != nil {
		log.Fatal(err)
	}
	defer db.Close()
	if err = database.Migrate(ctx, db, "migrations"); err != nil {
		log.Fatal(err)
	}
	srv := &http.Server{Addr: ":" + cfg.Port, Handler: httpapi.New(db, cfg.JWTSecret), ReadHeaderTimeout: 5 * time.Second}
	go func() {
		log.Printf("API em :%s", cfg.Port)
		if e := srv.ListenAndServe(); e != nil && e != http.ErrServerClosed {
			log.Fatal(e)
		}
	}()
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)
	<-stop
	shutdown, _ := context.WithTimeout(context.Background(), 10*time.Second)
	_ = srv.Shutdown(shutdown)
}
