using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Tests;

public class EntityFrameworkCollectorTests
{
    private const string Model = """
        using System.Collections.Generic;
        using System.Linq;
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Metadata.Builders;
        using Microsoft.EntityFrameworkCore.Migrations;

        namespace Shop
        {
            public class Customer
            {
                public int Id { get; set; }
                public ICollection<Order> Orders { get; set; } = new List<Order>();
            }

            public class Order
            {
                public int Id { get; set; }
                public Customer Customer { get; set; } = null!;
            }

            public class Archived { public int Id { get; set; } }

            public class ShopContext : DbContext
            {
                public DbSet<Customer> Customers => Set<Customer>();
                public DbSet<Order> Orders => Set<Order>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders", "sales");
                }
            }

            public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.ToTable("customers");
                    builder.HasMany(customer => customer.Orders).WithOne(order => order.Customer);
                }
            }

            public class OrderRepository
            {
                private readonly ShopContext _context;

                public OrderRepository(ShopContext context) => _context = context;

                public Order? Find(int id) => _context.Orders.FirstOrDefault(order => order.Id == id);

                public void Create(Order order) => _context.Orders.Add(order);

                public void Amend(Order order) => _context.Orders.Update(order);

                public void Drop(Order order) => _context.Orders.Remove(order);

                public Customer? Load(int id) => _context.Set<Customer>().FirstOrDefault(c => c.Id == id);
            }

            [Migration("20240101000000_AddOrders")]
            [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(ShopContext))]
            public partial class AddOrders : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropTable(name: "legacy_orders");
                    migrationBuilder.AddColumn<string>(name: "Note", table: "orders", nullable: true);
                }
            }
        }
        """;

    private static Task<ProjectIndexData> CollectAsync() =>
        SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Shop", ("Shop.cs", Model)),
            TestContext.Current.CancellationToken);

    private static EntityMapping For(ProjectIndexData data, string display) =>
        data.Entities.Single(entity => entity.EntityDisplay == display);

    private static IReadOnlyList<string> Targets(ProjectIndexData data, string source, RelationKind kind) =>
        data.Relations
            .Where(relation => relation.SourceFullyQualifiedName == source && relation.Kind == kind)
            .Select(relation => relation.TargetFullyQualifiedName)
            .Order()
            .ToList();

    [Fact]
    public async Task Finds_the_entities_a_context_declares()
    {
        var data = await CollectAsync();

        Assert.Equal(
            ["Shop.Customer", "Shop.Order"],
            Targets(data, "Shop.ShopContext", RelationKind.DeclaresEntity));

        Assert.Equal("ShopContext", For(data, "Order").ContextDisplay);
        Assert.Equal("Orders", For(data, "Order").SetName);
    }

    [Fact]
    public async Task Reads_a_table_name_from_both_places_it_can_be_written()
    {
        var data = await CollectAsync();

        // OnModelCreating names the schema too; the configuration class names only the table.
        Assert.Equal("sales.orders", For(data, "Order").QualifiedTable);
        Assert.Equal("customers", For(data, "Customer").QualifiedTable);
    }

    [Fact]
    public async Task Records_the_configuration_class_that_maps_an_entity()
    {
        var data = await CollectAsync();

        Assert.Equal("CustomerConfiguration", For(data, "Customer").ConfigurationDisplay);
        Assert.Equal(
            ["Shop.Customer"],
            Targets(data, "Shop.CustomerConfiguration", RelationKind.ConfiguresEntity));
    }

    [Fact]
    public async Task Reads_a_configured_relationship_between_two_entities()
    {
        var data = await CollectAsync();

        Assert.Equal(["Shop.Order"], Targets(data, "Shop.Customer", RelationKind.RelatesToEntity));
        Assert.Equal(["Shop.Customer"], Targets(data, "Shop.Order", RelationKind.RelatesToEntity));
    }

    [Fact]
    public async Task Tells_the_four_kinds_of_use_apart()
    {
        var data = await CollectAsync();

        Assert.Equal(["Shop.Order"], Targets(data, "Shop.OrderRepository.Find(System.Int32)", RelationKind.ReadsEntity));
        Assert.Equal(["Shop.Order"], Targets(data, "Shop.OrderRepository.Create(Shop.Order)", RelationKind.CreatesEntity));
        Assert.Equal(["Shop.Order"], Targets(data, "Shop.OrderRepository.Amend(Shop.Order)", RelationKind.ModifiesEntity));
        Assert.Equal(["Shop.Order"], Targets(data, "Shop.OrderRepository.Drop(Shop.Order)", RelationKind.DeletesEntity));

        // Reaching a set to write it is not also recorded as a read.
        Assert.Empty(Targets(data, "Shop.OrderRepository.Create(Shop.Order)", RelationKind.ReadsEntity));
    }

    [Fact]
    public async Task Reads_a_set_obtained_from_the_context_itself()
    {
        var data = await CollectAsync();

        Assert.Equal(["Shop.Customer"], Targets(data, "Shop.OrderRepository.Load(System.Int32)", RelationKind.ReadsEntity));
    }

    [Fact]
    public async Task Lists_a_migration_with_the_tables_it_names()
    {
        var data = await CollectAsync();
        var migration = Assert.Single(data.Migrations);

        Assert.Equal("20240101000000_AddOrders", migration.Name);
        Assert.Equal("Shop.ShopContext", migration.ContextFullyQualifiedName);
        Assert.Equal(["legacy_orders", "orders"], migration.Tables.Order().ToList());
        Assert.NotNull(migration.Line);
    }

    [Fact]
    public async Task Leaves_a_type_that_is_not_part_of_the_model_alone()
    {
        var data = await CollectAsync();

        Assert.DoesNotContain(data.Entities, entity => entity.EntityDisplay == "Archived");
    }
}
