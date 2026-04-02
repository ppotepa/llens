using Llens.Api;
using Llens.Models;

namespace Llens.Application.Reindex;

public interface ICompactReindexService
{
    Task<CompactReindexResponse> RunAsync(Project project, CompactReindexRequest request, CancellationToken ct);
}
