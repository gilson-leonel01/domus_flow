.PHONY: up down logs reset build frontend backend
up:
	docker compose up --build -d

down:
	docker compose down

logs:
	docker compose logs -f

reset:
	docker compose down -v
	docker compose up --build -d

build:
	docker compose build

frontend:
	cd frontend && npm install && npm start

backend:
	cd backend && go mod tidy && go run ./cmd/api
