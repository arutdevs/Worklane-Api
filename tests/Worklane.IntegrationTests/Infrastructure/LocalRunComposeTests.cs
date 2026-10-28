using System.Diagnostics;
using FluentAssertions;

namespace Worklane.IntegrationTests.Infrastructure;

public class LocalRunComposeTests
{
    [Fact]
    public void LocalCompose_PublishesPostgresOffTheExistingContainerPort_AndBuildsWebFromTheSiblingRepo()
    {
        var root = RepoRoot();
        var local = File.ReadAllText(Path.Combine(root, "compose.local.yml"));
        var script = File.ReadAllText(Path.Combine(root, "run-local.sh"));
        var example = File.ReadAllText(Path.Combine(root, ".env.example"));

        local.Should().Contain("command: [\"--migrate-only\"]");
        local.Should().Contain("${WORKLANE_WEB_DIR:-../Worklane-Web}");
        local.Should().Contain("ipv4_address: 172.30.20.3");
        local.Should().Contain("${WORKLANE_PG_PORT:-5434}:5432");
        local.Should().NotContain("5433:5432");
        local.Should().NotContain("rustfs");
        example.Should().Contain("WORKLANE_PG_PORT=5434");
        example.Should().NotContain("JWT_SIGNING_KEY=ey");
        script.Should().Contain("compose.local.yml");
        script.Should().Contain("docker-compose.yml");

        var deploy = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        deploy.Should().Contain("ipv4_address: 172.30.20.3");
        deploy.Should().Contain("Jwt__Secret: \"${JWT_SIGNING_KEY:?}\"");
    }

    [Fact]
    public void LocalCompose_RejectsAPublishedPortBoundToAllInterfaces()
    {
        const string json = """
            {
              "services": {
                "api": {
                  "ports": [
                    {
                      "mode": "ingress",
                      "host_ip": "0.0.0.0",
                      "target": 8080,
                      "published": "8080",
                      "protocol": "tcp"
                    }
                  ]
                }
              }
            }
            """;

        var (code, stdout, stderr) = RunPortCheck(json);

        code.Should().Be(1);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Published ports must bind 127.0.0.1 only");
        stderr.Should().Contain("0.0.0.0");
        stderr.Should().Contain("api publishes 8080 on 0.0.0.0");
    }

    [Fact]
    public void LocalCompose_AllowsAPublishedPortBoundToLoopback()
    {
        const string json = """
            {
              "services": {
                "web": {
                  "ports": [
                    {
                      "mode": "ingress",
                      "host_ip": "127.0.0.1",
                      "target": 8080,
                      "published": "4200",
                      "protocol": "tcp"
                    }
                  ]
                }
              }
            }
            """;

        var (code, _, stderr) = RunPortCheck(json);

        code.Should().Be(0);
        stderr.Should().NotContain("Refusing to start");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPortCheck(string json)
    {
        var start = new ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = RepoRoot(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(RepoRoot(), "run-local.sh"));
        start.Environment["WORKLANE_CHECK_CONFIG_JSON"] = "1";

        using var process = new Process { StartInfo = start };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(json);
        process.StandardInput.Close();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("run-local.sh port check did not exit.");
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Worklane.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Could not locate the repository root.");
        return root.FullName;
    }
}
