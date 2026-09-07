using System.Diagnostics;
using CodeAtlas.Core.Indexing;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// Indexes a real ASP.NET Core project through MSBuild and asserts its endpoints are
/// listed.
/// </summary>
/// <remarks>
/// The other endpoint tests bind in-memory compilations whose references come from the
/// test host, which is why they cannot catch a failure that depends on how a real web
/// project is loaded. This one goes through the path the application actually uses:
/// resolve the workspace, load it with MSBuild, index it, and read the endpoints back out
/// of the cache file.
/// </remarks>
[Collection(nameof(MSBuildCollection))]
public class WebEndpointIndexingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeatlas-web").FullName;
    private readonly string _cacheRoot = Directory.CreateTempSubdirectory("codeatlas-web-cache").FullName;

    public WebEndpointIndexingTests()
    {
        Write("Api/Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        Write("Api/Controllers/OrdersController.cs", """
            using Microsoft.AspNetCore.Mvc;

            namespace Api.Controllers;

            [ApiController]
            [Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok(id);

                [HttpPost]
                public IActionResult Create() => Ok();
            }
            """);

        Write("Api/Controllers/ApiControllerBase.cs", """
            using Microsoft.AspNetCore.Mvc;

            namespace Api.Controllers;

            [ApiController]
            [Route("api/v1/[controller]")]
            public abstract class ApiControllerBase : ControllerBase
            {
            }
            """);

        Write("Api/Controllers/InvoicesController.cs", """
            using Microsoft.AspNetCore.Mvc;

            namespace Api.Controllers;

            public class InvoicesController : ApiControllerBase
            {
                [HttpGet]
                public IActionResult List() => Ok();

                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok(id);
            }
            """);

        Write("Api/Program.cs", """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.Extensions.DependencyInjection;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();

            var app = builder.Build();
            app.MapGet("/health", () => Results.Ok("ok"));
            app.MapControllers();
            app.Run();
            """);

        RunDotnet("restore Api/Api.csproj");
    }

    private string CachePath => Path.Combine(_cacheRoot, "index.db");

    [Fact]
    public async Task Lists_the_endpoints_of_a_real_web_project()
    {
        var target = WorkspaceLocator.Resolve(Path.Combine(_root, "Api", "Api.csproj"));
        Assert.NotNull(target);

        var result = await new IndexingService().BuildAsync(
            target,
            CachePath,
            progress: null,
            TestContext.Current.CancellationToken);

        using var database = SymbolIndexDatabase.Open(CachePath);
        var endpoints = database.GetEndpoints();

        Assert.NotEmpty(endpoints);

        var routes = endpoints.Select(endpoint => $"{endpoint.HttpMethod} {endpoint.Route}").ToList();

        Assert.Contains("GET /api/Orders/{id}", routes);
        Assert.Contains("POST /api/Orders", routes);
        Assert.Contains("GET /health", routes);

        // The regression this file exists for: a controller routed by its base class.
        Assert.Contains("GET /api/v1/Invoices", routes);
        Assert.Contains("GET /api/v1/Invoices/{id}", routes);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void RunDotnet(string arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // MSBuildLocator points these at the SDK it registered; a child dotnet must
        // discover its own or it will fail to start.
        foreach (var variable in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath" })
        {
            startInfo.Environment.Remove(variable);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'dotnet {arguments}' failed:\n{output}\n{error}");
        }
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _cacheRoot })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Not worth failing a test over.
            }
        }
    }
}
