using System.Reflection;
using System.Runtime.Loader;

namespace Valleysoft.Dredge.Analyzers.Tests;

public class SettingsRuntimeTests
{
    [Fact]
    public void GeneratedAccessorsPreservePathAndNullableBehavior()
    {
        var compilation = GeneratorTestHelper.CreateCompilation("""
            using System;
            using System.Collections.Generic;
            using System.Text.Json.Serialization;
            using Valleysoft.Dredge;
            [GenerateSettings]
            public partial class Options
            {
                [JsonPropertyName("child")]
                public Child? Child { get; set; } = new();
                [JsonPropertyName("other")]
                public Child Other { get; } = new();
            }
            [GenerateSettings]
            public partial class Child
            {
                [JsonPropertyName("value")]
                public string? Value { get; private set; }
                [JsonPropertyName("\"quote\\slash\n\t\u263a")]
                public string Escaped { get; set; } = "";
            }
            public static class Scenario
            {
                public static void Run()
                {
                    var options = new Options();
                    Check(options.GetProperty(new(["child", "value"])) is null);
                    var path = new Queue<string>(["child", "value"]);
                    options.SetProperty(path, "updated");
                    Check(path.Count == 0 && options.Child!.Value == "updated");
                    path = new(["child", "value"]);
                    Check((string?)options.GetProperty(path) == "updated" && path.Count == 0);
                    options.SetProperty(new(["other", "value"]), "read-only branch");
                    Check(options.Other.Value == "read-only branch");
                    options.SetProperty(new(["child", "\"quote\\slash\n\t\u263a"]), "escaped");
                    Check((string?)options.GetProperty(new(["child", "\"quote\\slash\n\t\u263a"])) == "escaped");
                    foreach (string[] invalid in new string[][] {
                        [], ["unknown"], ["child"], ["child", "unknown"],
                        ["child", "value", "extra"], ["Child", "value"] })
                    {
                        Throws<ArgumentException>(() => options.GetProperty(new(invalid)));
                        Throws<ArgumentException>(() => options.SetProperty(new(invalid), "bad"));
                    }
                    Check(options.Child!.Value == "updated");
                    options.Child = null;
                    Throws<InvalidOperationException>(() => options.GetProperty(new(["child", "value"])));
                    Throws<InvalidOperationException>(() => options.SetProperty(new(["child", "value"]), "bad"));
                    Throws<ArgumentException>(() => options.GetProperty(new(["child"])));
                    Check(options.Child is null);
                }
                private static void Check(bool value)
                {
                    if (!value) throw new Exception("Generated accessor contract failed.");
                }
                private static void Throws<T>(Action action) where T : Exception
                {
                    try { action(); }
                    catch (T exception)
                    {
                        Check(exception.Message.Length > 0);
                        return;
                    }
                    throw new Exception("Expected " + typeof(T).Name);
                }
            }
            """);
        var driver = GeneratorTestHelper.Run(compilation, out var output);
        Assert.Empty(GeneratorTestHelper.Result(driver).Diagnostics);
        GeneratorTestHelper.AssertCompiles(output);

        using var stream = new MemoryStream();
        var emit = output.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var loadContext = new AssemblyLoadContext(nameof(SettingsRuntimeTests), isCollectible: true);
        try
        {
            var assembly = loadContext.LoadFromStream(stream);
            assembly.GetType("Scenario")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        }
        finally
        {
            loadContext.Unload();
        }
    }
}
