using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// git push 的参数拼装。
///
/// git 的语法是 push [&lt;remote&gt; [&lt;refspec&gt;]]，只给一个位置参数时它当远端名 ——
/// 「只给分支」如果不补 origin，就会变成 git push main，git 去找一个叫 main 的远端然后失败。
/// </summary>
public class GitWriterTests
{
    [Fact]
    public void 只给分支时补上origin()
    {
        Assert.Equal(new[] { "push", "origin", "main" }, GitWriter.BuildPushArgs("main", null));
    }

    [Fact]
    public void 远端与分支都给出时保持顺序()
    {
        Assert.Equal(new[] { "push", "upstream", "dev" }, GitWriter.BuildPushArgs("dev", "upstream"));
    }

    [Fact]
    public void 只给远端时只推远端()
    {
        Assert.Equal(new[] { "push", "upstream" }, GitWriter.BuildPushArgs(null, "upstream"));
    }

    [Fact]
    public void 都不给时用默认推送()
    {
        Assert.Equal(new[] { "push" }, GitWriter.BuildPushArgs(null, null));
    }
}
