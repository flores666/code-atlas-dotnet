using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Tests;

public class ConfigurationCollectorTests
{
    private const string Source = """
        using System;
        using Microsoft.Extensions.Configuration;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Options;

        namespace Crm
        {
            public class CrmOptions
            {
                public string BaseUrl { get; set; } = "";
            }

            public class CrmWebServicesClient
            {
                private readonly CrmOptions _options;

                public CrmWebServicesClient(IOptions<CrmOptions> options) => _options = options.Value;

                public string Endpoint => _options.BaseUrl;
            }

            public class LegacyClient
            {
                private readonly IConfiguration _configuration;

                public LegacyClient(IConfiguration configuration) => _configuration = configuration;

                public string Url => _configuration["Legacy:BaseUrl"] ?? "";

                public string Timeout => _configuration.GetSection("Legacy").GetSection("Http")["Timeout"] ?? "";

                public string Store => _configuration.GetConnectionString("Store") ?? "";

                public string Region => Environment.GetEnvironmentVariable("AWS_REGION") ?? "";
            }

            public static class Wiring
            {
                public static void Configure(IServiceCollection services, IConfiguration configuration)
                {
                    services.Configure<CrmOptions>(configuration.GetSection("Crm"));
                }
            }
        }
        """;

    private static Task<ProjectIndexData> CollectAsync() =>
        SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Crm", ("Crm.cs", Source)),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Binds_an_options_type_to_the_section_it_reads()
    {
        var data = await CollectAsync();

        var binding = data.Configuration.Single(usage =>
            usage.Access == ConfigurationAccess.Options && usage.Key == "Crm");

        Assert.Equal("Crm.CrmOptions", binding.OptionsFullyQualifiedName);
    }

    [Fact]
    public async Task Links_an_options_type_to_the_component_that_takes_it()
    {
        var data = await CollectAsync();

        Assert.Contains(data.Relations, relation =>
            relation.SourceFullyQualifiedName == "Crm.CrmWebServicesClient" &&
            relation.Kind == RelationKind.ReadsConfiguration &&
            relation.TargetFullyQualifiedName == "Crm.CrmOptions");

        Assert.Contains(data.Configuration, usage =>
            usage.Access == ConfigurationAccess.Options &&
            usage.ConsumerFullyQualifiedName == "Crm.CrmWebServicesClient" &&
            usage.OptionsFullyQualifiedName == "Crm.CrmOptions");
    }

    [Fact]
    public async Task Reads_an_indexer_key()
    {
        var data = await CollectAsync();

        Assert.Contains(data.Configuration, usage =>
            usage.Access == ConfigurationAccess.Value &&
            usage.Key == "Legacy:BaseUrl" &&
            usage.ConsumerFullyQualifiedName == "Crm.LegacyClient");
    }

    [Fact]
    public async Task Follows_nested_sections_into_one_path()
    {
        var data = await CollectAsync();

        Assert.Contains(data.Configuration, usage => usage.Key == "Legacy:Http:Timeout");
    }

    [Fact]
    public async Task Reads_connection_strings_and_environment_variables_by_name()
    {
        var data = await CollectAsync();

        Assert.Contains(data.Configuration, usage =>
            usage.Access == ConfigurationAccess.ConnectionString && usage.Key == "Store");

        Assert.Contains(data.Configuration, usage =>
            usage.Access == ConfigurationAccess.Environment && usage.Key == "AWS_REGION");
    }

    [Fact]
    public async Task Records_a_component_that_takes_IConfiguration_itself()
    {
        var data = await CollectAsync();

        Assert.Contains(data.Configuration, usage =>
            usage.Access == ConfigurationAccess.Provider &&
            usage.ConsumerFullyQualifiedName == "Crm.LegacyClient");
    }

    [Fact]
    public async Task Leaves_a_key_that_is_not_a_constant_out()
    {
        var data = await SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Dynamic", ("Dynamic.cs", """
                using Microsoft.Extensions.Configuration;

                namespace Dynamic
                {
                    public class Reader
                    {
                        private readonly IConfiguration _configuration;

                        public Reader(IConfiguration configuration) => _configuration = configuration;

                        public string? Get(string name) => _configuration[name];
                    }
                }
                """)),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(data.Configuration, usage => usage.Access == ConfigurationAccess.Value);
    }
}
