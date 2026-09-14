using Microsoft.CodeAnalysis;

namespace Restate.Sdk.Generators.Tests;

public class ClientGeneratorTests
{
    [Fact]
    public void Service_GeneratesClientInterface()
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

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "GreeterServiceClient.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("public interface IGreeterServiceClient", generated);
        Assert.Contains("public interface IGreeterServiceSendClient", generated);
        Assert.Contains("GreetAsync(", generated);
        Assert.Contains("GreetSend(", generated);
    }

    [Fact]
    public void VirtualObject_KeyBoundClient()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [VirtualObject]
                     public class CounterObject
                     {
                         [Handler]
                         public Task<int> Add(ObjectContext ctx, int delta) => Task.FromResult(delta);

                         [SharedHandler]
                         public Task<int> Get(SharedObjectContext ctx) => Task.FromResult(0);
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "CounterObjectClient.g.cs");

        Assert.NotNull(generated);
        // Key is stored in constructor, not per-method
        Assert.Contains("private readonly string _key;", generated);
        Assert.Contains("ICounterObjectClient", generated);
        Assert.Contains("ICounterObjectSendClient", generated);
        // Methods should NOT have key parameter
        Assert.Contains("AddAsync(int request)", generated);
        Assert.Contains("GetAsync()", generated);
    }

    [Fact]
    public void Workflow_GeneratesKeyBoundClient()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Workflow]
                     public class OrderWorkflow
                     {
                         [Handler]
                         public Task<string> Run(WorkflowContext ctx) => Task.FromResult("done");

                         [SharedHandler]
                         public Task<string> GetStatus(SharedWorkflowContext ctx) => Task.FromResult("pending");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "OrderWorkflowClient.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("private readonly string _key;", generated);
        Assert.Contains("IOrderWorkflowClient", generated);
        Assert.Contains("IOrderWorkflowSendClient", generated);
    }

    [Fact]
    public void CustomName_UsesOverride()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service(Name = "MyGreeter")]
                     public class GreeterService
                     {
                         [Handler(Name = "SayHi")]
                         public Task<string> Greet(Context ctx) => Task.FromResult("Hi");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "GreeterServiceClient.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("\"MyGreeter\"", generated);
        Assert.Contains("\"SayHi\"", generated);
        Assert.Contains("SayHiAsync", generated);
    }

    [Fact]
    public void VoidHandler_GeneratesValueTaskReturn()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class MyService
                     {
                         [Handler]
                         public Task DoWork(Context ctx) => Task.CompletedTask;
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "MyServiceClient.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("ValueTask DoWorkAsync()", generated);
    }

    [Fact]
    public void NoServiceAttribute_GeneratesNothing()
    {
        var source = """
                     namespace TestApp;

                     public class RegularClass
                     {
                         public void DoSomething() { }
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var result = driver.GetRunResult();

        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void NoDiagnosticErrors()
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

        var (_, _, diagnostics) = GeneratorTestHelper.RunGenerator(source);

        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void Implementation_DelegatesToContextCall()
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

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "GreeterServiceClient.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("_context.Call<", generated);
        Assert.Contains("\"GreeterService\"", generated);
        Assert.Contains("\"Greet\"", generated);
    }

    [Fact]
    public void SendClient_DelegatesToContextSend()
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

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "GreeterServiceClient.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("_context.Send(", generated);
        Assert.Contains("_options", generated);
    }

    [Fact]
    public void Diagnostic_MultipleInputParameters()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class BadService
                     {
                         [Handler]
                         public Task<string> Greet(Context ctx, string name, int count) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.Contains(diagnostics, d => d.Id == "RESTATE001");
    }

    [Fact]
    public void Diagnostic_MissingContextParameter()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class BadService
                     {
                         [Handler]
                         public Task<string> Greet(string name) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.Contains(diagnostics, d => d.Id == "RESTATE002");
    }

    [Fact]
    public void NestedServiceClass_EmitsRESTATE003()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     public class Outer
                     {
                         [Service]
                         public class NestedService
                         {
                             [Handler]
                             public Task<string> Greet(Context ctx) => Task.FromResult("Hello");
                         }
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        // Nested classes emit RESTATE003 and no code is generated
        Assert.Contains(diagnostics, d => d.Id == "RESTATE003");
        Assert.Single(diagnostics);
    }

    [Fact]
    public void NonPublicServiceClass_EmitsRESTATE008()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     internal class InternalService
                     {
                         [Handler]
                         public Task<string> Greet(Context ctx) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        // Non-public classes emit RESTATE008 and no code is generated
        Assert.Contains(diagnostics, d => d.Id == "RESTATE008");
        Assert.Single(diagnostics);
    }

    [Fact]
    public void AbstractServiceClass_EmitsRESTATE008()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public abstract class AbstractService
                     {
                         [Handler]
                         public Task<string> Greet(Context ctx) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        // Abstract classes emit RESTATE008 and no code is generated
        Assert.Contains(diagnostics, d => d.Id == "RESTATE008");
        Assert.Single(diagnostics);
    }

    [Fact]
    public void Diagnostic_ValidService_NoDiagnostics()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class GoodService
                     {
                         [Handler]
                         public Task<string> Greet(Context ctx, string name) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Service_InterfaceContextParameter_MatchesClassEquivalent()
    {
        var withInterface = """
                            using Restate.Sdk;
                            using System.Threading.Tasks;

                            namespace TestApp;

                            [Service]
                            public class GreeterService
                            {
                                [Handler]
                                public Task<string> Greet(IContext ctx, string name) => Task.FromResult("Hello");
                            }
                            """;

        var withClass = withInterface.Replace("IContext ctx", "Context ctx", StringComparison.Ordinal);

        var (interfaceDriver, _, _) = GeneratorTestHelper.RunGenerator(withInterface);
        var (classDriver, _, _) = GeneratorTestHelper.RunGenerator(withClass);

        Assert.Empty(GeneratorTestHelper.GetGeneratorDiagnostics(interfaceDriver));

        var interfaceClient = GeneratorTestHelper.GetGeneratedSource(interfaceDriver, "GreeterServiceClient.g.cs");
        var classClient = GeneratorTestHelper.GetGeneratedSource(classDriver, "GreeterServiceClient.g.cs");
        Assert.Equal(classClient, interfaceClient);

        // The invoker casts the context to the declared parameter type; everything else matches.
        var interfaceInvoker = GeneratorTestHelper.GetGeneratedSource(interfaceDriver, "GreeterServiceInvokers.g.cs");
        var classInvoker = GeneratorTestHelper.GetGeneratedSource(classDriver, "GreeterServiceInvokers.g.cs");
        Assert.NotNull(interfaceInvoker);
        Assert.Contains("(global::Restate.Sdk.IContext)context", interfaceInvoker);
        Assert.Equal(
            classInvoker,
            interfaceInvoker.Replace("global::Restate.Sdk.IContext", "global::Restate.Sdk.Context",
                StringComparison.Ordinal));
    }

    [Fact]
    public void VirtualObject_InterfaceContextParameters_MatchClassEquivalent()
    {
        var withInterfaces = """
                             using Restate.Sdk;
                             using System.Threading.Tasks;

                             namespace TestApp;

                             [VirtualObject]
                             public class CounterObject
                             {
                                 [Handler]
                                 public Task<int> Add(IObjectContext ctx, int delta) => Task.FromResult(delta);

                                 [SharedHandler]
                                 public Task<int> Get(ISharedObjectContext ctx) => Task.FromResult(0);
                             }
                             """;

        var withClasses = withInterfaces
            .Replace("IObjectContext ctx", "ObjectContext ctx", StringComparison.Ordinal)
            .Replace("ISharedObjectContext ctx", "SharedObjectContext ctx", StringComparison.Ordinal);

        var (interfaceDriver, _, _) = GeneratorTestHelper.RunGenerator(withInterfaces);
        var (classDriver, _, _) = GeneratorTestHelper.RunGenerator(withClasses);

        Assert.Empty(GeneratorTestHelper.GetGeneratorDiagnostics(interfaceDriver));

        Assert.Equal(
            GeneratorTestHelper.GetGeneratedSource(classDriver, "CounterObjectClient.g.cs"),
            GeneratorTestHelper.GetGeneratedSource(interfaceDriver, "CounterObjectClient.g.cs"));

        var interfaceInvoker = GeneratorTestHelper.GetGeneratedSource(interfaceDriver, "CounterObjectInvokers.g.cs");
        Assert.NotNull(interfaceInvoker);
        Assert.Contains("(global::Restate.Sdk.IObjectContext)context", interfaceInvoker);
        Assert.Contains("(global::Restate.Sdk.ISharedObjectContext)context", interfaceInvoker);
    }

    [Fact]
    public void Handler_UnrelatedInterfaceParameter_EmitsRESTATE002()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     public interface INotAContext;

                     [Service]
                     public class BadService
                     {
                         [Handler]
                         public Task<string> Greet(INotAContext ctx) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.Contains(diagnostics, d => d.Id == "RESTATE002");
    }

    [Fact]
    public void Workflow_LowercaseRunHandler_NoRESTATE004()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Workflow]
                     public class OrderWorkflow
                     {
                         [Handler(Name = "run")]
                         public Task<string> Execute(WorkflowContext ctx) => Task.FromResult("done");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.DoesNotContain(diagnostics, d => d.Id == "RESTATE004");

        // The declared casing is the wire name and must survive untouched.
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "OrderWorkflowClient.g.cs");
        Assert.NotNull(generated);
        Assert.Contains("\"run\"", generated);
    }

    [Fact]
    public void Workflow_RunHandler_NoRESTATE004()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Workflow]
                     public class OrderWorkflow
                     {
                         [Handler]
                         public Task<string> Run(WorkflowContext ctx) => Task.FromResult("done");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.DoesNotContain(diagnostics, d => d.Id == "RESTATE004");
    }

    [Fact]
    public void Workflow_NoRunHandler_EmitsRESTATE004()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Workflow]
                     public class OrderWorkflow
                     {
                         [SharedHandler]
                         public Task<string> GetStatus(SharedWorkflowContext ctx) => Task.FromResult("pending");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.Contains(diagnostics, d => d.Id == "RESTATE004");
    }

    [Fact]
    public void Service_GeneratesFutureMethods()
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

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "GreeterServiceClient.g.cs");

        Assert.NotNull(generated);
        // Future method on interface
        Assert.Contains("IDurableFuture<string>", generated);
        Assert.Contains("GreetFuture(", generated);
        // Future method delegates to CallFuture
        Assert.Contains("CallFuture<", generated);
    }

    [Fact]
    public void VoidHandler_NoFutureMethod()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class MyService
                     {
                         [Handler]
                         public Task DoWork(Context ctx) => Task.CompletedTask;
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "MyServiceClient.g.cs");

        Assert.NotNull(generated);
        // Void handlers should NOT get Future methods
        Assert.DoesNotContain("DoWorkFuture", generated);
    }

    [Fact]
    public void VirtualObject_FutureMethod_UsesKey()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [VirtualObject]
                     public class CounterObject
                     {
                         [Handler]
                         public Task<int> Add(ObjectContext ctx, int delta) => Task.FromResult(delta);
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var generated = GeneratorTestHelper.GetGeneratedSource(driver, "CounterObjectClient.g.cs");

        Assert.NotNull(generated);
        Assert.Contains("AddFuture(", generated);
        // Future call should include _key
        Assert.Contains("_context.CallFuture<int>", generated);
    }

    [Fact]
    public void Diagnostic_InvalidTimeSpanFormat_EmitsRESTATE009()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class TimerService
                     {
                         [Handler(InactivityTimeout = "invalid")]
                         public Task<string> Greet(Context ctx, string name) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.Contains(diagnostics, d => d.Id == "RESTATE009");
    }

    [Fact]
    public void Diagnostic_ValidTimeSpanFormat_NoDiagnostics()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class TimerService
                     {
                         [Handler(InactivityTimeout = "00:05:00")]
                         public Task<string> Greet(Context ctx, string name) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Diagnostic_InvalidAbortTimeout_EmitsRESTATE009()
    {
        var source = """
                     using Restate.Sdk;
                     using System.Threading.Tasks;

                     namespace TestApp;

                     [Service]
                     public class TimerService
                     {
                         [Handler(AbortTimeout = "xyz")]
                         public Task<string> Greet(Context ctx, string name) => Task.FromResult("Hello");
                     }
                     """;

        var (driver, _, _) = GeneratorTestHelper.RunGenerator(source);
        var diagnostics = GeneratorTestHelper.GetGeneratorDiagnostics(driver);

        Assert.Contains(diagnostics, d => d.Id == "RESTATE009");
    }
}