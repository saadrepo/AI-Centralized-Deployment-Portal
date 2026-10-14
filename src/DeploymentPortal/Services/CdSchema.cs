using DeploymentPortal.Data;
using Microsoft.EntityFrameworkCore;

namespace DeploymentPortal.Services;

public static class CdSchema
{
    public static async Task<bool> AvailableAsync(ApplicationDbContext db) => !db.Database.IsRelational() || (await db.Database.GetAppliedMigrationsAsync()).Any(x => x.EndsWith("_AddControlledCd", StringComparison.Ordinal));
}
