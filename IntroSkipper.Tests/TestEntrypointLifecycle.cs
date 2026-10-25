// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestEntrypointLifecycle
{
    [Fact]
    public async Task StartAsync_SubscribesHandlersUntilStopAsync()
    {
        var ffmpegService = new StubFFmpegService { VersionCheck = () => true };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AutoDetectIntros = true });
        var (entrypoint, _, library) = EntrypointTestHelpers.CreateEntrypoint(ffmpegService);

        await entrypoint.StartAsync(CancellationToken.None);

        Assert.Equal(1, ffmpegService.VersionCheckCalls);
        Assert.Equal(1, library.ItemAddedSubscriberCount);
        Assert.Equal(1, library.ItemUpdatedSubscriberCount);
        Assert.Equal(1, library.ItemRemovedSubscriberCount);

        await entrypoint.StopAsync(CancellationToken.None);

        Assert.Equal(0, library.ItemAddedSubscriberCount);
        Assert.Equal(0, library.ItemUpdatedSubscriberCount);
        Assert.Equal(0, library.ItemRemovedSubscriberCount);
    }

    [Fact]
    public async Task StartAsync_CompletesWhenFileTransformationRegistrationThrows()
    {
        var pluginInterface = DefineThrowingFileTransformationPlugin();
        var ffmpegService = new StubFFmpegService { VersionCheck = () => true };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { FileTransformationPluginEnabled = true });
        var (entrypoint, _, _) = EntrypointTestHelpers.CreateEntrypoint(ffmpegService);

        await entrypoint.StartAsync(CancellationToken.None);

        Assert.True((bool)pluginInterface.GetField("Called")!.GetValue(null)!);
    }

    // Loads a stand-in for File Transformation whose plugin failed to construct: its
    // RegisterTransformation sets Called, then throws, as the real one does when its plugin instance is null.
    private static Type DefineThrowingFileTransformationPlugin()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Jellyfin.Plugin.FileTransformation"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Jellyfin.Plugin.FileTransformation")
            .DefineType("Jellyfin.Plugin.FileTransformation.PluginInterface", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var called = type.DefineField("Called", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
        var method = type.DefineMethod("RegisterTransformation", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(JObject)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stsfld, called);
        il.Emit(OpCodes.Newobj, typeof(NullReferenceException).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Throw);
        return type.CreateType();
    }
}
