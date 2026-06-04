using LlmTornado.Common;
using System.Text.Json.Serialization;

namespace GUA_Blazor.Tools;

public class StopTool : AITool<StopToolArgs>
{
    public override ToolFunction GetToolFunction()
    {
        return new ToolFunction("stop_loop", "stops the agentic loop, true/false value", new
        {
            type = "object",
            properties = new
            {
                stoploop = new { type = "string" },
            },
            required = new List<string> { "stoploop" }
        });
    }

    protected override string Execute(StopToolArgs args)
    {
        return "stopping_loop";  // any call to stop_loop means stop
    }
}

public class StopToolArgs
{
    [JsonPropertyName("stoploop")]
    public string StopLoop { get; set; } = "false";
}