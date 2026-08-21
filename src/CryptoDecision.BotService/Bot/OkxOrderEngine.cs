using CryptoDecision.BotService.Exchanges;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// Places real spot orders on OKX.
///
/// The difference from <see cref="PaperOrderEngine"/> is not that this one talks
/// to a network — it is that nothing here may be assumed. The paper engine writes
/// the price it was given and the quantity it computed; this engine writes only
/// what OKX reports back after the fill, because a market order's price, its
/// filled size and its fee are all unknown at the moment it is sent. A trade row
/// built from the request rather than the fill is a row that does not match the
/// account it claims to describe.
///
/// Three ordering rules follow from that, and all three matter more than they look:
///
///  - Every check that can refuse an order happens <em>before</em> placement.
///    Once funds have moved there is no unwinding, so a failure after the fill is
///    handled by recording loudly, never by throwing away the record.
///  - The quantity persisted on the trade is the amount that can actually be sold
///    later — the fill minus the fee OKX took out of it, floored onto the lot
///    grid. Persisting the requested size instead produces an exit order for
///    coins that were never received, which fails at the worst possible moment:
///    when a stop loss is trying to fire.
///  - An entry arms an OCO at the exchange before this method returns. The bot's
///    own loop still runs trailing stops, breakeven and timeouts, but the hard
///    floor has to exist somewhere that does not depend on this process being
///    alive. Everything about <see cref="ReconcileAsync"/> follows from that: once
///    the exchange can close a position unasked, something must notice it did.
/// </summary>
public sealed class OkxOrderEngine(
    OkxTradingClient   trading,
    OkxInstrumentCache instruments,
    OkxOptions         okxOptions,
    BotRepository      repo,
    IFeatureRepository featureRepo,
    BotStateService    state,
    ILogger<OkxOrderEngine> log) : IOrderEngine
{
    public const string ExchangeName = "OKX";

    /// <summary>
    /// Headroom left for the entry fee when checking that the position will still
    /// be large enough to sell. Double the standard 0.1% taker fee, so a fee tier
    /// change cannot turn a valid entry into an unsellable one.
    /// </summary>
    private const decimal FeeHeadroom = 0.998m;

    public string? DescribeRefusal(BotOptions opts) => okxOptions.DescribeRefusal();

    /// <summary>
    /// Spot cash accounts cannot short. Reported here rather than enforced only at
    /// order time so the strategy layer can stop generating orders that physically
    /// cannot be filled.
    /// </summary>
    public bool SupportsShort(BotOptions opts) => false;

    // ── Entry ─────────────────────────────────────────────────────────────────

    public async Task<BotTrade> OpenPositionAsync(
        string symbol, string strategy, string side, decimal price, decimal capitalUsd,
        decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false)
    {
        var refusal = okxOptions.DescribeRefusal();
        if (refusal is not null)
            throw new InvalidOperationException($"OKX live order refused: {refusal}");

        // Still enforced at the boundary even though the strategy layer now filters
        // shorts: a SHORT silently placed as a buy would be a position pointing the
        // opposite way to the signal that asked for it.
        if (side is not ("LONG" or "BUY"))
            throw new InvalidOperationException(
                $"Cannot open a {side} position on OKX spot (tdMode=cash) — short selling needs a " +
                "margin or futures account, which this engine does not implement.");

        var instrument = await instruments.GetSpotAsync(symbol, ct);

        // ── Size the order ──
        var feature = await featureRepo.GetTodayAsync(symbol, ct);
        var size    = PositionSizer.Resolve(
            capitalUsd, positionPct, (double)(feature?.Volatility ?? 2.0m), confidence, useAiSizing);

        var notional = size.NotionalUsd;

        if (notional > okxOptions.MaxOrderNotionalUsd)
        {
            log.LogWarning(
                "[OKX] Sizing asked for ${Asked} but the per-order ceiling is ${Cap} — capping. " +
                "Check capital_usd and position_pct if this was not intended.",
                notional, okxOptions.MaxOrderNotionalUsd);
            notional = okxOptions.MaxOrderNotionalUsd;
        }

        if (notional < okxOptions.MinOrderNotionalUsd)
            throw new InvalidOperationException(
                $"Order notional ${notional} is below the ${okxOptions.MinOrderNotionalUsd} minimum. " +
                "Raise capital_usd or position_pct, or the exchange will reject the order as dust.");

        var quantity = OkxSizing.FloorToStep(notional / price, instrument.LotSize);

        if (quantity < instrument.MinSize)
            throw new InvalidOperationException(
                $"${notional} at {price} is {quantity} {instrument.BaseCcy}, under OKX's " +
                $"{instrument.MinSize} minimum for {instrument.InstId}. Raise the position size.");

        // Would the position still be sellable after the fee is taken out of it?
        // Cheaper to answer now than to own an unsellable position later.
        var sellableAfterFee = OkxSizing.FloorToStep(quantity * FeeHeadroom, instrument.LotSize);
        if (sellableAfterFee < instrument.MinSize)
            throw new InvalidOperationException(
                $"{quantity} {instrument.BaseCcy} would fall to {sellableAfterFee} after the entry fee, " +
                $"below the {instrument.MinSize} minimum needed to sell it again. Raise the position size.");

        // ── Do the funds exist? ──
        var required  = quantity * price * 1.002m;
        var available = await trading.GetAvailableAsync(instrument.QuoteCcy, ct);
        if (available < required)
            throw new InvalidOperationException(
                $"OKX {instrument.QuoteCcy} available balance is {available:F2}, short of the " +
                $"{required:F2} this order needs. Fund the account or lower the position size.");

        // ── Cleared. Place it. ──
        var orderId = await trading.PlaceSpotMarketOrderAsync(instrument.InstId, "buy", quantity, ct);
        var fill    = await trading.WaitForFillAsync(instrument.InstId, orderId, ct);

        // ── Reconcile the fill into a trade record ──
        //
        // OKX charges the fee on a spot buy in the base currency: you pay
        // filled × avgPx in USDT and receive filled minus the fee in coins. When
        // it is charged in quote instead, the coins arrive whole and the USDT out
        // is higher. Both are handled because getting it backwards misstates both
        // the cost basis and the amount available to sell.
        var feeInBase = string.Equals(fill.FeeCcy, instrument.BaseCcy, StringComparison.OrdinalIgnoreCase);

        var cost    = fill.FilledBase * fill.AveragePrice;
        var netBase = fill.FilledBase;
        decimal feeUsd;

        if (feeInBase)
        {
            netBase -= fill.FeeAbs;
            feeUsd   = fill.FeeAbs * fill.AveragePrice;
        }
        else
        {
            feeUsd = fill.FeeAbs;
            cost  += fill.FeeAbs;
        }

        var sellable = OkxSizing.FloorToStep(netBase, instrument.LotSize);

        if (sellable < instrument.MinSize)
            log.LogCritical(
                "[OKX] Order {OrdId} filled {Filled} {Base} but only {Sellable} is sellable, under the " +
                "{Min} minimum. This position cannot be closed by the bot and needs manual handling.",
                orderId, fill.FilledBase, instrument.BaseCcy, sellable, instrument.MinSize);

        var trade = new BotTrade
        {
            Symbol       = symbol,
            Side         = "LONG",
            Strategy     = strategy,
            EntryPrice   = fill.AveragePrice,
            Quantity     = sellable,
            NotionalUsd  = Math.Round(cost, 4),
            Status       = "OPEN",
            OpenedAt     = DateTime.UtcNow,
            Mode         = "LIVE",
            Exchange     = ExchangeName,
            EntryOrderId = orderId,
            FeeUsd       = Math.Round(feeUsd, 8),
        };

        try
        {
            trade = trade with { Id = await repo.InsertTradeAsync(trade, ct) };
        }
        catch (Exception ex)
        {
            // The coins are already bought. Losing the row would leave an untracked
            // position with no stop loss, so everything needed to recreate it goes
            // into the log before the exception continues.
            log.LogCritical(ex,
                "[OKX] FILLED BUT NOT RECORDED — order {OrdId} on {InstId} bought {Qty} {Base} at " +
                "{Price} for {Cost} {Quote}, and the bot_trades insert failed. This position is " +
                "open on the exchange and unmanaged. Record it manually before restarting the bot.",
                orderId, instrument.InstId, sellable, instrument.BaseCcy,
                fill.AveragePrice, Math.Round(cost, 4), instrument.QuoteCcy);
            throw;
        }

        log.LogInformation(
            "[OKX] OPEN LONG {Symbol} id={Id} filled {Qty} {Base} @ ${Price} cost=${Cost} " +
            "fee=${Fee} (signal price ${Signal}, slippage {Slip:+0.000;-0.000}%)",
            symbol, trade.Id, sellable, instrument.BaseCcy, fill.AveragePrice,
            trade.NotionalUsd, trade.FeeUsd, price,
            price > 0m ? (fill.AveragePrice - price) / price * 100m : 0m);

        await ArmProtectiveExitAsync(trade, instrument, ct);

        return trade;
    }

    // ── Exchange-side protection ──────────────────────────────────────────────

    /// <summary>
    /// Place the OCO that guards a freshly opened position, and persist its id.
    ///
    /// Never throws. The position exists either way, and the bot's own loop is
    /// still watching it — so failing to arm the exchange-side guard degrades
    /// protection rather than removing it, and unwinding a real fill over a
    /// transient API error would be the worse trade. It is logged as critical
    /// because the operator needs to know this position is only as safe as the
    /// container it is being watched from.
    /// </summary>
    private async Task ArmProtectiveExitAsync(BotTrade trade, OkxInstrument instrument, CancellationToken ct)
    {
        var opts = state.Options;

        var takeProfit = OkxSizing.FloorToStep(
            trade.EntryPrice * (1m + opts.TakeProfitPct), instrument.TickSize);
        var stopLoss = OkxSizing.CeilToStep(
            trade.EntryPrice * (1m - opts.StopLossPct), instrument.TickSize);

        if (takeProfit <= trade.EntryPrice || stopLoss >= trade.EntryPrice || stopLoss <= 0m)
        {
            log.LogCritical(
                "[OKX] Trade {Id} has no exchange-side stop: TP {Tp} / SL {Sl} do not straddle the " +
                "{Entry} entry price (take_profit_pct={TpPct:P2}, stop_loss_pct={SlPct:P2}). " +
                "This position is protected only while the bot process is alive.",
                trade.Id, takeProfit, stopLoss, trade.EntryPrice, opts.TakeProfitPct, opts.StopLossPct);
            return;
        }

        try
        {
            var algoId = await trading.PlaceOcoExitAsync(
                instrument.InstId, trade.Quantity, takeProfit, stopLoss, ct);

            trade.ExitAlgoId = algoId;
            await repo.UpdateExitAlgoIdAsync(trade.Id, algoId, ct);

            log.LogInformation(
                "[OKX] Trade {Id} guarded by OCO {AlgoId}: TP ${Tp} (+{TpPct:P2}) / SL ${Sl} (-{SlPct:P2}). " +
                "This survives a bot restart.",
                trade.Id, algoId, takeProfit, opts.TakeProfitPct, stopLoss, opts.StopLossPct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogCritical(ex,
                "[OKX] Trade {Id} is OPEN with {Qty} {Base} but its protective OCO could not be " +
                "placed. The position is protected only by this process — if the bot stops, nothing " +
                "will stop it out. Place a stop manually or close the position.",
                trade.Id, trade.Quantity, instrument.BaseCcy);
        }
    }

    // ── Reconciliation ────────────────────────────────────────────────────────

    public async Task<BotTrade?> ReconcileAsync(BotTrade trade, CancellationToken ct)
    {
        var algoId = trade.ExitAlgoId;
        if (string.IsNullOrEmpty(algoId)) return null;

        // Without credentials the exchange cannot be asked. Reporting "not closed"
        // is the safe answer: the trade stays open and the operator sees the
        // start-up refusal instead.
        if (okxOptions.DescribeRefusal() is not null) return null;

        var algo = await trading.ReadAlgoOrderAsync(algoId, ct);

        if (algo is null || algo.IsWaiting) return null;

        if (!algo.HasTriggered)
        {
            // Cancelled or failed at the exchange, without a fill. The position is
            // still held but no longer guarded, which the operator needs to know.
            log.LogWarning(
                "[OKX] OCO {AlgoId} guarding trade {Id} is in state '{State}' with no fill. " +
                "The position is open and no longer protected at the exchange.",
                algoId, trade.Id, algo.State);

            trade.ExitAlgoId = null;
            await repo.UpdateExitAlgoIdAsync(trade.Id, null, ct);
            return null;
        }

        var instrument = await instruments.GetSpotAsync(trade.Symbol, ct);
        var detail     = await trading.ReadOrderAsync(instrument.InstId, algo.OrdId!, ct);

        if (detail is null || detail.FilledSize <= 0m || detail.AverageFillPrice is null)
        {
            log.LogWarning(
                "[OKX] OCO {AlgoId} for trade {Id} reports triggered but order {OrdId} shows no fill " +
                "yet. Leaving the trade open for the next cycle.",
                algoId, trade.Id, algo.OrdId);
            return null;
        }

        var exitPrice = detail.AverageFillPrice.Value;
        var reason    = exitPrice >= trade.EntryPrice ? "EXCHANGE_TP" : "EXCHANGE_SL";

        if (detail.FilledSize < trade.Quantity)
            log.LogWarning(
                "[OKX] OCO {AlgoId} for trade {Id} sold {Sold} of {Held} {Base}. The remaining " +
                "{Residual} is still held and stops being tracked once this row closes.",
                algoId, trade.Id, detail.FilledSize, trade.Quantity, instrument.BaseCcy,
                trade.Quantity - detail.FilledSize);

        ApplyExit(trade, instrument, detail.FilledSize, exitPrice,
            detail.FeeAbs, detail.FeeCcy, algo.OrdId!, reason);

        trade.ExitAlgoId = null;
        await repo.CloseTradeAsync(trade, ct);

        log.LogWarning(
            "[OKX] Trade {Id} was closed by the exchange ({Reason}) — OCO {AlgoId} fired at ${Price}. " +
            "PnL={Pnl:+0.0000;-0.0000} USD ({PnlPct:P2}).",
            trade.Id, reason, algoId, exitPrice, trade.PnlUsd ?? 0m, trade.PnlPct ?? 0m);

        return trade;
    }

    // ── Exit ──────────────────────────────────────────────────────────────────

    public async Task<BotTrade> CloseTradeAsync(
        BotTrade trade, decimal exitPrice, string reason, CancellationToken ct)
    {
        var refusal = okxOptions.DescribeRefusal();
        if (refusal is not null)
            throw new InvalidOperationException(
                $"Live trade {trade.Id} cannot be closed on OKX: {refusal} " +
                "The position is still open on the exchange.");

        var instrument = await instruments.GetSpotAsync(trade.Symbol, ct);

        // ── Stand the exchange-side guard down first ──
        //
        // Selling while the OCO is still armed queues a second sell for the same
        // coins. Whichever lands first, the other is left trying to sell a balance
        // that no longer exists — and if the account happens to hold that asset for
        // another reason, it sells that instead.
        if (trade.ExitAlgoId is { Length: > 0 } algoId)
        {
            var cancelled = await trading.TryCancelAlgoAsync(instrument.InstId, algoId, ct);

            if (!cancelled)
            {
                // The ordinary reason a cancel fails is that the order just fired.
                // If it did, the position is already closed and the right answer is
                // the exchange's fill, not a new sell.
                var settled = await ReconcileAsync(trade, ct);
                if (settled is not null) return settled;

                throw new InvalidOperationException(
                    $"Could not cancel OCO {algoId} guarding trade {trade.Id}, and it has not " +
                    "triggered either. Refusing to place a second sell for the same coins. " +
                    "Retrying next cycle.");
            }

            trade.ExitAlgoId = null;
        }

        var quantity = OkxSizing.FloorToStep(trade.Quantity, instrument.LotSize);

        if (quantity < instrument.MinSize)
            throw new InvalidOperationException(
                $"Live trade {trade.Id} holds {trade.Quantity} {instrument.BaseCcy}, below OKX's " +
                $"{instrument.MinSize} minimum order size. The bot cannot sell it; close it manually.");

        // What the account actually holds wins over what the row says. A manual
        // trade, a withdrawal or a second bot on the same key all show up here, and
        // an oversized sell would be rejected outright — leaving the stop unfilled.
        var available = await trading.GetAvailableAsync(instrument.BaseCcy, ct);
        if (available < quantity)
        {
            var reduced = OkxSizing.FloorToStep(available, instrument.LotSize);

            log.LogWarning(
                "[OKX] Trade {Id} expects {Expected} {Base} but only {Available} is available — " +
                "selling {Reduced}. Something outside the bot moved this balance.",
                trade.Id, quantity, instrument.BaseCcy, available, reduced);

            if (reduced < instrument.MinSize)
                throw new InvalidOperationException(
                    $"Live trade {trade.Id} cannot be closed: only {available} {instrument.BaseCcy} " +
                    $"is available, below the {instrument.MinSize} minimum order size.");

            quantity = reduced;
        }

        var orderId = await trading.PlaceSpotMarketOrderAsync(instrument.InstId, "sell", quantity, ct);
        var fill    = await trading.WaitForFillAsync(instrument.InstId, orderId, ct);

        if (fill.FilledBase < quantity)
            log.LogWarning(
                "[OKX] Exit for trade {Id} sold {Sold} of {Asked} {Base}. The remaining " +
                "{Residual} is still held and is no longer tracked by this trade.",
                trade.Id, fill.FilledBase, quantity, instrument.BaseCcy,
                quantity - fill.FilledBase);

        ApplyExit(trade, instrument, fill.FilledBase, fill.AveragePrice,
            fill.FeeAbs, fill.FeeCcy, orderId, reason);

        try
        {
            await repo.CloseTradeAsync(trade, ct);
        }
        catch (Exception ex)
        {
            // The sell has happened. The row is now wrong in the safe direction —
            // it still reads OPEN — so the next cycle will try to sell again and
            // find no balance. Log everything needed to correct it by hand.
            log.LogCritical(ex,
                "[OKX] SOLD BUT NOT RECORDED — trade {Id} was closed by order {OrdId} at {Price} " +
                "(P&L {Pnl}), and the bot_trades update failed. The row still reads OPEN. " +
                "Correct it before the bot retries the exit.",
                trade.Id, orderId, fill.AveragePrice, trade.PnlUsd ?? 0m);
            throw;
        }

        log.LogInformation(
            "[OKX] CLOSE {Symbol} id={Id} sold {Qty} {Base} @ ${Price} reason={Reason} " +
            "fee=${Fee} PnL={Pnl:+0.0000;-0.0000} USD ({PnlPct:P2}) (signal price ${Signal})",
            trade.Symbol, trade.Id, fill.FilledBase, instrument.BaseCcy, fill.AveragePrice,
            reason, trade.FeeUsd ?? 0m, trade.PnlUsd ?? 0m, trade.PnlPct ?? 0m, exitPrice);

        return trade;
    }

    /// <summary>
    /// Write an exit fill onto the trade. Shared by the bot-driven close and by
    /// reconciliation of an exchange-driven one, so a position closed by the OCO is
    /// accounted for exactly like one the bot closed itself — the P&amp;L series
    /// stays comparable regardless of which side pulled the trigger.
    /// </summary>
    private static void ApplyExit(
        BotTrade trade, OkxInstrument instrument, decimal filledBase, decimal averagePrice,
        decimal feeAbs, string? feeCcy, string orderId, string reason)
    {
        // A spot sell is charged in the quote currency. The base-fee branch is kept
        // so an unexpected fee currency is converted rather than counted as USD.
        var feeInQuote = string.Equals(feeCcy, instrument.QuoteCcy, StringComparison.OrdinalIgnoreCase);
        var exitFeeUsd = feeInQuote ? feeAbs : feeAbs * averagePrice;

        var proceeds = filledBase * averagePrice - exitFeeUsd;
        var pnlUsd   = Math.Round(proceeds - trade.NotionalUsd, 4);

        trade.ExitPrice   = averagePrice;
        trade.PnlUsd      = pnlUsd;
        trade.PnlPct      = trade.NotionalUsd > 0m ? Math.Round(pnlUsd / trade.NotionalUsd, 6) : 0m;
        trade.Status      = "CLOSED";
        trade.ClosedAt    = DateTime.UtcNow;
        trade.CloseReason = reason;
        trade.ExitOrderId = orderId;
        trade.FeeUsd      = Math.Round((trade.FeeUsd ?? 0m) + exitFeeUsd, 8);
    }
}
