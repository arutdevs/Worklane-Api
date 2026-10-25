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
