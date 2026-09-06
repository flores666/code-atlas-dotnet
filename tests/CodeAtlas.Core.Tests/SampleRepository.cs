using System.Diagnostics;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// Materialises a small but real .NET repository on disk: two projects that build, one
/// that cannot be evaluated at all, and both solution formats over the same projects.
/// </summary>
public sealed class SampleRepository : IDisposable
{
    public SampleRepository()
    {
        Root = Directory.CreateTempSubdirectory("codeatlas-repo").FullName;

        try
        {
            Build();
        }
        catch
        {
            // xUnit never disposes a fixture whose constructor threw, so the temp
            // directory would otherwise be left behind.
            Dispose();
            throw;
        }
    }

    private void Build()
    {
        Write("Core/Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        Write("Core/Calculator.cs", """
            namespace Sample.Core;

            public interface ICalculator
            {
                int Add(int a, int b);
            }

            public sealed class Calculator : ICalculator
            {
                public int Add(int a, int b) => a + b;
            }
            """);

        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Core/Core.csproj" />
              </ItemGroup>
            </Project>
            """);

        Write("App/Runner.cs", """
            using Sample.Core;

            namespace Sample.App;

            public sealed class Runner
            {
                private readonly ICalculator _calculator = new Calculator();

                public int Run() => _calculator.Add(1, 2);
            }
            """);

        Write("Broken/Broken.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        const string projects = "Core/Core.csproj App/App.csproj Broken/Broken.csproj";

        RunDotnet("new sln --name Sample --format sln");
        RunDotnet($"sln Sample.sln add {projects}");

        RunDotnet("new sln --name SampleXml --format slnx");
        RunDotnet($"sln SampleXml.slnx add {projects}");

        // Only the healthy projects are restored; the broken one cannot be.
        RunDotnet("restore App/App.csproj");

        // Broken is listed by both solutions, and only now made unloadable: the XML no
        // longer parses, so MSBuild cannot produce a project for it at all.
        Write("Broken/Broken.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
            </Project>
            """);
    }

    public string Root { get; }

    public string SolutionPath => Path.Combine(Root, "Sample.sln");

    public string SolutionXmlPath => Path.Combine(Root, "SampleXml.slnx");

    public string AppProjectPath => Path.Combine(Root, "App", "App.csproj");

    public string CalculatorSourcePath => Path.Combine(Root, "Core", "Calculator.cs");

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void RunDotnet(string arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Root,
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
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
