using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class TriggerOrderScannerTests
{
    private static CatalogTriggerEvent Event(
        string trigger, string table, string eventType, bool isFirst = false, bool isLast = false,
        bool isInsteadOf = false, bool isDisabled = false) =>
        new($"dbo.{trigger}", table, eventType, isInsteadOf, isDisabled, isFirst, isLast, $"dbo.{trigger}", 0);

    [Fact]
    public void TwoTriggersSameEvent_NeitherPinned_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddTriggerEvent(Event("trg1", "dbo.T", "INSERT"));
        catalog.AddTriggerEvent(Event("trg2", "dbo.T", "INSERT"));

        var findings = TriggerOrderScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T", finding.TableQualifiedName);
        Assert.Equal("INSERT", finding.EventTypeDescription);
        Assert.Equal(["dbo.trg1", "dbo.trg2"], finding.UnorderedTriggerNames);
    }

    [Fact]
    public void ThreeTriggers_FirstAndLastPinned_MiddleSingleton_NeverFires()
    {

        var catalog = new DatabaseCatalog();
        catalog.AddTriggerEvent(Event("trg1", "dbo.T", "INSERT", isFirst: true));
        catalog.AddTriggerEvent(Event("trg2", "dbo.T", "INSERT"));
        catalog.AddTriggerEvent(Event("trg3", "dbo.T", "INSERT", isLast: true));

        var findings = TriggerOrderScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void FourTriggers_FirstAndLastPinned_TwoUnorderedInMiddle_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddTriggerEvent(Event("trg1", "dbo.T", "INSERT", isFirst: true));
        catalog.AddTriggerEvent(Event("trg2", "dbo.T", "INSERT"));
        catalog.AddTriggerEvent(Event("trg3", "dbo.T", "INSERT"));
        catalog.AddTriggerEvent(Event("trg4", "dbo.T", "INSERT", isLast: true));

        var findings = TriggerOrderScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(["dbo.trg2", "dbo.trg3"], finding.UnorderedTriggerNames);
    }

    [Fact]
    public void TwoTriggers_BothEndsPinned_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddTriggerEvent(Event("trg1", "dbo.T", "INSERT", isFirst: true));
        catalog.AddTriggerEvent(Event("trg2", "dbo.T", "INSERT", isLast: true));

        var findings = TriggerOrderScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void SingleTriggerOnEvent_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddTriggerEvent(Event("trg1", "dbo.T", "INSERT"));

        var findings = TriggerOrderScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void InsteadOfTriggers_ExcludedFromOrderingClaim()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddTriggerEvent(Event("trg1", "dbo.T", "INSERT", isInsteadOf: true));
        catalog.AddTriggerEvent(Event("trg2", "dbo.T", "INSERT", isInsteadOf: true));

        var findings = TriggerOrderScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void DisabledTrigger_ExcludedFromCountAndMiddleSet()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddTriggerEvent(Event("trg1", "dbo.T", "INSERT"));
        catalog.AddTriggerEvent(Event("trg2", "dbo.T", "INSERT"));
        catalog.AddTriggerEvent(Event("trg3", "dbo.T", "INSERT", isDisabled: true));

        var findings = TriggerOrderScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(["dbo.trg1", "dbo.trg2"], finding.UnorderedTriggerNames);
    }

    [Fact]
    public void DifferentEvents_AnalyzedIndependently()
    {

        var catalog = new DatabaseCatalog();
        catalog.AddTriggerEvent(Event("trg1", "dbo.T", "INSERT"));
        catalog.AddTriggerEvent(Event("trg2", "dbo.T", "INSERT"));
        catalog.AddTriggerEvent(Event("trg3", "dbo.T", "UPDATE"));

        var findings = TriggerOrderScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("INSERT", finding.EventTypeDescription);
    }
}
