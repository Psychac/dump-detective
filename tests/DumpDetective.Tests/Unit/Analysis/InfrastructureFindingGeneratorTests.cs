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
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, [], [])).Should().BeFalse();
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

    // ─────────────────────────────────────────────────────────────────────────
    // HttpObjectFindingGenerator
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HttpObject_NoFindings_WhenNotFound()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, [], []);
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
    public void HttpObject_BelowThresholds_NoFindings()
    {
        var gen = new HttpObjectFindingGenerator();
        var result = HttpResult(httpClients: 4, webRequests: 9, webResponses: 5, servicePoints: 10);
        gen.Generate(result).Should().BeEmpty();
    }

    [Fact]
    public void HttpObject_CanGenerate_OnlyForHttpObjectDomainResult()
    {
        var gen = new HttpObjectFindingGenerator();
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, [], [])).Should().BeTrue();
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
        gen.CanGenerate(new HttpObjectDomainResult(false, 0, 0, 0, 0, 0, 0, 0, [], [])).Should().BeFalse();
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
        int handlers = 0, int servicePoints = 0)
    {
        int total = httpClients + webRequests + webResponses + handlers + servicePoints;
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
            TotalBytes: byType.Aggregate(0UL, (sum, t) => sum + t.TotalBytes),
            ByType: byType,
            TopHttpClients: []);
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
