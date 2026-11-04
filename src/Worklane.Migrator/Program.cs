using Microsoft.EntityFrameworkCore;
using Worklane.Infrastructure.Persistence;

// Applies the migrations compiled into Worklane.Infrastructure.
// The factory reads ConnectionStrings__Default. Do not put that value in argv.
// A caught failure exits 1. An unhandled exception aborts, and that abort
// segfaults in the read-only migrator container.
// Npgsql's Message can include the connection string, so the log gets the
// exception type and SqlState only.
try
{
    var factory = new WorklaneDbContextFactory();
    await using var db = factory.CreateDbContext(args);
    await db.Database.MigrateAsync();
    return 0;
}
catch (Exception ex)
{
    WriteFailure(ex);
    return 1;
}

static void WriteFailure(Exception ex)
{
    for (Exception? current = ex; current is not null; current = current.InnerException)
    {
        var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
        if (string.IsNullOrEmpty(sqlState))
            Console.Error.WriteLine(current.GetType().FullName);
        else
            Console.Error.WriteLine($"{current.GetType().FullName} SqlState={sqlState}");
    }
}
