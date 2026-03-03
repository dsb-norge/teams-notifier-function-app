using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public interface IAliasService
{
    Task<AliasEntity?> GetAliasAsync(string name);
    Task<IReadOnlyList<AliasEntity>> GetAllAliasesAsync();
    Task<AliasEntity> SetAliasAsync(string name, AliasEntity entity);
    Task<bool> RemoveAliasAsync(string name);
}
