using API.Security;

namespace Tests;

public class LoginTests
{
    [Theory]
    [InlineData("jack", "longenough", true)]
    [InlineData("jack", "short", false)]
    [InlineData("", "longenough", false)]
    [InlineData("   ", "longenough", false)]
    public void PasswordProblem_AcceptsOnlyUsableCredentials(string username, string password, bool ok)
    {
        Assert.Equal(ok, AuthStore.PasswordProblem(username, password) is null);
    }
}
