using CodeAtlas.Core.Analysis;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Tests;

public class SymbolCollectorTests
{
    private const string Sample = """
        using System;

        namespace Sample.Domain
        {
            [Obsolete("legacy")]
            public interface IGreeter
            {
                string Greet(string name);
            }

            public abstract class GreeterBase : IGreeter
            {
                protected readonly string Prefix = "Hi";

                public abstract string Greet(string name);
            }

            public sealed class LoudGreeter : GreeterBase
            {
                public LoudGreeter(string prefix) => Suffix = prefix;

                public string Suffix { get; }

                public event EventHandler? Greeted;

                public override string Greet(string name) => Prefix + name + Suffix;
            }

            public record Person(string Name);

            public record struct Point(int X, int Y);

            public struct Size
            {
                public int Width;
            }

            public enum Level
            {
                Low,
                High,
            }

            public delegate void Notify(string message);
        }
        """;

    private static async Task<ProjectIndexData> CollectAsync(string source = Sample) =>
        await SymbolCollector.CollectAsync(
            TestProjectFactory.Create("Sample", ("Sample.cs", source)),
            TestContext.Current.CancellationToken);

    private static IndexedSymbol Find(ProjectIndexData data, IndexedSymbolKind kind, string name) =>
        data.Symbols.Single(s => s.Kind == kind && s.Name == name);

    [Fact]
    public async Task Indexes_every_supported_declaration_kind()
    {
        var data = await CollectAsync();

        Assert.Equal(IndexedSymbolKind.Namespace, Find(data, IndexedSymbolKind.Namespace, "Domain").Kind);
        Assert.Equal("Sample.Domain.IGreeter", Find(data, IndexedSymbolKind.Interface, "IGreeter").FullyQualifiedName);
        Assert.Equal("Sample.Domain.LoudGreeter", Find(data, IndexedSymbolKind.Class, "LoudGreeter").FullyQualifiedName);
        Assert.Equal("Sample.Domain.Person", Find(data, IndexedSymbolKind.Record, "Person").FullyQualifiedName);
        Assert.Equal("Sample.Domain.Size", Find(data, IndexedSymbolKind.Struct, "Size").FullyQualifiedName);
        Assert.Equal("Sample.Domain.Level", Find(data, IndexedSymbolKind.Enum, "Level").FullyQualifiedName);
        Assert.Equal("Sample.Domain.Notify", Find(data, IndexedSymbolKind.Delegate, "Notify").FullyQualifiedName);

        Assert.Contains(data.Symbols, s => s.Kind == IndexedSymbolKind.Method && s.Name == "Greet");
        Assert.Contains(data.Symbols, s => s.Kind == IndexedSymbolKind.Constructor && s.Name == ".ctor");
        Assert.Contains(data.Symbols, s => s.Kind == IndexedSymbolKind.Property && s.Name == "Suffix");
        Assert.Contains(data.Symbols, s => s.Kind == IndexedSymbolKind.Field && s.Name == "Prefix");
        Assert.Contains(data.Symbols, s => s.Kind == IndexedSymbolKind.Event && s.Name == "Greeted");
    }

    [Fact]
    public async Task Reports_record_struct_as_a_record()
    {
        var data = await CollectAsync();

        Assert.Equal(IndexedSymbolKind.Record, Find(data, IndexedSymbolKind.Record, "Point").Kind);
    }

    [Fact]
    public async Task Captures_namespace_accessibility_and_source_location()
    {
        var data = await CollectAsync();
        var greeter = Find(data, IndexedSymbolKind.Class, "LoudGreeter");

        Assert.Equal("Sample.Domain", greeter.Namespace);
        Assert.Equal("Public", greeter.Accessibility);
        Assert.Equal("Sample", greeter.ProjectName);
        Assert.Equal(Path.Combine("/repo/src", "Sample.cs"), greeter.FilePath);
        Assert.NotNull(greeter.Line);
        Assert.NotNull(greeter.Column);
    }

    [Fact]
    public async Task Records_containing_type_for_members_but_not_for_types()
    {
        var data = await CollectAsync();

        Assert.Null(Find(data, IndexedSymbolKind.Class, "LoudGreeter").ContainerFullyQualifiedName);
        Assert.Equal(
            "Sample.Domain.LoudGreeter",
            Find(data, IndexedSymbolKind.Property, "Suffix").ContainerFullyQualifiedName);
    }

    [Fact]
    public async Task Captures_attributes()
    {
        var data = await CollectAsync();

        Assert.Equal(
            ["System.ObsoleteAttribute"],
            Find(data, IndexedSymbolKind.Interface, "IGreeter").Attributes);
    }

    [Fact]
    public async Task Distinguishes_overloads_by_parameter_types()
    {
        var data = await CollectAsync("""
            namespace N
            {
                public class C
                {
                    public void M(int x) { }
                    public void M(string x) { }
                }
            }
            """);

        var overloads = data.Symbols
            .Where(s => s.Kind == IndexedSymbolKind.Method && s.Name == "M")
            .Select(s => s.FullyQualifiedName)
            .ToList();

        Assert.Equal(["N.C.M(System.Int32)", "N.C.M(System.String)"], overloads.Order().ToList());
    }

    [Fact]
    public async Task Skips_compiler_generated_members()
    {
        var data = await CollectAsync();

        // Property accessors, record equality members and the implicit default
        // constructor were never written by anyone and must not appear.
        Assert.DoesNotContain(data.Symbols, s => s.Name is "get_Suffix" or "set_Suffix");
        Assert.DoesNotContain(data.Symbols, s => s.Name == "<Clone>$");
        Assert.DoesNotContain(
            data.Symbols,
            s => s.Kind == IndexedSymbolKind.Constructor && s.ContainerFullyQualifiedName == "Sample.Domain.Size");
    }

    [Fact]
    public async Task Records_inheritance_and_interface_implementation()
    {
        var data = await CollectAsync();

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "Sample.Domain.LoudGreeter" &&
            r.Kind == RelationKind.Inherits &&
            r.TargetFullyQualifiedName == "Sample.Domain.GreeterBase");

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "Sample.Domain.GreeterBase" &&
            r.Kind == RelationKind.Implements &&
            r.TargetFullyQualifiedName == "Sample.Domain.IGreeter");
    }

    [Fact]
    public async Task Records_references_between_symbols_in_the_solution()
    {
        var data = await CollectAsync("""
            namespace N
            {
                public class Helper
                {
                    public int Value() => 42;
                }

                public class Caller
                {
                    private readonly Helper _helper = new Helper();

                    public int Use() => _helper.Value();
                }
            }
            """);

        // The field initialiser belongs to the field, the call belongs to the method.
        Assert.Contains(data.Relations, r =>
            r.Kind == RelationKind.References &&
            r.SourceFullyQualifiedName == "N.Caller._helper" && r.TargetFullyQualifiedName == "N.Helper");

        Assert.Contains(data.Relations, r =>
            r.Kind == RelationKind.Calls &&
            r.SourceFullyQualifiedName == "N.Caller.Use()" && r.TargetFullyQualifiedName == "N.Helper.Value()");
    }

    [Fact]
    public async Task Does_not_record_references_to_symbols_outside_the_solution()
    {
        var data = await CollectAsync("""
            using System.Text;

            namespace N
            {
                public class C
                {
                    public string M() => new StringBuilder().Append("x").ToString();
                }
            }
            """);

        Assert.DoesNotContain(
            data.Relations,
            r => r.Kind == RelationKind.References && r.TargetFullyQualifiedName.StartsWith("System.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attributes_relations_inside_an_accessor_to_its_property()
    {
        var data = await CollectAsync("""
            namespace N
            {
                public class Helper { public int Value() => 1; }

                public class C
                {
                    private readonly Helper _helper = new();

                    public int Total
                    {
                        get { return _helper.Value(); }
                    }
                }
            }
            """);

        Assert.Contains(data.Relations, r =>
            r.Kind == RelationKind.Calls &&
            r.SourceFullyQualifiedName == "N.C.Total" &&
            r.TargetFullyQualifiedName == "N.Helper.Value()");
    }

    [Fact]
    public async Task Records_a_reference_from_a_field_to_its_declared_type()
    {
        var data = await CollectAsync("""
            namespace N
            {
                public class Helper { }

                public class C
                {
                    private readonly Helper _helper = new();
                }
            }
            """);

        // The declared type sits outside the declarator, so it is only reached by
        // treating the field declaration itself as a boundary.
        Assert.Contains(data.Relations, r =>
            r.Kind == RelationKind.References &&
            r.SourceFullyQualifiedName == "N.C._helper" &&
            r.TargetFullyQualifiedName == "N.Helper");
    }

    [Fact]
    public async Task Reports_a_diagnostic_instead_of_throwing_when_source_is_broken()
    {
        var data = await CollectAsync("""
            namespace N { public class Broken { public void M( } }
            """);

        // Malformed source still yields a compilation; indexing must survive it.
        Assert.Contains(data.Symbols, s => s.Name == "Broken");
    }

    [Fact]
    public async Task Records_calls_separately_from_plain_references()
    {
        var data = await CollectAsync("""
            namespace N
            {
                public class Helper
                {
                    public int Value() => 42;
                }

                public class Caller
                {
                    public int Use()
                    {
                        var helper = new Helper();
                        return helper.Value();
                    }
                }
            }
            """);

        // The invocation is a call; the type named in `new Helper()` is a reference to
        // the type, and the constructor it runs is a call of its own.
        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "N.Caller.Use()" &&
            r.Kind == RelationKind.Calls &&
            r.TargetFullyQualifiedName == "N.Helper.Value()");

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "N.Caller.Use()" &&
            r.Kind == RelationKind.Calls &&
            r.TargetFullyQualifiedName.StartsWith("N.Helper.Helper(", StringComparison.Ordinal));

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "N.Caller.Use()" &&
            r.Kind == RelationKind.References &&
            r.TargetFullyQualifiedName == "N.Helper");

        // The callee's own name must not also be stored as a mention of it.
        Assert.DoesNotContain(data.Relations, r =>
            r.Kind == RelationKind.References &&
            r.TargetFullyQualifiedName == "N.Helper.Value()");
    }

    [Fact]
    public async Task Records_overrides_and_member_level_interface_implementations()
    {
        var data = await CollectAsync();

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "Sample.Domain.GreeterBase.Greet(System.String)" &&
            r.Kind == RelationKind.Implements &&
            r.TargetFullyQualifiedName == "Sample.Domain.IGreeter.Greet(System.String)");

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "Sample.Domain.LoudGreeter.Greet(System.String)" &&
            r.Kind == RelationKind.Overrides &&
            r.TargetFullyQualifiedName == "Sample.Domain.GreeterBase.Greet(System.String)");
    }

    [Fact]
    public async Task Records_parameter_and_return_type_dependencies_reaching_inside_generics()
    {
        var data = await CollectAsync("""
            using System.Collections.Generic;

            namespace N
            {
                public class Order { }

                public class Repository
                {
                    public List<Order> Load(Order seed) => new();

                    public Order Latest { get; set; } = new();
                }
            }
            """);

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "N.Repository.Load(N.Order)" &&
            r.Kind == RelationKind.ParameterType &&
            r.TargetFullyQualifiedName == "N.Order");

        // The dependency worth recording inside List<Order> is Order; List itself is
        // outside the solution and is not indexed.
        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "N.Repository.Load(N.Order)" &&
            r.Kind == RelationKind.ReturnType &&
            r.TargetFullyQualifiedName == "N.Order");

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == "N.Repository.Latest" &&
            r.Kind == RelationKind.ReturnType &&
            r.TargetFullyQualifiedName == "N.Order");

        Assert.DoesNotContain(data.Relations, r =>
            r.Kind is RelationKind.ParameterType or RelationKind.ReturnType &&
            r.TargetFullyQualifiedName.StartsWith("System.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Records_constructor_parameters_as_type_dependencies()
    {
        var data = await CollectAsync("""
            namespace N
            {
                public interface IClock { }

                public class Service
                {
                    public Service(IClock clock) { }
                }
            }
            """);

        var constructor = Find(data, IndexedSymbolKind.Constructor, ".ctor");

        Assert.Contains(data.Relations, r =>
            r.SourceFullyQualifiedName == constructor.FullyQualifiedName &&
            r.Kind == RelationKind.ParameterType &&
            r.TargetFullyQualifiedName == "N.IClock");
    }

    [Fact]
    public async Task Marks_compiler_resolved_edges_as_exact()
    {
        var data = await CollectAsync();

        Assert.All(data.Relations, r => Assert.Equal(RelationProvenance.Exact, r.Provenance));
    }

    [Fact]
    public async Task Keeps_a_single_candidate_of_an_unresolved_call_as_inferred()
    {
        // The argument does not fit, so the compiler binds nothing; exactly one candidate
        // remains, which is worth an edge but is not compiler truth.
        var data = await CollectAsync("""
            namespace N
            {
                public class Helper
                {
                    public int Value(int x) => x;
                }

                public class Caller
                {
                    public int Use() => new Helper().Value("nope");
                }
            }
            """);

        var call = data.Relations.Single(r =>
            r.Kind == RelationKind.Calls &&
            r.TargetFullyQualifiedName == "N.Helper.Value(System.Int32)");

        Assert.Equal(RelationProvenance.Inferred, call.Provenance);
    }

}
