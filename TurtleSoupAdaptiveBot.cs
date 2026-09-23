using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class TurtleSoupAdaptiveBot : Robot
    {
        private const string BotLabel = "TS_ADAPT_V1";
        private const int FeatureCount = 11;
        private const int ModelSeedVersion = 1;

        private Bars _dailyBars;
        private Bars _higherTimeFrameBars;
        private readonly List<ShadowObservation> _shadowObservations = new List<ShadowObservation>();
        private readonly double[] _modelWeights = new double[FeatureCount + 1]; // [0] = bias

        private DateTime _currentDay;
        private double _dayStartEquity;
        private int _tradesToday;
        private int _consecutiveLosses;
        private int _barsSinceTrade = 1000000;
        private int _learnedSamples;
        private int _learnedWins;

        // -------------------- SAFETY --------------------

        [Parameter("Enable Real Orders", Group = "01 Safety", DefaultValue = false)]
        public bool EnableRealOrders { get; set; }

        [Parameter("Risk % / Trade", Group = "01 Safety", DefaultValue = 0.25, MinValue = 0.05, MaxValue = 1.0, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("Hard Risk Cap %", Group = "01 Safety", DefaultValue = 0.50, MinValue = 0.10, MaxValue = 2.0, Step = 0.05)]
        public double HardRiskCapPercent { get; set; }

        [Parameter("Max Daily Equity Loss %", Group = "01 Safety", DefaultValue = 1.00, MinValue = 0.25, MaxValue = 5.0, Step = 0.25)]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Max Trades / Day", Group = "01 Safety", DefaultValue = 2, MinValue = 1, MaxValue = 10)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Max Consecutive Losses", Group = "01 Safety", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Max Open Positions", Group = "01 Safety", DefaultValue = 1, MinValue = 1, MaxValue = 3)]
        public int MaxOpenPositions { get; set; }

        [Parameter("Cooldown Bars", Group = "01 Safety", DefaultValue = 4, MinValue = 0, MaxValue = 100)]
        public int CooldownBars { get; set; }

        [Parameter("Max Free Margin Use %", Group = "01 Safety", DefaultValue = 20.0, MinValue = 1.0, MaxValue = 80.0, Step = 1.0)]
        public double MaxFreeMarginUsePercent { get; set; }

        [Parameter("Max Spread / ATR", Group = "01 Safety", DefaultValue = 0.12, MinValue = 0.01, MaxValue = 0.50, Step = 0.01)]
        public double MaxSpreadAtrFraction { get; set; }

        // -------------------- TURTLE SOUP / LIQUIDITY --------------------

        [Parameter("Liquidity Lookback", Group = "02 Turtle Soup", DefaultValue = 20, MinValue = 10, MaxValue = 100)]
        public int LiquidityLookback { get; set; }

        [Parameter("Min Extreme Age", Group = "02 Turtle Soup", DefaultValue = 4, MinValue = 2, MaxValue = 10)]
        public int MinimumExtremeAge { get; set; }

        [Parameter("ATR Period", Group = "02 Turtle Soup", DefaultValue = 14, MinValue = 5, MaxValue = 100)]
        public int AtrPeriod { get; set; }

        [Parameter("Max Sweep Depth ATR", Group = "02 Turtle Soup", DefaultValue = 0.35, MinValue = 0.05, MaxValue = 1.50, Step = 0.05)]
        public double MaxSweepAtr { get; set; }

        [Parameter("Stop Buffer ATR", Group = "02 Turtle Soup", DefaultValue = 0.10, MinValue = 0.00, MaxValue = 0.50, Step = 0.01)]
        public double StopBufferAtr { get; set; }

        [Parameter("Min Reward/Risk", Group = "02 Turtle Soup", DefaultValue = 1.50, MinValue = 1.0, MaxValue = 5.0, Step = 0.10)]
        public double MinimumRewardRisk { get; set; }

        [Parameter("Target Reward/Risk", Group = "02 Turtle Soup", DefaultValue = 2.00, MinValue = 1.0, MaxValue = 8.0, Step = 0.10)]
        public double TargetRewardRisk { get; set; }

        [Parameter("Min Signal Score", Group = "02 Turtle Soup", DefaultValue = 72.0, MinValue = 50.0, MaxValue = 95.0, Step = 1.0)]
        public double MinimumSignalScore { get; set; }

        [Parameter("Equal-Level Tolerance ATR", Group = "02 Turtle Soup", DefaultValue = 0.08, MinValue = 0.01, MaxValue = 0.30, Step = 0.01)]
        public double EqualLevelToleranceAtr { get; set; }

        [Parameter("Synthetic Mode", Group = "02 Turtle Soup", DefaultValue = false)]
        public bool SyntheticMode { get; set; }

        [Parameter("Higher Timeframe", Group = "02 Turtle Soup", DefaultValue = "Hour")]
        public TimeFrame HigherTimeFrame { get; set; }

        // -------------------- ACTIVE LEARNING --------------------

        [Parameter("Enable Shadow Learning", Group = "03 Learning", DefaultValue = true)]
        public bool EnableShadowLearning { get; set; }

        [Parameter("Learning Rate", Group = "03 Learning", DefaultValue = 0.035, MinValue = 0.001, MaxValue = 0.20, Step = 0.001)]
        public double LearningRate { get; set; }

        [Parameter("Min Samples Before Influence", Group = "03 Learning", DefaultValue = 50, MinValue = 10, MaxValue = 1000)]
        public int MinimumLearningSamples { get; set; }

        [Parameter("Max Learning Score +/-", Group = "03 Learning", DefaultValue = 10.0, MinValue = 0.0, MaxValue = 20.0, Step = 1.0)]
        public double MaxLearningScoreAdjustment { get; set; }

        [Parameter("Shadow Horizon Bars", Group = "03 Learning", DefaultValue = 24, MinValue = 5, MaxValue = 200)]
        public int ShadowHorizonBars { get; set; }

        [Parameter("Max Active Shadows", Group = "03 Learning", DefaultValue = 80, MinValue = 10, MaxValue = 500)]
        public int MaxShadowObservations { get; set; }

        [Parameter("Model Seed", Group = "03 Learning", DefaultValue = "")]
        public string ModelSeed { get; set; }

        // -------------------- DIAGNOSTICS --------------------

        [Parameter("Verbose Log", Group = "04 Diagnostics", DefaultValue = true)]
        public bool VerboseLog { get; set; }

        protected override void OnStart()
        {
            _dailyBars = MarketData.GetBars(TimeFrame.Daily, SymbolName);
            _higherTimeFrameBars = MarketData.GetBars(HigherTimeFrame, SymbolName);

            Positions.Closed += OnPositionClosed;

            ResetDailyState();
            LoadModelState();

            Print("{0} started on {1} {2}. Real orders: {3}. Shadow learning: {4}.",
                BotLabel, SymbolName, TimeFrame, EnableRealOrders, EnableShadowLearning);
            Print("Safety: target risk {0:F2}% | hard cap {1:F2}% | daily loss cap {2:F2}% | min volume {3} units.",
                RiskPercent, HardRiskCapPercent, MaxDailyLossPercent, Symbol.VolumeInUnitsMin);

            if (EnableRealOrders && Account.IsLive)
                Print("WARNING: LIVE account detected. The Account Survival Gate can reject every trade if minimum position size is unsafe.");
            else if (!EnableRealOrders)
                Print("SHADOW MODE: no market orders will be sent. Signals and learning still run.");
        }

        protected override void OnBarClosed()
        {
            RollDailyStateIfNeeded();
            _barsSinceTrade++;

            if (Bars.Count < Math.Max(LiquidityLookback + MinimumExtremeAge + 10, AtrPeriod + 10))
                return;

            var closedBar = Bars.Last(0);
            UpdateShadowObservations(closedBar);

            var signal = FindBestSignal();
            if (signal == null)
                return;

            QueueShadowObservation(signal);

            if (VerboseLog)
            {
                Print("SETUP {0}: {1} | score {2:F1} | manual {3:F1} | learned P {4:P0} | level {5} @ {6} | RR room {7:F2}",
                    signal.Direction, signal.SetupName, signal.Score, signal.ManualScore, signal.LearnedProbability,
                    signal.LevelName, signal.LiquidityLevel, signal.AvailableRewardRisk);
            }

            if (!EnableRealOrders)
                return;

            string rejectionReason;
            double volume;
            double stopLossPips;
            double takeProfitPips;

            if (!PassesTradeFirewall(signal, out volume, out stopLossPips, out takeProfitPips, out rejectionReason))
            {
                if (VerboseLog)
                    Print("NO TRADE: {0}", rejectionReason);
                return;
            }

            var comment = string.Format("TS score={0:F0} learn={1:P0}", signal.Score, signal.LearnedProbability);
            var result = ExecuteMarketOrder(signal.Direction, SymbolName, volume, BotLabel, stopLossPips, takeProfitPips, comment, false);

            if (result.IsSuccessful)
            {
                _tradesToday++;
                _barsSinceTrade = 0;
                Print("TRADE OPENED: {0} {1} units | SL {2:F1} pips | TP {3:F1} pips | estimated score {4:F1}",
                    signal.Direction, volume, stopLossPips, takeProfitPips, signal.Score);
            }
            else
            {
                Print("ORDER FAILED: {0}", result.Error);
            }
        }

        protected override void OnStop()
        {
            PersistModelState(true);
            Positions.Closed -= OnPositionClosed;
        }

        protected override void OnError(Error error)
        {
            Print("TRADE ERROR: {0}", error);
        }

        private SignalCandidate FindBestSignal()
        {
            // Conservative live pattern: sweep/reclaim bar + one confirmation bar (Turtle Soup Plus One style).
            var confirmation = Bars.Last(0);
            var sweep = Bars.Last(1);
            double atr = CalculateAtr(AtrPeriod);

            if (atr <= 0)
                return null;

            var levels = BuildLiquidityLevels(atr);
            var candidates = new List<SignalCandidate>();

            foreach (var level in levels)
            {
                var buy = BuildCandidate(TradeType.Buy, level, sweep, confirmation, atr);
                if (buy != null)
                    candidates.Add(buy);

                var sell = BuildCandidate(TradeType.Sell, level, sweep, confirmation, atr);
                if (sell != null)
                    candidates.Add(sell);
            }

            if (candidates.Count == 0)
                return null;

            return candidates.OrderByDescending(c => c.Score).First();
        }

        private List<LiquidityLevel> BuildLiquidityLevels(double atr)
        {
            var levels = new List<LiquidityLevel>();

            double classicHigh;
            int highOffset;
            if (TryGetExtremeBeforeSweep(true, out classicHigh, out highOffset) && highOffset - 1 >= MinimumExtremeAge)
                AddOrMergeLevel(levels, classicHigh, true, "20-bar high", true, false, atr);

            double classicLow;
            int lowOffset;
            if (TryGetExtremeBeforeSweep(false, out classicLow, out lowOffset) && lowOffset - 1 >= MinimumExtremeAge)
                AddOrMergeLevel(levels, classicLow, false, "20-bar low", true, false, atr);

            if (_dailyBars != null && _dailyBars.Count >= 2)
            {
                var previousDay = _dailyBars.Last(1);
                AddOrMergeLevel(levels, previousDay.High, true, "previous-day high", false, true, atr);
                AddOrMergeLevel(levels, previousDay.Low, false, "previous-day low", false, true, atr);
            }

            foreach (var level in levels)
                level.EqualLevelStrength = CalculateEqualLevelStrength(level.Price, level.IsHigh, atr);

            return levels;
        }

        private void AddOrMergeLevel(List<LiquidityLevel> levels, double price, bool isHigh, string name, bool classic, bool previousDay, double atr)
        {
            var existing = levels.FirstOrDefault(x => x.IsHigh == isHigh && Math.Abs(x.Price - price) <= atr * 0.05);
            if (existing != null)
            {
                existing.IsClassic = existing.IsClassic || classic;
                existing.IsPreviousDay = existing.IsPreviousDay || previousDay;
                if (!existing.Name.Contains(name))
                    existing.Name += "+" + name;
                return;
            }

            levels.Add(new LiquidityLevel
            {
                Price = price,
                IsHigh = isHigh,
                Name = name,
                IsClassic = classic,
                IsPreviousDay = previousDay
            });
        }

        private SignalCandidate BuildCandidate(TradeType direction, LiquidityLevel level, Bar sweep, Bar confirmation, double atr)
        {
            bool isBuy = direction == TradeType.Buy;

            if (isBuy && level.IsHigh)
                return null;
            if (!isBuy && !level.IsHigh)
                return null;

            bool sweptAndReclaimed;
            double penetration;

            if (isBuy)
            {
                sweptAndReclaimed = sweep.Low < level.Price && sweep.Close > level.Price;
                penetration = level.Price - sweep.Low;
            }
            else
            {
                sweptAndReclaimed = sweep.High > level.Price && sweep.Close < level.Price;
                penetration = sweep.High - level.Price;
            }

            if (!sweptAndReclaimed || penetration <= Symbol.TickSize || penetration > MaxSweepAtr * atr)
                return null;

            // Plus-One confirmation must keep price on the reclaimed side.
            if (isBuy && confirmation.Close <= level.Price)
                return null;
            if (!isBuy && confirmation.Close >= level.Price)
                return null;

            double entry = confirmation.Close;
            double stop = isBuy
                ? sweep.Low - StopBufferAtr * atr
                : sweep.High + StopBufferAtr * atr;

            double riskDistance = Math.Abs(entry - stop);
            if (riskDistance < Symbol.TickSize * 2)
                return null;

            double oppositeLiquidity = FindOppositeLiquidity(direction, entry);
            double availableRewardRisk = isBuy
                ? (oppositeLiquidity - entry) / riskDistance
                : (entry - oppositeLiquidity) / riskDistance;

            if (double.IsNaN(availableRewardRisk) || double.IsInfinity(availableRewardRisk))
                return null;

            // We still shadow-learn marginal candidates, but impossible/negative targets are discarded.
            if (availableRewardRisk <= 0)
                return null;

            double targetR = Math.Min(TargetRewardRisk, availableRewardRisk);
            double target = isBuy
                ? entry + targetR * riskDistance
                : entry - targetR * riskDistance;

            double range = Math.Max(sweep.High - sweep.Low, Symbol.TickSize);
            double wickFraction = isBuy
                ? (Math.Min(sweep.Open, sweep.Close) - sweep.Low) / range
                : (sweep.High - Math.Max(sweep.Open, sweep.Close)) / range;
            wickFraction = Clamp01(wickFraction);

            double penetrationRatio = penetration / atr;
            double penetrationQuality = Clamp01(1.0 - Math.Abs(penetrationRatio - 0.12) / Math.Max(0.12, MaxSweepAtr));

            double reclaimDistance = isBuy ? sweep.Close - level.Price : level.Price - sweep.Close;
            double reclaimStrength = Clamp01(reclaimDistance / Math.Max(atr * 0.35, Symbol.TickSize));

            double confirmationBody = Math.Abs(confirmation.Close - confirmation.Open);
            bool confirmationDirection = isBuy ? confirmation.Close > confirmation.Open : confirmation.Close < confirmation.Open;
            double displacementStrength = confirmationDirection
                ? Clamp01(confirmationBody / Math.Max(atr * 0.50, Symbol.TickSize))
                : 0.0;

            bool microStructureBreak = HasMicroStructureBreak(direction, confirmation);
            double htfFavorability = GetHigherTimeFrameFavorability(direction);
            double rangeRegimeQuality = GetRangeRegimeQuality();
            double attackPressure = GetPreSweepAttackPressure(direction);
            double rrQuality = Clamp01((availableRewardRisk - MinimumRewardRisk) / Math.Max(0.5, TargetRewardRisk - MinimumRewardRisk + 0.5));

            double sessionQuality = SyntheticMode ? 0.50 : GetSessionQuality();

            double manualScore = 25.0;
            if (level.IsPreviousDay) manualScore += 14.0;
            if (level.IsClassic) manualScore += 12.0;
            manualScore += 7.0 * level.EqualLevelStrength;
            manualScore += 8.0 * penetrationQuality;
            manualScore += 9.0 * wickFraction;
            manualScore += 7.0 * reclaimStrength;
            manualScore += 10.0 * displacementStrength;
            manualScore += microStructureBreak ? 8.0 : 0.0;
            manualScore += 4.0 * htfFavorability;
            manualScore += 3.0 * rangeRegimeQuality;
            manualScore += 2.0 * attackPressure;
            manualScore += 1.0 * sessionQuality;
            manualScore = Math.Min(100.0, manualScore);

            var features = new[]
            {
                level.IsPreviousDay ? 1.0 : 0.0,
                level.IsClassic ? 1.0 : 0.0,
                level.EqualLevelStrength,
                penetrationQuality,
                wickFraction,
                reclaimStrength,
                displacementStrength,
                microStructureBreak ? 1.0 : 0.0,
                htfFavorability,
                rangeRegimeQuality,
                attackPressure
            };

            double learnedProbability = Predict(features);
            double adjustedScore = manualScore;

            if (_learnedSamples >= MinimumLearningSamples)
            {
                double adjustment = MaxLearningScoreAdjustment * (2.0 * learnedProbability - 1.0);
                adjustedScore = Clamp(manualScore + adjustment, 0.0, 100.0);
            }

            return new SignalCandidate
            {
                Direction = direction,
                SetupName = level.IsClassic ? "Turtle Soup +1" : "Liquidity Sweep +1",
                LevelName = level.Name,
                LiquidityLevel = level.Price,
                EntryReference = entry,
                StopReference = stop,
                TargetReference = target,
                RiskDistance = riskDistance,
                AvailableRewardRisk = availableRewardRisk,
                ManualScore = manualScore,
                LearnedProbability = learnedProbability,
                Score = adjustedScore,
                Features = features,
                Atr = atr
            };
        }

        private bool PassesTradeFirewall(SignalCandidate signal, out double volume, out double stopLossPips, out double takeProfitPips, out string reason)
        {
            volume = 0;
            stopLossPips = 0;
            takeProfitPips = 0;
            reason = string.Empty;

            if (signal.Score < MinimumSignalScore)
            {
                reason = string.Format("score {0:F1} is below minimum {1:F1}", signal.Score, MinimumSignalScore);
                return false;
            }

            if (signal.AvailableRewardRisk < MinimumRewardRisk)
            {
                reason = string.Format("only {0:F2}R available before opposite liquidity; minimum is {1:F2}R", signal.AvailableRewardRisk, MinimumRewardRisk);
                return false;
            }

            if (_tradesToday >= MaxTradesPerDay)
            {
                reason = "maximum trades for the day reached";
                return false;
            }

            if (_consecutiveLosses >= MaxConsecutiveLosses)
            {
                reason = "consecutive-loss lockout is active";
                return false;
            }

            if (_barsSinceTrade < CooldownBars)
            {
                reason = "cooldown period is still active";
                return false;
            }

            if (Positions.FindAll(BotLabel, SymbolName).Length >= MaxOpenPositions)
            {
                reason = "maximum open positions reached";
                return false;
            }

            double dailyLossPercent = _dayStartEquity > 0
                ? Math.Max(0.0, (_dayStartEquity - Account.Equity) / _dayStartEquity * 100.0)
                : 100.0;

            if (dailyLossPercent >= MaxDailyLossPercent)
            {
                reason = string.Format("daily equity loss {0:F2}% reached the {1:F2}% cap", dailyLossPercent, MaxDailyLossPercent);
                return false;
            }

            double spreadPrice = Math.Max(Symbol.Ask - Symbol.Bid, Symbol.Spread);
            if (spreadPrice / Math.Max(signal.Atr, Symbol.TickSize) > MaxSpreadAtrFraction)
            {
                reason = "spread is too large relative to current ATR";
                return false;
            }

            double marketEntry = signal.Direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double stopPrice = signal.StopReference;
            double stopDistancePrice = Math.Abs(marketEntry - stopPrice);

            if ((signal.Direction == TradeType.Buy && stopPrice >= marketEntry) ||
                (signal.Direction == TradeType.Sell && stopPrice <= marketEntry))
            {
                reason = "market moved through the structural stop before entry";
                return false;
            }

            stopLossPips = stopDistancePrice / Symbol.PipSize;
            if (stopLossPips <= 0)
            {
                reason = "invalid stop distance";
                return false;
            }

            // Account Survival Gate: calculate normal risk-sized volume first.
            double calculatedVolume = Symbol.VolumeForProportionalRisk(
                ProportionalAmountType.Equity,
                RiskPercent,
                stopLossPips,
                RoundingMode.Down);

            calculatedVolume = Symbol.NormalizeVolumeInUnits(calculatedVolume, RoundingMode.Down);

            if (calculatedVolume < Symbol.VolumeInUnitsMin)
            {
                // Never force a minimum-size trade unless its structural stop still fits the hard risk cap.
                double allowedStopAtMinVolume = Symbol.PipsForProportionalRisk(
                    ProportionalAmountType.Equity,
                    HardRiskCapPercent,
                    Symbol.VolumeInUnitsMin);

                if (double.IsNaN(allowedStopAtMinVolume) || allowedStopAtMinVolume <= 0 || stopLossPips > allowedStopAtMinVolume)
                {
                    reason = string.Format("minimum trade size would risk more than the {0:F2}% hard cap", HardRiskCapPercent);
                    return false;
                }

                volume = Symbol.VolumeInUnitsMin;
            }
            else
            {
                volume = calculatedVolume;
            }

            if (volume < Symbol.VolumeInUnitsMin || volume > Symbol.VolumeInUnitsMax)
            {
                reason = "calculated volume is outside broker limits";
                return false;
            }

            double estimatedMargin = Symbol.GetEstimatedMargin(signal.Direction, volume);
            double allowedMargin = Math.Max(0.0, Account.FreeMargin * MaxFreeMarginUsePercent / 100.0);

            if (estimatedMargin > allowedMargin)
            {
                reason = string.Format("estimated margin {0:F4} exceeds allowed free-margin use {1:F4}", estimatedMargin, allowedMargin);
                return false;
            }

            double riskDistance = stopDistancePrice;
            double rrCap = Math.Min(TargetRewardRisk, signal.AvailableRewardRisk);
            double takeProfitPrice = signal.Direction == TradeType.Buy
                ? marketEntry + riskDistance * rrCap
                : marketEntry - riskDistance * rrCap;

            takeProfitPips = Math.Abs(takeProfitPrice - marketEntry) / Symbol.PipSize;

            if (takeProfitPips / stopLossPips < MinimumRewardRisk)
            {
                reason = "market movement since signal reduced reward/risk below minimum";
                return false;
            }

            return true;
        }

        private bool TryGetExtremeBeforeSweep(bool high, out double price, out int offset)
        {
            price = high ? double.MinValue : double.MaxValue;
            offset = -1;

            // Sweep is Bars.Last(1); examine the preceding N completed bars.
            for (int i = 2; i < 2 + LiquidityLookback && i < Bars.Count; i++)
            {
                var bar = Bars.Last(i);
                double value = high ? bar.High : bar.Low;

                if ((high && value > price) || (!high && value < price))
                {
                    price = value;
                    offset = i;
                }
            }

            return offset >= 0;
        }

        private double CalculateEqualLevelStrength(double level, bool highLevel, double atr)
        {
            double tolerance = Math.Max(Symbol.TickSize * 2, atr * EqualLevelToleranceAtr);
            int touches = 0;

            for (int i = 2; i < 2 + LiquidityLookback && i < Bars.Count; i++)
            {
                var bar = Bars.Last(i);
                double value = highLevel ? bar.High : bar.Low;
                if (Math.Abs(value - level) <= tolerance)
                    touches++;
            }

            if (touches <= 1)
                return 0.0;

            return Clamp01((touches - 1) / 3.0);
        }

        private bool HasMicroStructureBreak(TradeType direction, Bar confirmation)
        {
            int lookback = Math.Min(3, Bars.Count - 3);
            if (lookback <= 0)
                return false;

            if (direction == TradeType.Buy)
            {
                double microHigh = double.MinValue;
                for (int i = 2; i < 2 + lookback; i++)
                    microHigh = Math.Max(microHigh, Bars.Last(i).High);
                return confirmation.Close > microHigh;
            }
            else
            {
                double microLow = double.MaxValue;
                for (int i = 2; i < 2 + lookback; i++)
                    microLow = Math.Min(microLow, Bars.Last(i).Low);
                return confirmation.Close < microLow;
            }
        }

        private double FindOppositeLiquidity(TradeType direction, double entry)
        {
            if (direction == TradeType.Buy)
            {
                double target = double.MinValue;
                for (int i = 2; i < 2 + LiquidityLookback && i < Bars.Count; i++)
                    target = Math.Max(target, Bars.Last(i).High);

                if (_dailyBars != null && _dailyBars.Count >= 2)
                    target = Math.Max(target, _dailyBars.Last(1).High);

                return target;
            }
            else
            {
                double target = double.MaxValue;
                for (int i = 2; i < 2 + LiquidityLookback && i < Bars.Count; i++)
                    target = Math.Min(target, Bars.Last(i).Low);

                if (_dailyBars != null && _dailyBars.Count >= 2)
                    target = Math.Min(target, _dailyBars.Last(1).Low);

                return target;
            }
        }

        private double GetPreSweepAttackPressure(TradeType reversalDirection)
        {
            // A measured approach toward liquidity is treated as evidence that the level was being "attacked".
            // This does NOT assume manipulation; it only quantifies observable approach/compression behaviour.
            if (Bars.Count < 6)
                return 0.5;

            var b2 = Bars.Last(2);
            var b3 = Bars.Last(3);
            var b4 = Bars.Last(4);

            bool directionalApproach = reversalDirection == TradeType.Buy
                ? b2.Close < b3.Close && b3.Close < b4.Close
                : b2.Close > b3.Close && b3.Close > b4.Close;

            double atr = CalculateAtr(AtrPeriod);
            double avgRange = ((b2.High - b2.Low) + (b3.High - b3.Low) + (b4.High - b4.Low)) / 3.0;
            double compression = atr > 0 ? Clamp01(1.25 - avgRange / atr) : 0.0;

            return Clamp01((directionalApproach ? 0.60 : 0.20) + 0.40 * compression);
        }

        private double GetHigherTimeFrameFavorability(TradeType direction)
        {
            if (_higherTimeFrameBars == null || _higherTimeFrameBars.Count < 15)
                return 0.5;

            // Ignore the currently forming HTF bar; compare completed closes.
            double recent = _higherTimeFrameBars.Last(1).Close;
            double older = _higherTimeFrameBars.Last(8).Close;
            double change = recent - older;

            double htfAtr = CalculateAtr(_higherTimeFrameBars, 10, 1);
            if (htfAtr <= 0)
                return 0.5;

            double normalizedSlope = change / (htfAtr * 3.0);
            normalizedSlope = Clamp(normalizedSlope, -1.0, 1.0);

            // A reversal is safer when the HTF is supportive or at least not strongly hostile.
            if (direction == TradeType.Buy)
                return Clamp01(0.5 + 0.5 * normalizedSlope);
            return Clamp01(0.5 - 0.5 * normalizedSlope);
        }

        private double GetRangeRegimeQuality()
        {
            int period = Math.Min(20, Bars.Count - 2);
            if (period < 5)
                return 0.5;

            double directionalMove = Math.Abs(Bars.Last(0).Close - Bars.Last(period).Close);
            double path = 0.0;

            for (int i = 0; i < period; i++)
                path += Math.Abs(Bars.Last(i).Close - Bars.Last(i + 1).Close);

            if (path <= Symbol.TickSize)
                return 0.5;

            double efficiencyRatio = Clamp01(directionalMove / path);
            return Clamp01(1.0 - efficiencyRatio);
        }

        private double GetSessionQuality()
        {
            int hour = Server.Time.Hour;

            // Broad UTC weighting only. It is a soft score, never a hard trading restriction.
            if (hour >= 7 && hour < 16)
                return 1.0; // London into NY overlap
            if (hour >= 6 && hour < 18)
                return 0.70;
            return 0.35;
        }

        private double CalculateAtr(int period)
        {
            return CalculateAtr(Bars, period, 0);
        }

        private double CalculateAtr(Bars bars, int period, int startingOffset)
        {
            if (bars == null || bars.Count < period + startingOffset + 2)
                return 0.0;

            double sum = 0.0;
            for (int i = startingOffset; i < startingOffset + period; i++)
            {
                var current = bars.Last(i);
                var previous = bars.Last(i + 1);
                double tr = Math.Max(current.High - current.Low,
                    Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
                sum += tr;
            }
            return sum / period;
        }

        // -------------------- SHADOW LEARNING --------------------

        private void QueueShadowObservation(SignalCandidate signal)
        {
            if (!EnableShadowLearning)
                return;

            if (_shadowObservations.Count >= MaxShadowObservations)
            {
                // Active-learning preference: preserve uncertain examples around 50% model probability.
                var leastUseful = _shadowObservations
                    .OrderByDescending(x => Math.Abs(x.InitialProbability - 0.5))
                    .FirstOrDefault();

                double newUncertainty = Math.Abs(signal.LearnedProbability - 0.5);
                if (leastUseful != null && newUncertainty < Math.Abs(leastUseful.InitialProbability - 0.5))
                    _shadowObservations.Remove(leastUseful);
                else
                    return;
            }

            _shadowObservations.Add(new ShadowObservation
            {
                Direction = signal.Direction,
                Entry = signal.EntryReference,
                Stop = signal.StopReference,
                Target = signal.TargetReference,
                InitialRisk = Math.Abs(signal.EntryReference - signal.StopReference),
                Features = signal.Features.ToArray(),
                InitialProbability = signal.LearnedProbability,
                SetupTime = Bars.Last(0).OpenTime,
                AgeBars = 0,
                MaximumFavourableR = 0.0,
                MaximumAdverseR = 0.0
            });
        }

        private void UpdateShadowObservations(Bar closedBar)
        {
            if (!EnableShadowLearning || _shadowObservations.Count == 0)
                return;

            var resolved = new List<ShadowObservation>();

            foreach (var obs in _shadowObservations)
            {
                obs.AgeBars++;

                bool stopHit;
                bool targetHit;
                double favourableMove;
                double adverseMove;

                if (obs.Direction == TradeType.Buy)
                {
                    stopHit = closedBar.Low <= obs.Stop;
                    targetHit = closedBar.High >= obs.Target;
                    favourableMove = closedBar.High - obs.Entry;
                    adverseMove = obs.Entry - closedBar.Low;
                }
                else
                {
                    stopHit = closedBar.High >= obs.Stop;
                    targetHit = closedBar.Low <= obs.Target;
                    favourableMove = obs.Entry - closedBar.Low;
                    adverseMove = closedBar.High - obs.Entry;
                }

                if (obs.InitialRisk > 0)
                {
                    obs.MaximumFavourableR = Math.Max(obs.MaximumFavourableR, favourableMove / obs.InitialRisk);
                    obs.MaximumAdverseR = Math.Max(obs.MaximumAdverseR, adverseMove / obs.InitialRisk);
                }

                bool? success = null;

                if (stopHit && targetHit)
                    success = false; // conservative assumption when intrabar ordering is unknown
                else if (stopHit)
                    success = false;
                else if (targetHit)
                    success = true;
                else if (obs.AgeBars >= ShadowHorizonBars)
                {
                    double finalMove = obs.Direction == TradeType.Buy
                        ? closedBar.Close - obs.Entry
                        : obs.Entry - closedBar.Close;
                    double finalR = obs.InitialRisk > 0 ? finalMove / obs.InitialRisk : -1.0;
                    success = finalR >= 0.50;
                }

                if (success.HasValue)
                {
                    UpdateModel(obs.Features, success.Value ? 1.0 : 0.0);
                    resolved.Add(obs);

                    if (VerboseLog)
                    {
                        Print("SHADOW RESOLVED: {0} | success={1} | MFE={2:F2}R | MAE={3:F2}R | samples={4} | model win rate={5:P1}",
                            obs.Direction, success.Value, obs.MaximumFavourableR, obs.MaximumAdverseR,
                            _learnedSamples, _learnedSamples > 0 ? (double)_learnedWins / _learnedSamples : 0.0);
                    }
                }
            }

            foreach (var obs in resolved)
                _shadowObservations.Remove(obs);
        }

        private double Predict(double[] features)
        {
            if (features == null || features.Length != FeatureCount)
                return 0.5;

            double z = _modelWeights[0];
            for (int i = 0; i < FeatureCount; i++)
                z += _modelWeights[i + 1] * features[i];

            z = Clamp(z, -20.0, 20.0);
            return 1.0 / (1.0 + Math.Exp(-z));
        }

        private void UpdateModel(double[] features, double outcome)
        {
            if (features == null || features.Length != FeatureCount)
                return;

            double prediction = Predict(features);
            double error = outcome - prediction;
            double rate = LearningRate / Math.Sqrt(1.0 + _learnedSamples / 100.0);
            const double l2 = 0.0005;

            _modelWeights[0] = Clamp(_modelWeights[0] + rate * error, -5.0, 5.0);

            for (int i = 0; i < FeatureCount; i++)
            {
                double updated = _modelWeights[i + 1] + rate * error * features[i] - l2 * _modelWeights[i + 1];
                _modelWeights[i + 1] = Clamp(updated, -5.0, 5.0);
            }

            _learnedSamples++;
            if (outcome >= 0.5)
                _learnedWins++;

            if (_learnedSamples % 10 == 0)
                PersistModelState(_learnedSamples % 50 == 0);
        }

        private void LoadModelState()
        {
            bool loaded = false;

            if (!string.IsNullOrWhiteSpace(ModelSeed))
                loaded = TryImportModelSeed(ModelSeed.Trim());

            if (!loaded)
            {
                try
                {
                    var localSeed = LocalStorage.GetString("ModelSeed", LocalStorageScope.Instance);
                    if (!string.IsNullOrWhiteSpace(localSeed))
                        loaded = TryImportModelSeed(localSeed.Trim());
                }
                catch
                {
                    // Local storage is optional; failure must never block trading logic.
                }
            }

            if (loaded)
                Print("Adaptive model loaded: {0} samples, historical shadow success {1:P1}.",
                    _learnedSamples, _learnedSamples > 0 ? (double)_learnedWins / _learnedSamples : 0.0);
            else
                Print("Adaptive model starts fresh. It will not influence live scores until {0} resolved shadow samples.", MinimumLearningSamples);
        }

        private void PersistModelState(bool printSeed)
        {
            string seed = ExportModelSeed();

            try
            {
                LocalStorage.SetString("ModelSeed", seed, LocalStorageScope.Instance);
                LocalStorage.Flush(LocalStorageScope.Instance);
            }
            catch
            {
                // Cloud local storage can be ephemeral; the printed seed is the portable fallback.
            }

            if (printSeed)
                Print("MODEL SEED (copy this before restarting the cloud instance): {0}", seed);
        }

        private string ExportModelSeed()
        {
            int byteCount = 12 + (_modelWeights.Length * 8);
            var bytes = new byte[byteCount];
            int offset = 0;

            Buffer.BlockCopy(BitConverter.GetBytes(ModelSeedVersion), 0, bytes, offset, 4);
            offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(_learnedSamples), 0, bytes, offset, 4);
            offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(_learnedWins), 0, bytes, offset, 4);
            offset += 4;

            foreach (double weight in _modelWeights)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(weight), 0, bytes, offset, 8);
                offset += 8;
            }

            return Convert.ToBase64String(bytes);
        }

        private bool TryImportModelSeed(string seed)
        {
            try
            {
                var bytes = Convert.FromBase64String(seed);
                int expected = 12 + (_modelWeights.Length * 8);
                if (bytes.Length != expected)
                    return false;

                int offset = 0;
                int version = BitConverter.ToInt32(bytes, offset);
                offset += 4;
                if (version != ModelSeedVersion)
                    return false;

                _learnedSamples = Math.Max(0, BitConverter.ToInt32(bytes, offset));
                offset += 4;
                _learnedWins = Math.Max(0, BitConverter.ToInt32(bytes, offset));
                offset += 4;

                for (int i = 0; i < _modelWeights.Length; i++)
                {
                    _modelWeights[i] = Clamp(BitConverter.ToDouble(bytes, offset), -5.0, 5.0);
                    offset += 8;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // -------------------- DAILY / POSITION SAFETY --------------------

        private void ResetDailyState()
        {
            _currentDay = Server.Time.Date;
            _dayStartEquity = Account.Equity;
            _tradesToday = 0;
            _consecutiveLosses = 0;
        }

        private void RollDailyStateIfNeeded()
        {
            if (Server.Time.Date == _currentDay)
                return;

            PersistModelState(true);
            ResetDailyState();
            Print("New UTC trading day. Safety counters reset; day-start equity = {0:F4} {1}.", _dayStartEquity, Account.Asset.Name);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (position == null || position.Label != BotLabel || position.SymbolName != SymbolName)
                return;

            if (position.NetProfit < 0)
                _consecutiveLosses++;
            else if (position.NetProfit > 0)
                _consecutiveLosses = 0;

            Print("POSITION CLOSED: reason={0} net={1:F4} {2} | consecutive losses={3}",
                args.Reason, position.NetProfit, Account.Asset.Name, _consecutiveLosses);
        }

        // -------------------- HELPERS --------------------

        private static double Clamp01(double value)
        {
            return Clamp(value, 0.0, 1.0);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private sealed class LiquidityLevel
        {
            public double Price { get; set; }
            public bool IsHigh { get; set; }
            public string Name { get; set; }
            public bool IsClassic { get; set; }
            public bool IsPreviousDay { get; set; }
            public double EqualLevelStrength { get; set; }
        }

        private sealed class SignalCandidate
        {
            public TradeType Direction { get; set; }
            public string SetupName { get; set; }
            public string LevelName { get; set; }
            public double LiquidityLevel { get; set; }
            public double EntryReference { get; set; }
            public double StopReference { get; set; }
            public double TargetReference { get; set; }
            public double RiskDistance { get; set; }
            public double AvailableRewardRisk { get; set; }
            public double ManualScore { get; set; }
            public double LearnedProbability { get; set; }
            public double Score { get; set; }
            public double[] Features { get; set; }
            public double Atr { get; set; }
        }

        private sealed class ShadowObservation
        {
            public TradeType Direction { get; set; }
            public double Entry { get; set; }
            public double Stop { get; set; }
            public double Target { get; set; }
            public double InitialRisk { get; set; }
            public double[] Features { get; set; }
            public double InitialProbability { get; set; }
            public DateTime SetupTime { get; set; }
            public int AgeBars { get; set; }
            public double MaximumFavourableR { get; set; }
            public double MaximumAdverseR { get; set; }
        }
    }
}
