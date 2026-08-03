using System.Text.Json;
using CodexAgentSwitch.Application.Abstractions;
using CodexAgentSwitch.Application.Usage;
using CodexAgentSwitch.Application.Workers;
using CodexAgentSwitch.Domain.Orchestration;
using CodexAgentSwitch.Domain.Profiles;
using CodexAgentSwitch.Domain.Providers;
using CodexAgentSwitch.Domain.Usage;
using CodexAgentSwitch.Domain.Workers;
using CodexAgentSwitch.Infrastructure.Persistence;

namespace CodexAgentSwitch.Tests.Usage;

public sealed class UsageBudgetTests
{
    [Theory]
    [InlineData(0.24, true, 0)]
    [InlineData(0.25, true, 1)]
    [InlineData(0.50, true, 2)]
    [InlineData(0.80, true, 3)]
    [InlineData(1.00, false, 4)]
    [InlineData(1.20, false, 4)]
    public void Budget_checkpoints_pause_at_one_hundred_percent(double taskCost, bool allowed, int checkpoints)
    {
        var limits = new BudgetLimits(1m, null, null, null, null, "CNY");
        var result = new BudgetPolicy().Evaluate(limits, new BudgetConsumption((decimal)taskCost, 0m, 0m, 0, 0));

        Assert.Equal(allowed, result.AllowNewRequests);
        Assert.Equal(checkpoints, result.ReachedCheckpoints.Count);
    }

    [Fact]
    public void Cost_is_actual_estimated_or_unavailable_without_false_precision()
    {
        var calculator = new CostCalculator();
        var pricing = new ProviderPricing(2m, 4m, "CNY", new DateOnly(2026, 8, 3));

        Assert.Equal(EvidenceKind.Actual, calculator.Calculate(pricing, 100, 50, 0.01m).Evidence);
        var estimated = calculator.Calculate(pricing, 100_000, 50_000);
        Assert.Equal(EvidenceKind.Estimated, estimated.Evidence);
        Assert.Equal(0.4m, estimated.Value);
        Assert.Equal(EvidenceKind.Unavailable, calculator.Calculate(null, 100, 50).Evidence);
    }

    [Fact]
    public async Task Usage_and_result_are_persisted_before_worker_is_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cas-usage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new SqliteDatabase(Path.Combine(root, "state.db"));
            await database.InitializeAsync();
            var repository = new SqliteUsageLedgerRepository(database);
            var collector = new WorkerUsageCollector(new CostCalculator());
            var coordinator = new SafeWorkerDeletionCoordinator(repository, collector, new FakeClock());
            var adapter = new TerminalAdapter();
            var ledger = Ledger();
            var adoption = new AdoptionRecord(
                "job-1",
                AdoptionStatus.Adopted,
                "Sol skips original parsing",
                "Original parsing skipped",
                false,
                null,
                ReviewLevel.R1,
                null,
                DateTimeOffset.UtcNow);

            await coordinator.ArchiveAndDeleteAsync(
                ledger,
                "job-1",
                adapter,
                adoption,
                new WorkerUsageContext(
                    "deepseek",
                    "deepseek-chat",
                    "CNY",
                    new ProviderPricing(1m, 2m, "CNY", new DateOnly(2026, 8, 3))));

            Assert.True(adapter.Deleted);
            var storedLedger = await repository.GetTaskGroupAsync("group-1");
            var storedUsage = await repository.ListUsageAsync("group-1");
            Assert.NotNull(storedLedger);
            Assert.Equal("worker output", storedLedger.Workers[0].ResultSummary);
            Assert.Equal(AdoptionStatus.Adopted, storedLedger.Workers[0].AdoptionStatus);
            Assert.Single(storedUsage);
            Assert.Equal(15, storedUsage[0].TotalTokens.Value);
            Assert.Equal(EvidenceKind.Actual, storedUsage[0].TotalTokens.Evidence);
            Assert.Equal(EvidenceKind.Estimated, storedUsage[0].Cost.Evidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Report_uses_only_evidence_based_economic_language()
    {
        var ledger = Ledger() with
        {
            Workers =
            [
                Ledger().Workers[0] with
                {
                    AdoptionStatus = AdoptionStatus.Adopted,
                    ActualSkippedWork = "original scan",
                    DuplicateWork = false,
                },
            ],
        };
        var report = new EconomicReportService().Create(ledger, []);

        Assert.Equal(EconomicConclusion.PossiblySaved, report.Conclusion);
        Assert.Contains("没有对照实验", report.ConclusionReason);
        Assert.Equal(EvidenceKind.Unavailable, report.ExternalCost.Evidence);
        Assert.Equal(EvidenceKind.Unavailable, report.TotalTokens.Evidence);
    }

    private static TaskGroupLedger Ledger() => new(
        "group-1",
        "main-thread",
        "gpt-5.6-sol",
        "high",
        DateTimeOffset.UtcNow,
        null,
        [
            new WorkerLedgerEntry(
                "job-1",
                "thread-1",
                "external:deepseek",
                "deepseek-chat",
                "none",
                WorkerJobStatus.Running,
                DateTimeOffset.UtcNow,
                null,
                AdoptionStatus.Pending,
                "parse source",
                "Sol skips source parsing",
                null,
                false,
                null,
                null),
        ],
        DateTimeOffset.UtcNow);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class TerminalAdapter : IWorkerAdapter
    {
        private readonly WorkerJob job = new(
            "external:deepseek",
            "job-1",
            "thread-1",
            "request-1",
            "group-1-L1",
            WorkerJobStatus.Completed,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            "completed");

        public string AdapterId => "external:deepseek";

        public bool Deleted { get; private set; }

        public Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorkerJob> SpawnAsync(WorkerTask task, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorkerJob> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult(job);

        public Task<WorkerResult?> WaitAsync(string jobId, TimeSpan wait, CancellationToken cancellationToken = default)
        {
            var raw = JsonSerializer.Deserialize<JsonElement>("{\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}");
            return Task.FromResult<WorkerResult?>(new WorkerResult("group-1-L1", WorkerJobStatus.Completed, "worker output", raw, [], []));
        }

        public Task SteerAsync(string jobId, WorkerSteerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CancelAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
        {
            Deleted = true;
            return Task.CompletedTask;
        }
    }
}
