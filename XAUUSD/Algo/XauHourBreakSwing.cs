// =====================================================================
//  XAU Hour-Break Swing — FTMO-safe rebuild of the reference bot
//  "SmartTradeOnHoursBreak" (label "TradeOn4HDrop"), whose 2025-07 → 2026-10
//  backtest is in Backtests\beklenen-2026-10-17 ... .xlsx
//
//  Reference entry rule (checked against m1 prices: 92 of its 111 entries):
//    price trades ABOVE the high of the previous few hours → SELL (fade)
//    price trades BELOW the low  of the previous few hours → BUY  (fade)
//  The reference has no real stop and holds losers over weekends until they
//  recover — that part is not allowed here and is where most of its profit came from.
//
//  This bot:
//    - fade the break of the previous 180-minute range
//    - only in the direction of the H1 EMA(50): buy dips above it, sell rallies below it
//    - one position at a time, hard $60 stop, $120 target
//    - NOTHING held over the weekend: no entries late Friday, flat at Friday Flat Time
//      or 30 min before the session close, whichever comes first (holiday early closes
//      end Friday trading as early as 19:00 CE(S)T) — hard rule, no switch to disable it
//
//  VALIDATION STATUS (2026-10-18): cTrader tick backtest 2025-07..2026-10 = 175 trades,
//  +3.3R — break-even after costs. An earlier "+19R" figure came from a bug in the offline
//  simulator and is NOT valid. Under a hard stop + no weekend holding, no parameter set of
//  this range-break-fade family was robust across train/test periods.
//    - FTMO equity guards: daily loss, overall loss, order-ops cap, news blackout
//    - distances in PRICE ($), not pips: XAUUSD pip size differs between brokers
//
//  Attach to XAUUSD (any chart, h1 recommended). Backtest with tick data.
// =====================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.CentralEuropeanStandardTime, AccessRights = AccessRights.None)]
    public class XauHourBreakSwing : Robot
    {
        // ---------------- Signal ----------------
        [Parameter("Signal Mode", DefaultValue = SignalModeType.Fade, Group = "Signal")]
        public SignalModeType SignalMode { get; set; }

        [Parameter("Range Window (minutes)", DefaultValue = 180, MinValue = 15, MaxValue = 1440, Group = "Signal")]
        public int RangeWindowMinutes { get; set; }         // high/low of the previous N minutes (closed m1 bars)

        [Parameter("Min Range Size ($)", DefaultValue = 0.0, MinValue = 0, Group = "Signal")]
        public double MinRangeUsd { get; set; }

        [Parameter("Use Trend Filter", DefaultValue = true, Group = "Signal")]
        public bool UseTrendFilter { get; set; }            // trade only in the direction of price vs EMA

        [Parameter("Trend Timeframe", DefaultValue = "Hour", Group = "Signal")]
        public TimeFrame TrendTimeFrame { get; set; }

        [Parameter("Trend EMA Period", DefaultValue = 50, MinValue = 5, Group = "Signal")]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Max Open Positions", DefaultValue = 1, MinValue = 1, MaxValue = 5, Group = "Signal")]
        public int MaxOpenPositions { get; set; }

        [Parameter("Cooldown After Exit (minutes)", DefaultValue = 0, MinValue = 0, Group = "Signal")]
        public int CooldownMinutes { get; set; }

        public enum SignalModeType { Fade, Breakout }

        // ---------------- Session (CE(S)T) ----------------
        [Parameter("Entry Window Start", DefaultValue = "01:00", Group = "Session")]
        public string WindowStartStr { get; set; }

        [Parameter("Entry Window End", DefaultValue = "22:00", Group = "Session")]
        public string WindowEndStr { get; set; }

        [Parameter("Monday First Entry", DefaultValue = "02:00", Group = "Session")]
        public string MondayStartStr { get; set; }          // skip the Monday-open gap / wide spreads

        [Parameter("Friday Last Entry", DefaultValue = "16:00", Group = "Session")]
        public string FridayLastEntryStr { get; set; }      // a swing opened later has no room before the flatten

        [Parameter("Friday Flat Time", DefaultValue = "18:30", Group = "Session")]
        public string FridayFlatStr { get; set; }           // US-holiday Fridays close as early as 19:00 CE(S)T

        [Parameter("Max Spread ($)", DefaultValue = 0.8, MinValue = 0.05, Group = "Session")]
        public double MaxSpreadUsd { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 3, MinValue = 1, Group = "Session")]
        public int MaxTradesPerDay { get; set; }

        // ---------------- Risk / Exits ----------------
        [Parameter("Risk Per Trade (%)", DefaultValue = 1.0, MinValue = 0.05, MaxValue = 3.0, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Stop Loss ($)", DefaultValue = 60.0, MinValue = 5.0, Group = "Risk")]
        public double StopUsd { get; set; }

        [Parameter("Take Profit ($)", DefaultValue = 120.0, MinValue = 5.0, Group = "Risk")]
        public double TakeProfitUsd { get; set; }

        [Parameter("Break-even Trigger ($, 0=off)", DefaultValue = 0.0, MinValue = 0, Group = "Risk")]
        public double BeTriggerUsd { get; set; }

        [Parameter("Break-even Offset ($)", DefaultValue = 0.5, MinValue = 0, Group = "Risk")]
        public double BeOffsetUsd { get; set; }

        [Parameter("Max Hold (hours, 0=off)", DefaultValue = 0, MinValue = 0, Group = "Risk")]
        public int MaxHoldHours { get; set; }

        // ---------------- FTMO Guardrails (% of initial balance) ----------------
        [Parameter("Initial Balance (0 = use account balance)", DefaultValue = 0, MinValue = 0, Group = "FTMO")]
        public double InitialBalanceOverride { get; set; }

        [Parameter("Daily Loss Cutoff (% of initial)", DefaultValue = 4.0, MinValue = 0.1, Group = "FTMO")]
        public double DailyLossCutoffPct { get; set; }      // FTMO limit is 5% — keep a buffer for slippage

        [Parameter("Overall Loss Cutoff (% of initial)", DefaultValue = 9.0, MinValue = 0.1, Group = "FTMO")]
        public double OverallLossCutoffPct { get; set; }    // FTMO limit is 10%

        [Parameter("Max Consecutive Losses Today", DefaultValue = 2, MinValue = 1, Group = "FTMO")]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Max Order Ops Per Day", DefaultValue = 200, MinValue = 10, Group = "FTMO")]
        public int MaxOrderOpsPerDay { get; set; }

        [Parameter("News Blackout (HH:mm-HH:mm;...)", DefaultValue = "", Group = "FTMO")]
        public string NewsBlackoutStr { get; set; }

        [Parameter("News Flatten Buffer (mins before)", DefaultValue = 5, MinValue = 2, Group = "FTMO")]
        public int NewsFlattenMinsBefore { get; set; }

        // ---------------- State ----------------
        private const string Label = "XauHourBreak";

        private Bars _m1, _trendBars;
        private ExponentialMovingAverage _trendEma;

        // range of the previous N closed m1 bars — recomputed once per new m1 bar, not per tick
        private int _rangeBarCount = -1;
        private bool _rangeValid;
        private double _rangeHigh, _rangeLow;

        private TimeSpan _windowStart, _windowEnd, _mondayStart, _fridayLastEntry, _fridayFlat;
        private (TimeSpan From, TimeSpan To)[] _newsWindows;

        private DateTime _currentDay = DateTime.MinValue;
        private DateTime _lastExit = DateTime.MinValue;
        private double _initialBalance, _dailyLossCutoff, _equityHardFloor, _dayStartBalance;
        private int _tradesToday, _orderOpsToday, _consecutiveLossesToday;
        private bool _tradingHaltedToday;
        private string _haltReason = "";

        private bool IsBotPosition(Position p) => p.Label == Label && p.SymbolName == SymbolName;

        // =================================================================
        protected override void OnStart()
        {
            _m1 = MarketData.GetBars(TimeFrame.Minute);

            if (UseTrendFilter)
            {
                _trendBars = MarketData.GetBars(TrendTimeFrame);
                _trendEma = Indicators.ExponentialMovingAverage(_trendBars.ClosePrices, TrendEmaPeriod);
            }

            _windowStart = ParseTime(WindowStartStr, new TimeSpan(1, 0, 0));
            _windowEnd = ParseTime(WindowEndStr, new TimeSpan(22, 0, 0));
            _mondayStart = ParseTime(MondayStartStr, new TimeSpan(2, 0, 0));
            _fridayLastEntry = ParseTime(FridayLastEntryStr, new TimeSpan(16, 0, 0));
            _fridayFlat = ParseTime(FridayFlatStr, new TimeSpan(18, 30, 0));
            _newsWindows = ParseNewsWindows(NewsBlackoutStr);

            _initialBalance = InitialBalanceOverride > 0 ? InitialBalanceOverride : Account.Balance;
            _dailyLossCutoff = _initialBalance * DailyLossCutoffPct / 100.0;
            _equityHardFloor = _initialBalance * (1.0 - OverallLossCutoffPct / 100.0);

            if (InitialBalanceOverride <= 0)
                Print($"ATTENTION: 'Initial Balance' is 0. Using current Account.Balance ({Account.Balance:F0}) as FTMO baseline.");

            if (Account.Equity <= _equityHardFloor)
            {
                Print($"CONFIG ERROR: equity {Account.Equity:F0} is already below the overall-loss floor {_equityHardFloor:F0} " +
                      $"({OverallLossCutoffPct:F1}% of Initial Balance {_initialBalance:F0}). " +
                      "Set 'Initial Balance' to 0 or to the account's real starting balance (backtest: the starting capital).");
                Stop();
                return;
            }

            var riskUsd = Account.Balance * RiskPercent / 100.0;
            var plannedUnits = VolumeForRisk(StopUsd / Symbol.PipSize);
            Print($"{SignalMode} the {RangeWindowMinutes}-min range break | trend filter {(UseTrendFilter ? $"{TrendTimeFrame} EMA{TrendEmaPeriod}" : "off")} | " +
                  $"SL ${StopUsd:F0} TP ${TakeProfitUsd:F0} | risk {RiskPercent:F2}% = {riskUsd:F0} → {plannedUnits} units " +
                  $"(real risk {plannedUnits * StopUsd:F0}) | daily cutoff {_dailyLossCutoff:F0}, equity floor {_equityHardFloor:F0}");
            if (plannedUnits < Symbol.VolumeInUnitsMin)
                Print($"WARNING: risk {riskUsd:F0} is too small for a ${StopUsd:F0} stop at the minimum volume — no trades will be taken. Raise Risk %.");

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;
            StartNewDay();
        }

        protected override void OnTick()
        {
            RollDayIfNeeded();

            var equity = Account.Equity;
            if (equity <= _equityHardFloor)
            {
                HaltDay($"EQUITY FLOOR: equity {equity:F0} <= hard floor {_equityHardFloor:F0}");
                Stop();
                return;
            }
            if (_dayStartBalance - equity >= _dailyLossCutoff)
                HaltDay($"DAILY CUTOFF: day drawdown {_dayStartBalance - equity:F0} >= {_dailyLossCutoff:F0}");

            var now = Time.TimeOfDay;

            if (IsWeekendFlatWindow() && HasOpenPositions())
                FlattenAll("weekend flatten");

            if (IsInsideNewsGuard(now, forFlattening: true) && HasOpenPositions())
                FlattenAll("news blackout buffer");

            ManagePositions();
            TryEntry();
        }

        // =================================================================
        //  Entry: price trades through the previous N-minute high / low
        // =================================================================
        private void UpdateRange()
        {
            if (_m1.Count == _rangeBarCount) return;
            _rangeBarCount = _m1.Count;
            _rangeValid = false;

            var last = _m1.Count - 1;                        // forming bar — excluded
            if (last < 2) return;
            var now = _m1.OpenTimes[last];
            // skip right after a session gap (weekend / daily break)
            if ((now - _m1.OpenTimes[last - 1]).TotalMinutes >= 60) return;

            var from = now.AddMinutes(-RangeWindowMinutes);
            double hi = double.MinValue, lo = double.MaxValue;
            var k = last - 1;
            for (; k >= 0 && _m1.OpenTimes[k] >= from; k--)
            {
                hi = Math.Max(hi, _m1.HighPrices[k]);
                lo = Math.Min(lo, _m1.LowPrices[k]);
            }
            var oldest = k + 1;
            if (oldest > last - 1 || (now - _m1.OpenTimes[oldest]).TotalMinutes < RangeWindowMinutes * 0.9) return; // window not filled

            _rangeHigh = hi; _rangeLow = lo; _rangeValid = true;
        }

        private void TryEntry()
        {
            UpdateRange();
            if (!_rangeValid) return;
            if (_tradingHaltedToday || _tradesToday >= MaxTradesPerDay) return;
            if (!IsEntryTimeAllowed() || IsInsideNewsGuard(Time.TimeOfDay)) return;
            if (Positions.Count(IsBotPosition) >= MaxOpenPositions) return;
            if ((Time - _lastExit).TotalMinutes < CooldownMinutes) return;
            if (Symbol.Spread > MaxSpreadUsd) return;
            if (_rangeHigh - _rangeLow < MinRangeUsd) return;

            var brokeUp = Symbol.Bid > _rangeHigh;
            var brokeDown = Symbol.Bid < _rangeLow;
            if (brokeUp == brokeDown) return;

            var side = SignalMode == SignalModeType.Fade
                ? (brokeDown ? TradeType.Buy : TradeType.Sell)
                : (brokeDown ? TradeType.Sell : TradeType.Buy);

            if (UseTrendFilter && GetTrendSide() != side) return;

            var stopPips = StopUsd / Symbol.PipSize;
            var tpPips = TakeProfitUsd / Symbol.PipSize;
            var volume = VolumeForRisk(stopPips);
            if (volume < Symbol.VolumeInUnitsMin) return;    // warned once in OnStart

            if (!CountOrderOp()) return;

            var r = ExecuteMarketOrder(side, SymbolName, volume, Label, stopPips, tpPips);
            Print(r.IsSuccessful
                ? $"{side} {volume} @ {r.Position.EntryPrice:F2} | broke {(brokeDown ? "below" : "above")} {RangeWindowMinutes}-min range " +
                  $"{_rangeLow:F2}-{_rangeHigh:F2} | SL ${StopUsd:F0} TP ${TakeProfitUsd:F0}"
                : $"Market order failed: {r.Error}");
        }

        private bool IsEntryTimeAllowed()
        {
            var dow = Time.DayOfWeek;
            var now = Time.TimeOfDay;
            if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday) return false;
            if (dow == DayOfWeek.Monday && now < _mondayStart) return false;
            if (dow == DayOfWeek.Friday && now >= _fridayLastEntry) return false;
            return now >= _windowStart && now < _windowEnd;
        }

        private bool IsWeekendFlatWindow()
        {
            var dow = Time.DayOfWeek;
            if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday) return true;
            if (dow != DayOfWeek.Friday) return false;
            // Without ticks after an early close the time check below never fires — so also
            // flatten 30 minutes before the session close the symbol's market hours report.
            return Time.TimeOfDay >= _fridayFlat || Symbol.MarketHours.TimeTillClose() <= TimeSpan.FromMinutes(30);
        }

        private TradeType? GetTrendSide()
        {
            if (_trendBars == null || _trendBars.Count <= TrendEmaPeriod + 1) return null;
            var ema = _trendEma.Result[_trendBars.Count - 2]; // last closed trend bar
            if (Symbol.Bid > ema) return TradeType.Buy;
            if (Symbol.Bid < ema) return TradeType.Sell;
            return null;
        }

        private double VolumeForRisk(double stopPips)
        {
            var riskAmount = Account.Balance * RiskPercent / 100.0;
            var perUnitRisk = stopPips * Symbol.PipValue;
            if (perUnitRisk <= 0) return 0;
            var volume = Symbol.NormalizeVolumeInUnits(riskAmount / perUnitRisk, RoundingMode.Down);
            return Math.Min(volume, Symbol.VolumeInUnitsMax);
        }

        // =================================================================
        //  Position management: optional break-even and max hold
        // =================================================================
        private void ManagePositions()
        {
            if (BeTriggerUsd <= 0 && MaxHoldHours <= 0) return;

            foreach (var pos in Positions.Where(IsBotPosition).ToArray())
            {
                if (MaxHoldHours > 0 && Time - pos.EntryTime >= TimeSpan.FromHours(MaxHoldHours))
                {
                    Print($"Max hold reached for #{pos.Id} — closing.");
                    if (CountOrderOp(force: true)) ClosePosition(pos);
                    continue;
                }

                if (BeTriggerUsd <= 0 || pos.StopLoss == null) continue;
                var isLong = pos.TradeType == TradeType.Buy;
                var alreadyBe = isLong ? pos.StopLoss.Value >= pos.EntryPrice : pos.StopLoss.Value <= pos.EntryPrice;
                if (alreadyBe) continue;

                var gain = isLong ? Symbol.Bid - pos.EntryPrice : pos.EntryPrice - Symbol.Ask;
                if (gain < BeTriggerUsd || !CountOrderOp()) continue;

                var newSl = isLong ? pos.EntryPrice + BeOffsetUsd : pos.EntryPrice - BeOffsetUsd;
                ModifyPosition(pos, newSl, pos.TakeProfit, ProtectionType.Absolute);
                Print($"Break-even for #{pos.Id} at +${gain:F1} (SL -> {newSl:F2})");
            }
        }

        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            if (IsBotPosition(args.Position)) _tradesToday++;
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (!IsBotPosition(pos)) return;
            _lastExit = Time;

            if (pos.NetProfit < 0)
            {
                _consecutiveLossesToday++;
                Print($"Trade #{pos.Id} closed with LOSS ({pos.NetProfit:F2}). Consecutive losses today: {_consecutiveLossesToday}/{MaxConsecutiveLosses}");
                if (_consecutiveLossesToday >= MaxConsecutiveLosses)
                    HaltDay($"CONSECUTIVE LOSS CIRCUIT BREAKER: {_consecutiveLossesToday} losses in a row today.", closePositions: false);
            }
            else if (pos.NetProfit > 0)
                _consecutiveLossesToday = 0;
        }

        // =================================================================
        //  FTMO guardrails (same framework as FtmoSmcGoldScalper)
        // =================================================================
        private void HaltDay(string reason, bool closePositions = true)
        {
            if (_tradingHaltedToday) return;
            _tradingHaltedToday = true;
            _haltReason = reason;
            Print($"TRADING HALTED for the day — {reason}");
            if (closePositions) FlattenAll(reason);
        }

        private void FlattenAll(string reason)
        {
            foreach (var pos in Positions.Where(IsBotPosition).ToArray())
                if (CountOrderOp(force: true))
                {
                    Print($"Closing #{pos.Id} — {reason}");
                    ClosePosition(pos);
                }
        }

        private bool CountOrderOp(bool force = false)
        {
            if (!force && _orderOpsToday >= MaxOrderOpsPerDay)
            {
                HaltDay($"ORDER OPS CAP: {_orderOpsToday} operations today", closePositions: false);
                return false;
            }
            _orderOpsToday++;
            return true;
        }

        private bool IsInsideNewsGuard(TimeSpan now, bool forFlattening = false)
        {
            var preBuffer = forFlattening ? TimeSpan.FromMinutes(NewsFlattenMinsBefore) : TimeSpan.FromMinutes(2);
            var postBuffer = TimeSpan.FromMinutes(2); // FTMO requires 2 minutes after news release

            foreach (var w in _newsWindows)
                if (now >= w.From - preBuffer && now <= w.To + postBuffer)
                    return true;
            return false;
        }

        private void RollDayIfNeeded()
        {
            if (Time.Date != _currentDay)
                StartNewDay();
        }

        private void StartNewDay()
        {
            _currentDay = Time.Date;
            // FTMO RULE: midnight baseline is the higher of Balance or Equity
            _dayStartBalance = Math.Max(Account.Balance, Account.Equity);
            _tradesToday = 0;
            _consecutiveLossesToday = 0;
            _orderOpsToday = 0;
            _tradingHaltedToday = false;
            _haltReason = "";
        }

        private bool HasOpenPositions() => Positions.Any(IsBotPosition);

        private static TimeSpan ParseTime(string s, TimeSpan fallback) =>
            TimeSpan.TryParse(s?.Trim(), out var t) ? t : fallback;

        private static (TimeSpan, TimeSpan)[] ParseNewsWindows(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<(TimeSpan, TimeSpan)>();
            return s.Split(';')
                .Select(part => part.Split('-'))
                .Where(p => p.Length == 2
                            && TimeSpan.TryParse(p[0].Trim(), out _)
                            && TimeSpan.TryParse(p[1].Trim(), out _))
                .Select(p => (TimeSpan.Parse(p[0].Trim()), TimeSpan.Parse(p[1].Trim())))
                .ToArray();
        }

        protected override void OnStop()
        {
            Print($"Stopped. Trades today: {_tradesToday}, order ops: {_orderOpsToday}" +
                  (_tradingHaltedToday ? $" | halted: {_haltReason}" : ""));
        }
    }
}
