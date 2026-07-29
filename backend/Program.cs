using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DomusFlow.Api.Infrastructure;
using DomusFlow.Api.Models;
using DomusFlow.Api.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration["PORT"] ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var jwtSecret = builder.Configuration["JWT_SECRET"]
    ?? builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("JWT secret is not configured.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "DomusFlow";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "DomusFlow.Web";
if (Encoding.UTF8.GetByteCount(jwtSecret) < 32)
{
    throw new InvalidOperationException("JWT_SECRET must contain at least 32 bytes.");
}

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<AppClock>();
builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "uid",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return context.Response.WriteAsJsonAsync(new { error = "token em falta ou inválido" });
            },
            OnForbidden = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return context.Response.WriteAsJsonAsync(new { error = "sem permissão" });
            }
        };
    });
builder.Services.AddAuthorization();

var allowedOrigins = (builder.Configuration["CORS_ORIGINS"] ?? string.Empty)
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length == 0)
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            policy.WithOrigins(allowedOrigins);
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        app.Logger.LogError(exception, "Unhandled API error");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "erro interno do servidor" });
    });
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

await app.Services.GetRequiredService<Database>().MigrateAsync();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api");

api.MapPost("/auth/login", async (LoginRequest input, Database db, JwtTokenService tokens, CancellationToken ct) =>
{
    var email = input.Email?.Trim();
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(input.Password))
    {
        return Error(StatusCodes.Status400BadRequest, "dados inválidos");
    }

    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        SELECT u.id::text, u.household_id::text, u.name, u.email, u.role::text,
               coalesce(u.avatar, ''), h.name
        FROM users u
        JOIN households h ON h.id = u.household_id
        WHERE lower(u.email) = lower(@email)
          AND u.active
          AND u.password_hash = crypt(@password, u.password_hash)
        """, connection);
    command.Parameters.AddWithValue("email", email);
    command.Parameters.AddWithValue("password", input.Password);

    await using var reader = await command.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
    {
        return Error(StatusCodes.Status401Unauthorized, "credenciais inválidas");
    }

    var id = reader.GetString(0);
    var householdId = reader.GetString(1);
    var name = reader.GetString(2);
    var canonicalEmail = reader.GetString(3);
    var role = reader.GetString(4);
    var avatar = reader.GetString(5);
    var householdName = reader.GetString(6);

    return Results.Ok(new
    {
        token = tokens.Create(id, householdId, role),
        user = new { id, householdId, householdName, name, email = canonicalEmail, role, avatar }
    });
});

api.MapGet("/me", async (ClaimsPrincipal principal, Database db, CancellationToken ct) =>
{
    var auth = principal.Auth();
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        SELECT u.name, u.email, coalesce(u.avatar, ''), h.name
        FROM users u
        JOIN households h ON h.id = u.household_id
        WHERE u.id = @id AND u.active
        """, connection);
    command.Parameters.AddWithValue("id", Guid.Parse(auth.UserId));

    await using var reader = await command.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
    {
        return Error(StatusCodes.Status404NotFound, "utilizador não encontrado");
    }

    return Results.Ok(new
    {
        id = auth.UserId,
        householdId = auth.HouseholdId,
        name = reader.GetString(0),
        email = reader.GetString(1),
        role = auth.Role,
        avatar = reader.GetString(2),
        householdName = reader.GetString(3)
    });
}).RequireAuthorization();

api.MapGet("/dashboard", async (
    string? date,
    ClaimsPrincipal principal,
    Database db,
    AppClock clock,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    var selectedDate = ParseDate(date) ?? clock.Today;

    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    var (locked, reason) = await BusinessLockedAsync(connection, auth.HouseholdId, selectedDate, ct);

    var query = new StringBuilder(
        """
        SELECT count(*)::int,
               count(*) FILTER (WHERE status = 'DONE')::int,
               count(*) FILTER (WHERE status = 'IN_PROGRESS')::int,
               coalesce(sum(xp_awarded), 0)::int
        FROM tasks
        WHERE household_id = @householdId AND scheduled_date = @date
        """);
    if (auth.Role != "OWNER")
    {
        query.Append(" AND assignee_id = @userId");
    }

    await using var totals = new NpgsqlCommand(query.ToString(), connection);
    totals.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    totals.Parameters.AddWithValue("date", selectedDate);
    if (auth.Role != "OWNER")
    {
        totals.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
    }

    var total = 0;
    var done = 0;
    var inProgress = 0;
    var dayXp = 0;
    await using (var reader = await totals.ExecuteReaderAsync(ct))
    {
        if (await reader.ReadAsync(ct))
        {
            total = reader.GetInt32(0);
            done = reader.GetInt32(1);
            inProgress = reader.GetInt32(2);
            dayXp = reader.GetInt32(3);
        }
    }

    await using var monthXpCommand = new NpgsqlCommand(
        """
        SELECT coalesce(sum(x.points), 0)::int,
               h.xp_bonus_threshold,
               h.xp_dayoff_threshold
        FROM households h
        LEFT JOIN xp_ledger x
          ON x.household_id = h.id
         AND x.user_id = @userId
         AND x.created_at >= @monthStart
        WHERE h.id = @householdId
        GROUP BY h.id
        """, connection);
    monthXpCommand.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
    monthXpCommand.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    monthXpCommand.Parameters.AddWithValue("monthStart", clock.CurrentMonthStartUtc);
    var monthXp = 0;
    var bonusThreshold = 1000;
    var dayOffThreshold = 1500;
    await using (var reader = await monthXpCommand.ExecuteReaderAsync(ct))
    {
        if (await reader.ReadAsync(ct))
        {
            monthXp = reader.GetInt32(0);
            bonusThreshold = reader.GetInt32(1);
            dayOffThreshold = reader.GetInt32(2);
        }
    }

    await using var checkInCommand = new NpgsqlCommand(
        """
        SELECT EXISTS(
            SELECT 1 FROM work_sessions
            WHERE user_id = @userId AND work_date = @date AND checked_out_at IS NULL
        )
        """, connection);
    checkInCommand.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
    checkInCommand.Parameters.AddWithValue("date", selectedDate);
    var checkedIn = await checkInCommand.ExecuteScalarAsync(ct) is true;

    return Results.Ok(new
    {
        date = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        total,
        done,
        inProgress,
        dayXP = dayXp,
        monthXP = monthXp,
        bonusThreshold,
        dayOffThreshold,
        workLocked = locked && auth.Role == "EMPLOYEE",
        lockReason = reason,
        checkedIn
    });
}).RequireAuthorization();

api.MapGet("/users", async (ClaimsPrincipal principal, Database db, CancellationToken ct) =>
{
    var auth = principal.Auth();
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        SELECT id::text, name, email, role::text, coalesce(avatar, '')
        FROM users
        WHERE household_id = @householdId AND active
        ORDER BY role, name
        """, connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));

    var users = new List<object>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        users.Add(new
        {
            id = reader.GetString(0),
            name = reader.GetString(1),
            email = reader.GetString(2),
            role = reader.GetString(3),
            avatar = reader.GetString(4)
        });
    }

    return Results.Ok(users);
}).RequireAuthorization();

api.MapGet("/tasks", async (
    string? date,
    ClaimsPrincipal principal,
    Database db,
    AppClock clock,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    var selectedDate = ParseDate(date) ?? clock.Today;
    var sql = new StringBuilder(
        """
        SELECT t.id::text, t.title, t.description, t.scheduled_date, t.start_time,
               t.estimated_minutes, t.priority::int, t.status::text,
               t.started_at, t.completed_at, t.xp_awarded,
               u.id::text, u.name, u.email, u.role::text, coalesce(u.avatar, '')
        FROM tasks t
        JOIN users u ON u.id = t.assignee_id
        WHERE t.household_id = @householdId AND t.scheduled_date = @date
        """);
    if (auth.Role != "OWNER")
    {
        sql.Append(" AND t.assignee_id = @userId");
    }
    sql.Append(" ORDER BY t.start_time NULLS LAST, t.created_at");

    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(sql.ToString(), connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    command.Parameters.AddWithValue("date", selectedDate);
    if (auth.Role != "OWNER")
    {
        command.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
    }

    var tasks = new List<object>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        tasks.Add(new
        {
            id = reader.GetString(0),
            title = reader.GetString(1),
            description = reader.GetString(2),
            scheduledDate = reader.GetFieldValue<DateOnly>(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            startTime = reader.IsDBNull(4) ? null : reader.GetFieldValue<TimeOnly>(4).ToString("HH:mm", CultureInfo.InvariantCulture),
            estimatedMinutes = reader.GetInt32(5),
            priority = reader.GetInt32(6),
            status = reader.GetString(7),
            startedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTime>(8),
            completedAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTime>(9),
            xpAwarded = reader.GetInt32(10),
            assignee = new
            {
                id = reader.GetString(11),
                name = reader.GetString(12),
                email = reader.GetString(13),
                role = reader.GetString(14),
                avatar = reader.GetString(15)
            }
        });
    }

    return Results.Ok(tasks);
}).RequireAuthorization();

api.MapPost("/tasks", async (
    TaskRequest input,
    ClaimsPrincipal principal,
    Database db,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    var validation = ValidateTask(input);
    if (validation.Error is not null)
    {
        return Error(StatusCodes.Status400BadRequest, validation.Error);
    }

    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        INSERT INTO tasks(
            household_id, assignee_id, created_by, title, description,
            scheduled_date, start_time, estimated_minutes, priority)
        SELECT @householdId, @assigneeId, @createdBy, @title, @description,
               @scheduledDate, @startTime, @estimatedMinutes, @priority
        WHERE EXISTS(
            SELECT 1 FROM users
            WHERE id = @assigneeId AND household_id = @householdId AND active)
        RETURNING id::text
        """, connection);
    AddTaskParameters(command, input, validation, auth);

    var id = await command.ExecuteScalarAsync(ct) as string;
    return id is null
        ? Error(StatusCodes.Status400BadRequest, "não foi possível criar a tarefa")
        : Results.Created($"/api/tasks/{id}", new { id });
}).RequireAuthorization(policy => policy.RequireRole("OWNER"));

api.MapPatch("/tasks/{id:guid}", async (
    Guid id,
    TaskRequest input,
    ClaimsPrincipal principal,
    Database db,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    var validation = ValidateTask(input);
    if (validation.Error is not null)
    {
        return Error(StatusCodes.Status400BadRequest, validation.Error);
    }

    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        UPDATE tasks
        SET title = @title,
            description = @description,
            scheduled_date = @scheduledDate,
            start_time = @startTime,
            estimated_minutes = @estimatedMinutes,
            priority = @priority,
            assignee_id = @assigneeId,
            updated_at = now()
        WHERE id = @id
          AND household_id = @householdId
          AND status = 'PLANNED'
          AND EXISTS(
              SELECT 1 FROM users
              WHERE users.id = @assigneeId AND users.household_id = @householdId AND users.active)
        """, connection);
    AddTaskParameters(command, input, validation, auth);
    command.Parameters.AddWithValue("id", id);

    var changed = await command.ExecuteNonQueryAsync(ct);
    return changed == 0
        ? Error(StatusCodes.Status404NotFound, "tarefa não encontrada ou já iniciada")
        : Results.Ok(new { updated = true });
}).RequireAuthorization(policy => policy.RequireRole("OWNER"));

api.MapDelete("/tasks/{id:guid}", async (
    Guid id,
    ClaimsPrincipal principal,
    Database db,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        "DELETE FROM tasks WHERE id = @id AND household_id = @householdId AND status = 'PLANNED'", connection);
    command.Parameters.AddWithValue("id", id);
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));

    return await command.ExecuteNonQueryAsync(ct) == 0
        ? Error(StatusCodes.Status404NotFound, "tarefa não encontrada ou já iniciada")
        : Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole("OWNER"));

api.MapPost("/work/check-in", async (
    ClaimsPrincipal principal,
    Database db,
    AppClock clock,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    var (locked, reason) = await BusinessLockedAsync(connection, auth.HouseholdId, clock.Today, ct);
    if (locked)
    {
        return Error(StatusCodes.Status403Forbidden, reason);
    }

    await using var command = new NpgsqlCommand(
        """
        INSERT INTO work_sessions(household_id, user_id, work_date)
        VALUES (@householdId, @userId, @date)
        ON CONFLICT(user_id, work_date)
        DO UPDATE SET checked_in_at = now(), checked_out_at = NULL
        """, connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    command.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
    command.Parameters.AddWithValue("date", clock.Today);
    await command.ExecuteNonQueryAsync(ct);

    return Results.Ok(new { checkedIn = true, at = clock.UtcNow });
}).RequireAuthorization(policy => policy.RequireRole("EMPLOYEE"));

api.MapPost("/work/check-out", async (
    ClaimsPrincipal principal,
    Database db,
    AppClock clock,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        UPDATE work_sessions
        SET checked_out_at = now()
        WHERE user_id = @userId AND work_date = @date AND checked_out_at IS NULL
        """, connection);
    command.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
    command.Parameters.AddWithValue("date", clock.Today);

    var changed = await command.ExecuteNonQueryAsync(ct);
    return changed == 0
        ? Error(StatusCodes.Status409Conflict, "não existe jornada ativa")
        : Results.Ok(new { checkedIn = false });
}).RequireAuthorization(policy => policy.RequireRole("EMPLOYEE"));

api.MapPost("/tasks/{id:guid}/start", async (
    Guid id,
    ClaimsPrincipal principal,
    Database db,
    AppClock clock,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);

    if (auth.Role == "EMPLOYEE")
    {
        var (locked, reason) = await BusinessLockedAsync(connection, auth.HouseholdId, clock.Today, ct);
        if (locked)
        {
            return Error(StatusCodes.Status403Forbidden, reason);
        }

        await using var check = new NpgsqlCommand(
            """
            SELECT EXISTS(
                SELECT 1 FROM work_sessions
                WHERE user_id = @userId AND work_date = @date AND checked_out_at IS NULL)
            """, connection);
        check.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
        check.Parameters.AddWithValue("date", clock.Today);
        if (await check.ExecuteScalarAsync(ct) is not true)
        {
            return Error(StatusCodes.Status409Conflict, "valide primeiro o início do trabalho");
        }
    }

    await using var command = new NpgsqlCommand(
        """
        UPDATE tasks
        SET status = 'IN_PROGRESS', started_at = now(), updated_at = now()
        WHERE id = @id
          AND household_id = @householdId
          AND assignee_id = @userId
          AND status = 'PLANNED'
          AND scheduled_date = @date
        """, connection);
    command.Parameters.AddWithValue("id", id);
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    command.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
    command.Parameters.AddWithValue("date", clock.Today);

    return await command.ExecuteNonQueryAsync(ct) == 0
        ? Error(StatusCodes.Status409Conflict, "tarefa não pode ser iniciada")
        : Results.Ok(new { started = true });
}).RequireAuthorization();

api.MapPost("/tasks/{id:guid}/complete", async (
    Guid id,
    ClaimsPrincipal principal,
    Database db,
    AppClock clock,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var transaction = await connection.BeginTransactionAsync(ct);

    await using var read = new NpgsqlCommand(
        """
        SELECT t.estimated_minutes, t.started_at, u.role::text
        FROM tasks t
        JOIN users u ON u.id = t.assignee_id
        WHERE t.id = @id
          AND t.household_id = @householdId
          AND t.assignee_id = @userId
          AND t.status = 'IN_PROGRESS'
        FOR UPDATE
        """, connection, transaction);
    read.Parameters.AddWithValue("id", id);
    read.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    read.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));

    int estimatedMinutes;
    DateTime startedAt;
    string role;
    await using (var reader = await read.ExecuteReaderAsync(ct))
    {
        if (!await reader.ReadAsync(ct))
        {
            await transaction.RollbackAsync(ct);
            return Error(StatusCodes.Status409Conflict, "tarefa não está em execução");
        }

        estimatedMinutes = reader.GetInt32(0);
        startedAt = reader.GetFieldValue<DateTime>(1);
        role = reader.GetString(2);
    }

    var elapsedMinutes = Math.Max(1, (int)Math.Floor((clock.UtcNow.UtcDateTime - startedAt.ToUniversalTime()).TotalMinutes));
    var xp = elapsedMinutes <= Math.Max(1, estimatedMinutes * 80 / 100)
        ? 75
        : elapsedMinutes <= estimatedMinutes ? 50 : 20;

    await using (var update = new NpgsqlCommand(
        """
        UPDATE tasks
        SET status = 'DONE', completed_at = now(), xp_awarded = @xp, updated_at = now()
        WHERE id = @id
        """, connection, transaction))
    {
        update.Parameters.AddWithValue("xp", xp);
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(ct);
    }

    await using (var ledger = new NpgsqlCommand(
        """
        INSERT INTO xp_ledger(household_id, user_id, task_id, points, reason)
        VALUES (@householdId, @userId, @taskId, @points, @reason)
        """, connection, transaction))
    {
        ledger.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
        ledger.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
        ledger.Parameters.AddWithValue("taskId", id);
        ledger.Parameters.AddWithValue("points", xp);
        ledger.Parameters.AddWithValue("reason", $"Tarefa concluída em {elapsedMinutes}/{estimatedMinutes} minutos");
        await ledger.ExecuteNonQueryAsync(ct);
    }

    await transaction.CommitAsync(ct);
    await EnsureRewardsAsync(db, clock, auth.HouseholdId, auth.UserId, ct);

    return Results.Ok(new
    {
        completed = true,
        xp,
        elapsedMinutes,
        withinEstimate = elapsedMinutes <= estimatedMinutes,
        role
    });
}).RequireAuthorization();

api.MapGet("/holidays", async (
    int? year,
    ClaimsPrincipal principal,
    Database db,
    AppClock clock,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    var selectedYear = year is >= 2000 and <= 2200 ? year.Value : clock.Today.Year;
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        SELECT id::text, holiday_date, name, country_code
        FROM holidays
        WHERE household_id = @householdId AND extract(year from holiday_date) = @year
        ORDER BY holiday_date
        """, connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    command.Parameters.AddWithValue("year", selectedYear);

    var holidays = new List<object>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        holidays.Add(new
        {
            id = reader.GetString(0),
            date = reader.GetFieldValue<DateOnly>(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            name = reader.GetString(2),
            countryCode = reader.GetString(3).Trim()
        });
    }

    return Results.Ok(holidays);
}).RequireAuthorization();

api.MapPost("/holidays", async (
    HolidayRequest input,
    ClaimsPrincipal principal,
    Database db,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    var date = ParseDate(input.Date);
    if (date is null || string.IsNullOrWhiteSpace(input.Name))
    {
        return Error(StatusCodes.Status400BadRequest, "dados inválidos");
    }

    var countryCode = string.IsNullOrWhiteSpace(input.CountryCode)
        ? "AO"
        : input.CountryCode.Trim().ToUpperInvariant();
    if (countryCode.Length != 2)
    {
        return Error(StatusCodes.Status400BadRequest, "código do país inválido");
    }

    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        INSERT INTO holidays(household_id, holiday_date, name, country_code)
        VALUES (@householdId, @date, @name, @countryCode)
        ON CONFLICT(household_id, holiday_date)
        DO UPDATE SET name = excluded.name, country_code = excluded.country_code
        """, connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    command.Parameters.AddWithValue("date", date.Value);
    command.Parameters.AddWithValue("name", input.Name.Trim());
    command.Parameters.AddWithValue("countryCode", countryCode);
    await command.ExecuteNonQueryAsync(ct);

    return Results.Created("/api/holidays", new { created = true });
}).RequireAuthorization(policy => policy.RequireRole("OWNER"));

api.MapGet("/rewards", async (
    ClaimsPrincipal principal,
    Database db,
    AppClock clock,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    await EnsureRewardsAsync(db, clock, auth.HouseholdId, auth.UserId, ct);

    var sql = new StringBuilder(
        """
        SELECT id::text, month, reward_type, xp_cost, status, created_at
        FROM rewards
        WHERE household_id = @householdId
        """);
    if (auth.Role != "OWNER")
    {
        sql.Append(" AND user_id = @userId");
    }
    sql.Append(" ORDER BY created_at DESC");

    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(sql.ToString(), connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    if (auth.Role != "OWNER")
    {
        command.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));
    }

    var rewards = new List<object>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        rewards.Add(new
        {
            id = reader.GetString(0),
            month = reader.GetString(1),
            type = reader.GetString(2),
            xpCost = reader.GetInt32(3),
            status = reader.GetString(4),
            createdAt = reader.GetFieldValue<DateTime>(5)
        });
    }

    return Results.Ok(rewards);
}).RequireAuthorization();

api.MapPost("/rewards/{id:guid}/claim", async (
    Guid id,
    ClaimsPrincipal principal,
    Database db,
    CancellationToken ct) =>
{
    var auth = principal.Auth();
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        UPDATE rewards
        SET status = 'CLAIMED'
        WHERE id = @id AND user_id = @userId AND status = 'AVAILABLE'
        """, connection);
    command.Parameters.AddWithValue("id", id);
    command.Parameters.AddWithValue("userId", Guid.Parse(auth.UserId));

    return await command.ExecuteNonQueryAsync(ct) == 0
        ? Error(StatusCodes.Status409Conflict, "recompensa indisponível")
        : Results.Ok(new { claimed = true });
}).RequireAuthorization();

app.Run();

static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

static DateOnly? ParseDate(string? value) =>
    DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date
        : null;

static TaskValidation ValidateTask(TaskRequest input)
{
    if (string.IsNullOrWhiteSpace(input.Title)
        || string.IsNullOrWhiteSpace(input.AssigneeId)
        || !Guid.TryParse(input.AssigneeId, out _)
        || input.EstimatedMinutes < 1
        || input.EstimatedMinutes > 24 * 60
        || input.Priority is < 1 or > 3)
    {
        return new("dados da tarefa inválidos", null, null);
    }

    var scheduledDate = ParseDate(input.ScheduledDate);
    if (scheduledDate is null)
    {
        return new("data da tarefa inválida", null, null);
    }

    TimeOnly? startTime = null;
    if (!string.IsNullOrWhiteSpace(input.StartTime))
    {
        if (!TimeOnly.TryParseExact(input.StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
        {
            return new("hora da tarefa inválida", null, null);
        }
        startTime = parsedTime;
    }

    return new(null, scheduledDate, startTime);
}

static void AddTaskParameters(
    NpgsqlCommand command,
    TaskRequest input,
    TaskValidation validation,
    AuthContext auth)
{
    command.Parameters.AddWithValue("householdId", Guid.Parse(auth.HouseholdId));
    command.Parameters.AddWithValue("assigneeId", Guid.Parse(input.AssigneeId));
    command.Parameters.AddWithValue("createdBy", Guid.Parse(auth.UserId));
    command.Parameters.AddWithValue("title", input.Title.Trim());
    command.Parameters.AddWithValue("description", input.Description?.Trim() ?? string.Empty);
    command.Parameters.AddWithValue("scheduledDate", validation.ScheduledDate!.Value);
    command.Parameters.Add("startTime", NpgsqlTypes.NpgsqlDbType.Time).Value =
        validation.StartTime is null ? DBNull.Value : validation.StartTime.Value;
    command.Parameters.AddWithValue("estimatedMinutes", input.EstimatedMinutes);
    command.Parameters.AddWithValue("priority", input.Priority);
}

static async Task<(bool Locked, string Reason)> BusinessLockedAsync(
    NpgsqlConnection connection,
    string householdId,
    DateOnly date,
    CancellationToken ct)
{
    if (date.DayOfWeek == DayOfWeek.Sunday)
    {
        return (true, "Domingo: apenas tarefas dos filhos estão disponíveis");
    }

    await using var command = new NpgsqlCommand(
        "SELECT name FROM holidays WHERE household_id = @householdId AND holiday_date = @date", connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(householdId));
    command.Parameters.AddWithValue("date", date);
    var holiday = await command.ExecuteScalarAsync(ct) as string;
    return holiday is null ? (false, string.Empty) : (true, $"Feriado: {holiday}");
}

static async Task EnsureRewardsAsync(
    Database db,
    AppClock clock,
    string householdId,
    string userId,
    CancellationToken ct)
{
    await using var connection = db.OpenConnection();
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        SELECT coalesce(sum(x.points), 0)::int,
               h.xp_bonus_threshold,
               h.xp_dayoff_threshold
        FROM households h
        LEFT JOIN xp_ledger x
          ON x.household_id = h.id
         AND x.user_id = @userId
         AND x.created_at >= @monthStart
        WHERE h.id = @householdId
        GROUP BY h.id
        """, connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(householdId));
    command.Parameters.AddWithValue("userId", Guid.Parse(userId));
    command.Parameters.AddWithValue("monthStart", clock.CurrentMonthStartUtc);

    var xp = 0;
    var bonus = int.MaxValue;
    var dayOff = int.MaxValue;
    await using (var reader = await command.ExecuteReaderAsync(ct))
    {
        if (await reader.ReadAsync(ct))
        {
            xp = reader.GetInt32(0);
            bonus = reader.GetInt32(1);
            dayOff = reader.GetInt32(2);
        }
    }

    var month = clock.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    if (xp >= bonus)
    {
        await InsertRewardAsync(connection, householdId, userId, month, "BONUS", bonus, ct);
    }
    if (xp >= dayOff)
    {
        await InsertRewardAsync(connection, householdId, userId, month, "DAY_OFF", dayOff, ct);
    }
}

static async Task InsertRewardAsync(
    NpgsqlConnection connection,
    string householdId,
    string userId,
    string month,
    string type,
    int cost,
    CancellationToken ct)
{
    await using var command = new NpgsqlCommand(
        """
        INSERT INTO rewards(household_id, user_id, month, reward_type, xp_cost)
        VALUES (@householdId, @userId, @month, @type, @cost)
        ON CONFLICT DO NOTHING
        """, connection);
    command.Parameters.AddWithValue("householdId", Guid.Parse(householdId));
    command.Parameters.AddWithValue("userId", Guid.Parse(userId));
    command.Parameters.AddWithValue("month", month);
    command.Parameters.AddWithValue("type", type);
    command.Parameters.AddWithValue("cost", cost);
    await command.ExecuteNonQueryAsync(ct);
}

internal sealed record TaskValidation(string? Error, DateOnly? ScheduledDate, TimeOnly? StartTime);
internal sealed record AuthContext(string UserId, string HouseholdId, string Role);

internal static class ClaimsPrincipalExtensions
{
    public static AuthContext Auth(this ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue("uid")
            ?? throw new UnauthorizedAccessException("Missing uid claim.");
        var householdId = principal.FindFirstValue("hid")
            ?? throw new UnauthorizedAccessException("Missing hid claim.");
        var role = principal.FindFirstValue("role")
            ?? throw new UnauthorizedAccessException("Missing role claim.");
        return new(userId, householdId, role);
    }
}

public sealed class AppClock(IConfiguration configuration)
{
    private readonly TimeZoneInfo _timeZone = ResolveTimeZone(
        configuration["APP_TIMEZONE"] ?? "Africa/Luanda");

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(UtcNow, _timeZone).DateTime);

    public DateTimeOffset CurrentMonthStartUtc
    {
        get
        {
            var localNow = TimeZoneInfo.ConvertTime(UtcNow, _timeZone);
            var localStart = new DateTime(localNow.Year, localNow.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(localStart, _timeZone);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
