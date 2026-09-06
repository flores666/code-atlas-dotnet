using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Tests;

public class ServiceRegistrationCollectorTests
{
    private static async Task<IReadOnlyList<ServiceRegistration>> CollectAsync(string body, string extra = "") =>
        (await SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Sample", ("Sample.cs", $$"""
                using System;
                using Microsoft.Extensions.DependencyInjection;

                namespace Sample
                {
                    public interface IUserService { }
                    public interface IClock { }
                    public interface IRepository<T> { }

                    public class UserService : IUserService { }
                    public class CachedUserService : IUserService { }
                    public class SystemClock : IClock { }
                    public class Repository<T> : IRepository<T> { }
                    public class Standalone { }

                    public static class Wiring
                    {
                        public static void Configure(IServiceCollection services)
                        {
                            {{body}}
                        }
                    }

                    {{extra}}
                }
                """)),
            TestContext.Current.CancellationToken)).Registrations;

    private static ServiceRegistration For(IReadOnlyList<ServiceRegistration> registrations, string service) =>
        registrations.Single(registration => registration.ServiceDisplay == service);

    [Fact]
    public async Task Reads_the_three_lifetimes()
    {
        var registrations = await CollectAsync("""
            services.AddScoped<IUserService, UserService>();
            services.AddSingleton<IClock, SystemClock>();
            services.AddTransient<Standalone, Standalone>();
            """);

        Assert.Equal(ServiceLifetime.Scoped, For(registrations, "IUserService").Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, For(registrations, "IClock").Lifetime);
        Assert.Equal(ServiceLifetime.Transient, For(registrations, "Standalone").Lifetime);
    }

    [Fact]
    public async Task Records_the_implementation_named_by_a_type_argument()
    {
        var registrations = await CollectAsync("services.AddScoped<IUserService, UserService>();");
        var registration = Assert.Single(registrations);

        Assert.Equal("Sample.IUserService", registration.ServiceFullyQualifiedName);
        Assert.Equal("Sample.UserService", registration.ImplementationFullyQualifiedName);
        Assert.Equal(RegistrationKind.ImplementationType, registration.Kind);
        Assert.Equal(RelationProvenance.Exact, registration.Provenance);
    }

    [Fact]
    public async Task Treats_a_self_registration_as_its_own_implementation()
    {
        var registration = Assert.Single(await CollectAsync("services.AddSingleton<Standalone>();"));

        Assert.Equal(RegistrationKind.Self, registration.Kind);
        Assert.Equal("Sample.Standalone", registration.ImplementationFullyQualifiedName);
    }

    [Fact]
    public async Task Recovers_a_factory_result_but_never_calls_it_exact()
    {
        var registrations = await CollectAsync("""
            services.AddScoped<IUserService>(provider => new UserService());
            services.AddScoped<IClock>(provider => Helper.Build());
            """,
            "public static class Helper { public static IClock Build() => new SystemClock(); }");

        var recovered = For(registrations, "IUserService");
        Assert.Equal(RegistrationKind.Factory, recovered.Kind);
        Assert.Equal("Sample.UserService", recovered.ImplementationFullyQualifiedName);
        Assert.Equal(RelationProvenance.Inferred, recovered.Provenance);

        // A factory that does anything else leaves the implementation unknown rather than
        // producing a guess.
        var opaque = For(registrations, "IClock");
        Assert.Equal(RegistrationKind.Factory, opaque.Kind);
        Assert.False(opaque.HasImplementation);
    }

    [Fact]
    public async Task Reads_an_instance_registration()
    {
        var registration = Assert.Single(await CollectAsync("services.AddSingleton<IClock>(new SystemClock());"));

        Assert.Equal(RegistrationKind.Instance, registration.Kind);
        Assert.Equal("Sample.SystemClock", registration.ImplementationFullyQualifiedName);
        Assert.Equal(RelationProvenance.Exact, registration.Provenance);
    }

    [Fact]
    public async Task Reads_the_open_generic_form()
    {
        var registration = Assert.Single(
            await CollectAsync("services.AddScoped(typeof(IRepository<>), typeof(Repository<>));"));

        Assert.Equal("Sample.IRepository<T>", registration.ServiceFullyQualifiedName);
        Assert.Equal("Sample.Repository<T>", registration.ImplementationFullyQualifiedName);
    }

    [Fact]
    public async Task Keeps_every_registration_of_one_service()
    {
        var registrations = await CollectAsync("""
            services.AddScoped<IUserService, UserService>();
            services.AddScoped<IUserService, CachedUserService>();
            """);

        Assert.Equal(
            ["Sample.CachedUserService", "Sample.UserService"],
            registrations.Select(r => r.ImplementationFullyQualifiedName).Order().ToList());
    }

    [Fact]
    public async Task Finds_registrations_inside_a_project_defined_extension_method()
    {
        // The call sits in the project's own extension, not in Program.cs; nothing about
        // finding it should depend on where it was written.
        var registrations = await CollectAsync(
            "services.AddInfrastructure();",
            """
            public static class InfrastructureExtensions
            {
                public static IServiceCollection AddInfrastructure(this IServiceCollection services)
                {
                    services.AddScoped<IUserService, UserService>();
                    return services;
                }
            }
            """);

        var registration = Assert.Single(registrations);
        Assert.Equal("Sample.UserService", registration.ImplementationFullyQualifiedName);
        Assert.Equal("AddInfrastructure(IServiceCollection services)", registration.DeclaringMember);
    }

    [Fact]
    public async Task Accepts_TryAdd_as_a_registration()
    {
        var registrations = await CollectAsync("""
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                .TryAddScoped<IUserService, UserService>(services);
            """);

        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(registrations).Lifetime);
    }

    [Fact]
    public async Task Ignores_a_method_that_only_looks_like_a_registration()
    {
        var registrations = await CollectAsync(
            "Unrelated.AddSingleton<IClock, SystemClock>();",
            "public static class Unrelated { public static void AddSingleton<T, U>() { } }");

        Assert.Empty(registrations);
    }

    [Fact]
    public async Task Records_the_call_site()
    {
        var registration = Assert.Single(await CollectAsync("services.AddScoped<IUserService, UserService>();"));

        Assert.Equal(Path.Combine("/repo/src", "Sample.cs"), registration.FilePath);
        Assert.NotNull(registration.Line);
        Assert.Equal("Configure(IServiceCollection services)", registration.DeclaringMember);
    }
}
