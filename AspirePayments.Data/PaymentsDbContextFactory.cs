using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AspirePayments.Data;

/// <summary>
/// Fábrica de design-time. Permite gerar migrations sem a connection string
/// em tempo de design (ela só é necessária para aplicar a migration).
/// </summary>
public sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        // Em tempo de design a connection string não é usada para conectar;
        // ela só é necessária se a migration for aplicada pelo CLI. Permite
        // sobrescrever via variável de ambiente em vez de fixar credenciais.
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__postgres")
            ?? "Host=localhost;Database=payments;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PaymentsDbContext(options);
    }
}
