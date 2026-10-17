using API.Scheduling;
using API.Workers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// How often Tranga's scheduled jobs run (chapter checks, metadata, covers, notifications, maintenance)
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class TaskSchedulesController : ControllerBase
{
    /// <summary>A scheduled job</summary>
    /// <param name="Id">Use this to change it</param>
    /// <param name="Group">downloads, metadata, notifications, maintenance</param>
    /// <param name="Title">Name</param>
    /// <param name="Description">What it does and what changing it means</param>
    /// <param name="Minutes">Minutes between runs now</param>
    /// <param name="RecommendedMinutes">Tranga's recommendation</param>
    /// <param name="DefaultMinutes">Built-in value (or from an environment variable)</param>
    /// <param name="MinMinutes">Lowest allowed</param>
    /// <param name="MaxMinutes">Highest allowed</param>
    /// <param name="LastRunUtc">Last run (null if it hasn't run since Tranga started)</param>
    /// <param name="NextRunUtc">Next run</param>
    /// <param name="Running">Running right now</param>
    public record TaskScheduleDto(string Id, string Group, string Title, string Description, int Minutes, int RecommendedMinutes,
        int DefaultMinutes, int MinMinutes, int MaxMinutes, DateTime? LastRunUtc, DateTime? NextRunUtc, bool Running);

    /// <summary>
    /// Returns every scheduled job with its interval and the recommended one
    /// </summary>
    /// <response code="200"></response>
    [HttpGet]
    [ProducesResponseType<List<TaskScheduleDto>>(Status200OK, "application/json")]
    public Ok<List<TaskScheduleDto>> Get() => TypedResults.Ok(Current());

    /// <summary>
    /// Changes how often jobs run (applies straight away)
    /// </summary>
    /// <param name="minutes">Job id → minutes between runs</param>
    /// <response code="200">All jobs</response>
    /// <response code="400">Unknown job or value out of range</response>
    [HttpPatch]
    [ProducesResponseType<List<TaskScheduleDto>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    public Results<Ok<List<TaskScheduleDto>>, BadRequest<string>> Set([FromBody] Dictionary<string, int> minutes)
    {
        if (TaskSchedules.Set(minutes) is { } error)
            return TypedResults.BadRequest(error);
        return TypedResults.Ok(Current());
    }

    /// <summary>
    /// Sets every job back to the recommended interval
    /// </summary>
    /// <response code="200">All jobs</response>
    [HttpPost("Reset")]
    [ProducesResponseType<List<TaskScheduleDto>>(Status200OK, "application/json")]
    public Ok<List<TaskScheduleDto>> Reset()
    {
        TaskSchedules.ResetAll();
        return TypedResults.Ok(Current());
    }

    /// <summary>
    /// Runs a job now (within a few seconds) instead of waiting for its next run
    /// </summary>
    /// <param name="Id">Job id</param>
    /// <response code="202"></response>
    /// <response code="404">Unknown job</response>
    [HttpPost("{Id}/RunNow")]
    [ProducesResponseType(Status202Accepted)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Results<Accepted, NotFound<string>> RunNow(string Id) =>
        TaskSchedules.RequestRunNow(Id) ? TypedResults.Accepted((string?)null) : TypedResults.NotFound(nameof(Id));

    private static List<TaskScheduleDto> Current() => TaskSchedules.All.Select(d =>
    {
        IPeriodic w = d.Worker();
        bool ran = w.LastExecution > DateTime.UnixEpoch;
        bool running = w is BaseWorker b && b.State == WorkerExecutionState.Running;
        return new TaskScheduleDto(d.Id, d.Group, d.Title, d.Description, (int)Math.Round(w.Interval.TotalMinutes), d.RecommendedMinutes,
            (int)Math.Round(TaskSchedules.DefaultOf(d).TotalMinutes), d.MinMinutes, d.MaxMinutes,
            ran ? w.LastExecution : null, ran ? w.NextExecution : null, running);
    }).ToList();
}
