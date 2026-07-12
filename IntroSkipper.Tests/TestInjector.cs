using System;
using IntroSkipper.Configuration;
using IntroSkipper.Helper;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestInjector
{
    private const string CurrentJellyfinBundleSnippet =
        "t.prototype.showSkipButton=function(e){var t=this,n=this.skipElement;if(n){var r=document.activeElement&&ye.A.isCurrentlyFocusable(document.activeElement);e.keep||(t.hideTimeout=setTimeout(t.hideSkipButton.bind(t),8e3))}}" +
        "function onOsdClose(){return this.visible?this.show():this.hideTimeout||this.hideSkipButton()}" +
        "var a=((r={})[i.w.Intro]=o.M.AskToSkip,r[i.w.Outro]=o.M.AskToSkip,r)" +
        "function timeUpdate(){if(this.currentSegment){var e=100;H(this.currentSegment,e)||(this.currentSegment=null,this.hideSkipButton())}}";

    [Fact]
    public void FileTransformer_UpdatesCurrentJellyfinBundleShape_ForConfiguredHideDelay()
    {
        using var scope = CreatePluginScope(skipbuttonHideDelay: 5);

        var transformed = Transform(CurrentJellyfinBundleSnippet);

        Assert.Contains("e.keep||(t.hideTimeout=setTimeout(t.hideSkipButton.bind(t),5000))", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("8e3", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("e.keep||false||", transformed, StringComparison.Ordinal);
        Assert.Contains("var r=document.activeElement&&ye.A.isCurrentlyFocusable(document.activeElement)&&t.playbackManager.currentTime()>1000", transformed, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransformer_UsesMatchedPlaybackReceiver_ForFocusabilityCheck()
    {
        using var scope = CreatePluginScope(skipbuttonHideDelay: 5);
        const string bundle = "x.prototype.showSkipButton=function(o){var s=this,n=this.skipElement;if(n){var a=document.activeElement&&fm.A.isCurrentlyFocusable(document.activeElement);return a}}";

        var transformed = Transform(bundle);

        Assert.Contains("var a=document.activeElement&&fm.A.isCurrentlyFocusable(document.activeElement)&&s.playbackManager.currentTime()>1000", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("t.playbackManager", transformed, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransformer_MakesCurrentJellyfinBundleShapePersistent_ForZeroHideDelay()
    {
        using var scope = CreatePluginScope(skipbuttonHideDelay: 0);

        var transformed = Transform(CurrentJellyfinBundleSnippet);

        Assert.Contains("var r=document.activeElement&&ye.A.isCurrentlyFocusable(document.activeElement)&&t.playbackManager.currentTime()>1000;true", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout(", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("hideTimeout||this.hideSkipButton()", transformed, StringComparison.Ordinal);
        Assert.Contains("return this.visible?this.show():true}", transformed, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransformer_AutoSkipsIntro_WhenConfigured()
    {
        using var scope = CreatePluginScope(skipbuttonHideDelay: 8, autoSkipIntro: true);

        var transformed = Transform(CurrentJellyfinBundleSnippet);

        Assert.Contains("[i.w.Intro]=o.M.Skip", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("[i.w.Intro]=o.M.AskToSkip", transformed, StringComparison.Ordinal);
        // Outro should remain unchanged
        Assert.Contains("[i.w.Outro]=o.M.AskToSkip", transformed, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransformer_AutoSkipsCredits_WhenConfigured()
    {
        using var scope = CreatePluginScope(skipbuttonHideDelay: 8, autoSkipCredits: true);

        var transformed = Transform(CurrentJellyfinBundleSnippet);

        Assert.Contains("[i.w.Outro]=o.M.Skip", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("[i.w.Outro]=o.M.AskToSkip", transformed, StringComparison.Ordinal);
        // Intro should remain unchanged
        Assert.Contains("[i.w.Intro]=o.M.AskToSkip", transformed, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransformer_LimitsButtonVisibility_WhenSecondsConfigured()
    {
        using var scope = CreatePluginScope(skipbuttonHideDelay: 8, skipButtonVisibleSeconds: 10);

        var transformed = Transform(CurrentJellyfinBundleSnippet);

        // threshold=10s=100000000 ticks, floor=hideDelay 8s=80000000 ticks
        var cutoff = "Math.max(this.currentSegment.StartTicks+80000000,this.currentSegment.EndTicks-100000000)";
        Assert.Contains($"H(this.currentSegment,e)?(e>={cutoff}&&this.hideSkipButton()):(this.currentSegment=null,this.hideSkipButton())", transformed, StringComparison.Ordinal);
        Assert.Contains($"showSkipButton=function(e){{if(this.currentSegment&&this.playbackManager.currentTime(this.player)*1e4>={cutoff})return;", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("H(this.currentSegment,e)||(this.currentSegment=null", transformed, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransformer_LeavesDefaultsUntouched_WhenFeaturesDisabled()
    {
        using var scope = CreatePluginScope(skipbuttonHideDelay: 8);

        var transformed = Transform(CurrentJellyfinBundleSnippet);

        Assert.Contains("[i.w.Intro]=o.M.AskToSkip", transformed, StringComparison.Ordinal);
        Assert.Contains("[i.w.Outro]=o.M.AskToSkip", transformed, StringComparison.Ordinal);
        Assert.Contains("H(this.currentSegment,e)||(this.currentSegment=null", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("if(this.currentSegment&&this.playbackManager.currentTime", transformed, StringComparison.Ordinal);
    }

    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope(
        int skipbuttonHideDelay,
        bool autoSkipIntro = false,
        bool autoSkipCredits = false,
        int skipButtonVisibleSeconds = 0)
    {
        var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(
            Plugin.Instance!,
            "Configuration",
            new PluginConfiguration
            {
                UseFileTransformationPlugin = true,
                SkipbuttonHideDelay = skipbuttonHideDelay,
                AutoSkipIntro = autoSkipIntro,
                AutoSkipCredits = autoSkipCredits,
                SkipButtonVisibleSeconds = skipButtonVisibleSeconds,
            });
        return scope;
    }

    private static string Transform(string contents) => Injector.FileTransformer(new PayloadRequest { Contents = contents });
}
