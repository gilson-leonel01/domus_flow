FROM golang:1.23-alpine AS build

WORKDIR /app

COPY go.mod ./
RUN go mod download

COPY . .
RUN go mod tidy && CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /domusflow ./cmd/api

FROM alpine:3.21

RUN adduser -D app
WORKDIR /app

COPY --from=build /domusflow /usr/local/bin/domusflow
COPY migrations ./migrations

USER app

EXPOSE 8080

CMD ["domusflow"]
