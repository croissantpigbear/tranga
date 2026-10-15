using API.Schema.LibraryContext;
using API.Schema.NotificationsContext;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

/// <summary>
/// Tests whether library servers (Komga/Kavita) and notification services can be reached.
/// </summary>
[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class ConnectionTestsController(LibraryContext libraryContext, NotificationsContext notificationsContext) : ControllerBase
{
    /// <summary>Result of a connection test</summary>
    /// <param name="Ok">Whether it worked</param>
    /// <param name="Message">Details</param>
    public record TestResult(bool Ok, string Message);

    /// <summary>
    /// Tests the connection to a library server (Komga/Kavita), including its login
    /// </summary>
    /// <param name="LibraryConnectorId">LibraryConnector.Key</param>
    /// <response code="200"></response>
    /// <response code="404">Library connector not found</response>
    [HttpPost("LibraryConnector/{LibraryConnectorId}")]
    [ProducesResponseType<TestResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<TestResult>, NotFound<string>>> TestLibraryConnector(string LibraryConnectorId)
    {
        if (await libraryContext.LibraryConnectors.FirstOrDefaultAsync(c => c.Key == LibraryConnectorId, HttpContext.RequestAborted) is not { } connector)
            return TypedResults.NotFound(nameof(LibraryConnectorId));
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            bool ok = await connector.Test(timeout.Token);
            return TypedResults.Ok(new TestResult(ok, ok
                ? $"Connected to {connector.LibraryType} at {connector.BaseUrl}"
                : $"{connector.LibraryType} at {connector.BaseUrl} answered, but the login was rejected or the request failed"));
        }
        catch (Exception e)
        {
            return TypedResults.Ok(new TestResult(false, $"Could not reach {connector.LibraryType} at {connector.BaseUrl}: {e.Message}"));
        }
    }

    /// <summary>
    /// Sends a test notification through a notification service
    /// </summary>
    /// <param name="Name">NotificationConnector.Name</param>
    /// <response code="200"></response>
    /// <response code="404">Notification connector not found</response>
    [HttpPost("NotificationConnector/{Name}")]
    [ProducesResponseType<TestResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<TestResult>, NotFound<string>>> TestNotificationConnector(string Name)
    {
        if (await notificationsContext.NotificationConnectors.FirstOrDefaultAsync(c => c.Name == Name, HttpContext.RequestAborted) is not { } connector)
            return TypedResults.NotFound(nameof(Name));
        try
        {
            System.Net.HttpStatusCode status = await Task.Run(
                () => connector.SendNotification("Tranga test notification", "If you can read this, notifications work."),
                HttpContext.RequestAborted);
            int code = (int)status;
            return TypedResults.Ok(new TestResult(code is >= 200 and < 300, $"{connector.Name} answered HTTP {code} ({status})"));
        }
        catch (Exception e)
        {
            return TypedResults.Ok(new TestResult(false, $"Could not send to {connector.Name}: {e.Message}"));
        }
    }
}
