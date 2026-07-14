using Worklane.Application.Common.Interfaces;

namespace Worklane.Infrastructure.Multitenancy;

public class CurrentTenantService : ICurrentTenantService
{
    public Guid? WorkspaceId { get; private set; }

    public void SetWorkspace(Guid workspaceId) => WorkspaceId = workspaceId;
}
