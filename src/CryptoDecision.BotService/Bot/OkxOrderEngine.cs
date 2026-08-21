using CryptoDecision.BotService.Exchanges;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Bot;

/// <summary>
/// Places real orders on OKX USDT-margined perpetual swaps.
///
/// Perps rather than spot because the strategy's signal is symmetric — it scores
/// both directions and a cash account can only act on half of them. What comes
/// with that is liquidation, so several things here exist purely to keep the stop
/// loss the thing that closes a losing trade:
///
///  - Leverage is set explicitly per instrument before the first order, because
///    OKX stores it as instrument state that persists from whatever was set last.
///  - <see cref="DescribeRefusal"/> refuses to run a stop loss that sits anywhere
///    near the liquidation price.
///  - An entry arms a reduce-only OCO at the exchange before the method returns.
///
/// The other rules carried over from the spot version still hold, and matter more
/// here rather than less:
///
///  - Nothing is assumed. A market order's price, filled size and fee are all
///    unknown when it is sent, so the trade row is built from the fill.
///  - Every check that can refuse an order happens <em>before</em> placement.
///    After the fill there is no unwinding, so failures are recorded loudly and
///    never by discarding the record.
///  - Sizes cross a unit boundary. OKX counts contracts (0.01 BTC each for
///    BTC-USDT-SWAP); every risk figure the bot reasons about is in base units.
///    <see cref="BotTrade.Quantity"/> holds base units so the P&amp;L arithmetic
///    matches the paper engine, and contracts are derived at order time.
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
    /// How much of the distance to liquidation the stop loss may occupy. At 0.5 the
    /// stop fires no later than halfway there, which leaves room for the funding
    /// and fee drag that also erodes margin. Above this the stop stops being a stop.
    /// </summary>
    private const decimal MaxStopShareOfLiquidation = 0.5m;

    public string? DescribeRefusal(BotOptions opts)
    {
        var configRefusal = okxOptions.DescribeRefusal();
        if (configRefusal is not null) return configRefusal;

        // ── Would the stop loss actually get there first? ──
        //
        // This is the failure mode leverage introduces and spot does not have. With
        // the stop too far out relative to leverage, the exchange closes the
        // position at the liquidation price — which is worse than the stop in every
        // way: a market close at the worst available price, plus a liquidation fee,
        // and no take-profit leg left on the other side.
        var liquidationDistance = okxOptions.ApproxLiquidationDistance;
        var maxStop             = liquidationDistance * MaxStopShareOfLiquidation;

        if (opts.StopLossPct > maxStop)
            return $"a {opts.StopLossPct:P2} stop loss at {okxOptions.Leverage}x leverage sits past " +
                   $"{MaxStopShareOfLiquidation:P0} of the ~{liquidationDistance:P1} distance to " +
                   $"liquidation. Lower the leverage to about " +
                   $"{decimal.Floor(MaxStopShareOfLiquidation / Math.Max(opts.StopLossPct, 0.0001m))}x " +
                   $"or tighten the stop below {maxStop:P2}.";

        return null;
    }

    /// <summary>Perpetuals are symmetric — this is the reason for trading them.</summary>
    public bool SupportsShort(BotOptions opts) => true;

    // ── Entry ─────────────────────────────────────────────────────────────────

    public async Task<BotTrade> OpenPositionAsync(
        string symbol, string strategy, string side, decimal price, decimal capitalUsd,
        decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false)
    {
        var refusal = DescribeRefusal(state.Options);
        if (refusal is not null)
            throw new InvalidOperationException($"OKX live order refused: {refusal}");

        var positionSide = side switch
        {
            "LONG" or "BUY"   => "long",
            "SHORT" or "SELL" => "short",
            _ => throw new InvalidOperationException(
                     $"Unknown side '{side}'; expected LONG or SHORT."),
        };

        var instrument = await instruments.GetSwapAsync(symbol, ct);
        var config     = await trading.GetAccountConfigAsync(ct);

        await trading.EnsureLeverageAsync(instrument.InstId, ct);

        // ── Size the order ──
        //
        // Sizing stays notional-based, exactly as it is in paper mode: the position
        // is a percentage of capital, and leverage only reduces the margin that
        // notional requires. Sizing off margin instead would let a leverage change
        // silently multiply exposure, which is how a working configuration becomes
        // an account-ending one without anybody editing a risk parameter.
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

        // notional → base units → contracts, floored onto the contract grid.
        var targetBase = notional / price;
        var contracts  = OkxSizing.FloorToStep(targetBase / instrument.ContractValue, instrument.LotSize);

        if (contracts < instrument.MinSize)
            throw new InvalidOperationException(
                $"${notional} at {price} is {contracts} contracts of {instrument.InstId} " +
                $"({instrument.ContractValue} {instrument.BaseCcy} each), under OKX's " +
                $"{instrument.MinSize} minimum. Raise the position size.");

        // ── Is there margin for it? ──
        var marginNeeded = notional / okxOptions.Leverage;
        var available    = await trading.GetAvailableAsync(instrument.QuoteCcy, ct);

        if (available < marginNeeded * 1.05m)
            throw new InvalidOperationException(
                $"OKX {instrument.QuoteCcy} available balance is {available:F2}, short of the " +
                $"{marginNeeded:F2} margin this ${notional} position needs at " +
                $"{okxOptions.Leverage}x. Fund the account or lower the position size.");

        // ── Cleared. Place it. ──
        var entrySide  = positionSide == "long" ? "buy" : "sell";
        var payloadPos = config.IsHedgeMode ? positionSide : null;

        var orderId = await trading.PlaceSwapMarketOrderAsync(
            instrument.InstId, entrySide, payloadPos, contracts, reduceOnly: false, ct);

        var fill = await trading.WaitForFillAsync(instrument.InstId, orderId, ct);

        // ── Reconcile the fill into a trade record ──
        //
        // Unlike spot, a linear perp charges its fee in the quote currency, so the
        // filled size arrives intact and the fee is a separate USDT cost rather than
        // a haircut on the position. Nothing needs to be held back to stay sellable.
        var filledBase   = fill.FilledContracts * instrument.ContractValue;
        var entryFeeUsd  = fill.FeeAbs;
        var filledNotion = filledBase * fill.AveragePrice;

        var trade = new BotTrade
        {
            Symbol       = symbol,
            Side         = positionSide == "long" ? "LONG" : "SHORT",
            Strategy     = strategy,
            EntryPrice   = fill.AveragePrice,
            Quantity     = filledBase,
            NotionalUsd  = Math.Round(filledNotion, 4),
            Status       = "OPEN",
            OpenedAt     = DateTime.UtcNow,
            Mode         = "LIVE",
            Exchange     = ExchangeName,
            EntryOrderId = orderId,
            FeeUsd       = Math.Round(entryFeeUsd, 8),
            Leverage     = okxOptions.Leverage,
            MarginMode   = okxOptions.MarginMode,
        };

        try
        {
            trade = trade with { Id = await repo.InsertTradeAsync(trade, ct) };
        }
        catch (Exception ex)
        {
            // The position is already open. Losing the row would leave a leveraged
            // holding with no stop loss, so everything needed to recreate it goes
            // into the log before the exception continues.
            log.LogCritical(ex,
                "[OKX] FILLED BUT NOT RECORDED — order {OrdId} opened {Side} {Contracts} contracts " +
                "({Base} {BaseCcy}) on {InstId} at {Price}, and the bot_trades insert failed. This " +
                "position is open on the exchange and unmanaged. Record it manually before " +
                "restarting the bot.",
                orderId, positionSide, fill.FilledContracts, filledBase, instrument.BaseCcy,
                instrument.InstId, fill.AveragePrice);
            throw;
        }

        log.LogInformation(
            "[OKX] OPEN {Side} {Symbol} id={Id} {Contracts} contracts ({Base} {BaseCcy}) @ ${Price} " +
            "notional=${Notional} margin=${Margin} at {Lever}x fee=${Fee} " +
            "(signal ${Signal}, slippage {Slip:+0.000;-0.000}%)",
            trade.Side, symbol, trade.Id, fill.FilledContracts, filledBase, instrument.BaseCcy,
            fill.AveragePrice, trade.NotionalUsd, Math.Round(filledNotion / okxOptions.Leverage, 2),
            okxOptions.Leverage, trade.FeeUsd, price,
            price > 0m ? (fill.AveragePrice - price) / price * 100m : 0m);

        await ArmProtectiveExitAsync(trade, instrument, positionSide, fill.FilledContracts, ct);

        return trade;
    }

    // ── Exchange-side protection ──────────────────────────────────────────────

    /// <summary>
    /// Place the reduce-only OCO that guards a freshly opened position, and persist
    /// its id.
    ///
    /// Never throws. The position exists either way and the bot's own loop is still
    /// watching it, so failing to arm the exchange-side guard degrades protection
    /// rather than removing it, and unwinding a real fill over a transient API error
    /// would be the worse trade. Logged as critical because on a leveraged position
    /// the fallback — the bot process staying alive — is now the only thing standing
    /// between the trade and liquidation.
    /// </summary>
    private async Task ArmProtectiveExitAsync(
        BotTrade trade, OkxInstrument instrument, string positionSide, decimal contracts,
        CancellationToken ct)
    {
        var opts    = state.Options;
        var isLong  = positionSide == "long";
        var entry   = trade.EntryPrice;

        // Profit is up for a long and down for a short; the stop is the mirror.
        // Rounding always goes the direction that does not widen risk.
        var takeProfit = isLong
            ? OkxSizing.FloorToStep(entry * (1m + opts.TakeProfitPct), instrument.TickSize)
            : OkxSizing.CeilToStep (entry * (1m - opts.TakeProfitPct), instrument.TickSize);

        var stopLoss = isLong
            ? OkxSizing.CeilToStep (entry * (1m - opts.StopLossPct), instrument.TickSize)
            : OkxSizing.FloorToStep(entry * (1m + opts.StopLossPct), instrument.TickSize);

        var straddlesEntry = isLong
            ? takeProfit > entry && stopLoss < entry && stopLoss > 0m
            : takeProfit < entry && stopLoss > entry && takeProfit > 0m;

        if (!straddlesEntry)
        {
            log.LogCritical(
                "[OKX] Trade {Id} has no exchange-side stop: {Side} TP {Tp} / SL {Sl} do not straddle " +
                "the {Entry} entry price (take_profit_pct={TpPct:P2}, stop_loss_pct={SlPct:P2}). " +
                "This leveraged position is protected only while the bot process is alive.",
                trade.Id, trade.Side, takeProfit, stopLoss, entry, opts.TakeProfitPct, opts.StopLossPct);
            return;
        }

        try
        {
            var algoId = await trading.PlaceOcoExitAsync(
                instrument.InstId, positionSide, contracts, takeProfit, stopLoss, ct);

            trade.ExitAlgoId = algoId;
            await repo.UpdateExitAlgoIdAsync(trade.Id, algoId, ct);

            log.LogInformation(
                "[OKX] Trade {Id} guarded by OCO {AlgoId}: TP ${Tp} / SL ${Sl} " +
                "({TpPct:P2} / {SlPct:P2} from entry). This survives a bot restart.",
                trade.Id, algoId, takeProfit, stopLoss, opts.TakeProfitPct, opts.StopLossPct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogCritical(ex,
                "[OKX] Trade {Id} is OPEN — {Side} {Qty} {Base} at {Lever}x — but its protective OCO " +
                "could not be placed. If the bot stops, nothing will stop it out before liquidation. " +
                "Place a stop manually or close the position.",
                trade.Id, trade.Side, trade.Quantity, instrument.BaseCcy, okxOptions.Leverage);
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
            // Cancelled or failed at the exchange without a fill. The position is
            // still open but no longer guarded, which on leverage the operator needs
            // to know immediately.
            log.LogWarning(
                "[OKX] OCO {AlgoId} guarding trade {Id} is in state '{State}' with no fill. " +
                "The leveraged position is open and no longer protected at the exchange.",
                algoId, trade.Id, algo.State);

            trade.ExitAlgoId = null;
            await repo.UpdateExitAlgoIdAsync(trade.Id, null, ct);
            return null;
        }

        var instrument = await instruments.GetSwapAsync(trade.Symbol, ct);
        var detail     = await trading.ReadOrderAsync(instrument.InstId, algo.OrdId!, ct);

        if (detail is null || detail.FilledSize <= 0m || detail.AverageFillPrice is null)
        {
            log.LogWarning(
                "[OKX] OCO {AlgoId} for trade {Id} reports triggered but order {OrdId} shows no fill " +
                "yet. Leaving the trade open for the next cycle.",
                algoId, trade.Id, algo.OrdId);
            return null;
        }

        var exitPrice   = detail.AverageFillPrice.Value;
        var closedBase  = detail.FilledSize * instrument.ContractValue;
        var wasProfit   = trade.Side == "SHORT" ? exitPrice <= trade.EntryPrice : exitPrice >= trade.EntryPrice;
        var reason      = wasProfit ? "EXCHANGE_TP" : "EXCHANGE_SL";

        if (closedBase < trade.Quantity)
            log.LogWarning(
                "[OKX] OCO {AlgoId} for trade {Id} closed {Closed} of {Held} {Base}. The remainder " +
                "is still an open position and stops being tracked once this row closes.",
                algoId, trade.Id, closedBase, trade.Quantity, instrument.BaseCcy);

        ApplyExit(trade, closedBase, exitPrice, detail.FeeAbs, algo.OrdId!, reason);

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

        var instrument = await instruments.GetSwapAsync(trade.Symbol, ct);
        var config     = await trading.GetAccountConfigAsync(ct);

        var positionSide = trade.Side == "SHORT" ? "short" : "long";

        // ── Stand the exchange-side guard down first ──
        //
        // Closing while the OCO is armed queues a second reduce-only order against
        // the same position. Whichever lands first, the other is left trying to
        // reduce a position that no longer exists.
        if (trade.ExitAlgoId is { Length: > 0 } algoId)
        {
            var cancelled = await trading.TryCancelAlgoAsync(instrument.InstId, algoId, ct);

            if (!cancelled)
            {
                // The ordinary reason a cancel fails is that the order just fired.
                // If it did, the position is already closed and the right answer is
                // the exchange's fill, not a new order.
                var settled = await ReconcileAsync(trade, ct);
                if (settled is not null) return settled;

                throw new InvalidOperationException(
                    $"Could not cancel OCO {algoId} guarding trade {trade.Id}, and it has not " +
                    "triggered either. Refusing to place a second exit against the same position. " +
                    "Retrying next cycle.");
            }

            trade.ExitAlgoId = null;
        }

        // ── What does the exchange say is actually open? ──
        //
        // For futures the position — not any coin balance — is the source of truth,
        // and it can differ from the row: a manual close, a liquidation, or a second
        // bot on the same key all show up here.
        var position = await trading.GetPositionAsync(instrument.InstId, ct);

        if (position is null)
        {
            log.LogWarning(
                "[OKX] Trade {Id} expects an open {Side} position on {InstId} but the exchange reports " +
                "none — it was closed elsewhere (manual close, or liquidation). Settling the row at " +
                "the current price so it stops being retried.",
                trade.Id, trade.Side, instrument.InstId);

            // No fill to read, so the reference price is the best available figure.
            // Flagged in the close reason: this P&L is an estimate, not an exchange fill.
            ApplyExit(trade, trade.Quantity, exitPrice, 0m, orderId: null, $"{reason}_UNCONFIRMED");
            await repo.CloseTradeAsync(trade, ct);
            return trade;
        }

        var contracts = OkxSizing.FloorToStep(
            trade.Quantity / instrument.ContractValue, instrument.LotSize);

        if (position.AbsContracts < contracts)
        {
            var reduced = OkxSizing.FloorToStep(position.AbsContracts, instrument.LotSize);

            log.LogWarning(
                "[OKX] Trade {Id} expects {Expected} contracts but the position holds {Actual} — " +
                "closing {Reduced}. Something outside the bot changed this position.",
                trade.Id, contracts, position.AbsContracts, reduced);

            if (reduced < instrument.MinSize)
                throw new InvalidOperationException(
                    $"Live trade {trade.Id} cannot be closed: the position holds " +
                    $"{position.AbsContracts} contracts, below the {instrument.MinSize} minimum " +
                    "order size.");

            contracts = reduced;
        }

        // Opposite side, reduce-only: this shrinks the position rather than opening
        // a new one in the other direction.
        var exitSide   = positionSide == "long" ? "sell" : "buy";
        var payloadPos = config.IsHedgeMode ? positionSide : null;

        var orderId = await trading.PlaceSwapMarketOrderAsync(
            instrument.InstId, exitSide, payloadPos, contracts, reduceOnly: true, ct);

        var fill       = await trading.WaitForFillAsync(instrument.InstId, orderId, ct);
        var closedBase = fill.FilledContracts * instrument.ContractValue;

        if (fill.FilledContracts < contracts)
            log.LogWarning(
                "[OKX] Exit for trade {Id} closed {Closed} of {Asked} contracts. The remaining " +
                "{Residual} is still an open position and is no longer tracked by this trade.",
                trade.Id, fill.FilledContracts, contracts, contracts - fill.FilledContracts);

        ApplyExit(trade, closedBase, fill.AveragePrice, fill.FeeAbs, orderId, reason);

        try
        {
            await repo.CloseTradeAsync(trade, ct);
        }
        catch (Exception ex)
        {
            // The position is already closed. The row is now wrong in the safe
            // direction — it still reads OPEN — so the next cycle will try again and
            // find nothing open. Log everything needed to correct it by hand.
            log.LogCritical(ex,
                "[OKX] CLOSED BUT NOT RECORDED — trade {Id} was closed by order {OrdId} at {Price} " +
                "(P&L {Pnl}), and the bot_trades update failed. The row still reads OPEN. " +
                "Correct it before the bot retries the exit.",
                trade.Id, orderId, fill.AveragePrice, trade.PnlUsd ?? 0m);
            throw;
        }

        log.LogInformation(
            "[OKX] CLOSE {Side} {Symbol} id={Id} {Contracts} contracts @ ${Price} reason={Reason} " +
            "fee=${Fee} PnL={Pnl:+0.0000;-0.0000} USD ({PnlPct:P2}) (signal ${Signal})",
            trade.Side, trade.Symbol, trade.Id, fill.FilledContracts, fill.AveragePrice,
            reason, trade.FeeUsd ?? 0m, trade.PnlUsd ?? 0m, trade.PnlPct ?? 0m, exitPrice);

        return trade;
    }

    /// <summary>
    /// Write an exit onto the trade. Shared by the bot-driven close and by
    /// reconciliation of an exchange-driven one, so a position closed by the OCO is
    /// accounted for exactly like one the bot closed itself — the P&amp;L series
    /// stays comparable regardless of which side pulled the trigger.
    ///
    /// The direction term is the whole reason perps were worth the rework: a short
    /// profits when price falls, so P&amp;L is signed by the side rather than always
    /// measured upward. Getting this backwards would report every winning short as a
    /// loss and vice versa, and the circuit breakers act on those numbers.
    ///
    /// Fees are quote-denominated on a linear perp, so both legs are already USD.
    /// Funding is <em>not</em> included: OKX charges or pays it every eight hours
    /// against the position, and it lands in the account bills rather than on either
    /// order. A position held across funding will show a small unexplained gap
    /// between this figure and the account balance.
    /// </summary>
    private static void ApplyExit(
        BotTrade trade, decimal closedBase, decimal exitPrice, decimal exitFeeUsd,
        string? orderId, string reason)
    {
        var grossPnl = trade.Side == "SHORT"
            ? (trade.EntryPrice - exitPrice) * closedBase
            : (exitPrice - trade.EntryPrice) * closedBase;

        var totalFeeUsd = (trade.FeeUsd ?? 0m) + exitFeeUsd;
        var pnlUsd      = Math.Round(grossPnl - totalFeeUsd, 4);

        trade.ExitPrice   = exitPrice;
        trade.PnlUsd      = pnlUsd;
        trade.PnlPct      = trade.NotionalUsd > 0m ? Math.Round(pnlUsd / trade.NotionalUsd, 6) : 0m;
        trade.Status      = "CLOSED";
        trade.ClosedAt    = DateTime.UtcNow;
        trade.CloseReason = reason;
        trade.ExitOrderId = orderId;
        trade.FeeUsd      = Math.Round(totalFeeUsd, 8);
    }
}
