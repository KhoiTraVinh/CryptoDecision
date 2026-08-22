using System.Text.Json.Nodes;
using CryptoDecision.BotService.Bot;
using CryptoDecision.BotService.Domain;
using CryptoDecision.BotService.Infrastructure;
using CryptoDecision.Shared.Bot;

namespace CryptoDecision.BotService.Agent;

// ── Read-only tools ───────────────────────────────────────────────────────────

/// <summary>Current market state: order flow, whale pressure, daily features, AI prediction.</summary>
public sealed class GetMarketSnapshotTool(
    IMomentumRepository   momentumRepo,
    IFeatureRepository    featureRepo,
    IPredictionRepository predictionRepo,
    AgentContext          context) : ITradingTool
{
    public string Name => "get_market_snapshot";

    public string Description =>
        "Get the current market state for the symbol being traded: multi-timeframe order " +
        "flow (5m/15m/1h buy vs sell pressure), whale activity, 24h return, volatility, and " +
        "the latest AI price-direction prediction. Call this first, before deciding anything.";

    public JsonObject ParameterSchema => ToolSchema.Empty();

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var symbol = context.Options.Symbol;

            var mtf     = await momentumRepo.GetMultiTimeframeAsync(symbol, ct);
            var feature = await featureRepo.GetTodayAsync(symbol, ct);

            PredictionSnapshot? prediction = null;
            try { prediction = await predictionRepo.GetLatestAsync(symbol, ct); }
            catch { /* prediction is optional context, never fatal */ }

            // Described in words, not just ratios. The same lesson the prediction
            // prompt taught: a small model reads "18% buy-side" as buying unless
            // the dominant side is stated outright.
            string Lean(decimal ratio) => ratio switch
            {
                >= 0.58m => "strongly BUYING",
                >= 0.52m => "mildly buying",
                <= 0.42m => "strongly SELLING",
                <= 0.48m => "mildly selling",
                _        => "balanced",
            };

            string Frame(string label, TimeframeMomentum f)
            {
                if (f.TotalTrades == 0) return $"{label}: no trades";

                // Trade count and notional can genuinely point opposite ways — many
                // small sells against a few large buys. That is worth flagging.
                //
                // The test compares *direction*, not the size of the numeric gap.
                // Gap alone produced "MIXED — strongly BUYING by trade count but
                // strongly BUYING by volume", which is self-contradictory nonsense:
                // 78% and 91% are 13 points apart but both plainly mean buying.
                static int Direction(decimal ratio) =>
                    ratio >= 0.52m ? 1 : ratio <= 0.48m ? -1 : 0;

                var countDir = Direction(f.BuyRatio);
                var volDir   = Direction(f.VolBuyRatio);

                var verdict = countDir != 0 && volDir != 0 && countDir != volDir
                    ? $"MIXED — {Lean(f.BuyRatio)} by trade count but {Lean(f.VolBuyRatio)} by volume, " +
                      (volDir > 0
                          ? "meaning the buys are fewer but much larger"
                          : "meaning the sells are fewer but much larger")
                    // Same direction (or one side neutral): report the volume view,
                    // since notional is what actually moves price.
                    : Lean(f.VolBuyRatio);

                // Each figure carries its own label. The previous phrasing —
                // "39% of trades and 43% of volume were aggressive buys" — required
                // carrying "were aggressive buys" across a conjunction, and the model
                // reliably read the second number as the *sell* share instead,
                // inverting the direction of the tape it was reasoning about.
                return $"{label}: {f.TotalTrades} trades, {verdict}. " +
                       $"buy share: {f.BuyRatio:P0} of trade count, {f.VolBuyRatio:P0} of volume " +
                       $"(so sell share is {1m - f.BuyRatio:P0} and {1m - f.VolBuyRatio:P0}). " +
                       $"whales: {f.WhaleBuyCount} buy, {f.WhaleSellCount} sell";
            }

            var lines = new List<string>
            {
                $"symbol: {symbol}",
                $"current_price: {context.CurrentPrice:F2} USDT",
                Frame("flow_5m",  mtf.M5),
                Frame("flow_15m", mtf.M15),
                Frame("flow_1h",  mtf.M1h),
            };

            if (feature is not null)
                lines.Add(
                    $"daily: 24h return {feature.Return24h:+0.00;-0.00}%, volatility {feature.Volatility:F2}%, " +
                    $"volume change {feature.VolumeChange:+0.00;-0.00}%, {feature.WhaleCount} whale trades");

            lines.Add(prediction is not null
                ? $"ai_prediction: {prediction.Direction} at {prediction.Confidence:P0} confidence " +
                  $"({prediction.ModelVersion}) — {prediction.Rationale}"
                : "ai_prediction: unavailable");

            return ToolResult.Ok(string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"could not read market snapshot: {ex.Message}");
        }
    }
}

/// <summary>Open positions with live unrealised P&amp;L.</summary>
public sealed class GetOpenPositionsTool(AgentContext context) : ITradingTool
{
    public string Name => "get_open_positions";

    public string Description =>
        "List every position currently open, with its side, entry price, size and " +
        "unrealised profit or loss at the current price. Call this before opening a new " +
        "position so you know what exposure already exists.";

    public JsonObject ParameterSchema => ToolSchema.Empty();

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var open = context.OpenTrades;
        if (open.Count == 0)
            return Task.FromResult(ToolResult.Ok("No open positions."));

        var price = context.CurrentPrice;
        var lines = open.Select(t =>
        {
            var raw       = (price - t.EntryPrice) / t.EntryPrice;
            var changePct = t.Side == "SHORT" ? -raw : raw;
            var pnlUsd    = changePct * t.NotionalUsd;
            var heldMin   = (DateTime.UtcNow - t.OpenedAt).TotalMinutes;

            return $"id={t.Id} {t.Side} entry={t.EntryPrice:F2} notional=${t.NotionalUsd:F2} " +
                   $"unrealised={changePct:P2} (${pnlUsd:F2}) held={heldMin:F0}min";
        });

        return Task.FromResult(ToolResult.Ok(string.Join("\n", lines)));
    }
}

/// <summary>Capital, realised P&amp;L, exposure and how much risk budget is left.</summary>
public sealed class GetAccountStateTool(BotRepository repo, AgentContext context) : ITradingTool
{
    public string Name => "get_account_state";

    public string Description =>
        "Get account capital, today's realised profit or loss, how much capital is already " +
        "committed to open positions, and how much risk budget remains before the daily loss " +
        "limit stops trading. Call this before opening a position.";

    public JsonObject ParameterSchema => ToolSchema.Empty();

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var opts     = context.Options;
            var todayPnl = await repo.GetTodayPnlAsync(
                opts.Symbol, opts.PaperMode ? "PAPER" : "LIVE", ct);
            var exposure = context.OpenTrades.Sum(t => t.NotionalUsd);
            var limitUsd = opts.CapitalUsd * opts.DailyLossLimitPct;
            var headroom = limitUsd + todayPnl;   // todayPnl is negative when losing

            var profile = RiskEngine.Expectancy(opts.TakeProfitPct, opts.StopLossPct);

            return ToolResult.Ok(string.Join("\n",
                $"capital: ${opts.CapitalUsd:F2}",
                $"today_realised_pnl: ${todayPnl:F2}",
                $"open_exposure: ${exposure:F2} across {context.OpenTrades.Count} positions",
                $"position_size_per_trade: {opts.PositionPctOfCapital:P0} of capital",
                $"max_positions: {opts.MaxOpenTradesPerStrategy}",
                $"daily_loss_limit: ${limitUsd:F2} (${headroom:F2} of headroom left today)",
                $"take_profit: {opts.TakeProfitPct:P2}, stop_loss: {opts.StopLossPct:P2}",
                $"breakeven_win_rate_required: {profile.BreakevenWinRate:P1} " +
                $"(reward:risk {profile.RewardRiskRatio:F2}:1 after fees)"));
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"could not read account state: {ex.Message}");
        }
    }
}

// ── Order tools (risk-gated) ──────────────────────────────────────────────────

/// <summary>
/// Open a position — the one tool that commits capital, and therefore the one the
/// risk engine guards.
///
/// The model can ask for anything; it gets what RiskEngine allows. Every refusal
/// is returned as readable text so the agent can reason about the constraint
/// instead of retrying the same rejected order. There is deliberately no argument
/// for position size: sizing is the engine's decision, not the model's, so a
/// confident-sounding model cannot talk itself into a larger bet.
/// </summary>
public sealed class OpenPositionTool(
    IOrderEngine  orderEngine,
    BotRepository repo,
    AgentContext  context,
    ILogger<OpenPositionTool> log) : ITradingTool
{
    public string Name => "open_position";

    public string Description =>
        "Open a new position. Only call this when the evidence from get_market_snapshot " +
        "genuinely supports it — doing nothing is a valid and often correct outcome. " +
        "Position size is determined by the risk engine, not by you. The order may be " +
        "refused if it would breach risk limits; if so, read the reason and do not retry " +
        "the same order.";

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("side",       ToolSchema.Enum("LONG if you expect the price to rise, SHORT if you expect it to fall", "LONG", "SHORT")),
        ("confidence", ToolSchema.Number("How confident you are, from 0.0 to 1.0. Be honest: this scales the position size.", 0.0, 1.0)),
        ("reason",     ToolSchema.String("One or two sentences citing the specific figures that justify this entry."))
    );

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var opts = context.Options;

        var side       = (ToolSchema.GetString(arguments, "side") ?? "").Trim().ToUpperInvariant();
        var confidence = Math.Clamp(ToolSchema.GetDecimal(arguments, "confidence", 0.5m), 0m, 1m);
        var reason     = ToolSchema.GetString(arguments, "reason") ?? "(no reason given)";

        if (side is not ("LONG" or "SHORT"))
            return ToolResult.Refused($"side must be LONG or SHORT, got '{side}'.");

        // ── Gate 0: can the venue even fill this direction? ──
        // Refused as a constraint rather than surfaced as an error, so the model
        // reads a reason it can reason about instead of retrying a SHORT that the
        // exchange will reject every time.
        if (side == "SHORT" && !orderEngine.SupportsShort(opts))
            return ToolResult.Refused(
                $"{opts.Exchange} is a spot account in this mode and cannot open short positions. " +
                "Only LONG entries are available — do not propose SHORT again this session.");

        // ── Gate 1: configuration expectancy ──
        // If the configured TP/SL cannot profit, no entry should be taken at all.
        var assessment = RiskEngine.Validate(opts);
        if (assessment.HasCritical)
        {
            var codes = string.Join(", ", assessment.Critical.Select(f => f.Code));
            return ToolResult.Refused(
                $"the bot's risk configuration is invalid ({codes}). No position may be opened " +
                "until an operator fixes it.");
        }

        // ── Gate 2: position count ──
        if (context.OpenTrades.Count >= opts.MaxOpenTradesPerStrategy)
            return ToolResult.Refused(
                $"already holding {context.OpenTrades.Count} of a maximum " +
                $"{opts.MaxOpenTradesPerStrategy} positions. Close one before opening another.");

        // ── Gate 3: aggregate exposure ──
        var exposure    = context.OpenTrades.Sum(t => t.NotionalUsd);
        var newNotional = opts.CapitalUsd * opts.PositionPctOfCapital;
        if (exposure + newNotional > opts.CapitalUsd)
            return ToolResult.Refused(
                $"opening this would commit ${exposure + newNotional:F2} against ${opts.CapitalUsd:F2} " +
                "of capital. Exposure limit reached.");

        // ── Gate 4: circuit breakers on realised history ──
        try
        {
            // Same narrowing as the deterministic loop: the breakers must judge this
            // instrument in this execution mode, not whatever else is in the table.
            var mode     = opts.PaperMode ? "PAPER" : "LIVE";
            var todayPnl = await repo.GetTodayPnlAsync(opts.Symbol, mode, ct);
            var closed   = (await repo.GetRecentTradesAsync(500, ct))
                .Where(t => t.Status is "CLOSED" or "STOPPED")
                .Where(t => string.Equals(t.Symbol, opts.Symbol, StringComparison.OrdinalIgnoreCase))
                .Where(t => string.Equals(t.Mode, mode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var breach = RiskEngine.CheckCircuitBreakers(closed, opts, todayPnl);
            if (breach is not null)
                return ToolResult.Refused($"{breach.Code} — {breach.Message} Trading is halted for now.");
        }
        catch (Exception ex)
        {
            // Fail closed: if the risk state cannot be established, do not trade.
            log.LogWarning(ex, "[Agent] Risk check failed; refusing entry");
            return ToolResult.Refused("could not verify risk limits, so the order is refused.");
        }

        // ── Gate 5: cooldown ──
        if (context.LastEntryAt is { } last)
        {
            var elapsed = (DateTime.UtcNow - last).TotalSeconds;
            if (elapsed < opts.CooldownSeconds)
                return ToolResult.Refused(
                    $"cooldown active: {opts.CooldownSeconds - elapsed:F0}s remaining before another entry.");
        }

        // ── Cleared. Place it. ──
        try
        {
            var trade = await orderEngine.OpenPositionAsync(
                opts.Symbol, AgentContext.AgentStrategyName, side, context.CurrentPrice,
                opts.CapitalUsd, opts.PositionPctOfCapital, ct, confidence, opts.UseAiSizing);

            context.RecordEntry(trade, reason);

            log.LogInformation(
                "[Agent] OPENED {Side} id={Id} @ {Price} confidence={Conf:P0} — {Reason}",
                side, trade.Id, context.CurrentPrice, confidence, reason);

            return ToolResult.Ok(
                $"Opened {side} position id={trade.Id} at {trade.EntryPrice:F2}, " +
                $"notional ${trade.NotionalUsd:F2}. Stop loss and take profit are managed " +
                "automatically. You are done unless you want to review other positions.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Agent] Order placement failed");
            return ToolResult.Failed($"order placement failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Close a position early.
///
/// Note this is discretionary only. Stop loss, take profit, trailing and breakeven
/// exits are evaluated deterministically every cycle in TradingBotService and never
/// wait on the model — a 7B taking 40+ seconds to answer must not sit between a
/// position and its stop.
/// </summary>
public sealed class ClosePositionTool(
    IOrderEngine orderEngine,
    AgentContext context,
    ILogger<ClosePositionTool> log) : ITradingTool
{
    public string Name => "close_position";

    public string Description =>
        "Close an open position early, before its automatic take-profit or stop-loss " +
        "triggers. Use this only when the market has clearly turned against the original " +
        "reason for the trade. Stop-loss and take-profit are already handled automatically, " +
        "so you do not need to close positions to protect them.";

    public JsonObject ParameterSchema => ToolSchema.Object(
        ("trade_id", ToolSchema.Integer("The id of the position to close, from get_open_positions")),
        ("reason",   ToolSchema.String("Why this position should be closed now."))
    );

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken ct)
    {
        var tradeId = ToolSchema.GetLong(arguments, "trade_id", -1);
        var reason  = ToolSchema.GetString(arguments, "reason") ?? "agent discretionary close";

        var trade = context.OpenTrades.FirstOrDefault(t => t.Id == tradeId);
        if (trade is null)
            return ToolResult.Refused(
                $"no open position with id={tradeId}. Call get_open_positions for the current list.");

        try
        {
            var closed = await orderEngine.CloseTradeAsync(trade, context.CurrentPrice, "AGENT_CLOSE", ct);
            context.RecordClose(closed);

            log.LogInformation(
                "[Agent] CLOSED id={Id} pnl={Pnl:F2} — {Reason}", tradeId, closed.PnlUsd ?? 0m, reason);

            return ToolResult.Ok(
                $"Closed position id={tradeId} at {context.CurrentPrice:F2}. " +
                $"Realised P&L ${closed.PnlUsd ?? 0m:F2} ({closed.PnlPct ?? 0m:P2}).");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Agent] Close failed");
            return ToolResult.Failed($"could not close position: {ex.Message}");
        }
    }
}
