namespace Restate.Sdk.Generators.Tests;

public class ClientFactoryGeneratorTests
{
    [Fact]
    public void GeneratesModuleInitializer()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class GreeterService
                     {
                         [Handler]
                         public Task<string> Greet(Context ctx, string name) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, output, _) = GeneratorTestHelper.RunGenerator(source);
        GeneratorTestHelper.AssertGeneratedCodeCompiles(output);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "RestateClientFactory.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("[ModuleInitializer]", generated);
        Assert.Contains("ClientFactory.Register", generated);
        Assert.Contains("IGreeterServiceClient", generated);
        Assert.Contains("IGreeterServiceSendClient", generated);
        Assert.Contains("GreeterServiceClient", generated);
        Assert.Contains("GreeterServiceSendClient", generated);
    }

    [Fact]
    public void RegistersMultipleServices()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class ServiceA
                     {
                         [Handler]
                         public Task<string> DoA(Context ctx) => Task.FromResult("A");
                     }

                     [Service]
                     public class ServiceB
                     {
                         [Handler]
                         public Task<string> DoB(Context ctx) => Task.FromResult("B");
                     }
                     """;

        var (driver, output, _) = GeneratorTestHelper.RunGenerator(source);
        GeneratorTestHelper.AssertGeneratedCodeCompiles(output);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "RestateClientFactory.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("IServiceAClient", generated);
        Assert.Contains("IServiceBClient", generated);
    }
}