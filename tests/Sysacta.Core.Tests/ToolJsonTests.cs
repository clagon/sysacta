using System.Text.Json.Nodes;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;

namespace Sysacta.Core.Tests;

public class ToolJsonTests
{
    [Fact]
    public void Schema_is_strict_object_with_constraints()
    {
        var schema = ToolJson.SchemaFor(typeof(AudioSetVolumeArgs));

        Assert.Equal("object", (string?)schema["type"]);
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.Equal(["level"], schema["required"]!.AsArray().Select(n => (string?)n));
        var level = schema["properties"]!["level"]!;
        Assert.Equal(0, (long)level["minimum"]!);
        Assert.Equal(100, (long)level["maximum"]!);
        var direction = schema["properties"]!["direction"]!;
        Assert.Equal(["output", "input"], direction["enum"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("output", (string?)direction["default"]);
        Assert.Equal(512, (int)schema["properties"]!["device"]!["maxLength"]!);
        Assert.NotNull(schema["properties"]!["device"]!["description"]);
    }

    [Fact]
    public void Output_schema_wraps_data_in_envelope()
    {
        var schema = ToolJson.EnvelopeSchemaFor(typeof(AudioSetVolumeData));
        Assert.NotNull(schema["properties"]!["data"]!["properties"]!["previous_volume"]);
        Assert.Contains("request_id", schema["required"]!.AsArray().Select(n => (string?)n));
    }

    [Theory]
    [InlineData("""{"level":50,"extra":1}""")]          // unknown property
    [InlineData("""{"level":"50"}""")]                  // string instead of number
    [InlineData("""{"level":50.5}""")]                  // fractional
    [InlineData("""{"level":101}""")]                   // out of range
    [InlineData("""{"level":-1}""")]                    // out of range
    [InlineData("""{}""")]                              // missing required
    [InlineData("""{"level":50,"direction":"both"}""")] // invalid enum
    [InlineData("""{"level":50,"direction":1}""")]      // integer enum
    [InlineData("""{"level":50,"Direction":"input"}""")] // wrong casing = unknown
    [InlineData("""{"level":50,"level":60}""")]         // duplicate property
    [InlineData("""{"level":null}""")]                  // null for required int
    [InlineData("""{"level":50,"device":""}""")]        // too short
    [InlineData("""[50]""")]                            // not an object
    public void Invalid_arguments_are_rejected(string json)
    {
        var ex = Assert.Throws<ToolFailureException>(() => ToolJson.ParseArguments<AudioSetVolumeArgs>(TestJson.Raw(json)));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Error_message_does_not_echo_values()
    {
        var ex = Assert.Throws<ToolFailureException>(() => ToolJson.ParseArguments<AudioSetVolumeArgs>(TestJson.Raw("""{"level":50,"device":123}""")));
        Assert.DoesNotContain("123", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_arguments_use_defaults()
    {
        var args = ToolJson.ParseArguments<AudioGetStateArgs>(null);
        Assert.Null(args.Device);
        Assert.Equal(AudioDirection.Output, args.Direction);
    }

    [Fact]
    public void Valid_arguments_parse()
    {
        var args = ToolJson.ParseArguments<AudioSetVolumeArgs>(TestJson.Raw("""{"level":30,"device":"spk","direction":"input"}"""));
        Assert.Equal(30, args.Level);
        Assert.Equal("spk", args.Device);
        Assert.Equal(AudioDirection.Input, args.Direction);
    }

    [Fact]
    public void Results_serialize_snake_case()
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(new ToolResult { Status = ToolStatus.Success, Verification = Verification.NotApplicable, RequestId = "r" }, ToolJson.Options)!.AsObject();
        Assert.Equal("success", (string?)node["status"]);
        Assert.Equal("not_applicable", (string?)node["verification"]);
        Assert.False(node.ContainsKey("error"));
    }
}

public class ToolRegistryTests
{
    [Fact]
    public void Rejects_duplicates_and_bad_names()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new EchoTool(), new EchoTool()]));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new BadNameTool()]));
    }

    private sealed class BadNameTool : SysactaTool<EchoArgs, EchoData>
    {
        public override ToolDescriptor Descriptor { get; } = new("AudioThing", ToolCategories.Audio, "x", "x", DefaultPolicy.On, true, []);

        protected override Task<ToolOutcome<EchoData>> ExecuteAsync(EchoArgs args, ToolTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
