# AWS Lambda

Deploy handlers as Lambda functions using the `Restate.Sdk.Lambda` package:

```bash
dotnet add package Restate.Sdk.Lambda
```

Derive from `RestateLambdaHandler` and bind your services:

```csharp
using Restate.Sdk;

public class Handler : RestateLambdaHandler
{
    public override void Register()
    {
        Bind<GreeterService>();
        Bind<CounterObject>();
    }
}
```

Configure the Lambda function handler as `YourAssembly::YourNamespace.Handler::FunctionHandler`.

The adapter uses Restate's REQUEST_RESPONSE invocation mode. Register the Lambda deployment
with Restate the same way as an HTTP deployment; see the
[Restate documentation](https://docs.restate.dev) for deployment registration details.
