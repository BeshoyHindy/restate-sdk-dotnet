namespace SignupWorkflow;

/// <summary>
///     Stub for an account management API.
/// </summary>
public static class AccountService
{
    public static string Create(string email, string name)
    {
        return $"acct-{Guid.NewGuid():N}";
    }

    public static void Activate(string accountId)
    {
        // Mark the account as fully active in the database.
    }
}

/// <summary>
///     Stub for an email delivery service. The verification email contains a link with the
///     workflow key so the external click can reach the handler that resolves the signal.
/// </summary>
public static class EmailService
{
    public static void SendVerification(string email, string workflowKey)
    {
        // In production: send an email with a link like
        // https://myapp.com/verify?user={workflowKey}
        // When clicked, the frontend invokes SignupWorkflow/{workflowKey}/Verify
    }
}