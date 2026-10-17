using Microsoft.EntityFrameworkCore;
using Worklane.Infrastructure.Persistence;

// Applies the migrations compiled into Worklane.Infrastructure.
// The factory reads ConnectionStrings__Default. Do not put that value in argv.
// A caught failure exits 1. An unhandled exception aborts, and that abort
// segfaults in the read-only migrator container.
try
{
    var factory = new WorklaneDbContextFactory();
    await using var db = factory.CreateDbContext(args);
    await db.Database.MigrateAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
