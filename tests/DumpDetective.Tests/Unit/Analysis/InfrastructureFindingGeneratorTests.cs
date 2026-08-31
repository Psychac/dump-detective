using System.Linq;

using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Unit tests for infrastructure finding generators.
/// All tests use in-memory domain results — no ClrMD/heap access required.
/// </summary>
public sealed class InfrastructureFindingGeneratorTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // DbConnectionFindingGenerator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DbConnection_NoFindings_WhenNotFound()
    {
        var gen = new DbConnectionFindingGenerator();
        var result = new DbConnectionDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);
        gen.CanGenerate(result).Should().BeTrue();
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void DbConnection_NoFindings_BelowThreshold()
    {
        var gen = new DbConnectionFindingGenerator();
        // 49 connections, 10 open — below both the 50-connection Warning threshold and 20-open threshold
        var result = DbConnResult(total: 49, open: 10, closed: 39);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void DbConnection_Warning_AtConnectionCountThreshold()
    {
        var gen = new DbConnectionFindingGenerator();
        var result = DbConnResult(total: 50, open: 5, closed: 45);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Title.Contains("50"));
    }

    [Fact]
    public void DbConnection_Critical_AtHighConnectionCount()
    {
        var gen = new DbConnectionFindingGenerator();
        var result = DbConnResult(total: 200, open: 100, closed: 100);
        var findings = gen.Generate(result);
        findings.Should().Contain(f => f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void DbConnection_OpenConnectionFinding_WhenManyOpen()
    {
        var gen = new DbConnectionFindingGenerator();
        // 60 total, 25 open — both the count finding and the open-count finding should fire
        var result = DbConnResult(total: 60, open: 25, closed: 35);
        var findings = gen.Generate(result);
        findings.Should().HaveCount(2);
        findings.Should().Contain(f => f.Tags.Contains("open"));
    }

    [Fact]
    public void DbConnection_CanGenerate_OnlyForDbConnectionDomainResult()
    {
        var gen = new DbConnectionFindingGenerator();
        gen.CanGenerate(new DbConnectionDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [])).Should().BeTrue();
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [])).Should().BeFalse();
    }

    [Fact]
    public void DbConnection_Gen2Finding_IncludesRootPath_ForHighestRetainedConnection()
    {
        var gen = new DbConnectionFindingGenerator();
        var topOpenConnections = new List<DbConnectionSnapshot>
        {
            new("Microsoft.Data.SqlClient.SqlConnection", 0x1, "Open", 1, null, 2, 100, "Gen2Root: SqlConnection@0x1"),
            new("Microsoft.Data.SqlClient.SqlConnection", 0x2, "Open", 1, null, 2, 9000, "Gen2Root: SqlConnection@0x2"),
        };
        var result = new DbConnectionDomainResult(
            ConnectionsFound: true, TotalConnections: 10, OpenConnections: 10, ClosedConnections: 0,
            BrokenConnections: 0, OtherConnections: 0, UnknownStateConnections: 0,
            Gen2OpenConnections: 5, Gen0OpenConnections: 0, ByType: [], TopOpenConnections: topOpenConnections, TopPools: []);

        var findings = gen.Generate(result);

        findings.Should().Contain(f =>
            f.Title.Contains("Gen2") && f.Evidence.Contains("SqlConnection@0x2"));
    }

    [Fact]
    public void DbConnection_Gen2Finding_OmitsRootPathNote_WhenNoneComputed()
    {
        var gen = new DbConnectionFindingGenerator();
        var result = new DbConnectionDomainResult(
            ConnectionsFound: true, TotalConnections: 10, OpenConnections: 10, ClosedConnections: 0,
            BrokenConnections: 0, OtherConnections: 0, UnknownStateConnections: 0,
            Gen2OpenConnections: 5, Gen0OpenConnections: 0, ByType: [], TopOpenConnections: [], TopPools: []);

        var findings = gen.Generate(result);

        findings.Should().Contain(f => f.Title.Contains("Gen2"));
        findings.Single(f => f.Title.Contains("Gen2")).Evidence.Should().NotContain("Retention path");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SqlConnectionPoolFindingGenerator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlConnectionPool_NoFindings_WhenNotFound()
    {
        var gen = new SqlConnectionPoolFindingGenerator();
        var result = new SqlConnectionPoolDomainResult(false, 0, 0, []);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void SqlConnectionPool_NoFindings_WhenNoneNearCapacity()
    {
        var gen = new SqlConnectionPoolFindingGenerator();
        var pools = new List<SqlConnectionPoolSnapshot>
        {
            new("Microsoft.Data.ProviderBase.DbConnectionPool", 0x1000, 10, 100, 0, "Server=A"),
        };
        var result = new SqlConnectionPoolDomainResult(true, 1, 0, pools);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void SqlConnectionPool_Warning_AtNearCapacity()
    {
        var gen = new SqlConnectionPoolFindingGenerator();
        var pools = new List<SqlConnectionPoolSnapshot>
        {
            new("Microsoft.Data.ProviderBase.DbConnectionPool", 0x1000, 85, 100, 0, "Server=A"),
        };
        var result = new SqlConnectionPoolDomainResult(true, 1, 1, pools);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void SqlConnectionPool_Critical_AtVeryHighUtilization()
    {
        var gen = new SqlConnectionPoolFindingGenerator();
        var pools = new List<SqlConnectionPoolSnapshot>
        {
            new("Microsoft.Data.ProviderBase.DbConnectionPool", 0x1000, 98, 100, 0, "Server=A"),
        };
        var result = new SqlConnectionPoolDomainResult(true, 1, 1, pools);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void SqlConnectionPool_CanGenerate_OnlyForSqlConnectionPoolDomainResult()
    {
        var gen = new SqlConnectionPoolFindingGenerator();
        gen.CanGenerate(new SqlConnectionPoolDomainResult(false, 0, 0, [])).Should().BeTrue();
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [])).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SqlTransactionFindingGenerator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlTransaction_NoFindings_WhenNotFound()
    {
        var gen = new SqlTransactionFindingGenerator();
        var result = new SqlTransactionDomainResult(false, 0, 0, 0, 0, [], []);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void SqlTransaction_NoFindings_BelowThreshold()
    {
        var gen = new SqlTransactionFindingGenerator();
        var result = SqlTxnResult(total: 10, active: 4, disposed: 6);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void SqlTransaction_Warning_AtActiveThreshold()
    {
        var gen = new SqlTransactionFindingGenerator();
        var result = SqlTxnResult(total: 20, active: 5, disposed: 15);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Title.Contains("5"));
    }

    [Fact]
    public void SqlTransaction_Critical_AtHighActiveCount()
    {
        var gen = new SqlTransactionFindingGenerator();
        var result = SqlTxnResult(total: 100, active: 30, disposed: 70);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void SqlTransaction_CanGenerate_OnlyForSqlTransactionDomainResult()
    {
        var gen = new SqlTransactionFindingGenerator();
        gen.CanGenerate(new SqlTransactionDomainResult(false, 0, 0, 0, 0, [], [])).Should().BeTrue();
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [])).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SqlCommandFindingGenerator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlCommand_NoFindings_WhenNotFound()
    {
        var gen = new SqlCommandFindingGenerator();
        var result = new SqlCommandDomainResult(false, 0, 0, 0, [], []);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void SqlCommand_NoFindings_BelowThreshold()
    {
        var gen = new SqlCommandFindingGenerator();
        var result = SqlCmdResult(total: 50, active: 40, disposed: 10);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void SqlCommand_Warning_AtActiveThreshold()
    {
        var gen = new SqlCommandFindingGenerator();
        var result = SqlCmdResult(total: 200, active: 100, disposed: 100);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Title.Contains("100"));
    }

    [Fact]
    public void SqlCommand_Critical_AtHighActiveCount()
    {
        var gen = new SqlCommandFindingGenerator();
        var result = SqlCmdResult(total: 2000, active: 1000, disposed: 1000);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void SqlCommand_CanGenerate_OnlyForSqlCommandDomainResult()
    {
        var gen = new SqlCommandFindingGenerator();
        gen.CanGenerate(new SqlCommandDomainResult(false, 0, 0, 0, [], [])).Should().BeTrue();
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [])).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WcfChannelFindingGenerator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WcfChannel_NoFindings_WhenNotPresent()
    {
        var gen = new WcfChannelFindingGenerator();
        var result = new WcfChannelDomainResult(false, 0, 0, 0, 0, 0, 0, 0, [], []);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void WcfChannel_Critical_WhenFaultedChannels()
    {
        var gen = new WcfChannelFindingGenerator();
        var result = WcfResult(total: 10, opened: 5, faulted: 3, closed: 2);
        var findings = gen.Generate(result);
        findings.Should().Contain(f => f.Severity == FindingSeverity.Critical && f.Tags.Contains("fault"));
    }

    [Fact]
    public void WcfChannel_Warning_AtChannelCountThreshold()
    {
        var gen = new WcfChannelFindingGenerator();
        // 100 channels, none faulted
        var result = WcfResult(total: 100, opened: 50, faulted: 0, closed: 50);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Warning && f.Tags.Contains("leak"));
    }

    [Fact]
    public void WcfChannel_BothFindings_WhenFaultedAndAboveThreshold()
    {
        var gen = new WcfChannelFindingGenerator();
        var result = WcfResult(total: 200, opened: 100, faulted: 10, closed: 90);
        var findings = gen.Generate(result);
        findings.Should().HaveCount(2, "both faulted and count findings should fire");
    }

    [Fact]
    public void WcfChannel_NoCountFinding_BelowThreshold()
    {
        var gen = new WcfChannelFindingGenerator();
        var result = WcfResult(total: 5, opened: 5, faulted: 0, closed: 0);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void WcfChannel_Critical_AtHighCount()
    {
        var gen = new WcfChannelFindingGenerator();
        var result = WcfResult(total: 500, opened: 400, faulted: 0, closed: 100);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void WcfChannel_CountFindingEvidence_IncludesInvalidState_WhenPresent()
    {
        var gen = new WcfChannelFindingGenerator();
        var result = WcfResult(total: 100, opened: 90, faulted: 0, closed: 5) with { InvalidStateCount = 5 };
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("leak") && f.Evidence.Contains("Invalid: 5"));
    }

    [Fact]
    public void WcfChannel_CountFindingEvidence_OmitsInvalidState_WhenZero()
    {
        var gen = new WcfChannelFindingGenerator();
        var result = WcfResult(total: 100, opened: 100, faulted: 0, closed: 0);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("leak") && !f.Evidence.Contains("Invalid"));
    }

    [Fact]
    public void WcfChannel_CountFindingEvidence_IncludesDuplexAndSessionCounts()
    {
        var gen = new WcfChannelFindingGenerator();
        var result = WcfResult(total: 100, opened: 100, faulted: 0, closed: 0) with
        {
            DuplexChannelCount = 40,
            SessionChannelCount = 60,
        };
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f =>
            f.Tags.Contains("leak") && f.Evidence.Contains("Duplex-capable: 40, Session-based: 60."));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HttpObjectFindingGenerator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HttpObject_NoFindings_WhenNotFound()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void HttpObject_Warning_WhenHttpClientAboveThreshold()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 5, webRequests: 0, webResponses: 0);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpclient"));
    }

    [Fact]
    public void HttpObject_Critical_WhenManyHttpClients()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 20, webRequests: 0, webResponses: 0);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void HttpObject_HttpClientEvidence_MentionsGen0Churn_WhenMostlyGen0()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 10, httpClientGen0: 8, httpClientGen1: 1, httpClientGen2: 1);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpclient") && f.Evidence.Contains("per-request allocation"));
    }

    [Fact]
    public void HttpObject_HttpClientEvidence_MentionsGen2Reuse_WhenMostlyGen2()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 10, httpClientGen0: 1, httpClientGen1: 1, httpClientGen2: 8);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpclient") && f.Evidence.Contains("long-lived reuse — the count"));
    }

    [Fact]
    public void HttpObject_HttpClientEvidence_OmitsGenerationCommentary_WhenGenerationUnresolved()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 10);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpclient") && !f.Evidence.Contains("Gen0") && !f.Evidence.Contains("Gen2"));
    }

    [Fact]
    public void HttpObject_Warning_WhenManyWebRequests()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 0, webRequests: 10, webResponses: 0);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpwebrequest"));
    }

    [Fact]
    public void HttpObject_Warning_WhenManyWebResponses()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 0, webRequests: 0, webResponses: 20);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpwebresponse"));
    }

    [Fact]
    public void HttpObject_Warning_WhenManyServicePoints()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 0, webRequests: 0, webResponses: 0, servicePoints: 50);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("servicepoint"));
    }

    [Fact]
    public void HttpObject_ServicePointEvidence_MentionsLowConnectionLimit_WhenSampled()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(servicePoints: 50, topHttpInstances:
        [
            new HttpInstanceSnapshot("ServicePoint", "System.Net.ServicePoint", 0x1000, ConnectionLimit: 2),
            new HttpInstanceSnapshot("ServicePoint", "System.Net.ServicePoint", 0x2000, ConnectionLimit: 100),
        ]);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("servicepoint") && f.Evidence.Contains("ConnectionLimit=2"));
    }

    [Fact]
    public void HttpObject_ServicePointEvidence_OmitsConnectionLimitClause_WhenNoneSampledOrAllHealthy()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(servicePoints: 50, topHttpInstances:
        [
            new HttpInstanceSnapshot("ServicePoint", "System.Net.ServicePoint", 0x1000, ConnectionLimit: 100),
        ]);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("servicepoint") && !f.Evidence.Contains("ConnectionLimit="));
    }

    [Fact]
    public void HttpObject_Warning_WhenManyHandlers()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(handlers: 10);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpmessagehandler") && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void HttpObject_Critical_WhenVeryManyHandlers()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(handlers: 50);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpmessagehandler") && f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void HttpObject_HandlerEvidence_NamesTopModule_WhenHandlerModulesPresent()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(handlers: 10, handlerModules:
        [
            new HttpHandlerModuleSummary("Polly.dll", 7, 700),
            new HttpHandlerModuleSummary("MyApp.dll", 3, 300),
        ]);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpmessagehandler") && f.Evidence.Contains("Polly.dll"));
    }

    [Fact]
    public void HttpObject_Warning_WhenManyExpiredHandlerTrackingEntries()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(activeHandlerTrackingEntries: 3, expiredHandlerTrackingEntries: 20);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Tags.Contains("httpclientfactory"));
    }

    [Fact]
    public void HttpObject_BelowThresholds_NoFindings()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 4, webRequests: 9, webResponses: 5, servicePoints: 10,
            activeHandlerTrackingEntries: 3, expiredHandlerTrackingEntries: 19);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void HttpObject_CanGenerate_OnlyForHttpObjectDomainResult()
    {
        var gen = new HttpObjectFindingGenerator();
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [])).Should().BeTrue();
        gen.CanGenerate(new WcfChannelDomainResult(false, 0, 0, 0, 0, 0, 0, 0, [], [])).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TimerLeakFindingGenerator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TimerLeak_NoFindings_WhenNotFound()
    {
        var gen = new TimerLeakFindingGenerator();
        var result = new TimerLeakDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], []);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void TimerLeak_Warning_AtLogicalCountThreshold()
    {
        var gen = new TimerLeakFindingGenerator();
        // TotalTimers=200 (threading=80 + queue=100 + holder=20), LogicalTimerCount=100 (queue only)
        var result = TimerResult(total: 200, threading: 80, queue: 100, holder: 20);
        var findings = gen.Generate(result);
        findings.Should().Contain(f => f.Severity == FindingSeverity.Warning && f.Title.Contains("100"));
    }

    [Fact]
    public void TimerLeak_Critical_AtHighLogicalCountThreshold()
    {
        var gen = new TimerLeakFindingGenerator();
        // TotalTimers=500 (threading=200 + queue=250 + holder=50), LogicalTimerCount=250 (queue only)
        var result = TimerResult(total: 500, threading: 200, queue: 250, holder: 50);
        var findings = gen.Generate(result);
        findings.Should().Contain(f => f.Severity == FindingSeverity.Critical);
    }

    [Fact]
    public void TimerLeak_QueuePressureFinding_AtThreshold()
    {
        var gen = new TimerLeakFindingGenerator();
        var result = TimerResult(total: 60, threading: 10, queue: 25, holder: 25);
        var findings = gen.Generate(result);
        findings.Should().ContainSingle(f => f.Title.Contains("Timer queue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TimerLeak_CanGenerate_OnlyForTimerLeakDomainResult()
    {
        var gen = new TimerLeakFindingGenerator();
        gen.CanGenerate(new TimerLeakDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [])).Should().BeTrue();
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [])).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static DbConnectionDomainResult DbConnResult(int total, int open, int closed, int broken = 0, int other = 0, int unknown = 0)
    {
        var summary = new List<DbConnectionTypeSummary>
        {
            new("System.Data.SqlClient.SqlConnection", total, open, closed, broken, other, unknown, (ulong)(total * 200))
        };
        return new DbConnectionDomainResult(
            ConnectionsFound: total > 0,
            TotalConnections: total,
            OpenConnections: open,
            ClosedConnections: closed,
            BrokenConnections: broken,
            OtherConnections: other,
            UnknownStateConnections: unknown,
            Gen2OpenConnections: 0,
            Gen0OpenConnections: 0,
            ByType: summary,
            TopOpenConnections: [],
            TopPools: []);
    }

    private static SqlTransactionDomainResult SqlTxnResult(int total, int active, int disposed, int other = 0)
    {
        var summary = new List<SqlTransactionTypeSummary>
        {
            new("System.Data.SqlClient.SqlTransaction", total, disposed, active, other, (ulong)(total * 80))
        };
        return new SqlTransactionDomainResult(
            TransactionsFound: total > 0,
            TotalTransactions: total,
            DisposedCount: disposed,
            ActiveCount: active,
            OtherCount: other,
            ByType: summary,
            TopActiveTransactions: []);
    }

    private static SqlCommandDomainResult SqlCmdResult(int total, int active, int disposed)
    {
        var summary = new List<SqlCommandTypeSummary>
        {
            new("System.Data.SqlClient.SqlCommand", total, disposed, active, (ulong)(total * 120))
        };
        return new SqlCommandDomainResult(
            CommandsFound: total > 0,
            TotalCommands: total,
            DisposedCount: disposed,
            ActiveCount: active,
            ByType: summary,
            TopActiveCommands: []);
    }

    private static WcfChannelDomainResult WcfResult(int total, int opened, int faulted, int closed, int other = 0, int opening = 0, int closing = 0)
    {
        var summary = new List<WcfChannelTypeSummary>
        {
            new("System.ServiceModel.Channels.ServiceChannel", total, opening, opened, faulted, closing, closed, other, (ulong)(total * 512))
        };
        return new WcfChannelDomainResult(
            WcfPresent: total > 0,
            TotalChannels: total,
            OpeningChannels: opening,
            OpenedChannels: opened,
            FaultedChannels: faulted,
            ClosingChannels: closing,
            ClosedChannels: closed,
            OtherChannels: other,
            ByType: summary,
            TopFaultedChannels: []);
    }

    private static HttpObjectDomainResult HttpResult(
        int httpClients = 0, int webRequests = 0, int webResponses = 0,
        int handlers = 0, int servicePoints = 0,
        int activeHandlerTrackingEntries = 0, int expiredHandlerTrackingEntries = 0,
        int httpClientGen0 = 0, int httpClientGen1 = 0, int httpClientGen2 = 0,
        IReadOnlyList<HttpHandlerModuleSummary>? handlerModules = null,
        IReadOnlyList<HttpInstanceSnapshot>? topHttpInstances = null)
    {
        int total = httpClients + webRequests + webResponses + handlers + servicePoints
                  + activeHandlerTrackingEntries + expiredHandlerTrackingEntries;
        var byType = new List<HttpObjectTypeSummary>();
        if (httpClients > 0)  byType.Add(new("System.Net.Http.HttpClient", httpClients, (ulong)(httpClients * 400)));
        if (webRequests > 0)  byType.Add(new("System.Net.HttpWebRequest", webRequests, (ulong)(webRequests * 300)));
        if (webResponses > 0) byType.Add(new("System.Net.HttpWebResponse", webResponses, (ulong)(webResponses * 300)));
        if (servicePoints > 0) byType.Add(new("System.Net.ServicePoint", servicePoints, (ulong)(servicePoints * 200)));
        return new HttpObjectDomainResult(
            HttpObjectsFound: total > 0,
            TotalHttpObjects: total,
            HttpClientCount: httpClients,
            HttpWebRequestCount: webRequests,
            HttpWebResponseCount: webResponses,
            HttpMessageHandlerCount: handlers,
            ServicePointCount: servicePoints,
            ActiveHandlerTrackingEntryCount: activeHandlerTrackingEntries,
            ExpiredHandlerTrackingEntryCount: expiredHandlerTrackingEntries,
            HttpClientGen0Count: httpClientGen0,
            HttpClientGen1Count: httpClientGen1,
            HttpClientGen2Count: httpClientGen2,
            TotalBytes: byType.Aggregate(0UL, (sum, t) => sum + t.TotalBytes),
            ByType: byType,
            TopHttpInstances: topHttpInstances ?? [],
            HandlerModules: handlerModules ?? []);
    }

    private static TimerLeakDomainResult TimerResult(
        int total,
        int threading = 0,
        int timers = 0,
        int queue = 0,
        int holder = 0,
        int periodic = 0,
        int other = 0)
    {
        var byType = new List<TimerObjectTypeSummary>();
        if (threading > 0) byType.Add(new("System.Threading.Timer", threading, (ulong)(threading * 120)));
        if (timers > 0) byType.Add(new("System.Timers.Timer", timers, (ulong)(timers * 120)));
        if (queue > 0) byType.Add(new("System.Threading.TimerQueueTimer", queue, (ulong)(queue * 96)));
        if (holder > 0) byType.Add(new("System.Threading.TimerHolder", holder, (ulong)(holder * 96)));
        if (periodic > 0) byType.Add(new("System.Threading.PeriodicTimer", periodic, (ulong)(periodic * 88)));

        return new TimerLeakDomainResult(
            TimersFound: total > 0,
            TotalTimers: total,
            LogicalTimerCount: queue,
            ThreadingTimerCount: threading,
            TimersTimerCount: timers,
            TimerQueueTimerCount: queue,
            TimerHolderCount: holder,
            PeriodicTimerCount: periodic,
            OtherTimerCount: other,
            TotalBytes: byType.Aggregate(0UL, (sum, t) => sum + t.TotalBytes),
            ByType: byType,
            IntervalHistogram: []);
    }
}
