using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Tests;

public class ExternalDependencyCollectorTests
{
    /// <summary>
    /// The infrastructure is recognised by namespace, so the samples declare the shapes
    /// they need rather than the test project taking a reference on every broker client
    /// in the ecosystem. What is under test is the recognition, not the packages.
    /// </summary>
    private const string Stubs = """
        namespace StackExchange.Redis
        {
            public interface IDatabase { string StringGet(string key); }
            public interface IConnectionMultiplexer { IDatabase GetDatabase(); }
        }

        namespace Confluent.Kafka
        {
            public interface IProducer<TKey, TValue> { void Produce(string topic, TValue message); }
        }

        namespace MassTransit
        {
            public interface IBus { void Publish(object message); }
        }

        namespace RabbitMQ.Client
        {
            public interface IConnection { void Close(); }
        }

        namespace Amazon.S3
        {
            public interface IAmazonS3 { string GetObject(string bucket, string key); }
        }

        namespace Grpc.Core
        {
            public abstract class ClientBase<T> { }
        }

        namespace WebDav
        {
            public interface IWebDavClient { void Propfind(string path); }
        }
        """;

    private const string Source = """
        using System.IO;
        using System.Net.Http;
        using Microsoft.Extensions.DependencyInjection;

        namespace Shop
        {
            public interface ICrmClient { }

            public class CrmClient : ICrmClient
            {
                private readonly HttpClient _http;

                public CrmClient(HttpClient http) => _http = http;
            }

            public class CartCache
            {
                private readonly StackExchange.Redis.IConnectionMultiplexer _redis;

                public CartCache(StackExchange.Redis.IConnectionMultiplexer redis) => _redis = redis;

                public string Read(string key) => _redis.GetDatabase().StringGet(key);
            }

            public class OrderPublisher
            {
                private readonly MassTransit.IBus _bus;
                private readonly Confluent.Kafka.IProducer<string, string> _producer;

                public OrderPublisher(MassTransit.IBus bus, Confluent.Kafka.IProducer<string, string> producer)
                {
                    _bus = bus;
                    _producer = producer;
                }

                public void Send(string message)
                {
                    _bus.Publish(message);
                    _producer.Produce("orders", message);
                }
            }

            public class ReceiptStore
            {
                private readonly Amazon.S3.IAmazonS3 _storage;

                public ReceiptStore(Amazon.S3.IAmazonS3 storage) => _storage = storage;

                public string Archive(string path) => File.ReadAllText(path);
            }

            public class PricingClient : Grpc.Core.ClientBase<PricingClient> { }

            public class DocumentStore
            {
                private readonly WebDav.IWebDavClient _client;

                public DocumentStore(WebDav.IWebDavClient client) => _client = client;
            }

            public class Broker
            {
                private readonly RabbitMQ.Client.IConnection _connection;

                public Broker(RabbitMQ.Client.IConnection connection) => _connection = connection;
            }

            public class Plain { public int Value { get; set; } }

            public static class Wiring
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddHttpClient<ICrmClient, CrmClient>("crm");
                    services.AddHttpClient("reporting");
                }
            }
        }
        """;

    private static Task<ProjectIndexData> CollectAsync() =>
        SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Shop", ("Stubs.cs", Stubs), ("Shop.cs", Source)),
            TestContext.Current.CancellationToken);

    private static ExternalDependency For(ProjectIndexData data, string consumer) =>
        data.ExternalDependencies.Single(dependency => dependency.ConsumerDisplay == consumer);

    [Fact]
    public async Task Recognises_each_technology_by_the_client_it_binds()
    {
        var data = await CollectAsync();

        Assert.Equal(ExternalTechnology.Redis, For(data, "CartCache").Technology);
        Assert.Equal(ExternalTechnology.WebDav, For(data, "DocumentStore").Technology);
        Assert.Equal(ExternalTechnology.RabbitMq, For(data, "Broker").Technology);
        Assert.Equal(ExternalTechnology.Grpc, For(data, "PricingClient").Technology);

        Assert.Equal(
            [ExternalTechnology.MassTransit, ExternalTechnology.Kafka],
            data.ExternalDependencies
                .Where(dependency => dependency.ConsumerDisplay == "OrderPublisher")
                .Select(dependency => dependency.Technology)
                .Order()
                .ToList());
    }

    [Fact]
    public async Task Names_a_typed_client_after_the_registration_that_wired_it()
    {
        var data = await CollectAsync();
        var client = For(data, "CrmClient");

        Assert.Equal(ExternalTechnology.HttpApi, client.Technology);
        Assert.Equal(ExternalBinding.TypedClient, client.Binding);
        Assert.Equal("crm", client.Name);
        Assert.Equal("System.Net.Http.HttpClient", client.ClientFullyQualifiedName);
        Assert.Equal("HTTP · crm", client.Resource);
    }

    [Fact]
    public async Task Registers_a_typed_client_as_a_service_so_the_flow_crosses_to_it()
    {
        var data = await CollectAsync();
        var registration = Assert.Single(data.Registrations);

        Assert.Equal("Shop.ICrmClient", registration.ServiceFullyQualifiedName);
        Assert.Equal("Shop.CrmClient", registration.ImplementationFullyQualifiedName);
        Assert.Equal(ServiceLifetime.Transient, registration.Lifetime);
    }

    [Fact]
    public async Task Keeps_a_named_client_that_has_no_type_of_its_own()
    {
        var data = await CollectAsync();

        Assert.Contains(data.ExternalDependencies, dependency =>
            dependency.Binding == ExternalBinding.NamedClient && dependency.Name == "reporting");
    }

    [Fact]
    public async Task Records_a_call_that_touches_the_disk()
    {
        var data = await CollectAsync();

        Assert.Contains(data.ExternalDependencies, dependency =>
            dependency.Technology == ExternalTechnology.FileSystem &&
            dependency.ConsumerDisplay == "ReceiptStore" &&
            dependency.Binding == ExternalBinding.Call);
    }

    [Fact]
    public async Task Links_the_consumer_to_the_client_it_reaches()
    {
        var data = await CollectAsync();

        Assert.Contains(data.Relations, relation =>
            relation.SourceFullyQualifiedName == "Shop.CartCache" &&
            relation.Kind == RelationKind.UsesExternal &&
            relation.TargetFullyQualifiedName == "StackExchange.Redis.IConnectionMultiplexer");
    }

    [Fact]
    public async Task Leaves_a_type_that_crosses_no_boundary_alone()
    {
        var data = await CollectAsync();

        Assert.DoesNotContain(data.ExternalDependencies, dependency => dependency.ConsumerDisplay == "Plain");
    }
}
