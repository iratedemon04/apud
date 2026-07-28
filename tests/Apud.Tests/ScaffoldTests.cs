using Marc.Core;

namespace Apud.Tests;

public class ScaffoldTests
{
    [Fact]
    public void TestRunner_IsWired_AndSeesMarcCore()
    {
        Assert.Equal(24, MarcConstants.LeaderLength);
    }
}
