namespace NetPI.Abstractions;

// astra-2 11.2 (package F): shared cloud budget + reservations. A team's paid
// allowance is ONE shared number; every direct-cloud segment (descendants and
// retries included) draws from it through ATOMIC reservations so parallel
// requests can never double-spend the same remaining allowance (16: "Parallel
// cloud requests near budget limit -> Shared reservations prevent
// double-spending the same remaining allowance").
//
// Money is measured in a single currency unit (configurable per allowance);
// usage reported by the provider is reconciled against the reservation
// afterward. Unknown prices are NOT invented: a deployment with no configured
// cost-per-1k is governed by the token-based policy path instead (the same
// store, counted in tokens), never by a false currency hard cap.

/// <summary>What one cloud budget allowance is denominated in (11.2).</summary>
public enum CloudBudgetUnit
{
    /// <summary>Currency units (e.g. USD). The provider must report currency usage.</summary>
    Currency = 0,

    /// <summary>
    /// Raw token count (the token-based policy path for deployments whose
    /// price is unknown: bound the output, count tokens, no invented money).
    /// </summary>
    Tokens = 1,
}

/// <summary>
/// One team's cloud allowance (the unit of shared spend).
/// </summary>
public sealed record CloudBudgetAllowance(
    string TeamId,
    string Currency,
    double Limit,
    /// <summary>Denomination of <see cref="Limit"/> (11.2 token-based policy path).</summary>
    CloudBudgetUnit Unit = CloudBudgetUnit.Currency)
{
    public static CloudBudgetAllowance For(string teamId, string currency, double limit)
        => new(teamId, currency, limit, CloudBudgetUnit.Currency);
}

/// <summary>
/// The outcome of an atomic reservation attempt (11.2: "Reserve estimated
/// maximum request cost transactionally before sending each paid request").
/// </summary>
public sealed record CloudReservationResult(
    /// <summary>True when the allowance could cover the estimate and was drawn.</summary>
    bool Admitted,
    /// <summary>The durable reservation id (set only when Admitted).</summary>
    string? ReservationId,
    /// <summary>The allowance's remaining amount AFTER the reservation (best-known).</summary>
    double Remaining,
    /// <summary>Actionable reason when <see cref="Admitted"/> is false (never a silent drop).</summary>
    string? Reason = null)
{
    public static CloudReservationResult Ok(string id, double remaining) => new(true, id, remaining);
    public static CloudReservationResult Denied(string reason, double remaining) => new(false, null, remaining, reason);

    /// <summary>
    /// No accounting was applied (no gate / no budget configured): the request
    /// proceeds without a reservation, so the caller must NOT conclude it.
    /// </summary>
    public static CloudReservationResult None { get; } = new(false, null, 0);
}


/// <summary>
/// Shared team budget + atomic reservation authority (astra-2 11.2, service
/// "cloud-budgets" registered by the storage plugin). All descendants and
/// retries of a team share the same allowance; a reservation is the ONLY way
/// spend is committed, and it is drawn atomically against the remainder so
/// exactly one of N parallel equal requests can win when only one fits.
///
/// Concurrency is a contract: implementations must make ReserveAsync an
/// atomic check-and-draw (single transaction / row lock) -- an in-memory
/// lock-free "check then decrement" is the double-spend bug this exists to
/// prevent.
/// </summary>
public interface ICloudBudgetStore
{
    /// <summary>
    /// Ensure <paramref name="allowance"/> exists (idempotent upsert: a
    /// repeat with the same TeamId keeps the FIRST limit unless the new one is
    /// larger -- an agent can never RAISE a limit, only a user configures
    /// allowances, and a smaller re-upsert is a no-op so a stale writer
    /// cannot shrink the team's allowance mid-spend).
    /// </summary>
    ValueTask UpsertAllowanceAsync(CloudBudgetAllowance allowance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserve <paramref name="estimate"/> (the estimated MAXIMUM
    /// cost of one request -- bounded output tokens make it calculable) from
    /// the team's remaining allowance.
    ///
    /// Concurrent callers with the same team and the same remaining allowance:
    /// the draw is atomic, so when the remainder covers exactly one estimate,
    /// EXACTLY ONE caller is admitted. Rejected callers get an actionable
    /// reason (the 16 "exhausted budget" checkpoint/block -- never a hidden
    /// fallback to local inference).
    /// </summary>
    ValueTask<CloudReservationResult> ReserveAsync(
        string teamId, double estimate, double convertRate, string? runId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Durable facts about one reservation (which team, in which denomination,
    /// at what rate) — the gate uses this at conclude time to convert reported
    /// usage (tokens) into the team's denomination. The store is the durable
    /// home of reservation state: an in-memory policy snapshot alone would
    /// lose the conversion after a host reload.
    /// </summary>
    ValueTask<CloudReservationInfo?> ReserveInfoAsync(string reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Release a reservation without spending it (the request never happened:
    /// a policy rejection, a crash before the call). Re-funds the allowance:
    /// the estimate drawn at reservation time is returned to the remainder.
    /// Idempotent (a second release is a no-op).
    /// </summary>
    ValueTask ReleaseAsync(string reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconcile reported usage against the reservation (11.2: "reconcile
    /// reported usage afterward"). The estimate drawn at reservation time is
    /// replaced by the true <paramref name="actual"/>: excess is drawn from
    /// the remainder, shortfall is re-funded, so <c>spent</c> ends up exactly
    /// at the true usage. Idempotent on reservationId (a second settle is a
    /// no-op).
    /// </summary>
    ValueTask<bool> SettleAsync(string reservationId, double actual, CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot for the usage UI (11.2: "User-facing settings show enabled
    /// cloud models and per-team usage").
    /// </summary>
    ValueTask<IReadOnlyList<CloudBudgetUsage>> UsageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable facts about one reservation (11.2): which team drew it, in which
/// denomination, and the rate that turns reported usage (raw tokens) into
/// that team's denomination. Persisted on the reservation row itself so a
/// host reload cannot lose the conversion.
/// </summary>
public sealed record CloudReservationInfo(
    string TeamId,
    string Currency,
    CloudBudgetUnit Unit,
    double ConvertRate)
{
    /// <summary>Convert reported tokens into this reservation's denomination.</summary>
    public double ToDenominated(double tokens) => tokens * ConvertRate;
}

/// <summary>Per-team usage snapshot (for the usage UI).</summary>
public sealed record CloudBudgetUsage(
    string TeamId,
    string Currency,
    CloudBudgetUnit Unit,
    double Limit,
    double Reserved,
    double Spent,
    double Remaining)
{
    /// <summary>Remaining = Limit - Spent (reserved but unsettled spend is included in Spent's draw).</summary>
}

/// <summary>
/// The gate a cloud-capable provider consults BEFORE sending a paid request
/// (11.2: "Cloud eligibility requires ... remaining budget"; 16: "Cloud
/// disabled or team disallows cloud -> No paid provider call"). Providers
/// resolve this lazily under service id "cloud-gate": null means no cloud
/// accounting is configured and direct execution is allowed (the legacy
/// behavior). The gate itself resolves the team's policy (default-deny when
/// <c>cloudAllowedByDefault=false</c>) and the shared reservation store, so a
/// provider never keys cloud eligibility on its own opinion.
/// </summary>
public interface ICloudExecutionGate
{
    /// <summary>
    /// Check + reserve for ONE paid request. The gate resolves the team
    /// policy (allowances, denomination, default output bound) from the
    /// deployment, then draws the estimate atomically from the shared
    /// allowance. <paramref name="maxOutputTokens"/> is the request's output
    /// bound (the calculable reservation ceiling, §11.2); null means the
    /// policy's default bound.
    /// </summary>
    ValueTask<CloudReservationResult> AuthorizeAsync(
        string? modelId,
        string? deploymentId,
        int? maxOutputTokens,
        string? runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconcile a prior reservation after the model finishes: settle it with
    /// the reported usage (§11.2 "reconcile reported usage afterward"), or
    /// release it untouched when the request never happened (failed before
    /// content / cancelled). No-op when the reservation was already
    /// settled or released (idempotent conclusion).
    /// </summary>
    ValueTask ConcludeAsync(string reservationId, bool succeeded, double? actual,
        CancellationToken cancellationToken = default);
}
