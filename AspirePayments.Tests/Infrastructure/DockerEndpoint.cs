using System.Diagnostics;

namespace AspirePayments.Tests.Infrastructure;

/// <summary>
/// Resolve o endpoint do Docker ativo antes de criar os containers.
/// O Testcontainers assume o pipe padrão (<c>docker_engine</c>), mas o Docker
/// Desktop usa <c>dockerDesktopLinuxEngine</c>. Sem esta correção os testes
/// falham com <c>DockerUnavailableException</c> mesmo com o Docker no ar.
/// </summary>
public static class DockerEndpoint
{
    private static int _initialized;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("docker", "context inspect --format \"{{.Endpoints.docker.Host}}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return;
            }

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(10_000))
            {
                return;
            }

            var endpoint = output.Trim();

            if (!string.IsNullOrWhiteSpace(endpoint) && !endpoint.Contains("docker_engine"))
            {
                Environment.SetEnvironmentVariable("DOCKER_HOST", endpoint);
            }
        }
        catch
        {
            // Sem Docker CLI: o Testcontainers usa o endpoint padrão.
        }
    }
}
