// =====================================================================
//  VERSION TAG: 64kProfit — saved 2026-07-25
//  This is the exact version that produced the 1-year XAUUSD tick backtest:
//  85 trades, ~$63,682 net, ~45% win rate @ 2% risk, max eq DD 12.15% (from HWM),
//  no EQUITY FLOOR events, some DAILY CUTOFF halts.
//  Differences vs. later versions: single zone per direction (no zone queue),
//  fixed "FtmoSMC" label (no Instance Label parameter).
// =====================================================================
//
//  FTMO SMC Gold Scalper — Strategy 1 (HTF sweep + OB → LTF IFVG) for cTrader
//
//  Mechanical translation of the "liquidity sweep + order block + IFVG"
//  setup, hardened for the FTMO $100k 2-Step Evaluation:
//    - Max daily loss 5% on EQUITY (incl. floating), resets midnight CE(S)T
//    - Max overall loss 10% — static floor from initial balance
//    - Hyperactivity: < 2,000 order operations/day
//    - Funded Normal: news filter (±2 min) + weekend flatten
//
//  Logic (bullish; bearish is mirrored):
//    1. HTF (default 15m): price sweeps a confirmed swing low (trades below,
//       closes back above) — liquidity grab.
//    2. Within MaxSweepToBosBars HTF bars, price closes above the last
//       confirmed swing high — break of structure.
//    3. Order block = last down-close candle between the sweep and the BOS
//       bar. That zone becomes the HTF point of interest.
//    4. Wait for price to trade back into the OB zone.
//    5. On the chart timeframe (recommended 5m): after the zone is tapped,
//       find a bearish FVG that price then CLOSES above — it inverts into
//       a bullish IFVG (support).
//    6. Entry: buy limit at the IFVG top (or market on confirmation).
//       SL below the most recent LTF swing low. TP = R multiple.
//
//  Attach to XAUUSD on the 5-minute chart. HTF = 15m or 30m.
//  Backtest with TICK data — m1-bar backtests will misprice IFVG fills.
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
    public class FtmoSmcGoldScalper : Robot
    {
        // ---------------- Higher-Timeframe Trend / Direction Engine ----------------
        [Parameter("Use HTF Trend Filter", DefaultValue = true, Group = "HTF Trend / Direction")]
        public bool UseHtfTrendFilter { get; set; }

        [Parameter("Trend Timeframe", DefaultValue = "Hour", Group = "HTF Trend / Direction")]
        public TimeFrame TrendTimeFrame { get; set; }

        [Parameter("Trend Fast EMA (Direction)", DefaultValue = 20, MinValue = 5, Group = "HTF Trend / Direction")]
        public int TrendFastEmaPeriod { get; set; }

        [Parameter("Trend Slow EMA (Baseline)", DefaultValue = 50, MinValue = 10, Group = "HTF Trend / Direction")]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Require Dual EMA Alignment", DefaultValue = true, Group = "HTF Trend / Direction")]
        public bool RequireDualEmaAlignment { get; set; }

        // ---------------- Trend Continuation Setups ----------------
        // Allows entries on HTF FVG pullbacks during established trends (without requiring liquidity sweeps)
        [Parameter("Enable Trend Continuation Setups", DefaultValue = true, Group = "Trend Continuation")]
        public bool EnableTrendContinuation { get; set; }

        // ---------------- Structure detection ----------------
        [Parameter("Higher Timeframe", DefaultValue = "Minute15", Group = "Structure")]
        public TimeFrame HigherTimeframe { get; set; }

        [Parameter("HTF Swing Strength (bars each side)", DefaultValue = 3, MinValue = 1, Group = "Structure")]
        public int HtfSwingStrength { get; set; }

        [Parameter("LTF Swing Strength (bars each side)", DefaultValue = 3, MinValue = 1, Group = "Structure")]
        public int LtfSwingStrength { get; set; }

        [Parameter("Max Sweep→BOS Distance (HTF bars)", DefaultValue = 12, MinValue = 2, Group = "Structure")]
        public int MaxSweepToBosBars { get; set; }

        [Parameter("Zone Max Age (HTF bars)", DefaultValue = 96, MinValue = 4, Group = "Structure")]
        public int ZoneMaxAgeBars { get; set; }             // 96 x 15m = 24h — stale zones expire

        // ---------------- Entry ----------------
        [Parameter("Entry Mode", DefaultValue = EntryModeType.MarketOnConfirmation, Group = "Entry")]
        public EntryModeType EntryMode { get; set; }

        [Parameter("Require LTF MSS Confirmation", DefaultValue = false, Group = "Entry")]
        public bool RequireMss { get; set; }                 // extra confluence: LTF close beyond last LTF swing

        [Parameter("Min FVG Size (pips)", DefaultValue = 20.0, MinValue = 1.0, Group = "Entry")]
        public double MinFvgPips { get; set; }               // 20 pips = $0.20 minimum FVG height on Gold

        [Parameter("Limit Order Expiry (minutes)", DefaultValue = 90, MinValue = 5, Group = "Entry")]
        public int LimitExpiryMinutes { get; set; }

        [Parameter("Cancel Limit If Target Progress (R)", DefaultValue = 1.0, MinValue = 0.3, Group = "Entry")]
        public double CancelLimitIfPriceHitsR { get; set; }   // cancels stale limit if price moved towards target without us

        [Parameter("Max Trades Per Day", DefaultValue = 3, MinValue = 1, Group = "Entry")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Trading Window Start (CE(S)T)", DefaultValue = "09:00", Group = "Entry")]
        public string WindowStartStr { get; set; }           // London open

        [Parameter("Trading Window End (CE(S)T)", DefaultValue = "16:30", Group = "Entry")]
        public string WindowEndStr { get; set; }             // New York peak (before late session chop)

        [Parameter("Avoid Lunch Chop", DefaultValue = false, Group = "Entry")]
        public bool AvoidLunchChop { get; set; }

        [Parameter("Lunch Chop Start (CE(S)T)", DefaultValue = "13:00", Group = "Entry")]
        public string LunchStartStr { get; set; }

        [Parameter("Lunch Chop End (CE(S)T)", DefaultValue = "15:00", Group = "Entry")]
        public string LunchEndStr { get; set; }

        // ---------------- Risk / Exits ----------------
        [Parameter("Risk Per Trade (%)", DefaultValue = 0.75, MinValue = 0.05, MaxValue = 5.0, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Max Consecutive Losses Today", DefaultValue = 2, MinValue = 1, Group = "Risk")]
        public int MaxConsecutiveLosses { get; set; }        // circuit breaker: stops trading today after 2 losses

        [Parameter("SL Buffer (pips)", DefaultValue = 30.0, MinValue = 0, Group = "Risk")]
        public double SlBufferPips { get; set; }             // 30 pips = $0.30 buffer below/above the LTF swing

        [Parameter("Min Stop (pips)", DefaultValue = 100.0, MinValue = 10, Group = "Risk")]
        public double MinStopPips { get; set; }              // 100 pips = $1.00 min stop (reject if smaller than spread noise)

        [Parameter("Max Stop (pips)", DefaultValue = 1000.0, MinValue = 50, MaxValue = 3000, Group = "Risk")]
        public double MaxStopPips { get; set; }              // 1000 pips = $10.00 max stop on Gold (reject if wider)

        [Parameter("Take Profit (R multiple)", DefaultValue = 2.0, MinValue = 0.5, Group = "Risk")]
        public double TakeProfitR { get; set; }

        [Parameter("Move SL to BE at +1R", DefaultValue = false, Group = "Risk")]
        public bool UseBreakEven { get; set; }

        // How far in profit (in R) a trade must go before the stop moves to break-even. 1.0 = original behaviour.
        [Parameter("Break-even Trigger (R in profit)", DefaultValue = 1.3, MinValue = 0.1, MaxValue = 3.0, Group = "Risk")]
        public double BeTriggerR { get; set; }

        [Parameter("Break-even Offset (pips)", DefaultValue = 10.0, MinValue = 0, Group = "Risk")]
        public double BeOffsetPips { get; set; }             // 10 pips = $0.10 buffer to cover spread & commissions on BE

        // ---------------- Runner / Moonbag Architecture ----------------
        // Closes main position at TakeProfitR (e.g. 2R) and lets runner ride the major macro trend with structure trailing
        [Parameter("Enable Runner (Split Position)", DefaultValue = true, Group = "Runner Architecture")]
        public bool EnableRunner { get; set; }

        [Parameter("Runner Size (% of position)", DefaultValue = 40.0, MinValue = 10, MaxValue = 90, Group = "Runner Architecture")]
        public double RunnerPct { get; set; }

        [Parameter("Runner Trail Starts At (R in profit)", DefaultValue = 1.5, MinValue = 0.5, Group = "Runner Architecture")]
        public double RunnerTrailStartR { get; set; }

        [Parameter("Enable Runner Step-Lock", DefaultValue = false, Group = "Runner Architecture")]
        public bool EnableRunnerStepLock { get; set; }

        [Parameter("Runner Step-Lock Trigger (R in profit)", DefaultValue = 3.0, MinValue = 2.0, Group = "Runner Architecture")]
        public double RunnerStepTriggerR { get; set; }

        [Parameter("Runner Step-Lock Level (R locked)", DefaultValue = 1.5, MinValue = 0.5, Group = "Runner Architecture")]
        public double RunnerStepLockR { get; set; }

        // ---------------- Profit trailing (optional; the 2R target stays in place) ----------------
        //   Structure : after +TrailStartR, stop moves just beyond each new confirmed M5 swing (low for longs)
        //   StepLock  : at +1.5R the stop is moved to +0.5R
        //   Atr       : after +TrailStartR, stop follows TrailAtrMult x ATR(14, chart TF) behind the best close
        // The stop only ever tightens. Off = original behaviour.
        [Parameter("Trailing Mode", DefaultValue = TrailMode.Off, Group = "Profit Trailing")]
        public TrailMode Trailing { get; set; }

        [Parameter("Trail Starts At (R in profit)", DefaultValue = 1.0, MinValue = 0.1, Group = "Profit Trailing")]
        public double TrailStartR { get; set; }

        [Parameter("ATR Trail Multiple", DefaultValue = 2.0, MinValue = 0.5, Group = "Profit Trailing")]
        public double TrailAtrMult { get; set; }

        // FixedPips: once the trade is TrailPips in profit, the stop follows TrailPips behind the best price (every tick).
        [Parameter("Fixed Trail Distance (pips)", DefaultValue = 100.0, MinValue = 10, Group = "Profit Trailing")]
        public double TrailPips { get; set; }

        public enum TrailMode { Off, Structure, StepLock, Atr, FixedPips }

        // ---------------- Partial close (optional) ----------------
        // When a trade first reaches PartialCloseAtR in profit (1R = initial stop distance), close PartialClosePct
        // of the position. The rest keeps the normal stop / break-even / target.
        [Parameter("Use Partial Close", DefaultValue = false, Group = "Partial Close")]
        public bool UsePartialClose { get; set; }

        [Parameter("Partial Close At (R in profit)", DefaultValue = 1.0, MinValue = 0.1, Group = "Partial Close")]
        public double PartialCloseAtR { get; set; }

        [Parameter("Partial Close (% of position)", DefaultValue = 50.0, MinValue = 1, MaxValue = 99, Group = "Partial Close")]
        public double PartialClosePct { get; set; }

        [Parameter("Flat-By Time (HH:mm CE(S)T)", DefaultValue = "22:30", Group = "Risk")]
        public string SessionCutoffStr { get; set; }

        // Off = no daily time exit: trades run until stop / target / break-even (still flat before the weekend).
        [Parameter("Use Session Close (Flat-By Time)", DefaultValue = true, Group = "Risk")]
        public bool UseSessionClose { get; set; }

        // ---------------- FTMO Guardrails (% of balance at bot start) ----------------
        [Parameter("Initial Balance (0 = use account balance)", DefaultValue = 0, MinValue = 0, Group = "FTMO")]
        public double InitialBalanceOverride { get; set; }  // fix the drawdown baseline (e.g. 100000 for a $100k account)

        [Parameter("Daily Loss Cutoff (% of initial)", DefaultValue = 3.5, MinValue = 0.1, Group = "FTMO")]
        public double DailyLossCutoffPct { get; set; }

        [Parameter("Overall Loss Cutoff (% of initial)", DefaultValue = 8.5, MinValue = 0.1, Group = "FTMO")]
        public double OverallLossCutoffPct { get; set; }

        [Parameter("Daily Profit Lock (% of initial, 0=off)", DefaultValue = 1.5, MinValue = 0, Group = "FTMO")]
        public double DailyProfitLockPct { get; set; }

        [Parameter("Max Order Ops Per Day", DefaultValue = 200, MinValue = 10, Group = "FTMO")]
        public int MaxOrderOpsPerDay { get; set; }

        [Parameter("Trade On Friday", DefaultValue = true, Group = "FTMO")]
        public bool TradeOnFriday { get; set; }

        [Parameter("Flatten Before Weekend", DefaultValue = true, Group = "FTMO")]
        public bool WeekendFlatten { get; set; }

        [Parameter("Friday Flat Time (HH:mm CE(S)T)", DefaultValue = "21:30", Group = "FTMO")]
        public string FridayFlatStr { get; set; }

        [Parameter("News Blackout (HH:mm-HH:mm;...)", DefaultValue = "", Group = "FTMO")]
        public string NewsBlackoutStr { get; set; }

        [Parameter("News Flatten Buffer (mins before)", DefaultValue = 5, MinValue = 2, Group = "FTMO")]
        public int NewsFlattenMinsBefore { get; set; }

        public enum EntryModeType { LimitAtIfvgTop, MarketOnConfirmation }

        // ---------------- Internal types ----------------
        private class Swing { public int Index; public double Price; public bool Swept; }

        private class ObZone
        {
            public double High, Low;
            public int CreatedHtfIndex;
            public bool Touched;
            public List<Fvg> Fvgs = new List<Fvg>();
            public bool MssConfirmed;
        }

        private class Fvg { public double Top, Bottom; public int BarIndex; }

        private const string Label = "FtmoSMC";
        private const string RunnerLabel = "FtmoSMC_Run";

        private bool IsBotPosition(Position p) => (p.Label == Label || p.Label == RunnerLabel) && p.SymbolName == SymbolName;
        private bool IsBotOrder(PendingOrder o) => (o.Label == Label || o.Label == RunnerLabel) && o.SymbolName == SymbolName;

        private Bars _htf;
        private Bars _trendBars;
        private ExponentialMovingAverage _trendEma;
        private ExponentialMovingAverage _trendFastEma;
        private int _lastHtfCount, _lastLtfCount;

        private readonly List<Swing> _htfHighs = new List<Swing>();
        private readonly List<Swing> _htfLows = new List<Swing>();
        private readonly List<Swing> _ltfHighs = new List<Swing>();
        private readonly List<Swing> _ltfLows = new List<Swing>();

        private int _pendingSweepLowBar = -1, _pendingSweepHighBar = -1; // HTF bar index of the sweep
        private ObZone _bullZone, _bearZone;

        private TimeSpan _windowStart, _windowEnd, _sessionCutoff, _fridayFlat, _lunchStart, _lunchEnd;
        private (TimeSpan From, TimeSpan To)[] _newsWindows;

        private DateTime _currentDay = DateTime.MinValue;
        private double _initialBalance, _dailyLossCutoff, _equityHardFloor, _dailyProfitLock;
        private double _dayStartBalance;
        private int _tradesToday, _orderOpsToday, _consecutiveLossesToday;
        private bool _tradingHaltedToday;
        private string _haltReason = "";

        // =================================================================
        protected override void OnStart()
        {
            _htf = MarketData.GetBars(HigherTimeframe);
            _lastHtfCount = _htf.Count;
            _lastLtfCount = Bars.Count;

            if (Bars.TimeFrame >= HigherTimeframe)
                Print("WARNING: chart timeframe should be LOWER than the HTF (e.g. 5m chart with 15m HTF).");

            if (UseHtfTrendFilter)
            {
                _trendBars = MarketData.GetBars(TrendTimeFrame);
                _trendFastEma = Indicators.ExponentialMovingAverage(_trendBars.ClosePrices, TrendFastEmaPeriod);
                _trendEma = Indicators.ExponentialMovingAverage(_trendBars.ClosePrices, TrendEmaPeriod);
                Print($"HTF Direction Engine ACTIVE: {TrendTimeFrame} with Fast EMA({TrendFastEmaPeriod}) & Slow EMA({TrendEmaPeriod}) (Dual Alignment: {RequireDualEmaAlignment})");
            }

            _windowStart = ParseTime(WindowStartStr, new TimeSpan(9, 0, 0));
            _windowEnd = ParseTime(WindowEndStr, new TimeSpan(16, 30, 0));
            _lunchStart = ParseTime(LunchStartStr, new TimeSpan(13, 0, 0));
            _lunchEnd = ParseTime(LunchEndStr, new TimeSpan(15, 0, 0));
            _sessionCutoff = ParseTime(SessionCutoffStr, new TimeSpan(22, 30, 0));
            _fridayFlat = ParseTime(FridayFlatStr, new TimeSpan(21, 30, 0));
            _newsWindows = ParseNewsWindows(NewsBlackoutStr);

            Print($"Trading Window: {WindowStartStr} - {WindowEndStr} CE(S)T (Trade Fridays: {TradeOnFriday}, Lunch Avoidance: {AvoidLunchChop})");

            _initialBalance = InitialBalanceOverride > 0 ? InitialBalanceOverride : Account.Balance;
            _dailyLossCutoff = _initialBalance * DailyLossCutoffPct / 100.0;
            _equityHardFloor = _initialBalance * (1.0 - OverallLossCutoffPct / 100.0);
            _dailyProfitLock = _initialBalance * DailyProfitLockPct / 100.0;

            if (InitialBalanceOverride <= 0)
                Print($"ATTENTION: 'Initial Balance' is 0. Using current Account.Balance ({Account.Balance:F0}) as FTMO baseline. For prop challenges, set this explicitly (e.g. 100000).");

            Print($"FTMO guards: daily cutoff {_dailyLossCutoff:F0}, equity floor {_equityHardFloor:F0}, " +
                  $"profit lock {(_dailyProfitLock > 0 ? _dailyProfitLock.ToString("F0") : "off")}, " +
                  $"max consecutive losses: {MaxConsecutiveLosses} " +
                  $"(initial balance {_initialBalance:F0})");

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;
            _ltfAtr = Indicators.AverageTrueRange(14, MovingAverageType.Simple);
            StartNewDay();
        }

        protected override void OnTick()
        {
            RollDayIfNeeded();

            var equity = Account.Equity;
            var dayLoss = _dayStartBalance - equity;

            if (equity <= _equityHardFloor)
                HaltDay($"EQUITY FLOOR: equity {equity:F0} <= hard floor {_equityHardFloor:F0}");
            else if (dayLoss >= _dailyLossCutoff)
                HaltDay($"DAILY CUTOFF: day drawdown {dayLoss:F0} >= {_dailyLossCutoff:F0}");
            else if (_dailyProfitLock > 0 && !_tradingHaltedToday && equity - _dayStartBalance >= _dailyProfitLock && _tradesToday > 0)
                HaltDay($"PROFIT LOCK: +{equity - _dayStartBalance:F0} banked for the day", closePositions: false);

            var now = Time.TimeOfDay;

            if (UseSessionClose && now >= _sessionCutoff && (HasOpenPositions() || HasPendingOrders()))
                FlattenAll("session cutoff");

            if (WeekendFlatten && Time.DayOfWeek == DayOfWeek.Friday && now >= _fridayFlat
                && (HasOpenPositions() || HasPendingOrders()))
                FlattenAll("weekend flatten");

            if (IsInsideNewsGuard(now, forFlattening: true) && (HasOpenPositions() || HasPendingOrders()))
                FlattenAll("news blackout buffer");

            CheckPendingOrdersTargetReached();

            if (UseBreakEven)
                ManageBreakEven();

            if (Trailing == TrailMode.StepLock || Trailing == TrailMode.FixedPips)
                ManageTrailing(onBarClose: false);

            if (UsePartialClose)
                ManagePartialClose();

            if (EnableRunner)
                ManageRunnerTrailing(onBarClose: false);
        }

        protected override void OnBar()
        {
            RollDayIfNeeded();

            if (Trailing == TrailMode.Structure || TrailMode.Atr == Trailing)
                ManageTrailing(onBarClose: true);

            if (EnableRunner)
                ManageRunnerTrailing(onBarClose: true);

            // ---- HTF structure: process each newly closed HTF bar ----
            while (_htf.Count > _lastHtfCount)
            {
                _lastHtfCount++;
                ProcessHtfBar(_lastHtfCount - 2); // -2 = the bar that just closed
            }

            // ---- LTF: confirmed swings + zone interaction ----
            ConfirmSwing(Bars, _ltfHighs, _ltfLows, LtfSwingStrength, ref _lastLtfCount);
            TrimSwings(_ltfHighs); TrimSwings(_ltfLows);

            if (_tradingHaltedToday) return;

            ProcessZoneOnLtf(_bullZone, TradeType.Buy);
            ProcessZoneOnLtf(_bearZone, TradeType.Sell);
        }

        // =================================================================
        //  HTF: swings → sweep → BOS → order block zone
        // =================================================================
        private void ProcessHtfBar(int closedIdx)
        {
            if (closedIdx < 2 * HtfSwingStrength) return;

            ConfirmHtfSwingAt(closedIdx);
            TrimSwings(_htfHighs); TrimSwings(_htfLows);

            var close = _htf.ClosePrices[closedIdx];
            var high = _htf.HighPrices[closedIdx];
            var low = _htf.LowPrices[closedIdx];

            // ---- Liquidity sweeps (grab + immediate reclaim on the same bar) ----
            var sweptLow = _htfLows.LastOrDefault(s => !s.Swept && s.Index < closedIdx && low < s.Price && close > s.Price);
            if (sweptLow != null)
            {
                sweptLow.Swept = true;
                _pendingSweepLowBar = closedIdx;
            }

            var sweptHigh = _htfHighs.LastOrDefault(s => !s.Swept && s.Index < closedIdx && high > s.Price && close < s.Price);
            if (sweptHigh != null)
            {
                sweptHigh.Swept = true;
                _pendingSweepHighBar = closedIdx;
            }

            // ---- Bullish BOS after a low sweep → bullish OB ----
            if (_pendingSweepLowBar > 0)
            {
                if (closedIdx - _pendingSweepLowBar > MaxSweepToBosBars)
                    _pendingSweepLowBar = -1;
                else
                {
                    var lastHigh = _htfHighs.LastOrDefault(s => s.Index < _pendingSweepLowBar);
                    if (lastHigh != null && close > lastHigh.Price)
                    {
                        _bullZone = BuildOrderBlock(_pendingSweepLowBar, closedIdx, bullish: true);
                        _pendingSweepLowBar = -1;
                        if (_bullZone != null)
                            Print($"Bullish OB zone {_bullZone.Low:F2}-{_bullZone.High:F2} (sweep→BOS on HTF)");
                    }
                }
            }

            // ---- Bearish BOS after a high sweep → bearish OB ----
            if (_pendingSweepHighBar > 0)
            {
                if (closedIdx - _pendingSweepHighBar > MaxSweepToBosBars)
                    _pendingSweepHighBar = -1;
                else
                {
                    var lastLow = _htfLows.LastOrDefault(s => s.Index < _pendingSweepHighBar);
                    if (lastLow != null && close < lastLow.Price)
                    {
                        _bearZone = BuildOrderBlock(_pendingSweepHighBar, closedIdx, bullish: false);
                        _pendingSweepHighBar = -1;
                        if (_bearZone != null)
                            Print($"Bearish OB zone {_bearZone.Low:F2}-{_bearZone.High:F2} (sweep→BOS on HTF)");
                    }
                }
            }

            // ---- Trend Continuation Setups (Without Sweep Requirement) ----
            // When HTF trend is strictly aligned, allow pullback entries to newly formed HTF FVGs / OBs on BOS
            if (EnableTrendContinuation)
            {
                var dir = GetMarketDirection();
                if (dir == TradeType.Buy && _bullZone == null)
                {
                    var lastHigh = _htfHighs.LastOrDefault(s => s.Index < closedIdx - 1);
                    if (lastHigh != null && close > lastHigh.Price)
                    {
                        _bullZone = FindContinuationZone(closedIdx, bullish: true);
                        if (_bullZone != null)
                            Print($"Bullish CONTINUATION zone {_bullZone.Low:F2}-{_bullZone.High:F2} (HTF Breakout Pullback)");
                    }
                }
                else if (dir == TradeType.Sell && _bearZone == null)
                {
                    var lastLow = _htfLows.LastOrDefault(s => s.Index < closedIdx - 1);
                    if (lastLow != null && close < lastLow.Price)
                    {
                        _bearZone = FindContinuationZone(closedIdx, bullish: false);
                        if (_bearZone != null)
                            Print($"Bearish CONTINUATION zone {_bearZone.Low:F2}-{_bearZone.High:F2} (HTF Breakout Pullback)");
                    }
                }
            }

            // ---- Zone lifecycle: expiry + invalidation on HTF close-through ----
            if (_bullZone != null &&
                (closedIdx - _bullZone.CreatedHtfIndex > ZoneMaxAgeBars || close < _bullZone.Low))
                _bullZone = null;

            if (_bearZone != null &&
                (closedIdx - _bearZone.CreatedHtfIndex > ZoneMaxAgeBars || close > _bearZone.High))
                _bearZone = null;
        }

        // Order block = last opposite-close candle between sweep bar and BOS bar.
        private ObZone BuildOrderBlock(int sweepBar, int bosBar, bool bullish)
        {
            for (var i = bosBar; i >= sweepBar; i--)
            {
                var isOpposite = bullish
                    ? _htf.ClosePrices[i] < _htf.OpenPrices[i]
                    : _htf.ClosePrices[i] > _htf.OpenPrices[i];
                if (!isOpposite) continue;

                return new ObZone
                {
                    High = _htf.HighPrices[i],
                    Low = _htf.LowPrices[i],
                    CreatedHtfIndex = bosBar
                };
            }
            return null; // all candles in the leg were same-direction — no valid OB
        }

        private ObZone FindContinuationZone(int closedIdx, bool bullish)
        {
            var minFvgHeight = MinFvgPips * Symbol.PipSize;
            var lookback = Math.Min(8, closedIdx - 2);

            // 1. Look for the freshest HTF FVG created in this breakout impulse
            for (var k = closedIdx; k >= closedIdx - lookback; k--)
            {
                if (k < 2) break;
                if (bullish && _htf.LowPrices[k] > _htf.HighPrices[k - 2])
                {
                    var gap = _htf.LowPrices[k] - _htf.HighPrices[k - 2];
                    if (gap >= minFvgHeight)
                    {
                        return new ObZone
                        {
                            High = _htf.LowPrices[k],
                            Low = _htf.HighPrices[k - 2],
                            CreatedHtfIndex = closedIdx
                        };
                    }
                }
                else if (!bullish && _htf.HighPrices[k] < _htf.LowPrices[k - 2])
                {
                    var gap = _htf.LowPrices[k - 2] - _htf.HighPrices[k];
                    if (gap >= minFvgHeight)
                    {
                        return new ObZone
                        {
                            High = _htf.LowPrices[k - 2],
                            Low = _htf.HighPrices[k],
                            CreatedHtfIndex = closedIdx
                        };
                    }
                }
            }

            // 2. Fallback: Last opposite candle in the impulse leg
            for (var k = closedIdx; k >= closedIdx - lookback; k--)
            {
                var isOpposite = bullish
                    ? _htf.ClosePrices[k] < _htf.OpenPrices[k]
                    : _htf.ClosePrices[k] > _htf.OpenPrices[k];
                if (isOpposite)
                {
                    return new ObZone
                    {
                        High = _htf.HighPrices[k],
                        Low = _htf.LowPrices[k],
                        CreatedHtfIndex = closedIdx
                    };
                }
            }

            return null;
        }

        private void ConfirmHtfSwingAt(int closedIdx)
        {
            var p = closedIdx - HtfSwingStrength;
            if (p < HtfSwingStrength) return;

            var isHigh = true; var isLow = true;
            for (var k = p - HtfSwingStrength; k <= p + HtfSwingStrength; k++)
            {
                if (k == p) continue;
                if (_htf.HighPrices[k] >= _htf.HighPrices[p]) isHigh = false;
                if (_htf.LowPrices[k] <= _htf.LowPrices[p]) isLow = false;
                if (!isHigh && !isLow) break;
            }
            if (isHigh && _htfHighs.All(s => s.Index != p))
                _htfHighs.Add(new Swing { Index = p, Price = _htf.HighPrices[p] });
            if (isLow && _htfLows.All(s => s.Index != p))
                _htfLows.Add(new Swing { Index = p, Price = _htf.LowPrices[p] });
        }

        // =================================================================
        //  LTF: zone tap → bearish FVG inverts to bullish IFVG → entry
        //  (bearish zone is the exact mirror)
        // =================================================================
        private void ProcessZoneOnLtf(ObZone zone, TradeType side)
        {
            if (zone == null) return;

            var i = Bars.Count - 2; // just-closed LTF bar
            if (i < 3) return;

            var closedLow = Bars.LowPrices[i];
            var closedHigh = Bars.HighPrices[i];
            var closedClose = Bars.ClosePrices[i];

            // 1) Wait for the zone tap
            if (!zone.Touched)
            {
                var tapped = side == TradeType.Buy ? closedLow <= zone.High : closedHigh >= zone.Low;
                if (!tapped) return;
                zone.Touched = true;
                zone.Fvgs.Clear();
                Print($"{side} OB zone tapped at {(side == TradeType.Buy ? closedLow : closedHigh):F2} — watching LTF for IFVG");
            }

            // 2) Collect counter-trend FVGs formed after the tap (filtered by MinFvgPips to eliminate noise)
            //    Buy side: bearish FVG = high[i] < low[i-2], zone [high[i], low[i-2]]
            //    Sell side: bullish FVG = low[i] > high[i-2], zone [high[i-2], low[i]]
            var minFvgHeight = MinFvgPips * Symbol.PipSize;
            if (side == TradeType.Buy && Bars.HighPrices[i] < Bars.LowPrices[i - 2])
            {
                var gap = Bars.LowPrices[i - 2] - Bars.HighPrices[i];
                if (gap >= minFvgHeight)
                    zone.Fvgs.Add(new Fvg { Bottom = Bars.HighPrices[i], Top = Bars.LowPrices[i - 2], BarIndex = i });
            }
            if (side == TradeType.Sell && Bars.LowPrices[i] > Bars.HighPrices[i - 2])
            {
                var gap = Bars.LowPrices[i] - Bars.HighPrices[i - 2];
                if (gap >= minFvgHeight)
                    zone.Fvgs.Add(new Fvg { Bottom = Bars.HighPrices[i - 2], Top = Bars.LowPrices[i], BarIndex = i });
            }
            if (zone.Fvgs.Count > 10)
                zone.Fvgs.RemoveAt(0);

            // 3) Optional MSS confluence
            if (RequireMss && !zone.MssConfirmed)
            {
                if (side == TradeType.Buy)
                {
                    var lastLtfHigh = _ltfHighs.LastOrDefault();
                    if (lastLtfHigh != null && closedClose > lastLtfHigh.Price) zone.MssConfirmed = true;
                }
                else
                {
                    var lastLtfLow = _ltfLows.LastOrDefault();
                    if (lastLtfLow != null && closedClose < lastLtfLow.Price) zone.MssConfirmed = true;
                }
            }

            // 4) Inversion: close through the FVG confirms the IFVG → entry
            foreach (var fvg in zone.Fvgs.ToArray())
            {
                var inverted = side == TradeType.Buy ? closedClose > fvg.Top : closedClose < fvg.Bottom;
                if (!inverted || fvg.BarIndex == i) continue; // need a later bar to close through it

                if (RequireMss && !zone.MssConfirmed) return;

                TryEnter(side, fvg);
                // one entry per zone — consume it regardless of order success
                if (side == TradeType.Buy) _bullZone = null; else _bearZone = null;
                return;
            }
        }

        private void TryEnter(TradeType side, Fvg fvg)
        {
            if (_tradingHaltedToday || _tradesToday >= MaxTradesPerDay) return;

            if (!TradeOnFriday && Time.DayOfWeek == DayOfWeek.Friday)
            {
                Print("Setup skipped: Trading on Friday is disabled.");
                return;
            }

            var now = Time.TimeOfDay;
            if (now < _windowStart || now > _windowEnd) { Print("Setup outside trading window — skipped."); return; }
            if (AvoidLunchChop && now >= _lunchStart && now < _lunchEnd) { Print("Setup inside lunch chop window — skipped."); return; }
            if (IsInsideNewsGuard(now)) { Print("Setup inside news blackout — skipped."); return; }
            if (WeekendFlatten && Time.DayOfWeek == DayOfWeek.Friday && now >= _fridayFlat) return;
            if (HasOpenPositions() || HasPendingOrders()) return; // one setup at a time

            // HTF Trend / Direction confluence check (Institutional Dual-EMA Alignment)
            if (UseHtfTrendFilter)
            {
                var direction = GetMarketDirection();
                if (direction == null)
                {
                    Print($"Setup skipped: Market direction is NEUTRAL / CHOP on {TrendTimeFrame} (Fast EMA & Slow EMA not aligned).");
                    return;
                }
                if (direction != side)
                {
                    Print($"Setup skipped: Setup {side} opposes HTF Direction ({direction}) on {TrendTimeFrame}.");
                    return;
                }
            }

            // Entry price
            var entry = EntryMode == EntryModeType.LimitAtIfvgTop
                ? (side == TradeType.Buy ? fvg.Top : fvg.Bottom)
                : (side == TradeType.Buy ? Symbol.Ask : Symbol.Bid);

            // SL at the most recent LTF swing low/high + buffer
            double? swing = side == TradeType.Buy
                ? _ltfLows.LastOrDefault()?.Price
                : _ltfHighs.LastOrDefault()?.Price;

            if (swing == null) { Print("No LTF swing for SL — skipped."); return; }

            var slPrice = side == TradeType.Buy
                ? swing.Value - SlBufferPips * Symbol.PipSize
                : swing.Value + SlBufferPips * Symbol.PipSize;

            var stopPips = Math.Abs(entry - slPrice) / Symbol.PipSize;

            // Strict Institutional Risk: Do NOT clamp! Reject invalid stops to maintain SMC logic.
            if (stopPips < MinStopPips)
            {
                Print($"Setup rejected: Stop distance ({stopPips:F1}p) is below minimum noise threshold ({MinStopPips:F1}p).");
                return;
            }
            if (stopPips > MaxStopPips)
            {
                Print($"Setup rejected: Stop distance ({stopPips:F1}p) exceeds maximum allowed ({MaxStopPips:F1}p) — swing too wide.");
                return;
            }

            var tpPips = stopPips * TakeProfitR;

            var volume = VolumeForRisk(stopPips);
            if (volume < Symbol.VolumeInUnitsMin) { Print("Volume below minimum — skipped."); return; }

            if (!CountOrderOp()) return;

            if (EnableRunner)
            {
                var runnerVol = Symbol.NormalizeVolumeInUnits(volume * RunnerPct / 100.0, RoundingMode.Down);
                var mainVol = Symbol.NormalizeVolumeInUnits(volume - runnerVol, RoundingMode.Down);

                if (runnerVol >= Symbol.VolumeInUnitsMin && mainVol >= Symbol.VolumeInUnitsMin)
                {
                    if (EntryMode == EntryModeType.MarketOnConfirmation)
                    {
                        var r1 = ExecuteMarketOrder(side, SymbolName, mainVol, Label, stopPips, tpPips);
                        var r2 = CountOrderOp() ? ExecuteMarketOrder(side, SymbolName, runnerVol, RunnerLabel, stopPips, null) : null;
                        Print(r1.IsSuccessful
                            ? $"MARKET SPLIT {side} | Main {mainVol} (TP {tpPips:F1}p) | Runner {runnerVol} (Unlimited TP / Structure Trailing)"
                            : $"Market order failed: {r1.Error}");
                    }
                    else
                    {
                        var expiry = Time.AddMinutes(LimitExpiryMinutes);
                        var r1 = PlaceLimitOrder(side, SymbolName, mainVol, entry, Label, stopPips, tpPips, expiry);
                        var r2 = CountOrderOp() ? PlaceLimitOrder(side, SymbolName, runnerVol, entry, RunnerLabel, stopPips, null, expiry) : null;
                        Print(r1.IsSuccessful
                            ? $"LIMIT SPLIT {side} @ {entry:F2} | Main {mainVol} (TP {tpPips:F1}p) | Runner {runnerVol} (Unlimited TP)"
                            : $"Limit order failed: {r1.Error}");
                    }
                    return;
                }
            }

            if (EntryMode == EntryModeType.MarketOnConfirmation)
            {
                var r = ExecuteMarketOrder(side, SymbolName, volume, Label, stopPips, tpPips);
                Print(r.IsSuccessful
                    ? $"MARKET {side} {volume} | SL {stopPips:F1}p TP {tpPips:F1}p"
                    : $"Market order failed: {r.Error}");
            }
            else
            {
                var expiry = Time.AddMinutes(LimitExpiryMinutes);
                var r = PlaceLimitOrder(side, SymbolName, volume, entry, Label, stopPips, tpPips, expiry);
                Print(r.IsSuccessful
                    ? $"LIMIT {side} {volume} @ {entry:F2} | SL {stopPips:F1}p TP {tpPips:F1}p | IFVG {fvg.Bottom:F2}-{fvg.Top:F2}"
                    : $"Limit order failed: {r.Error}");
            }
        }

        private double VolumeForRisk(double stopPips)
        {
            var riskAmount = Account.Balance * RiskPercent / 100.0;
            var perUnitRisk = stopPips * Symbol.PipValue;
            if (perUnitRisk <= 0) return 0;
            return Symbol.NormalizeVolumeInUnits(riskAmount / perUnitRisk, RoundingMode.Down);
        }

        private TradeType? GetMarketDirection()
        {
            if (!UseHtfTrendFilter || _trendBars == null) return null;
            var requiredBars = Math.Max(TrendEmaPeriod, TrendFastEmaPeriod);
            if (_trendBars.Count <= requiredBars) return null;

            var tIdx = _trendBars.Count - 2;
            var close = _trendBars.ClosePrices[tIdx];
            var fast = _trendFastEma.Result[tIdx];
            var slow = _trendEma.Result[tIdx];

            if (RequireDualEmaAlignment)
            {
                // Strict Institutional Direction:
                // Bullish: Price > Fast EMA AND Fast EMA > Slow EMA
                if (close > fast && fast > slow) return TradeType.Buy;
                // Bearish: Price < Fast EMA AND Fast EMA < Slow EMA
                if (close < fast && fast < slow) return TradeType.Sell;
                return null; // Neutral / Choppy / Squeeze — do not take low-probability trades
            }
            else
            {
                if (close > slow) return TradeType.Buy;
                if (close < slow) return TradeType.Sell;
                return null;
            }
        }

        // =================================================================
        //  Generic confirmed-fractal swing detection for the chart bars
        // =================================================================
        private void ConfirmSwing(Bars bars, List<Swing> highs, List<Swing> lows, int strength, ref int lastCount)
        {
            while (bars.Count > lastCount)
            {
                lastCount++;
                var closedIdx = lastCount - 2;
                var p = closedIdx - strength;
                if (p < strength) continue;

                var isHigh = true; var isLow = true;
                for (var k = p - strength; k <= p + strength; k++)
                {
                    if (k == p) continue;
                    if (bars.HighPrices[k] >= bars.HighPrices[p]) isHigh = false;
                    if (bars.LowPrices[k] <= bars.LowPrices[p]) isLow = false;
                    if (!isHigh && !isLow) break;
                }
                if (isHigh && highs.All(s => s.Index != p))
                    highs.Add(new Swing { Index = p, Price = bars.HighPrices[p] });
                if (isLow && lows.All(s => s.Index != p))
                    lows.Add(new Swing { Index = p, Price = bars.LowPrices[p] });
            }
        }

        private static void TrimSwings(List<Swing> swings)
        {
            while (swings.Count > 30) swings.RemoveAt(0);
        }

        // =================================================================
        //  Position events / management
        // =================================================================
        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            if (!IsBotPosition(args.Position)) return;
            // Only count trades for the main position (runner is part of the same setup)
            if (args.Position.Label == Label) _tradesToday++;
            var p = args.Position;
            if (p.StopLoss.HasValue)
                _trailState[p.Id] = (Math.Abs(p.EntryPrice - p.StopLoss.Value), p.EntryPrice);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (!IsBotPosition(pos)) return;

            _trailState.Remove(pos.Id);
            _partialDone.Remove(pos.Id);

            // Runner exits are informational only — don't affect consecutive loss counter
            if (pos.Label == RunnerLabel)
            {
                Print($"RUNNER #{pos.Id} closed ({(pos.NetProfit >= 0 ? "+" : "")}{pos.NetProfit:F2}).");
                return;
            }

            if (pos.NetProfit < 0)
            {
                _consecutiveLossesToday++;
                Print($"Trade #{pos.Id} closed with LOSS ({pos.NetProfit:F2}). Consecutive losses today: {_consecutiveLossesToday}/{MaxConsecutiveLosses}");
                if (_consecutiveLossesToday >= MaxConsecutiveLosses)
                {
                    HaltDay($"CONSECUTIVE LOSS CIRCUIT BREAKER: {_consecutiveLossesToday} losses in a row today.");
                }
            }
            else if (pos.NetProfit > 0)
            {
                Print($"Trade #{pos.Id} closed with PROFIT (+{pos.NetProfit:F2}). Resetting consecutive loss counter.");
                _consecutiveLossesToday = 0;
            }
        }

        private void CheckPendingOrdersTargetReached()
        {
            if (CancelLimitIfPriceHitsR <= 0) return;

            foreach (var order in PendingOrders.Where(o => IsBotOrder(o)).ToArray())
            {
                if (!order.StopLoss.HasValue) continue;
                var riskDist = Math.Abs(order.TargetPrice - order.StopLoss.Value);
                if (riskDist <= 0) continue;

                if (order.TradeType == TradeType.Buy)
                {
                    var progress = Symbol.Bid - order.TargetPrice;
                    if (progress >= CancelLimitIfPriceHitsR * riskDist)
                    {
                        Print($"Pending BUY LIMIT cancelled: Price moved +{progress / riskDist:F2}R towards target without filling (setup played out).");
                        if (CountOrderOp(force: true))
                            CancelPendingOrder(order);
                    }
                }
                else if (order.TradeType == TradeType.Sell)
                {
                    var progress = order.TargetPrice - Symbol.Ask;
                    if (progress >= CancelLimitIfPriceHitsR * riskDist)
                    {
                        Print($"Pending SELL LIMIT cancelled: Price moved +{progress / riskDist:F2}R towards target without filling (setup played out).");
                        if (CountOrderOp(force: true))
                            CancelPendingOrder(order);
                    }
                }
            }
        }

        // ---------------- Partial close ----------------
        private readonly HashSet<int> _partialDone = new HashSet<int>();

        private void ManagePartialClose()
        {
            // Partial close only applies to main positions, NOT runners (runners ride the full trend)
            foreach (var pos in Positions.Where(p => p.Label == Label && p.SymbolName == SymbolName).ToArray())
            {
                if (_partialDone.Contains(pos.Id) || !_trailState.TryGetValue(pos.Id, out var st) || st.risk <= 0) continue;
                var isLong = pos.TradeType == TradeType.Buy;
                var price = isLong ? Symbol.Bid : Symbol.Ask;
                var gain = isLong ? price - pos.EntryPrice : pos.EntryPrice - price;
                if (gain < PartialCloseAtR * st.risk) continue;

                _partialDone.Add(pos.Id);
                var toClose = Symbol.NormalizeVolumeInUnits(pos.VolumeInUnits * PartialClosePct / 100.0, RoundingMode.Down);
                if (toClose >= Symbol.VolumeInUnitsMin && toClose < pos.VolumeInUnits && CountOrderOp())
                    ClosePosition(pos, toClose);
            }
        }

        // ---------------- Profit trailing ----------------
        private AverageTrueRange _ltfAtr;
        private readonly Dictionary<int, (double risk, double bestClose)> _trailState = new Dictionary<int, (double, double)>();

        private void ManageTrailing(bool onBarClose)
        {
            var i = Bars.Count - 2;                     // last closed chart bar
            foreach (var pos in Positions.Where(p => p.Label == Label && p.SymbolName == SymbolName).ToArray())
            {
                if (pos.StopLoss == null || !_trailState.TryGetValue(pos.Id, out var st) || st.risk <= 0) continue;
                var isLong = pos.TradeType == TradeType.Buy;
                var price = isLong ? Symbol.Bid : Symbol.Ask;

                double? newSl = null;
                if (Trailing == TrailMode.FixedPips)
                {
                    var best = isLong ? Math.Max(st.bestClose, price) : Math.Min(st.bestClose, price);
                    _trailState[pos.Id] = (st.risk, best);
                    var dist = TrailPips * Symbol.PipSize;
                    var bestGain = isLong ? best - pos.EntryPrice : pos.EntryPrice - best;
                    if (bestGain >= dist)
                        newSl = isLong ? best - dist : best + dist;
                }
                else if (Trailing == TrailMode.StepLock)
                {
                    var gain = isLong ? price - pos.EntryPrice : pos.EntryPrice - price;
                    if (gain >= 1.5 * st.risk)
                        newSl = isLong ? pos.EntryPrice + 0.5 * st.risk : pos.EntryPrice - 0.5 * st.risk;
                }
                else if (onBarClose && i > 0)
                {
                    var close = Bars.ClosePrices[i];
                    var best = isLong ? Math.Max(st.bestClose, close) : Math.Min(st.bestClose, close);
                    _trailState[pos.Id] = (st.risk, best);
                    var bestGain = isLong ? best - pos.EntryPrice : pos.EntryPrice - best;
                    if (bestGain < TrailStartR * st.risk) continue;

                    if (Trailing == TrailMode.Structure)
                    {
                        var swing = isLong ? _ltfLows.LastOrDefault() : _ltfHighs.LastOrDefault();
                        if (swing != null)
                            newSl = isLong ? swing.Price - SlBufferPips * Symbol.PipSize : swing.Price + SlBufferPips * Symbol.PipSize;
                    }
                    else if (Trailing == TrailMode.Atr)
                    {
                        var atr = _ltfAtr.Result[i];
                        if (!double.IsNaN(atr) && atr > 0)
                            newSl = isLong ? best - TrailAtrMult * atr : best + TrailAtrMult * atr;
                    }
                }
                if (newSl == null) continue;

                var tighter = isLong ? newSl.Value > pos.StopLoss.Value + Symbol.PipSize : newSl.Value < pos.StopLoss.Value - Symbol.PipSize;
                var valid = isLong ? newSl.Value < price : newSl.Value > price;
                if (tighter && valid && CountOrderOp())
                    ModifyPosition(pos, newSl.Value, pos.TakeProfit);
            }
        }

        private void ManageBreakEven()
        {
            // Break-even only applies to main positions — runners use ManageRunnerTrailing instead
            foreach (var pos in Positions.Where(p => p.Label == Label && p.SymbolName == SymbolName))
            {
                if (pos.StopLoss == null) continue;
                var risk = Math.Abs(pos.EntryPrice - pos.StopLoss.Value);
                if (risk <= 0) continue;

                var alreadyBe = pos.TradeType == TradeType.Buy
                    ? pos.StopLoss.Value >= pos.EntryPrice
                    : pos.StopLoss.Value <= pos.EntryPrice;
                if (alreadyBe) continue;

                var current = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                var triggered = pos.TradeType == TradeType.Buy
                    ? current >= pos.EntryPrice + BeTriggerR * risk
                    : current <= pos.EntryPrice - BeTriggerR * risk;

                if (triggered && CountOrderOp())
                {
                    var newSl = pos.TradeType == TradeType.Buy
                        ? pos.EntryPrice + BeOffsetPips * Symbol.PipSize
                        : pos.EntryPrice - BeOffsetPips * Symbol.PipSize;

                    ModifyPosition(pos, newSl, pos.TakeProfit);
                    Print($"Break-even triggered for {pos.TradeType} #{pos.Id} at +{BeTriggerR:F1}R (SL -> {newSl:F2})");
                }
            }
        }

        // ---------------- Runner Structure Trailing ----------------
        // The runner has NO take-profit — it rides the macro trend using M5 structure swings.
        // After RunnerTrailStartR (default 1.5R) in profit, the stop trails behind each new
        // confirmed LTF swing (low for longs, high for shorts) + SlBufferPips.
        // The stop only ever tightens, never loosens.
        private void ManageRunnerTrailing(bool onBarClose)
        {
            foreach (var pos in Positions.Where(p => p.Label == RunnerLabel && p.SymbolName == SymbolName).ToArray())
            {
                if (pos.StopLoss == null || !_trailState.TryGetValue(pos.Id, out var st) || st.risk <= 0) continue;
                var isLong = pos.TradeType == TradeType.Buy;
                var price = isLong ? Symbol.Bid : Symbol.Ask;
                var gain = isLong ? price - pos.EntryPrice : pos.EntryPrice - price;

                // Stage 1: Before RunnerTrailStartR, do nothing (runner keeps its original SL)
                if (gain < RunnerTrailStartR * st.risk) continue;

                // Step-Lock: Once gain reaches RunnerStepTriggerR (default +3.0R), immediately lock in RunnerStepLockR (default +1.5R)
                if (EnableRunnerStepLock && gain >= RunnerStepTriggerR * st.risk)
                {
                    var lockPrice = isLong
                        ? pos.EntryPrice + RunnerStepLockR * st.risk
                        : pos.EntryPrice - RunnerStepLockR * st.risk;

                    var isTighter = isLong
                        ? lockPrice > pos.StopLoss.Value + Symbol.PipSize
                        : lockPrice < pos.StopLoss.Value - Symbol.PipSize;

                    if (isTighter && CountOrderOp())
                    {
                        ModifyPosition(pos, lockPrice, pos.TakeProfit);
                        Print($"RUNNER STEP-LOCK: {pos.TradeType} #{pos.Id} locked at +{RunnerStepLockR:F1}R (SL -> {lockPrice:F2}, current gain +{gain / st.risk:F1}R)");
                    }
                }

                // Stage 2: On bar close, trail using LTF confirmed structure swings
                if (!onBarClose) continue;

                var swing = isLong ? _ltfLows.LastOrDefault() : _ltfHighs.LastOrDefault();
                if (swing == null) continue;

                var newSl = isLong
                    ? swing.Price - SlBufferPips * Symbol.PipSize
                    : swing.Price + SlBufferPips * Symbol.PipSize;

                // Safety: new SL must be tighter than current AND on the correct side of price
                var tighter = isLong
                    ? newSl > pos.StopLoss.Value + Symbol.PipSize
                    : newSl < pos.StopLoss.Value - Symbol.PipSize;
                var valid = isLong ? newSl < price : newSl > price;

                if (tighter && valid && CountOrderOp())
                {
                    ModifyPosition(pos, newSl, pos.TakeProfit);
                    Print($"RUNNER TRAIL: {pos.TradeType} #{pos.Id} SL -> {newSl:F2} (structure swing @ {swing.Price:F2}, gain +{gain / st.risk:F1}R)");
                }
            }
        }

        // =================================================================
        //  FTMO guardrails (shared framework with FtmoOrbScalper)
        // =================================================================
        private void HaltDay(string reason, bool closePositions = true)
        {
            if (_tradingHaltedToday) return;
            _tradingHaltedToday = true;
            _haltReason = reason;
            Print($"TRADING HALTED for the day — {reason}");
            if (closePositions) FlattenAll(reason);
            else CancelAllPending(reason);
        }

        private void FlattenAll(string reason)
        {
            CancelAllPending(reason);
            foreach (var pos in Positions.Where(p => IsBotPosition(p)).ToArray())
                if (CountOrderOp(force: true))
                    ClosePosition(pos);
        }

        private void CancelAllPending(string reason)
        {
            foreach (var order in PendingOrders.Where(o => IsBotOrder(o)).ToArray())
                if (CountOrderOp(force: true))
                    CancelPendingOrder(order);
        }

        private bool CountOrderOp(bool force = false)
        {
            if (!force && _orderOpsToday >= MaxOrderOpsPerDay)
            {
                HaltDay($"ORDER OPS CAP: {_orderOpsToday} operations today");
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
            {
                if (now >= w.From - preBuffer && now <= w.To + postBuffer)
                    return true;
            }
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
            Print($"--- New CE(S)T day {_currentDay:yyyy-MM-dd} | day-start baseline {_dayStartBalance:F0} (Balance: {Account.Balance:F0}, Equity: {Account.Equity:F0}) ---");
        }


        private bool HasOpenPositions() =>
            Positions.Any(p => IsBotPosition(p));

        private bool HasPendingOrders() =>
            PendingOrders.Any(o => IsBotOrder(o));

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
