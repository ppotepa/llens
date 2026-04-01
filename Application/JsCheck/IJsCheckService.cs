using Llens.Api;
using Llens.Models;

namespace Llens.Application.JsCheck;

public interface IJsCheckService
{
    Task<JsCheckOutcome> RunAsync(Project project, CompactJsCheckRequest request, CancellationToken ct);
}

public enum JsCheckErrorKind
{
    None = 0,
    BadRequest = 1,
    NotFound = 2
}

public sealed record JsCheckOutcome(
    bool Ok,
    CompactJsCheckResponse? Response = null,
    JsCheckErrorKind ErrorKind = JsCheckErrorKind.None,
    string? ErrorMessage = null);
