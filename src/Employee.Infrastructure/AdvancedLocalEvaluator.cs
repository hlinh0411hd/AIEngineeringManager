using System.Diagnostics;
using System.Text.Json;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed class AdvancedLocalEvaluator(OllamaProvider provider, IAdvancedModelStore store)
{
    // Explicitly invoked local-only objective probes. These never qualify coding, planning or subjective writing.
    public async Task<IReadOnlyList<AdvancedEvaluation>> EvaluateAsync(string model, CancellationToken token)
    {
        var cases = new[]
        {
            ("JSON compliance", "Return ONLY JSON {\"ok\":true,\"count\":3}.", "{\"ok\":true,\"count\":3}"),
            ("JSON compliance", "Return ONLY JSON {\"items\":[\"red\",\"blue\"]}.", "{\"items\":[\"red\",\"blue\"]}"),
            ("Basic reasoning", "Return ONLY JSON {\"answer\":N}. What is 17 + 25?", "{\"answer\":42}"),
            ("Basic reasoning", "Return ONLY JSON {\"answer\":N}. Three boxes contain four sprites each. How many sprites?", "{\"answer\":12}")
        };
        var result = new List<AdvancedEvaluation>();
        foreach (var group in cases.GroupBy(c => c.Item1))
        {
            var passed = 0;
            var time = Stopwatch.StartNew();
            var before = ResourceMonitor.Snapshot()?.AvailableMemoryMb;
            foreach (var sample in group)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var text = await provider.GenerateAsync(model, sample.Item2, true, timeout.Token);
                    using var actual = JsonDocument.Parse(text);
                    using var expected = JsonDocument.Parse(sample.Item3);
                    if (System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(actual.RootElement.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(expected.RootElement.GetRawText())))
                        passed++;
                }
                catch (OperationCanceledException)when (!token.IsCancellationRequested)
                {
                }
                catch (JsonException)
                {
                }

                token.ThrowIfCancellationRequested();
            }

            time.Stop();
            var after = ResourceMonitor.Snapshot()?.AvailableMemoryMb;
            var evaluation = new AdvancedEvaluation(Guid.NewGuid().ToString("N"), model, group.Key, passed, group.Count(), time.ElapsedMilliseconds, before.HasValue && after.HasValue ? before.Value - after.Value : null, DateTimeOffset.UtcNow);
            store.SaveAdvancedEvaluation(evaluation);
            result.Add(evaluation);
        }

        return result;
    }
}
