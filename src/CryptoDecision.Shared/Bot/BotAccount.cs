namespace CryptoDecision.Shared.Bot;

/// <summary>
/// Which trading account THIS process is responsible for.
///
/// Execution is one process per account. The whole bot -- its circuit breakers, its
/// open positions, its daily entry cap, its credentials -- was written around a single
/// account and is correct exactly as it stands, provided it only ever sees one. So
/// rather than teaching that code to interleave several, each account gets its own
/// container and this value tells it which one it is.
///
/// It is deliberately a type rather than a loose int. An int threaded through
/// constructors is the kind of argument that gets passed in the wrong position once
/// and then trades the wrong person's money; a distinct type cannot be confused with
/// the trade ids, strategy counts and notional figures it travels beside.
///
/// Parsing lives in the host (Program.cs) rather than here, so this project keeps its
/// single dependency on Npgsql.
/// </summary>
/// <param name="Id">
/// Matches <c>bot_config.id</c> and <c>client_link.id</c>. 1 is the operator, which is
/// also the default when nothing is configured -- so a deployment that knows nothing
/// about accounts keeps behaving exactly as it did before any of this existed.
/// </param>
public sealed record BotAccount(int Id)
{
    public const int OperatorId = 1;

    /// <summary>The operator's own account: the default when nothing is set.</summary>
    public static readonly BotAccount Operator = new(OperatorId);

    public bool IsOperator => Id == OperatorId;

    public override string ToString() =>
        IsOperator ? "account 1 (operator)" : $"account {Id}";
}
