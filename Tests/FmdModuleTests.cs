using API.LuaModules;

namespace Tests;

public class FmdModuleTests
{
    [Theory]
    [InlineData("hello\n")]
    [InlineData("hello\r\n")] // Windows line endings count as the repository's
    [InlineData("﻿hello\n")] // a byte order mark is ignored
    public void GitSha_MatchesGitsBlobHash(string content)
    {
        // git hash-object of "hello\n"
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", LuaUpdateChecker.GitSha(content));
    }
}
