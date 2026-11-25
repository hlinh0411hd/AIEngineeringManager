using Employee.Core;
using Employee.Infrastructure;

// Which CLIs a Pixel Art request may use, and in what order (cliOrder).
static class PixelCliOrderChecks
{
    public static int Run()
    {
        void Assert(bool condition, string message)
        {
            if (!condition)
                throw new Exception("Pixel CLI order check failed: " + message);
        }

        static ProviderSettings P(string id, bool enabled, bool turnedOff = false) =>
            new() { Id = id, Enabled = enabled, TurnedOff = turnedOff };

        var settings = new[] { P("codex", true), P("claude", false), P("antigravity", true) };

        // 1. Switched-on CLIs, in Settings order; switched-off ones are left out.
        var order = ToolTaskExecutor.PixelCliOrder([null, ""], settings);
        Assert(order.SequenceEqual(["codex", "antigravity"]), "enabled CLIs must follow Settings order, got " + string.Join(",", order));

        // 2. A CLI turned off stays out even when Enabled is set.
        var off = ToolTaskExecutor.PixelCliOrder([null], [P("codex", true, turnedOff: true), P("antigravity", true)]);
        Assert(off.SequenceEqual(["antigravity"]), "a turned-off CLI must not be used, got " + string.Join(",", off));

        // Claude CLI is not offered for Pixel Art, even when it is switched on in Settings or named in the request.
        var claudeOn = ToolTaskExecutor.PixelCliOrder([null, "dùng claude cho ảnh này"], [P("claude", true)]);
        Assert(claudeOn.Count == 0, "Claude must never be used for Pixel Art, got " + string.Join(",", claudeOn));

        // 3. A CLI named in the request goes first, even when it is switched off in Settings.
        var named = ToolTaskExecutor.PixelCliOrder(["dùng antigravity cho ảnh này"], settings);
        Assert(named.SequenceEqual(["antigravity", "codex"]), "the named CLI must come first, got " + string.Join(",", named));

        // 4. "gemini" and "chatgpt" in the request mean the web sources.
        var gemini = ToolTaskExecutor.PixelCliOrder(["dùng gemini để vẽ"], settings);
        Assert(gemini.FirstOrDefault() == "gemini-web", "'gemini' must map to gemini-web, got " + string.Join(",", gemini));
        var chatgpt = ToolTaskExecutor.PixelCliOrder(["dùng chatgpt để vẽ"], settings);
        Assert(chatgpt.FirstOrDefault() == "chatgpt-web", "'chatgpt' must map to chatgpt-web, got " + string.Join(",", chatgpt));

        // 5. The web sources are never added on their own.
        Assert(!order.Contains("gemini-web") && !order.Contains("chatgpt-web"), "web sources must not appear without being named");

        // 6. A CLI the request says to avoid is removed.
        var avoid = ToolTaskExecutor.PixelCliOrder(["không dùng codex"], settings);
        Assert(!avoid.Contains("codex") && avoid.Contains("antigravity"), "an avoided CLI must be removed, got " + string.Join(",", avoid));

        // 7. "X thay cho Y": X first, Y removed.
        var swap = ToolTaskExecutor.PixelCliOrder(["dùng antigravity thay cho codex"], settings);
        Assert(swap.FirstOrDefault() == "antigravity" && !swap.Contains("codex"), "'use X instead of Y' must put X first and drop Y, got " + string.Join(",", swap));

        // 8. Nothing switched on and nothing named gives an empty order (the caller reports it).
        var none = ToolTaskExecutor.PixelCliOrder([null], [P("codex", false)]);
        Assert(none.Count == 0, "no CLI switched on must give an empty order");

        // 9. The first text that names a CLI wins; later texts are not read for the choice.
        var first = ToolTaskExecutor.PixelCliOrder([null, "dùng antigravity", "dùng codex"], settings);
        Assert(first.FirstOrDefault() == "antigravity", "the first text that names a CLI must win, got " + string.Join(",", first));

        Console.WriteLine("PASS pixel CLI order: Settings order, turned-off CLIs, named CLI first, web aliases, avoid, swap, empty, first named wins");
        return 0;
    }
}
