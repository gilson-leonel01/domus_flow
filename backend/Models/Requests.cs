namespace DomusFlow.Api.Models;

public sealed record LoginRequest(string Email, string Password);

public sealed record TaskRequest(
    string Title,
    string? Description,
    string ScheduledDate,
    string? StartTime,
    int EstimatedMinutes,
    int Priority,
    string AssigneeId);

public sealed record HolidayRequest(string Date, string Name, string? CountryCode);
