using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);
if (args.FirstOrDefault()?.StartsWith("rpc-", StringComparison.Ordinal) == true)
    return await ProtocolFixture.RunCodexAsync(args[0]);
if (args.FirstOrDefault()?.StartsWith("claude-", StringComparison.Ordinal) == true)
    return await ProtocolFixture.RunClaudeAsync(args);
var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
if (args.Contains("--version"))
{
    Console.WriteLine(name == "codex" ? "codex-cli 0.1.0-test.1" : "2.1.0 (Claude Code)");
    return 0;
}
if (args.Contains("--help"))
{
    Console.WriteLine(name == "codex" ? "app-server --listen --stdio" : "--print --input-format --output-format --resume --session-id --append-system-prompt --permission-mode --permission-prompts --max-budget-usd");
    return 0;
}
switch (args.FirstOrDefault())
{
    case "echo": Console.WriteLine(JsonSerializer.Serialize(args.Skip(1))); return 0;
    case "fail": Console.Error.WriteLine("Falha de teste"); return 7;
    case "flood":
        await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 20000; i++) Console.WriteLine("stdout teste"); }),
            Task.Run(() => { for (var i = 0; i < 20000; i++) Console.Error.WriteLine("stderr teste"); }));
        return 0;
    case "child":
        using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "wait" } })!)
        {
            Console.WriteLine($"CHILD:{child.Id}");
            await Task.Delay(TimeSpan.FromMinutes(5));
        }
        return 0;
    case "wait": await Task.Delay(TimeSpan.FromMinutes(5)); return 0;
    default: return 1;
}
