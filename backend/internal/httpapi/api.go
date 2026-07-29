package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"strconv"
	"strings"
	"time"

	"domusflow/backend/internal/auth"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type API struct {
	db     *pgxpool.Pool
	secret string
}
type ctxKey string

const claimsKey ctxKey = "claims"

func New(db *pgxpool.Pool, secret string) http.Handler {
	a := &API{db: db, secret: secret}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", a.health)
	mux.HandleFunc("POST /api/auth/login", a.login)
	mux.Handle("GET /api/me", a.auth(http.HandlerFunc(a.me)))
	mux.Handle("GET /api/dashboard", a.auth(http.HandlerFunc(a.dashboard)))
	mux.Handle("GET /api/users", a.auth(http.HandlerFunc(a.users)))
	mux.Handle("GET /api/tasks", a.auth(http.HandlerFunc(a.tasks)))
	mux.Handle("POST /api/tasks", a.auth(a.role("OWNER", http.HandlerFunc(a.createTask))))
	mux.Handle("PATCH /api/tasks/{id}", a.auth(a.role("OWNER", http.HandlerFunc(a.updateTask))))
	mux.Handle("DELETE /api/tasks/{id}", a.auth(a.role("OWNER", http.HandlerFunc(a.deleteTask))))
	mux.Handle("POST /api/work/check-in", a.auth(a.role("EMPLOYEE", http.HandlerFunc(a.checkIn))))
	mux.Handle("POST /api/work/check-out", a.auth(a.role("EMPLOYEE", http.HandlerFunc(a.checkOut))))
	mux.Handle("POST /api/tasks/{id}/start", a.auth(http.HandlerFunc(a.startTask)))
	mux.Handle("POST /api/tasks/{id}/complete", a.auth(http.HandlerFunc(a.completeTask)))
	mux.Handle("GET /api/holidays", a.auth(http.HandlerFunc(a.holidays)))
	mux.Handle("POST /api/holidays", a.auth(a.role("OWNER", http.HandlerFunc(a.createHoliday))))
	mux.Handle("GET /api/rewards", a.auth(http.HandlerFunc(a.rewards)))
	mux.Handle("POST /api/rewards/{id}/claim", a.auth(http.HandlerFunc(a.claimReward)))
	return cors(logging(mux))
}

func write(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
func fail(w http.ResponseWriter, status int, msg string) {
	write(w, status, map[string]string{"error": msg})
}
func decode(r *http.Request, v any) error {
	d := json.NewDecoder(r.Body)
	d.DisallowUnknownFields()
	return d.Decode(v)
}
func claims(r *http.Request) *auth.Claims { return r.Context().Value(claimsKey).(*auth.Claims) }
func (a *API) health(w http.ResponseWriter, r *http.Request) {
	write(w, 200, map[string]string{"status": "ok"})
}
func (a *API) auth(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		h := strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "))
		if h == "" {
			fail(w, 401, "token em falta")
			return
		}
		c, e := auth.Parse(a.secret, h)
		if e != nil {
			fail(w, 401, "token inválido")
			return
		}
		next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), claimsKey, c)))
	})
}
func (a *API) role(role string, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if claims(r).Role != role {
			fail(w, 403, "sem permissão")
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (a *API) login(w http.ResponseWriter, r *http.Request) {
	var in struct{ Email, Password string }
	if decode(r, &in) != nil {
		fail(w, 400, "dados inválidos")
		return
	}
	var id, hid, name, role, hash string
	e := a.db.QueryRow(r.Context(), `SELECT id,household_id,name,role,password_hash FROM users WHERE lower(email)=lower($1) AND active`, in.Email).Scan(&id, &hid, &name, &role, &hash)
	if e != nil || !auth.Verify(hash, in.Password) {
		fail(w, 401, "credenciais inválidas")
		return
	}
	token, e := auth.Sign(a.secret, id, hid, role)
	if e != nil {
		fail(w, 500, "erro ao gerar token")
		return
	}
	write(w, 200, map[string]any{"token": token, "user": map[string]any{"id": id, "householdId": hid, "name": name, "email": in.Email, "role": role}})
}
func (a *API) me(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	var name, email, avatar string
	_ = a.db.QueryRow(r.Context(), `SELECT name,email,coalesce(avatar,'') FROM users WHERE id=$1`, c.UserID).Scan(&name, &email, &avatar)
	write(w, 200, map[string]any{"id": c.UserID, "householdId": c.HouseholdID, "name": name, "email": email, "role": c.Role, "avatar": avatar})
}

func businessLocked(ctx context.Context, db *pgxpool.Pool, hid string, date time.Time) (bool, string) {
	if date.Weekday() == time.Sunday {
		return true, "Domingo: apenas tarefas dos filhos estão disponíveis"
	}
	var name string
	e := db.QueryRow(ctx, `SELECT name FROM holidays WHERE household_id=$1 AND holiday_date=$2`, hid, date.Format("2006-01-02")).Scan(&name)
	if e == nil {
		return true, "Feriado: " + name
	}
	return false, ""
}

func (a *API) dashboard(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	date := time.Now()
	if q := r.URL.Query().Get("date"); q != "" {
		if d, e := time.Parse("2006-01-02", q); e == nil {
			date = d
		}
	}
	locked, reason := businessLocked(r.Context(), a.db, c.HouseholdID, date)
	var total, done, inprogress, xp int
	query := `SELECT count(*),count(*) FILTER(WHERE status='DONE'),count(*) FILTER(WHERE status='IN_PROGRESS'),coalesce(sum(xp_awarded),0) FROM tasks WHERE household_id=$1 AND scheduled_date=$2`
	args := []any{c.HouseholdID, date.Format("2006-01-02")}
	if c.Role != "OWNER" {
		query += ` AND assignee_id=$3`
		args = append(args, c.UserID)
	}
	_ = a.db.QueryRow(r.Context(), query, args...).Scan(&total, &done, &inprogress, &xp)
	var monthXP int
	_ = a.db.QueryRow(r.Context(), `SELECT coalesce(sum(points),0) FROM xp_ledger WHERE user_id=$1 AND created_at>=date_trunc('month',now())`, c.UserID).Scan(&monthXP)
	var checked bool
	_ = a.db.QueryRow(r.Context(), `SELECT exists(SELECT 1 FROM work_sessions WHERE user_id=$1 AND work_date=$2 AND checked_out_at IS NULL)`, c.UserID, date.Format("2006-01-02")).Scan(&checked)
	write(w, 200, map[string]any{"date": date.Format("2006-01-02"), "total": total, "done": done, "inProgress": inprogress, "dayXP": xp, "monthXP": monthXP, "workLocked": locked && c.Role == "EMPLOYEE", "lockReason": reason, "checkedIn": checked})
}
func (a *API) users(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	rows, e := a.db.Query(r.Context(), `SELECT id,name,email,role,coalesce(avatar,'') FROM users WHERE household_id=$1 AND active ORDER BY role,name`, c.HouseholdID)
	if e != nil {
		fail(w, 500, "erro ao listar utilizadores")
		return
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, n, e, role, av string
		_ = rows.Scan(&id, &n, &e, &role, &av)
		out = append(out, map[string]any{"id": id, "name": n, "email": e, "role": role, "avatar": av})
	}
	write(w, 200, out)
}

func (a *API) tasks(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	date := r.URL.Query().Get("date")
	if date == "" {
		date = time.Now().Format("2006-01-02")
	}
	query := `SELECT t.id,t.title,t.description,t.scheduled_date::text,to_char(t.start_time,'HH24:MI'),t.estimated_minutes,t.priority,t.status,t.started_at,t.completed_at,t.xp_awarded,u.id,u.name,u.role FROM tasks t JOIN users u ON u.id=t.assignee_id WHERE t.household_id=$1 AND t.scheduled_date=$2`
	args := []any{c.HouseholdID, date}
	if c.Role != "OWNER" {
		query += ` AND t.assignee_id=$3`
		args = append(args, c.UserID)
	}
	query += ` ORDER BY t.start_time NULLS LAST,t.created_at`
	rows, e := a.db.Query(r.Context(), query, args...)
	if e != nil {
		fail(w, 500, "erro ao listar tarefas")
		return
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, title, desc, d, status, uid, uname, urole string
		var st *time.Time
		var start *time.Time
		var complete *time.Time
		var mins, priority, xp int
		if e = rows.Scan(&id, &title, &desc, &d, &st, &mins, &priority, &status, &start, &complete, &xp, &uid, &uname, &urole); e != nil {
			log.Println(e)
			continue
		}
		var stv any
		if st != nil {
			stv = st.Format("15:04")
		}
		out = append(out, map[string]any{"id": id, "title": title, "description": desc, "scheduledDate": d, "startTime": stv, "estimatedMinutes": mins, "priority": priority, "status": status, "startedAt": start, "completedAt": complete, "xpAwarded": xp, "assignee": map[string]any{"id": uid, "name": uname, "role": urole}})
	}
	write(w, 200, out)
}

type taskInput struct {
	Title, Description, ScheduledDate, StartTime, AssigneeID string
	EstimatedMinutes, Priority                               int
}

func (a *API) createTask(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	var in taskInput
	if decode(r, &in) != nil || in.Title == "" || in.AssigneeID == "" || in.EstimatedMinutes < 1 {
		fail(w, 400, "dados da tarefa inválidos")
		return
	}
	var id string
	e := a.db.QueryRow(r.Context(), `INSERT INTO tasks(household_id,assignee_id,created_by,title,description,scheduled_date,start_time,estimated_minutes,priority) SELECT $1,$2,$3,$4,$5,$6,NULLIF($7,'')::time,$8,$9 WHERE EXISTS(SELECT 1 FROM users WHERE id=$2 AND household_id=$1) RETURNING id`, c.HouseholdID, in.AssigneeID, c.UserID, in.Title, in.Description, in.ScheduledDate, in.StartTime, in.EstimatedMinutes, in.Priority).Scan(&id)
	if e != nil {
		fail(w, 400, "não foi possível criar a tarefa")
		return
	}
	write(w, 201, map[string]string{"id": id})
}
func (a *API) updateTask(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	var in taskInput
	if decode(r, &in) != nil {
		fail(w, 400, "dados inválidos")
		return
	}
	tag, e := a.db.Exec(r.Context(), `UPDATE tasks SET title=$1,description=$2,scheduled_date=$3,start_time=NULLIF($4,'')::time,estimated_minutes=$5,priority=$6,assignee_id=$7,updated_at=now() WHERE id=$8 AND household_id=$9 AND status='PLANNED'`, in.Title, in.Description, in.ScheduledDate, in.StartTime, in.EstimatedMinutes, in.Priority, in.AssigneeID, r.PathValue("id"), c.HouseholdID)
	if e != nil || tag.RowsAffected() == 0 {
		fail(w, 404, "tarefa não encontrada ou já iniciada")
		return
	}
	write(w, 200, map[string]bool{"updated": true})
}
func (a *API) deleteTask(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	tag, e := a.db.Exec(r.Context(), `DELETE FROM tasks WHERE id=$1 AND household_id=$2 AND status='PLANNED'`, r.PathValue("id"), c.HouseholdID)
	if e != nil || tag.RowsAffected() == 0 {
		fail(w, 404, "tarefa não encontrada ou já iniciada")
		return
	}
	w.WriteHeader(204)
}
func (a *API) checkIn(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	now := time.Now()
	locked, reason := businessLocked(r.Context(), a.db, c.HouseholdID, now)
	if locked {
		fail(w, 403, reason)
		return
	}
	_, e := a.db.Exec(r.Context(), `INSERT INTO work_sessions(household_id,user_id,work_date) VALUES($1,$2,CURRENT_DATE) ON CONFLICT(user_id,work_date) DO NOTHING`, c.HouseholdID, c.UserID)
	if e != nil {
		fail(w, 500, "erro ao iniciar trabalho")
		return
	}
	write(w, 200, map[string]any{"checkedIn": true, "at": now})
}
func (a *API) checkOut(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	_, e := a.db.Exec(r.Context(), `UPDATE work_sessions SET checked_out_at=now() WHERE user_id=$1 AND work_date=CURRENT_DATE AND checked_out_at IS NULL`, c.UserID)
	if e != nil {
		fail(w, 500, "erro ao terminar trabalho")
		return
	}
	write(w, 200, map[string]bool{"checkedIn": false})
}
func (a *API) startTask(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	if c.Role == "EMPLOYEE" {
		locked, reason := businessLocked(r.Context(), a.db, c.HouseholdID, time.Now())
		if locked {
			fail(w, 403, reason)
			return
		}
		var checked bool
		_ = a.db.QueryRow(r.Context(), `SELECT exists(SELECT 1 FROM work_sessions WHERE user_id=$1 AND work_date=CURRENT_DATE AND checked_out_at IS NULL)`, c.UserID).Scan(&checked)
		if !checked {
			fail(w, 409, "valide primeiro o início do trabalho")
			return
		}
	}
	tag, e := a.db.Exec(r.Context(), `UPDATE tasks SET status='IN_PROGRESS',started_at=now(),updated_at=now() WHERE id=$1 AND household_id=$2 AND assignee_id=$3 AND status='PLANNED' AND scheduled_date=CURRENT_DATE`, r.PathValue("id"), c.HouseholdID, c.UserID)
	if e != nil || tag.RowsAffected() == 0 {
		fail(w, 409, "tarefa não pode ser iniciada")
		return
	}
	write(w, 200, map[string]bool{"started": true})
}
func (a *API) completeTask(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	tx, e := a.db.Begin(r.Context())
	if e != nil {
		fail(w, 500, "erro interno")
		return
	}
	defer tx.Rollback(r.Context())
	var mins int
	var started time.Time
	var role string
	e = tx.QueryRow(r.Context(), `SELECT estimated_minutes,started_at,u.role FROM tasks t JOIN users u ON u.id=t.assignee_id WHERE t.id=$1 AND t.household_id=$2 AND t.assignee_id=$3 AND t.status='IN_PROGRESS' FOR UPDATE`, r.PathValue("id"), c.HouseholdID, c.UserID).Scan(&mins, &started, &role)
	if e != nil {
		fail(w, 409, "tarefa não está em execução")
		return
	}
	elapsed := int(time.Since(started).Minutes())
	if elapsed < 1 {
		elapsed = 1
	}
	xp := 20
	if elapsed <= mins {
		xp = 50
	}
	if elapsed <= max(1, mins*80/100) {
		xp = 75
	}
	_, e = tx.Exec(r.Context(), `UPDATE tasks SET status='DONE',completed_at=now(),xp_awarded=$1,updated_at=now() WHERE id=$2`, xp, r.PathValue("id"))
	if e == nil {
		_, e = tx.Exec(r.Context(), `INSERT INTO xp_ledger(household_id,user_id,task_id,points,reason) VALUES($1,$2,$3,$4,$5)`, c.HouseholdID, c.UserID, r.PathValue("id"), xp, fmt.Sprintf("Tarefa concluída em %d/%d minutos", elapsed, mins))
	}
	if e != nil {
		fail(w, 500, "erro ao concluir tarefa")
		return
	}
	if e = tx.Commit(r.Context()); e != nil {
		fail(w, 500, "erro ao concluir tarefa")
		return
	}
	a.ensureRewards(r.Context(), c.HouseholdID, c.UserID)
	write(w, 200, map[string]any{"completed": true, "xp": xp, "elapsedMinutes": elapsed, "withinEstimate": elapsed <= mins, "role": role})
}
func max(a, b int) int {
	if a > b {
		return a
	}
	return b
}
func (a *API) ensureRewards(ctx context.Context, hid, uid string) {
	var xp, bonus, dayoff int
	_ = a.db.QueryRow(ctx, `SELECT coalesce(sum(points),0),h.xp_bonus_threshold,h.xp_dayoff_threshold FROM households h LEFT JOIN xp_ledger x ON x.household_id=h.id AND x.user_id=$2 AND x.created_at>=date_trunc('month',now()) WHERE h.id=$1 GROUP BY h.id`, hid, uid).Scan(&xp, &bonus, &dayoff)
	month := time.Now().Format("2006-01")
	if xp >= bonus {
		_, _ = a.db.Exec(ctx, `INSERT INTO rewards(household_id,user_id,month,reward_type,xp_cost) VALUES($1,$2,$3,'BONUS',$4) ON CONFLICT DO NOTHING`, hid, uid, month, bonus)
	}
	if xp >= dayoff {
		_, _ = a.db.Exec(ctx, `INSERT INTO rewards(household_id,user_id,month,reward_type,xp_cost) VALUES($1,$2,$3,'DAY_OFF',$4) ON CONFLICT DO NOTHING`, hid, uid, month, dayoff)
	}
}
func (a *API) holidays(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	year := r.URL.Query().Get("year")
	if year == "" {
		year = strconv.Itoa(time.Now().Year())
	}
	rows, e := a.db.Query(r.Context(), `SELECT id,holiday_date::text,name,country_code FROM holidays WHERE household_id=$1 AND extract(year from holiday_date)=$2::int ORDER BY holiday_date`, c.HouseholdID, year)
	if e != nil {
		fail(w, 500, "erro ao listar feriados")
		return
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, d, n, cc string
		_ = rows.Scan(&id, &d, &n, &cc)
		out = append(out, map[string]any{"id": id, "date": d, "name": n, "countryCode": cc})
	}
	write(w, 200, out)
}
func (a *API) createHoliday(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	var in struct{ Date, Name, CountryCode string }
	if decode(r, &in) != nil || in.Date == "" || in.Name == "" {
		fail(w, 400, "dados inválidos")
		return
	}
	if in.CountryCode == "" {
		in.CountryCode = "AO"
	}
	_, e := a.db.Exec(r.Context(), `INSERT INTO holidays(household_id,holiday_date,name,country_code) VALUES($1,$2,$3,$4) ON CONFLICT(household_id,holiday_date) DO UPDATE SET name=excluded.name,country_code=excluded.country_code`, c.HouseholdID, in.Date, in.Name, strings.ToUpper(in.CountryCode))
	if e != nil {
		fail(w, 400, "não foi possível guardar o feriado")
		return
	}
	write(w, 201, map[string]bool{"created": true})
}
func (a *API) rewards(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	a.ensureRewards(r.Context(), c.HouseholdID, c.UserID)
	query := `SELECT id,month,reward_type,xp_cost,status,created_at FROM rewards WHERE household_id=$1`
	args := []any{c.HouseholdID}
	if c.Role != "OWNER" {
		query += ` AND user_id=$2`
		args = append(args, c.UserID)
	}
	query += ` ORDER BY created_at DESC`
	rows, e := a.db.Query(r.Context(), query, args...)
	if e != nil {
		fail(w, 500, "erro ao listar recompensas")
		return
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var id, m, t, s string
		var xp int
		var at time.Time
		_ = rows.Scan(&id, &m, &t, &xp, &s, &at)
		out = append(out, map[string]any{"id": id, "month": m, "type": t, "xpCost": xp, "status": s, "createdAt": at})
	}
	write(w, 200, out)
}
func (a *API) claimReward(w http.ResponseWriter, r *http.Request) {
	c := claims(r)
	tag, e := a.db.Exec(r.Context(), `UPDATE rewards SET status='CLAIMED' WHERE id=$1 AND user_id=$2 AND status='AVAILABLE'`, r.PathValue("id"), c.UserID)
	if e != nil || tag.RowsAffected() == 0 {
		fail(w, 409, "recompensa indisponível")
		return
	}
	write(w, 200, map[string]bool{"claimed": true})
}

func cors(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Headers", "Authorization, Content-Type")
		w.Header().Set("Access-Control-Allow-Methods", "GET,POST,PATCH,DELETE,OPTIONS")
		if r.Method == http.MethodOptions {
			w.WriteHeader(204)
			return
		}
		next.ServeHTTP(w, r)
	})
}
func logging(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		log.Printf("%s %s %s", r.Method, r.URL.Path, time.Since(start))
	})
}

var _ = errors.Is
var _ = pgx.ErrNoRows
