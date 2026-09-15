namespace Restate.Sdk.Generators.Tests;

public class SerializationContextGeneratorTests
{
    [Fact]
    public void GeneratesModuleInitializer_ConfiguresJsonSerde()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     public record OrderRequest(string ItemId, int Quantity);
                     public record OrderResponse(string OrderId);

                     [Service]
                     public class OrderService
                     {
                         [Handler]
                         public Task<OrderResponse> PlaceOrder(Context ctx, OrderRequest request)
                             => Task.FromResult(new OrderResponse("123"));
                     }
                     """;

        var (driver, output, _) = GeneratorTestHelper.RunGenerator(source);
        GeneratorTestHelper.AssertGeneratedCodeCompiles(output);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "RestateSerializationContext.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("internal static class RestateSerializationConfig", generated);
        Assert.Contains("[ModuleInitializer]", generated);
        Assert.Contains("JsonSerde.Configure(options)", generated);
        Assert.Contains("CamelCase", generated);
        // Types should be listed as comments for AOT documentation
        Assert.Contains("[JsonSerializable(typeof(global::TestApp.OrderRequest))]", generated);
        Assert.Contains("[JsonSerializable(typeof(global::TestApp.OrderResponse))]", generated);
    }

    [Fact]
    public void GeneratesConfig_ForServiceWithPrimitiveTypes()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class MathService
                     {
                         [Handler]
                         public Task<int> Add(Context ctx, int value) => Task.FromResult(value);
                     }
                     """;

        var (driver, output, _) = GeneratorTestHelper.RunGenerator(source);
        GeneratorTestHelper.AssertGeneratedCodeCompiles(output);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "RestateSerializationContext.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("ConfigureJsonSerde", generated);
    }

    [Fact]
    public void GeneratesConfig_ForMultipleServices()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     public record MyType(string Value);

                     [Service]
                     public class ServiceA
                     {
                         [Handler]
                         public Task<MyType> GetA(Context ctx, MyType input) => Task.FromResult(input);
                     }

                     [Service]
                     public class ServiceB
                     {
                         [Handler]
                         public Task<MyType> GetB(Context ctx, MyType input) => Task.FromResult(input);
                     }
                     """;

        var (driver, output, _) = GeneratorTestHelper.RunGenerator(source);
        GeneratorTestHelper.AssertGeneratedCodeCompiles(output);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "RestateSerializationContext.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("RestateSerializationConfig", generated);
    }
}