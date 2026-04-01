using Llens.Api;
using Llens.Models;

namespace Llens.Application.Fs;

public interface ICompactFsService
{
    CompactFsTreeOutcome Tree(Project project, CompactFsTreeRequest request);
    Task<CompactFsReadRangeOutcome> ReadRangeAsync(Project project, CompactFsReadRangeRequest request, CancellationToken ct);
    CompactFsWriteFileOutcome WriteFile(Project project, CompactFsWriteFileRequest request);
    Task<CompactFsEditOutcome> EditAsync(Project project, CompactFsEditRequest request, CancellationToken ct);
    Task<CompactFsDiffOutcome> DiffAsync(Project project, CompactFsDiffRequest request, CancellationToken ct);
    Task<CompactGitStatusOutcome> GitStatusAsync(Project project, CompactGitStatusRequest request, CancellationToken ct);
}

public enum FsErrorKind
{
    None = 0,
    BadRequest = 1,
    NotFound = 2,
    Conflict = 3
}

public sealed record CompactFsTreeOutcome(bool Ok, CompactFsTreeResponse? Response = null, FsErrorKind ErrorKind = FsErrorKind.None, string? ErrorMessage = null);
public sealed record CompactFsReadRangeOutcome(bool Ok, CompactFsReadRangeResponse? Response = null, FsErrorKind ErrorKind = FsErrorKind.None, string? ErrorMessage = null);
public sealed record CompactFsWriteFileOutcome(bool Ok, CompactFsWriteFileResponse? Response = null, FsErrorKind ErrorKind = FsErrorKind.None, string? ErrorMessage = null);
public sealed record CompactFsEditOutcome(bool Ok, CompactFsEditResponse? Response = null, FsErrorKind ErrorKind = FsErrorKind.None, string? ErrorMessage = null);
public sealed record CompactFsDiffOutcome(bool Ok, CompactFsDiffResponse? Response = null, FsErrorKind ErrorKind = FsErrorKind.None, string? ErrorMessage = null);
public sealed record CompactGitStatusOutcome(bool Ok, CompactGitStatusResponse? Response = null, FsErrorKind ErrorKind = FsErrorKind.None, string? ErrorMessage = null);
