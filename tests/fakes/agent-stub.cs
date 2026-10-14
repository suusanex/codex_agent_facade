#:property TargetFramework=net10.0
#:property OutputType=Exe
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false

using System.Text.Json;

// テストが PATH 上の agent CLI の代わりに起動する。実推論はしない。
var capture = Environment.GetEnvironmentVariable("FAKE_AGENT_CAPTURE");
if (string.IsNullOrWhiteSpace(capture))
{
    Console.Error.WriteLine("FAKE_AGENT_CAPTURE is required.");
    return 2;
}

Directory.CreateDirectory(capture);
if (args.Length > 0 && string.Equals(args[0], "models", StringComparison.Ordinal))
{
    Console.Out.Write("""
        composer-2.5 - Composer 2.5
        composer-2.5-fast - Composer 2.5 Fast
        claude-opus-4-8-high - Claude Opus 4.8 1M
        claude-opus-4-8-high-fast - Claude Opus 4.8 1M Fast
        gpt-5.3-codex - Codex 5.3
        gpt-5.3-codex-high - Codex 5.3 High

        """);
    return 0;
}

var stdin = Console.In.ReadToEnd();
File.WriteAllText(Path.Combine(capture, "stdin.txt"), stdin);
File.WriteAllText(Path.Combine(capture, "args.json"), JsonSerializer.Serialize(args));
var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
File.WriteAllText(Path.Combine(capture, "name.txt"), processName);
File.WriteAllText(Path.Combine(capture, "pid.txt"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

var sleepText = Environment.GetEnvironmentVariable("FAKE_AGENT_SLEEP_MS");
if (int.TryParse(sleepText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sleepMs)
    && sleepMs > 0)
{
    Thread.Sleep(sleepMs);
}

var exitText = Environment.GetEnvironmentVariable("FAKE_AGENT_EXIT_CODE");
if (int.TryParse(exitText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var exitCode)
    && exitCode != 0)
{
    Console.Error.WriteLine("fake agent failed");
    return exitCode;
}

var profile = Environment.GetEnvironmentVariable("FAKE_AGENT_PROFILE");
if (string.IsNullOrWhiteSpace(profile))
{
    profile = processName;
}

Console.Out.Write(ProfileStdout(profile));
return 0;

static string ProfileStdout(string profile)
{
    return profile switch
    {
        "github-copilot" or "copilot" =>
            """
            {"type":"assistant.turn_start"}
            {"type":"assistant.message","data":{"content":"pong"}}
            {"type":"tool","name":"RAW_TOOL_MARKER"}
            {"type":"assistant.turn_end"}
            {"type":"result","sessionId":"sess-copilot"}

            """,
        "grok-build" or "grok" =>
            """
            {"type":"text","data":"pong"}
            {"type":"tool","name":"RAW_TOOL_MARKER"}
            {"type":"end","sessionId":"22222222-2222-2222-2222-222222222222"}

            """,
        "cursor" or "cursor-agent" or "cursor-stub" =>
            """
            {"type":"system","subtype":"init","session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}
            {"type":"tool_call","subtype":"started","tool_call":{"name":"RAW_TOOL_MARKER"}}
            {"type":"result","subtype":"success","is_error":false,"result":"pong","session_id":"c6b62c6f-7ead-4fd6-9922-e952131177ff"}

            """,
        "codex" => CodexStdout(),
        _ => throw new InvalidOperationException("Unknown fake agent profile '" + profile + "'."),
    };
}

static string CodexStdout()
{
    var threadId = Environment.GetEnvironmentVariable("FAKE_AGENT_THREAD_ID");
    if (string.IsNullOrWhiteSpace(threadId))
    {
        threadId = "thread-new";
    }

    return "{\"type\":\"thread.started\",\"thread_id\":\"" + threadId + "\"}\n"
        + "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"pong\"}}\n"
        + "{\"type\":\"item.completed\",\"item\":{\"type\":\"tool\",\"name\":\"RAW_TOOL_MARKER\"}}\n"
        + "{\"type\":\"turn.completed\"}\n";
}
