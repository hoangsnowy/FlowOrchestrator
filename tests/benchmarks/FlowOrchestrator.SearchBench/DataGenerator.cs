using System.Text;

namespace FlowOrchestrator.SearchBench;

/// <summary>One generated <c>FlowSteps</c> row.</summary>
public sealed record StepRow(
    string StepKey,
    string StepType,
    string Status,
    string? OutputJson,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>One generated <c>FlowRuns</c> row plus its current step rows.</summary>
public sealed class RunRow
{
    public required int Index { get; init; }
    public required Guid Id { get; init; }
    public required Guid FlowId { get; init; }
    public required string FlowName { get; init; }
    public required string Status { get; init; }
    public required string TriggerKey { get; init; }
    public required string TriggerDataJson { get; init; }
    public required string BackgroundJobId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required List<StepRow> Steps { get; init; }
}

/// <summary>Search terms with known selectivity, recorded while the data set is generated.</summary>
public sealed class Needles
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public string this[string name] => Values[name];
}

/// <summary>
/// Deterministic production-shape generator: the same seed yields byte-identical runs, so the SQL
/// Server and PostgreSQL data sets are the same rows.
/// </summary>
/// <remarks>
/// Shape: 20 flows with Zipf-like volume, 8-12 steps each, step output JSON in size classes
/// (none / 0.4-1.2K / 1.2-4K / 4-16K / 16-48K chars), business identifiers (order, invoice, shipment,
/// payment, customer, claim) embedded in trigger bodies, step outputs and error messages, ~3% of step
/// rows carrying an error message, runs spread uniformly over 90 days ending at <see cref="Anchor"/>.
/// </remarks>
public sealed class DataGenerator
{
    /// <summary>Fixed "now" for the data set and every windowed query against it.</summary>
    public static readonly DateTimeOffset Anchor = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    public static readonly TimeSpan Span = TimeSpan.FromDays(90);

    private static readonly string[] Words =
    [
        "standard", "express", "pallet", "carton", "fragile", "insured", "return", "warehouse", "priority",
        "economy", "bundle", "replacement", "refurbished", "seasonal", "promo", "wholesale", "retail",
        "backorder", "dropship", "consignment", "sample", "voucher", "subscription", "renewal", "premium",
        "basic", "deluxe", "compact", "outdoor", "indoor", "steel", "cotton", "ceramic", "walnut", "oak",
        "graphite", "titanium", "linen", "bamboo", "copper", "matte", "glossy", "waterproof", "wireless",
    ];

    private static readonly string[] FirstNames = ["anna", "ben", "chloe", "daan", "emma", "finn", "greta", "hugo", "iris", "jonas", "lena", "milan", "nora", "oscar", "pia", "quinn", "ruben", "sara", "tom", "vera"];
    private static readonly string[] LastNames = ["jansen", "devries", "bakker", "visser", "smit", "meyer", "schmidt", "muller", "dubois", "martin", "rossi", "garcia", "novak", "kowalski", "nielsen", "berg", "lind", "costa", "silva", "murphy"];
    private static readonly string[] Domains = ["example.com", "mail.example.net", "shop.example.org", "corp.example.io"];
    private static readonly string[] Currencies = ["EUR", "EUR", "EUR", "USD", "GBP", "SEK", "PLN"];
    private static readonly string[] Countries = ["NL", "DE", "BE", "FR", "GB", "SE", "PL", "ES", "IT", "US"];
    private static readonly string[] Warehouses = ["AMS-2", "RTM-1", "DUS-4", "LYS-1", "MAD-3"];

    private static readonly (string Name, double Weight, string[] Triggers)[] FlowSpecs =
    [
        ("OrderFulfilmentFlow", 18, ["order-created", "order-created", "order-created", "manual"]),
        ("InvoiceSyncFlow", 14, ["invoice-sync-15m"]),
        ("ClaimIntakeFlow", 10, ["claim-submitted", "claim-submitted", "manual"]),
        ("PolicyRenewalFlow", 8, ["renewal-nightly"]),
        ("CustomerOnboardingFlow", 7, ["customer-signup", "manual"]),
        ("PaymentReconciliationFlow", 6, ["reconcile-hourly"]),
        ("ShipmentTrackingFlow", 6, ["tracking-poll-5m"]),
        ("RefundProcessingFlow", 5, ["refund-requested", "manual"]),
        ("InventorySnapshotFlow", 4, ["inventory-snapshot-30m"]),
        ("PriceListImportFlow", 3, ["pricelist-upload"]),
        ("SupplierOrderFlow", 3, ["supplier-po", "manual"]),
        ("LeadScoringFlow", 3, ["crm-lead-created"]),
        ("DocumentArchivalFlow", 2, ["archive-daily"]),
        ("KycVerificationFlow", 2, ["kyc-requested"]),
        ("LoyaltyPointsFlow", 2, ["loyalty-accrual"]),
        ("ReportGenerationFlow", 2, ["report-weekly", "manual"]),
        ("ChurnPredictionFlow", 1.5, ["churn-model-daily"]),
        ("DataExportFlow", 1, ["export-requested"]),
        ("PartnerBackfillFlow", 0.4, ["partner-backfill"]),
        ("GdprErasureFlow", 0.1, ["gdpr-request"]),
    ];

    private static readonly string[] Verbs = ["validate", "fetch", "map", "enrich", "reserve", "charge", "create", "push", "reconcile", "notify", "archive", "score", "book", "print", "update", "emit", "transform", "load", "check", "publish"];
    private static readonly string[] Nouns = ["Order", "Invoice", "Payment", "Shipment", "Label", "Customer", "Policy", "Claim", "Documents", "Ledger", "Inventory", "Prices", "Report", "Erp", "Crm", "Metrics", "Batch", "Snapshot", "Partner", "Audit"];

    // Size classes in UTF-16 chars: none, small, medium, large, extra-large.
    private static readonly (int Min, int Max)[] SizeClasses = [(0, 0), (400, 1_200), (1_200, 4_000), (4_000, 12_000), (20_000, 64_000)];

    private readonly FlowDef[] _flows;
    private readonly double _weightTotal;

    private sealed record StepDef(string Key, string Type, int SizeClass);

    private sealed record FlowDef(Guid Id, string Name, double Weight, string[] Triggers, StepDef[] Steps);

    public DataGenerator(int runCount, int seed = 20261123)
    {
        RunCount = runCount;
        Seed = seed;
        var rnd = new Random(seed);
        _flows = new FlowDef[FlowSpecs.Length];
        for (var f = 0; f < FlowSpecs.Length; f++)
        {
            var spec = FlowSpecs[f];
            var stepCount = rnd.Next(8, 13);
            var steps = new StepDef[stepCount];
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var s = 0; s < stepCount; s++)
            {
                string key;
                do key = Verbs[rnd.Next(Verbs.Length)] + Nouns[rnd.Next(Nouns.Length)];
                while (!used.Add(key));
                var roll = rnd.NextDouble();
                var sizeClass = roll < 0.10 ? 0 : roll < 0.60 ? 1 : roll < 0.88 ? 2 : roll < 0.98 ? 3 : 4;
                steps[s] = new StepDef(key, char.ToUpperInvariant(key[0]) + key[1..] + "Handler", sizeClass);
            }
            // "Some larger": one export-style step of 20-64K chars on the three batch/report flows.
            if (spec.Name is "PriceListImportFlow" or "ReportGenerationFlow" or "DataExportFlow")
                steps[1] = steps[1] with { SizeClass = 4 };
            for (var s = 0; s < stepCount; s++)
                if (steps[s].SizeClass == 4 && s != 1) steps[s] = steps[s] with { SizeClass = 3 };
            _flows[f] = new FlowDef(NewGuid(rnd), spec.Name, spec.Weight, spec.Triggers, steps);
            _weightTotal += spec.Weight;
        }
    }

    public int RunCount { get; }

    public int Seed { get; }

    public Needles Needles { get; } = new();

    /// <summary>Yields every run oldest-first. Needles are populated as a side effect.</summary>
    public IEnumerable<RunRow> Generate()
    {
        var rnd = new Random(Seed + 1);
        var sb = new StringBuilder(64 * 1024);
        var stepTicks = Span.Ticks / RunCount;
        var start = Anchor - Span;
        var recentIndex = RunCount - RunCount / 130;          // ~0.7 day old
        var windowIndex = RunCount - RunCount * 3 / 90;       // ~3 days old
        var oldIndex = RunCount / 3;                          // ~60 days old
        bool recentInvoice = false, oldInvoice = false, recentErr = false, oldErr = false, recentOrder = false;

        for (var i = 0; i < RunCount; i++)
        {
            var flow = PickFlow(rnd);
            var startedAt = start + TimeSpan.FromTicks(stepTicks * i + (long)(rnd.NextDouble() * stepTicks));
            var id = NewGuid(rnd);
            var trigger = flow.Triggers[rnd.Next(flow.Triggers.Length)];
            var currency = Currencies[rnd.Next(Currencies.Length)];
            var country = Countries[rnd.Next(Countries.Length)];
            var orderId = $"ORD-{startedAt.Year}-{i:D7}";
            var invoice = $"INV-{(long)i * 7 + 13:D9}";
            var shipment = $"SHP{(long)i * 3 + 5:D10}";
            var payment = "pi_" + RandomAlnum(rnd, 24);
            var customer = $"CUST-{rnd.Next(1, 60_000):D6}";
            var email = $"{FirstNames[rnd.Next(FirstNames.Length)]}.{LastNames[rnd.Next(LastNames.Length)]}{rnd.Next(1, 999)}@{Domains[rnd.Next(Domains.Length)]}";
            var correlation = NewGuid(rnd);
            var claim = $"CLM-{i:D8}";

            var trig = new StringBuilder(512);
            trig.Append("{\"orderId\":\"").Append(orderId).Append("\",\"customerId\":\"").Append(customer)
                .Append("\",\"email\":\"").Append(email).Append("\",\"correlationId\":\"").Append(correlation)
                .Append("\",\"source\":\"").Append(trigger).Append("\",\"amount\":").Append(rnd.Next(5, 5_000)).Append('.').Append(rnd.Next(10, 99))
                .Append(",\"currency\":\"").Append(currency).Append("\",\"country\":\"").Append(country)
                .Append("\",\"items\":").Append(rnd.Next(1, 12)).Append('}');

            var roll = rnd.NextDouble();
            var running = i >= RunCount - 30;
            var status = running ? "Running"
                : roll < 0.86 ? "Succeeded"
                : roll < 0.91 ? "Failed"
                : roll < 0.97 ? "Skipped"
                : roll < 0.985 ? "Cancelled"
                : "TimedOut";
            var failAt = status is "Failed" or "Cancelled" or "TimedOut" or "Running" ? rnd.Next(flow.Steps.Length) : -1;
            var errorTemplate = rnd.Next(6);

            var steps = new List<StepRow>(flow.Steps.Length);
            var t = startedAt;
            for (var s = 0; s < flow.Steps.Length; s++)
            {
                var def = flow.Steps[s];
                var dur = TimeSpan.FromMilliseconds(rnd.Next(20, 4_000));
                string stepStatus;
                string? output = null, error = null;
                DateTimeOffset? completed = t + dur;
                if (status == "Skipped")
                {
                    stepStatus = "Skipped";
                }
                else if (failAt >= 0 && s == failAt)
                {
                    stepStatus = status switch { "Failed" => "Failed", "Running" => "Running", "Cancelled" => "Cancelled", _ => "TimedOut" };
                    if (stepStatus == "Running") completed = null;
                    error = status switch
                    {
                        "Failed" => BuildError(rnd, errorTemplate, def.Key, orderId, shipment, payment, invoice, country, sb),
                        "Cancelled" => "Run cancelled by operator: duplicate order detected.",
                        "TimedOut" => "Run timed out after 00:30:00 while step '" + def.Key + "' was in progress.",
                        _ => null,
                    };
                }
                else if (failAt >= 0 && s > failAt)
                {
                    stepStatus = "Skipped";
                    if (status != "Running")
                        error = "Skipped because upstream step '" + flow.Steps[failAt].Key + "' did not succeed.";
                    else
                        break; // not started yet
                }
                else
                {
                    stepStatus = "Succeeded";
                    if (def.SizeClass > 0)
                    {
                        var (min, max) = SizeClasses[def.SizeClass];
                        output = BuildOutput(rnd, sb, rnd.Next(min, max), def.Key, orderId, invoice, shipment, payment, customer, correlation, claim, currency, country);
                    }
                }

                steps.Add(new StepRow(def.Key, def.Type, stepStatus, output, error, t, completed));
                t += dur;
            }

            var run = new RunRow
            {
                Index = i,
                Id = id,
                FlowId = flow.Id,
                FlowName = flow.Name,
                Status = status,
                TriggerKey = trigger,
                TriggerDataJson = trig.ToString(),
                BackgroundJobId = (5_000_000L + (long)i * 3 + rnd.Next(3)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                StartedAt = startedAt,
                CompletedAt = running ? null : t,
                Steps = steps,
            };

            // Needles: recorded at known ages so window / no-window behaviour is deterministic.
            if (i == recentIndex) { Needles.Values["runPrefixRecent"] = id.ToString("D")[..8]; Needles.Values["runIdRecent"] = id.ToString("D"); }
            if (i == oldIndex) { Needles.Values["runPrefixOld"] = id.ToString("D")[..8]; Needles.Values["runIdOld"] = id.ToString("D"); }
            if (!recentOrder && i >= windowIndex) { Needles.Values["orderIdRecent"] = orderId; recentOrder = true; }
            if (!recentInvoice && i >= windowIndex && run.Status == "Succeeded" && steps.Any(x => x.OutputJson?.Contains(invoice, StringComparison.Ordinal) == true))
            { Needles.Values["invoiceRecent"] = invoice; recentInvoice = true; }
            if (!oldInvoice && i >= oldIndex && run.Status == "Succeeded" && steps.Any(x => x.OutputJson?.Contains(invoice, StringComparison.Ordinal) == true))
            { Needles.Values["invoiceOld"] = invoice; oldInvoice = true; }
            if (!recentErr && i >= windowIndex && status == "Failed" && errorTemplate == 0)
            { Needles.Values["errorShipmentRecent"] = shipment; recentErr = true; }
            if (!oldErr && i >= oldIndex && status == "Failed" && errorTemplate == 0)
            { Needles.Values["errorShipmentOld"] = shipment; oldErr = true; }

            yield return run;
        }
    }

    private FlowDef PickFlow(Random rnd)
    {
        var x = rnd.NextDouble() * _weightTotal;
        foreach (var f in _flows)
        {
            x -= f.Weight;
            if (x <= 0) return f;
        }
        return _flows[^1];
    }

    private static string BuildError(Random rnd, int template, string stepKey, string orderId, string shipment, string payment, string invoice, string country, StringBuilder sb)
    {
        switch (template)
        {
            case 0:
                return $"HTTP 503 (Service Unavailable) calling POST https://api.carrier.example/v2/shipments for {shipment}; retries exhausted (5/5).";
            case 1:
                return $"Timeout after 00:00:30 waiting for payment provider response (paymentRef {payment}, order {orderId}).";
            case 2:
                return $"Validation failed: postcode '{rnd.Next(1000, 9999)} {(char)('A' + rnd.Next(26))}{(char)('A' + rnd.Next(26))}' is not valid for country '{country}' (order {orderId}).";
            case 3:
                return $"Transaction (Process ID {rnd.Next(50, 300)}) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.";
            case 4:
                return $"Upstream returned 409 Conflict: invoice {invoice} already exists in ERP.";
            default:
                sb.Clear();
                sb.Append("System.NullReferenceException: Object reference not set to an instance of an object.\n");
                var frames = rnd.Next(12, 30);
                for (var f = 0; f < frames; f++)
                    sb.Append("   at Acme.Integration.").Append(Nouns[rnd.Next(Nouns.Length)]).Append('.').Append(Nouns[rnd.Next(Nouns.Length)])
                      .Append("Mapper.").Append(Verbs[rnd.Next(Verbs.Length)]).Append("(JsonElement source, MappingContext context) in /src/Mapping/")
                      .Append(stepKey).Append(".cs:line ").Append(rnd.Next(10, 900)).Append('\n');
                return sb.ToString();
        }
    }

    private static string BuildOutput(Random rnd, StringBuilder sb, int targetChars, string stepKey, string orderId, string invoice, string shipment,
        string payment, string customer, Guid correlation, string claim, string currency, string country)
    {
        sb.Clear();
        sb.Append("{\"step\":\"").Append(stepKey).Append("\",\"orderId\":\"").Append(orderId)
          .Append("\",\"correlationId\":\"").Append(correlation).Append("\",\"customerId\":\"").Append(customer).Append('"');
        if (stepKey.Contains("Invoice", StringComparison.Ordinal) || stepKey.Contains("Ledger", StringComparison.Ordinal) || stepKey.Contains("Erp", StringComparison.Ordinal))
            sb.Append(",\"invoiceNumber\":\"").Append(invoice).Append("\",\"invoiceStatus\":\"posted\"");
        if (stepKey.Contains("Shipment", StringComparison.Ordinal) || stepKey.Contains("Label", StringComparison.Ordinal))
            sb.Append(",\"shipmentId\":\"").Append(shipment).Append("\",\"trackingNumber\":\"1Z").Append(RandomAlnum(rnd, 16)).Append('"');
        if (stepKey.Contains("Payment", StringComparison.Ordinal))
            sb.Append(",\"paymentRef\":\"").Append(payment).Append("\",\"paymentStatus\":\"captured\"");
        if (stepKey.Contains("Claim", StringComparison.Ordinal) || stepKey.Contains("Policy", StringComparison.Ordinal))
            sb.Append(",\"claimNumber\":\"").Append(claim).Append("\",\"policyNumber\":\"POL-").Append(rnd.Next(100_000, 999_999)).Append('"');
        sb.Append(",\"country\":\"").Append(country).Append("\",\"httpStatus\":200,\"lines\":[");
        var n = 0;
        while (sb.Length < targetChars)
        {
            if (n > 0) sb.Append(',');
            sb.Append("{\"line\":").Append(++n).Append(",\"sku\":\"SKU-").Append(rnd.Next(10_000, 99_999))
              .Append("\",\"description\":\"");
            var words = rnd.Next(3, 9);
            for (var w = 0; w < words; w++)
            {
                if (w > 0) sb.Append(' ');
                sb.Append(Words[rnd.Next(Words.Length)]);
            }
            sb.Append("\",\"qty\":").Append(rnd.Next(1, 40)).Append(",\"unitPrice\":").Append(rnd.Next(1, 900)).Append('.').Append(rnd.Next(10, 99))
              .Append(",\"currency\":\"").Append(currency).Append("\",\"warehouse\":\"").Append(Warehouses[rnd.Next(Warehouses.Length)]).Append("\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string RandomAlnum(Random rnd, int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        return string.Create(length, rnd, static (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[r.Next(alphabet.Length)];
        });
    }

    private static Guid NewGuid(Random rnd)
    {
        Span<byte> bytes = stackalloc byte[16];
        rnd.NextBytes(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);   // version 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(bytes);
    }
}
