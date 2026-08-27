using CryptoDecision.BotService.Exchanges;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;
using CryptoDecision.Shared.Signals;

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

        // ── Can this key actually place an order? ──
        //
        // A read_only key authenticates, reports the account level, returns balances
        // and max order sizes, and is refused only when an order is submitted — as
        // code 50123, once per signal, inside the catch that keeps one bad order
        // from killing the cycle. So the bot logged an error every 30 seconds and
        // looked healthy while placing nothing. Asked here instead, from the
        // permission list the account config already carries, it blocks the start
        // with something an operator can act on. Null means the config has not been
        // read yet, which is not a refusal — the startup probe fills it in.
        if (trading.CanTrade == false)
            return "this OKX API key is read-only. Enable the Trade permission on it in the OKX API " +
                   "settings, or create a key that has it — a read-only key authenticates fine and " +
                   "is refused only at the moment an order is placed (code 50123).";

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
                   $"liquidation. Lower the leverage below " +
                   $"{decimal.Floor(MaxStopShareOfLiquidation / Math.Max(opts.StopLossPct, 0.0001m))}x " +
                   $"— well below, not to it, since that figure puts the stop exactly on the " +
                   $"boundary this margin exists to keep clear of — or tighten the stop under " +
                   $"{maxStop:P2}.";

        return null;
    }

    /// <summary>Perpetuals are symmetric — this is the reason for trading them.</summary>
    public bool SupportsShort(BotOptions opts) => true;

    // ── Entry ─────────────────────────────────────────────────────────────────

    public async Task<BotTrade> OpenPositionAsync(
        string symbol, string strategy, string side, decimal price, decimal capitalUsd,
        decimal positionPct, CancellationToken ct, decimal confidence = 1.0m, bool useAiSizing = false,
        StopGeometry? geometry = null)
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
        // Two sizing rules, chosen by whether the strategy supplied a stop distance.
        //
        // With one, size is whatever makes that stop cost risk_pct_per_trade of
        // capital — so every trade risks the same money whatever the regime. Without
        // one, fall back to the older percentage-of-capital rule, which is all a
        // strategy that does not express a stop distance can support.
        //
        // They must not be combined. Sizing a fraction of capital and *then* shrinking
        // it for volatility, while the stop separately widens for volatility, adjusts
        // one decision with two readings of the same thing: on this account a 15.9%
        // daily volatility pinned the scalar at its 0.5 floor and halved every order,
        // while the 15-minute ATR that sets the stop was 1.07%.
        var sizingOpts = state.Options;
        PositionSize size;

        if (geometry is { StopPct: > 0m } g)
        {
            size = PositionSizer.ResolveByRisk(
                capitalUsd, sizingOpts.RiskPctPerTrade, g.StopPct, confidence, useAiSizing);

            log.LogInformation(
                "[OKX] Risk-based size: {Risk:P2} of {Capital} capital over a {Stop:P2} stop = " +
                "{Notional} notional, {RiskUsd:F2} USD at risk.",
                sizingOpts.RiskPctPerTrade, capitalUsd, g.StopPct, size.NotionalUsd,
                capitalUsd * sizingOpts.RiskPctPerTrade);
        }
        else
        {
            var feature = await featureRepo.GetTodayAsync(symbol, ct);
            size = PositionSizer.Resolve(
                capitalUsd, positionPct, (double)(feature?.Volatility ?? 2.0m), confidence, useAiSizing);

            // Say what sizing did, on the same terms PaperOrderEngine does. The paper
            // engine logged the volatility haircut and this one did not, so the single
            // mode where the number moves real money was the mode where it was invisible.
            if (size.VolatilityScalar < 1.0)
                log.LogInformation(
                    "[OKX] Vol-adjusted size: vol={Vol:F1}% scalar={Scalar:P0} notional={Notional} " +
                    "(from {Pct:P0} of {Capital})",
                    feature?.Volatility ?? (decimal)PositionSizer.BaseVolatilityPct,
                    size.VolatilityScalar, size.NotionalUsd, positionPct, capitalUsd);
        }

        var notional = size.NotionalUsd;

        if (useAiSizing && size.ConfidenceScalar != 1.0m)
            log.LogInformation(
                "[OKX] AI-sized: confidence={Conf:P0} scalar={Scalar:F2} adjustedPct={Pct:P1}",
                confidence, size.ConfidenceScalar, size.AdjustedPct);

        if (notional > okxOptions.MaxOrderNotionalUsd)
        {
            // Restate the risk AFTER the cap. The risk-based sizing above already
            // logged "$0.60 USD at risk" from capital x risk_pct, and then this cap
            // cut the notional to a tenth of what that figure assumed — so the only
            // number an operator saw was wrong by the ratio of the two, and it was
            // wrong in the reassuring direction. Whenever the cap binds,
            // risk_pct_per_trade stops describing anything: every order is exactly
            // the ceiling, and the real risk is the ceiling times the stop.
            var askedRisk  = geometry is { StopPct: > 0m } gA ? notional * gA.StopPct : 0m;
            var cappedRisk = geometry is { StopPct: > 0m } gC
                ? okxOptions.MaxOrderNotionalUsd * gC.StopPct
                : 0m;

            log.LogWarning(
                "[OKX] Sizing asked for ${Asked} but the per-order ceiling is ${Cap} — capping. " +
                "Real risk on this trade is ${CappedRisk:F3}, not the ${AskedRisk:F3} the risk " +
                "model asked for. While the ceiling binds, risk_pct_per_trade has no effect and " +
                "every order is ${Cap}. Raise Okx__MaxOrderNotionalUsd to make the risk setting " +
                "mean what it says, or lower capital_usd so the two agree.",
                notional, okxOptions.MaxOrderNotionalUsd, cappedRisk, askedRisk,
                okxOptions.MaxOrderNotionalUsd);

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

        // Re-check the floor against what will actually be sent, not what was asked
        // for. Flooring onto the lot grid only ever shrinks the order, so checking
        // the request let the real notional slip under the configured minimum.
        var actualNotional = contracts * instrument.ContractValue * price;

        // A request that cleared the minimum and was pushed under it purely by the
        // grid is the grid's granularity talking, not a risk decision — so round up
        // rather than refuse.
        //
        // This is not hypothetical. With the volatility scalar pinned at its 0.5
        // floor the request landed on exactly $5.00, the configured minimum: the
        // pre-floor check passed on `5.00 < 5.00` being false, then flooring to the
        // 0.01 SOL grid produced $4.02 and the order was refused. Every entry for
        // hours, while the bot reported RUNNING and every health check stayed green.
        //
        // Stepping up is bounded by the per-order ceiling — it may round up to reach
        // the floor, never past the cap. If one lot up would breach the ceiling the
        // two limits genuinely cannot both be met, and refusing is then correct.
        if (actualNotional < okxOptions.MinOrderNotionalUsd
            && notional >= okxOptions.MinOrderNotionalUsd)
        {
            var steppedContracts = contracts + instrument.LotSize;
            var steppedNotional  = steppedContracts * instrument.ContractValue * price;

            if (steppedNotional <= okxOptions.MaxOrderNotionalUsd)
            {
                log.LogInformation(
                    "[OKX] ${Asked} floors to {Floored} contracts (${FlooredNotional:F2}), under the " +
                    "${Min} minimum — stepping up one {Lot} lot to {Stepped} contracts " +
                    "(${SteppedNotional:F2}).",
                    notional, contracts, actualNotional, okxOptions.MinOrderNotionalUsd,
                    instrument.LotSize, steppedContracts, steppedNotional);

                contracts      = steppedContracts;
                actualNotional = steppedNotional;
            }
        }

        if (actualNotional < okxOptions.MinOrderNotionalUsd)
            throw new InvalidOperationException(
                $"After flooring to the {instrument.LotSize} lot grid the order is ${actualNotional:F2}, " +
                $"below the ${okxOptions.MinOrderNotionalUsd} minimum, and one lot up would breach the " +
                $"${okxOptions.MaxOrderNotionalUsd} per-order ceiling. Raise capital_usd or position_pct.");

        // ── Is there margin for it? ──
        // Off the order actually being sent, not the request. Flooring shrinks it and
        // the step-up above can nudge it back over, so sizing off `notional` checked
        // margin against a number that was never placed.
        var marginNeeded = actualNotional / okxOptions.Leverage;
        var available    = await trading.GetAvailableAsync(instrument.QuoteCcy, ct);

        if (available < marginNeeded * 1.05m)
            throw new InvalidOperationException(
                $"OKX {instrument.QuoteCcy} available balance is {available:F2}, short of the " +
                $"{marginNeeded:F2} margin this ${actualNotional:F2} position needs at " +
                $"{okxOptions.Leverage}x. Fund the account or lower the position size.");

        // ── Cleared. Place it. ──
        var entrySide  = positionSide == "long" ? "buy" : "sell";
        var payloadPos = config.IsHedgeMode ? positionSide : null;

        var (orderId, fill) = await AcquireEntryFillAsync(
            instrument, entrySide, payloadPos, contracts, price, ct);

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

            // Set here as well as persisted by the caller, so the in-memory trade the
            // bot manages this cycle already carries the right levels rather than
            // waiting a round trip for them.
            StopPrice     = geometry?.RebaseTo(fill.AveragePrice, side).StopPrice,
            TargetPrice   = geometry?.RebaseTo(fill.AveragePrice, side).TargetPrice,
            AtrPctAtEntry = geometry is null ? null : (decimal)geometry.AtrPctUsed,
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

        await ArmProtectiveExitAsync(trade, instrument, positionSide, fill.FilledContracts, geometry, ct);

        return trade;
    }

    /// <summary>
    /// Get an entry fill: resting on the book when maker entries are on, crossing the
    /// spread when they are not.
    ///
    /// Throws <see cref="InvalidOperationException"/> when no position was opened. The
    /// caller in TradingBotService already treats that as a refused entry — logged,
    /// persisted to the refusal trail, cooldown deliberately not stamped — which is
    /// the correct handling here too: an unfilled maker order is a normal outcome, not
    /// a fault, and the next cycle re-decides on fresh evidence.
    /// </summary>
    private async Task<(string OrderId, OkxFill Fill)> AcquireEntryFillAsync(
        OkxInstrument instrument, string entrySide, string? payloadPos,
        decimal contracts, decimal signalPrice, CancellationToken ct)
    {
        if (!okxOptions.UseMakerEntries)
        {
            var takerId = await trading.PlaceSwapMarketOrderAsync(
                instrument.InstId, entrySide, payloadPos, contracts, reduceOnly: false, ct);

            return (takerId, await trading.WaitForFillAsync(instrument.InstId, takerId, ct));
        }

        // ── Where to rest ─────────────────────────────────────────────────────
        var quote = await trading.GetQuoteAsync(instrument.InstId, ct);

        if (quote is null || !quote.HasTwoSidedQuote)
            throw new InvalidOperationException(
                $"OKX returned no usable two-sided quote for {instrument.InstId} " +
                $"(bid={quote?.BidRaw ?? "none"}, ask={quote?.AskRaw ?? "none"}). A maker order needs " +
                "a side to rest on; not entering this cycle.");

        var isBuy = entrySide == "buy";

        // The passive side: buy at the bid, sell at the ask. Offset steps *away* from
        // the touch, never across it — a post-only order that crosses is refused by
        // the exchange, so stepping the wrong way would simply never fill.
        var offset = instrument.TickSize * okxOptions.MakerPriceOffsetTicks;

        var limitPrice = isBuy
            ? quote.Bid!.Value - offset
            : quote.Ask!.Value + offset;

        limitPrice = OkxSizing.FloorToStep(limitPrice, instrument.TickSize);

        if (limitPrice <= 0m)
            throw new InvalidOperationException(
                $"Computed a non-positive limit price ({limitPrice}) for {instrument.InstId} from " +
                $"bid {quote.Bid} / ask {quote.Ask}. Not entering.");

        // ── Rest it ───────────────────────────────────────────────────────────
        var placement = await trading.PlaceSwapPostOnlyOrderAsync(
            instrument.InstId, entrySide, payloadPos, contracts, limitPrice,
            reduceOnly: false, ct);

        if (!placement.Placed)
            throw new InvalidOperationException(
                $"Post-only {entrySide} on {instrument.InstId} at {limitPrice} was not accepted " +
                $"({placement.RejectCode}: {placement.RejectMessage ?? "no message"}). Usually the " +
                "book moved and the order would have crossed. Nothing is resting.");

        var orderId = placement.OrdId!;

        var fill = await trading.WaitForRestingFillAsync(
            instrument.InstId, orderId,
            okxOptions.MakerFillPollAttempts, okxOptions.MakerFillPollDelayMs, ct);

        if (fill is null)
        {
            var waited = okxOptions.MakerFillPollAttempts * okxOptions.MakerFillPollDelayMs / 1000.0;
            throw new InvalidOperationException(
                $"Post-only {entrySide} on {instrument.InstId} rested at {limitPrice} for " +
                $"{waited:F0}s without filling and was cancelled. No position was opened. This is " +
                "the expected cost of maker entries — the next cycle will re-decide.");
        }

        // ── Partial fills ─────────────────────────────────────────────────────
        //
        // A resting order that filled part way leaves a real position smaller than
        // the one that was sized and risk-checked. Accepting it silently would put
        // the account into a position nobody approved the size of; refusing without
        // cleaning up would leave it open and unmanaged.
        var filledNotional = fill.FilledContracts * instrument.ContractValue * fill.AveragePrice;

        if (!fill.FullyFilled && filledNotional < okxOptions.MinOrderNotionalUsd)
        {
            log.LogWarning(
                "[OKX] Post-only {Side} on {InstId} filled only {Filled} contract(s) " +
                "(${Notional:F2}), under the ${Min} minimum. Closing the remainder immediately " +
                "rather than keeping a position too small to manage.",
                entrySide, instrument.InstId, fill.FilledContracts, filledNotional,
                okxOptions.MinOrderNotionalUsd);

            await UnwindDustAsync(instrument, entrySide, payloadPos, fill.FilledContracts, ct);

            throw new InvalidOperationException(
                $"Post-only {entrySide} on {instrument.InstId} filled ${filledNotional:F2}, under " +
                $"the ${okxOptions.MinOrderNotionalUsd} minimum; the fill has been closed out. " +
                "No position is open.");
        }

        if (!fill.FullyFilled)
            log.LogWarning(
                "[OKX] Post-only {Side} on {InstId} filled {Filled} contract(s) (${Notional:F2}) of " +
                "the {Requested} requested. Keeping the partial position — it clears the minimum — " +
                "and the stop will be armed against what actually filled.",
                entrySide, instrument.InstId, fill.FilledContracts, filledNotional, contracts);

        var slippageBps = signalPrice > 0m
            ? (double)((fill.AveragePrice - signalPrice) / signalPrice) * 10_000.0
            : 0.0;

        log.LogInformation(
            "[OKX] Maker entry filled: {Side} {InstId} at {Fill} (rested at {Limit}, signal was " +
            "{Signal}, {Slip:+0.0;-0.0} bps) fee=${Fee} — taker would have cost roughly " +
            "3 bps more on this leg.",
            entrySide, instrument.InstId, fill.AveragePrice, limitPrice, signalPrice,
            slippageBps, fill.FeeAbs);

        return (orderId, fill);
    }

    /// <summary>
    /// Close a fill too small to keep, with a reduce-only market order.
    ///
    /// Taker on purpose: this is cleanup of an unwanted position, and the thing that
    /// matters is that it is gone, not what it cost. Never throws — the caller is
    /// already on its way to reporting a refused entry, and replacing that message
    /// with a cleanup error would lose the reason. A failure here is logged as
    /// critical because it leaves a real, unmanaged position behind.
    /// </summary>
    private async Task UnwindDustAsync(
        OkxInstrument instrument, string entrySide, string? payloadPos,
        decimal contracts, CancellationToken ct)
    {
        try
        {
            var closeSide = entrySide == "buy" ? "sell" : "buy";

            var closeId = await trading.PlaceSwapMarketOrderAsync(
                instrument.InstId, closeSide, payloadPos, contracts, reduceOnly: true, ct);

            await trading.WaitForFillAsync(instrument.InstId, closeId, ct);

            log.LogInformation(
                "[OKX] Closed the {Contracts}-contract partial fill on {InstId} via order {OrdId}.",
                contracts, instrument.InstId, closeId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogCritical(ex,
                "[OKX] Could not close a {Contracts}-contract partial fill on {InstId}. There is a " +
                "real position open that the bot has no row for, and therefore no stop loss. Close " +
                "it manually.", contracts, instrument.InstId);
        }
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
    /// <summary>
    /// Rebuild the geometry from levels already stored on a trade, for the re-arming
    /// paths — a position whose OCO was never placed, and the residual left by a
    /// partial exit.
    ///
    /// Reconstructed rather than recomputed from a fresh volatility reading. Both call
    /// sites run minutes or hours after the entry, and measuring the market again there
    /// would move the stop under a position that already exists — the levels are a
    /// decision made at entry, and re-arming has to reproduce them, not revise them.
    ///
    /// Returns null for a trade that has no stored levels, which puts those rows back
    /// on the configured percentages. That is correct for them: they were opened under
    /// that policy.
    /// </summary>
    private static StopGeometry? GeometryFromTrade(BotTrade trade)
    {
        if (!trade.HasGeometry || trade.EntryPrice <= 0m) return null;

        var isLong = trade.Side != "SHORT";
        var entry  = trade.EntryPrice;

        var stopPct = isLong
            ? (entry - trade.StopPrice!.Value) / entry
            : (trade.StopPrice!.Value - entry) / entry;

        var targetPct = isLong
            ? (trade.TargetPrice!.Value - entry) / entry
            : (entry - trade.TargetPrice!.Value) / entry;

        // A stored pair that does not straddle the entry is corrupt rather than merely
        // unusual, and re-arming from it would place a stop on the wrong side of the
        // position. Falling back to the configured percentages is the safe reading.
        if (stopPct <= 0m || targetPct <= 0m) return null;

        return new StopGeometry(
            StopPct:     stopPct,
            TargetPct:   targetPct,
            StopPrice:   trade.StopPrice.Value,
            TargetPrice: trade.TargetPrice.Value,
            AtrPctUsed:  (double)(trade.AtrPctAtEntry ?? 0m),
            RewardRisk:  stopPct > 0m ? targetPct / stopPct : 0m,
            Basis:       "restored from the stored levels");
    }

    private async Task ArmProtectiveExitAsync(
        BotTrade trade, OkxInstrument instrument, string positionSide, decimal contracts,
        StopGeometry? geometry, CancellationToken ct)
    {
        var opts    = state.Options;
        var isLong  = positionSide == "long";
        var entry   = trade.EntryPrice;

        // The OCO must guard the levels the strategy chose, not whatever bot_config
        // currently says.
        //
        // This is the order that fires when the bot process is not running, so it is
        // the real stop — and pointing it at the configured percentages while the bot
        // watched volatility-scaled ones put two different stops on one position, with
        // the exchange holding the one that actually executes. Concretely: a 3.2%
        // target against a configured 2% take profit means the exchange closes every
        // winner a third early, and the 2:1 reward:risk the entry was accepted on
        // never exists. Rebased onto the real fill for the same reason the row is.
        var levels = geometry?.RebaseTo(entry, trade.Side);

        var takeProfitPct = levels?.TargetPct ?? opts.TakeProfitPct;
        var stopLossPct   = levels?.StopPct   ?? opts.StopLossPct;

        // Profit is up for a long and down for a short; the stop is the mirror.
        // Rounding always goes the direction that does not widen risk.
        var takeProfit = isLong
            ? OkxSizing.FloorToStep(entry * (1m + takeProfitPct), instrument.TickSize)
            : OkxSizing.CeilToStep (entry * (1m - takeProfitPct), instrument.TickSize);

        var stopLoss = isLong
            ? OkxSizing.CeilToStep (entry * (1m - stopLossPct), instrument.TickSize)
            : OkxSizing.FloorToStep(entry * (1m + stopLossPct), instrument.TickSize);

        var straddlesEntry = isLong
            ? takeProfit > entry && stopLoss < entry && stopLoss > 0m
            : takeProfit < entry && stopLoss > entry && takeProfit > 0m;

        if (!straddlesEntry)
        {
            log.LogCritical(
                "[OKX] Trade {Id} has no exchange-side stop: {Side} TP {Tp} / SL {Sl} do not straddle " +
                "the {Entry} entry price (tp={TpPct:P2}, sl={SlPct:P2}). " +
                "This leveraged position is protected only while the bot process is alive.",
                trade.Id, trade.Side, takeProfit, stopLoss, entry, takeProfitPct, stopLossPct);
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
                trade.Id, algoId, takeProfit, stopLoss, takeProfitPct, stopLossPct);
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

    /// <summary>
    /// Close the row for a position the exchange no longer has, using the
    /// exchange's own realised P&amp;L rather than a price-derived guess.
    ///
    /// The guess is not merely imprecise, it is biased: a liquidation removes the
    /// position at a price far past the stop, but by the time the next cycle
    /// notices, the market price used for the estimate has often recovered most of
    /// the way back. That understates the loss, and the daily-loss circuit breaker
    /// sums exactly these numbers — so the estimate would keep a bot trading
    /// through the run of losses the breaker exists to stop.
    /// </summary>
    private async Task SettleVanishedPositionAsync(
        BotTrade trade, OkxInstrument instrument, string positionSide, string reason,
        CancellationToken ct)
    {
        OkxPositionHistory? history = null;
        try
        {
            history = await trading.GetLastClosedPositionAsync(instrument.InstId, positionSide, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex,
                "[OKX] Could not read position history for trade {Id}; falling back to an estimate.",
                trade.Id);
        }

        if (history?.RealisedPnl is { } realised)
        {
            // The exchange figure already nets fees and funding, so it replaces the
            // computed P&L outright instead of being adjusted by it.
            trade.ExitPrice   = history.CloseAvgPx ?? trade.EntryPrice;
            trade.PnlUsd      = Math.Round(realised, 4);
            trade.PnlPct      = trade.NotionalUsd > 0m ? Math.Round(realised / trade.NotionalUsd, 6) : 0m;
            trade.Status      = "CLOSED";
            trade.ClosedAt    = DateTime.UtcNow;
            trade.CloseReason = history.WasLiquidated ? "LIQUIDATED" : $"{reason}_EXTERNAL";
            trade.ExitOrderId = null;
            trade.ExitAlgoId  = null;

            await repo.CloseTradeAsync(trade, ct);

            if (history.WasLiquidated)
                log.LogCritical(
                    "[OKX] Trade {Id} was LIQUIDATED on {InstId} at {Price}. Realised P&L {Pnl} USD " +
                    "(exchange figure, includes the liquidation fee and funding). Review leverage and " +
                    "stop distance before the bot opens another position.",
                    trade.Id, instrument.InstId, trade.ExitPrice, trade.PnlUsd);
            else
                log.LogWarning(
                    "[OKX] Trade {Id} was closed outside the bot on {InstId} at {Price}. " +
                    "Realised P&L {Pnl} USD (exchange figure).",
                    trade.Id, instrument.InstId, trade.ExitPrice, trade.PnlUsd);

            return;
        }

        // No history to read. The row still has to close or it is retried forever,
        // but the number is a guess and the close reason says so — nothing should
        // read _UNCONFIRMED as a settled result.
        log.LogError(
            "[OKX] Trade {Id} has no position on {InstId} and no history entry to settle it from. " +
            "Closing at the entry price with zero P&L so it stops being retried; the real result is " +
            "not recorded. Reconcile this row against the exchange by hand.",
            trade.Id, instrument.InstId);

        trade.ExitPrice   = trade.EntryPrice;
        trade.PnlUsd      = 0m;
        trade.PnlPct      = 0m;
        trade.Status      = "CLOSED";
        trade.ClosedAt    = DateTime.UtcNow;
        trade.CloseReason = $"{reason}_UNCONFIRMED";
        trade.ExitAlgoId  = null;

        await repo.CloseTradeAsync(trade, ct);
    }

    // ── Reconciliation ────────────────────────────────────────────────────────

    public async Task<BotTrade?> ReconcileAsync(BotTrade trade, CancellationToken ct)
    {
        if (okxOptions.DescribeRefusal() is not null) return null;

        var algoId = trade.ExitAlgoId;

        // ── No exchange-side guard on a live position: put one back ──
        //
        // Arming the OCO is best-effort at entry, because the fill has already
        // happened and unwinding it over a transient API error would be worse. That
        // made a failed arm permanent: nothing re-tried it, and this method used to
        // return immediately on a null algoId, so the gap was never noticed either.
        // A leveraged position with no stop at the exchange is the one state worth
        // spending a request per cycle to get out of.
        if (string.IsNullOrEmpty(algoId))
        {
            var positionSide = trade.Side == "SHORT" ? "short" : "long";
            var instr        = await instruments.GetSwapAsync(trade.Symbol, ct);
            var live         = await trading.GetPositionAsync(instr.InstId, positionSide, ct);

            if (live is null)
            {
                await SettleVanishedPositionAsync(trade, instr, positionSide, "RECONCILE", ct);
                return trade;
            }

            log.LogWarning(
                "[OKX] Trade {Id} is open with no exchange-side stop — re-arming its OCO.", trade.Id);

            await ArmProtectiveExitAsync(
                trade, instr, positionSide, live.AbsContracts, GeometryFromTrade(trade), ct);
            return null;
        }

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
        await ReconcileRealisedPnlAsync(
            trade, instrument, trade.Side == "SHORT" ? "short" : "long", ct);

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
        var position = await trading.GetPositionAsync(instrument.InstId, positionSide, ct);

        if (position is null)
        {
            await SettleVanishedPositionAsync(trade, instrument, positionSide, reason, ct);
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

        // ── A partial close leaves the trade open, not closed ──
        //
        // Marking the row CLOSED on a partial fill abandons the remainder: it is
        // still a leveraged position, its OCO was cancelled above, and nothing in
        // state or the database would refer to it again. Instead the trade keeps
        // what it still holds and stays OPEN, so the next cycle tries again — and
        // the protective order is re-armed for the remainder in the meantime,
        // because that remainder may be carried for another 30 seconds or longer.
        if (fill.FilledContracts < contracts)
        {
            var residualBase = trade.Quantity - closedBase;

            log.LogWarning(
                "[OKX] Exit for trade {Id} closed {Closed} of {Asked} contracts. Keeping the trade " +
                "OPEN with the remaining {Residual} {Base} and retrying next cycle.",
                trade.Id, fill.FilledContracts, contracts, residualBase, instrument.BaseCcy);

            trade.Quantity = residualBase;
            trade.FeeUsd   = Math.Round((trade.FeeUsd ?? 0m) + fill.FeeAbs, 8);
            await repo.UpdateOpenQuantityAsync(trade.Id, residualBase, trade.FeeUsd.Value, ct);

            await ArmProtectiveExitAsync(
                trade, instrument, positionSide, contracts - fill.FilledContracts,
                GeometryFromTrade(trade), ct);

            return trade;   // Status is still OPEN — the caller must not record a close.
        }

        ApplyExit(trade, closedBase, fill.AveragePrice, fill.FeeAbs, orderId, reason);
        await ReconcileRealisedPnlAsync(trade, instrument, positionSide, ct);

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
    /// <summary>
    /// Replace a derived P&L with the exchange's own realised figure when it can be
    /// read.
    ///
    /// ApplyExit computes P&L from entry price, exit price and both legs' fees, and
    /// that arithmetic is right as far as it goes — but it cannot see funding. OKX
    /// charges or pays funding every eight hours against the position and settles it
    /// in the account bills rather than on either order, so a derived figure is short
    /// by whatever funding accrued.
    ///
    /// Measured against positions-history for the three live trades, rather than
    /// estimated:
    ///
    ///     trade 14  held 5.7h   derived -0.1258   exchange -0.1267872   funding -0.0009832
    ///     trade 15  held 4.7h   derived +0.1681   exchange +0.1681183   funding  0
    ///     trade 16  held 0.2h   derived -0.4935   exchange -0.4934884   funding  0
    ///
    /// So one trade in three crossed a funding boundary, and the whole gap on that
    /// one was the funding: -0.119 pnl, -0.006804 fee, -0.0009832 funding sums to the
    /// exchange figure exactly. That is 1.0 bps on a $9.81 notional — smaller than the
    /// 0.75-3 bps I guessed before measuring, and worth having anyway: it is
    /// one-directional (a long paying funding reads as a better trade than it was),
    /// the live R-multiples are the only evidence being accumulated, and the measured
    /// break-even margin is itself single-digit bps.
    ///
    /// The other two trades also confirm the derived arithmetic to within $0.00002,
    /// which is worth knowing separately.
    ///
    /// SettleVanishedPositionAsync already reasons this way for liquidations and
    /// external closes. This applies the same rule to the ordinary path, where all
    /// three live trades so far have closed.
    ///
    /// Best-effort by design: a failed read leaves the derived number in place and
    /// says so. Closing the row matters more than the last basis point, and a trade
    /// that will not close is retried forever.
    /// </summary>
    private async Task ReconcileRealisedPnlAsync(
        BotTrade trade, OkxInstrument instrument, string positionSide, CancellationToken ct)
    {
        OkxPositionHistory? history = null;
        try
        {
            history = await trading.GetLastClosedPositionAsync(instrument.InstId, positionSide, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex,
                "[OKX] Trade {Id}: could not read position history, so P&L stays derived from price " +
                "and fees and EXCLUDES funding. The recorded result is optimistic by whatever funding " +
                "accrued over {Hours:F1}h.",
                trade.Id,
                (DateTime.UtcNow - trade.OpenedAt).TotalHours);
            return;
        }

        if (history?.RealisedPnl is not { } realised)
        {
            log.LogWarning(
                "[OKX] Trade {Id}: no position-history entry on {InstId}, so P&L stays derived and " +
                "EXCLUDES funding.",
                trade.Id, instrument.InstId);
            return;
        }

        var derived = trade.PnlUsd ?? 0m;
        var delta   = realised - derived;

        trade.PnlUsd = Math.Round(realised, 4);
        trade.PnlPct = trade.NotionalUsd > 0m
            ? Math.Round(realised / trade.NotionalUsd, 6)
            : 0m;

        // Logged with the gap, because the gap IS the funding plus any fee rounding,
        // and it is the number that was previously invisible.
        log.LogInformation(
            "[OKX] Trade {Id} P&L reconciled to the exchange figure: {Realised:+0.0000;-0.0000} USD " +
            "against {Derived:+0.0000;-0.0000} derived, a {Delta:+0.0000;-0.0000} difference that is " +
            "funding and fee rounding the derived figure could not see.",
            trade.Id, realised, derived, delta);
    }

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
