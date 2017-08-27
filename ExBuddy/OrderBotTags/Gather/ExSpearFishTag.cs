﻿namespace ExBuddy.OrderBotTags.Gather
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Windows.Media;
    using Windows;
    using Attributes;
    using Behaviors;
    using Buddy.Coroutines;
    using Clio.Utilities;
    using Clio.XmlEngine;
    using Enumerations;
    using ff14bot;
    using ff14bot.Behavior;
    using ff14bot.Enums;
    using ff14bot.Helpers;
    using ff14bot.Managers;
    using ff14bot.Navigation;
    using ff14bot.Objects;
    using ff14bot.RemoteWindows;
    using GatherSpots;
    using Helpers;
    using Interfaces;
    using Localization;
    using Strategies;
    using TreeSharp;

#if RB_CN
    using ActionManager = ff14bot.Managers.Actionmanager;
#endif

    [LoggerName("ExSpearFish")]
    [XmlElement("ExSpearFish")]
    public class ExSpearFish : ExGatherTag
    {
        private static readonly object Lock = new object();

        protected static Regex SpearFishRegex = new Regex(
            @"You spear(?: a| an| [2-3])? (.+) measuring (\d{1,4}\.\d) ilms!",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        protected static Regex SpearFishGetAwayRegex = new Regex(
            @"The fish gets away\.\.\.",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        protected static Regex FishSizeRegex = new Regex(@"(\d{1,4}\.\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        protected static SpearResult SpearResult = new SpearResult();

        private readonly HarpoonTip _gigWindow = new HarpoonTip();

        private Func<bool> freeRangeConditionFunc;

        private bool gigSelected;

        private bool interactedWithNode;

        private int loopCount;

        private Composite poiCoroutine;

        private DateTime startTime;

        [DefaultValue(false)]
        [XmlAttribute("Collect")]
        public bool Collect { get; set; }

        [DefaultValue(false)]
        [XmlAttribute("BountifulCatch")]
        public bool BountifulCatch { get; set; }

        [XmlElement("Items")]
        public new NamedItemCollection Items { get; set; }

        [DefaultValue(0)]
        [XmlAttribute("GigId")]
        public int GigId { get; set; }

        protected override Color Info => Colors.Aquamarine;

        protected override async Task<bool> Main()
        {
            await CommonTasks.HandleLoading();

            return HandleDeath() || HandleCondition() || await CastTruth() || await SelectGig() || HandleReset() || await MoveToHotSpot()
                   || await FindNode() || await ResetOrDone();
        }

        protected override void OnDone() { TreeHooks.Instance.RemoveHook("PoiAction", poiCoroutine); }

        protected override void OnStart()
        {
            SpellDelay = SpellDelay < 0 ? 0 : SpellDelay;
            WindowDelay = WindowDelay < 1000 ? 1000 : WindowDelay;
            SkipWindowDelay = SkipWindowDelay < 200 ? 200 : SkipWindowDelay;
            GpPerTick = CharacterResource.GetGpPerTick();

            if (Distance > 3.5f)
                TreeRoot.Stop(Localization.ExGather_Distance);

            if (HotSpots != null)
                HotSpots.IsCyclic = Loops < 1;

            // backwards compatibility
            if (GatherObjects == null && !string.IsNullOrWhiteSpace(GatherObject))
                GatherObjects = new List<string> {GatherObject};

            startTime = DateTime.Now;

            if (Items == null)
                Items = new NamedItemCollection();

            if (string.IsNullOrWhiteSpace(Name))
                Name = Items.Count > 0 ? Items.First().Name : string.Format(Localization.ExGather_Zone, WorldManager.ZoneId, ExProfileBehavior.Me.Location);

            StatusText = Name;

            GamelogManager.MessageRecevied += ReceiveMessage;
            poiCoroutine = new ActionRunCoroutine(ctx => ExecutePoiLogic());
            TreeHooks.Instance.AddHook("PoiAction", poiCoroutine);
        }

        protected void ReceiveMessage(object sender, ChatEventArgs e)
        {
            if (e.ChatLogEntry.MessageType == (MessageType) 2115 && e.ChatLogEntry.Contents.StartsWith("You spear") ||
                e.ChatLogEntry.MessageType == (MessageType) 67 && e.ChatLogEntry.Contents.StartsWith("The fish"))
                MatchSpearResult(e.ChatLogEntry.Contents);
        }

        protected void MatchSpearResult(string message)
        {
            var spearResult = new SpearResult();

            var spearFishMatch = SpearFishRegex.Match(message);
            var spearFishAwayMatch = SpearFishGetAwayRegex.Match(message);
            if (spearFishMatch.Success)
            {
                spearResult.Name = spearFishMatch.Groups[1].Value;
                float.TryParse(spearFishMatch.Groups[2].Value, out float size);
                spearResult.Size = size;
                if (spearResult.Name[spearResult.Name.Length - 2] == ' ')
                    spearResult.IsHighQuality = true;
            }
            if (spearFishAwayMatch.Success)
                spearResult.Name = "none";

            SpearResult = spearResult;
        }

        private static bool HandleDeath()
        {
            if (!ExProfileBehavior.Me.IsDead || Poi.Current.Type == PoiType.Death) return false;
            Poi.Current = new Poi(ExProfileBehavior.Me, PoiType.Death);
            return true;
        }

        private bool HandleCondition()
        {
            if (WhileFunc == null)
                WhileFunc = ScriptManager.GetCondition(While);

            // If statement is true, return false so we can continue the routine
            if (WhileFunc())
                return false;

            isDone = true;
            return true;
        }

        private async Task<bool> CastTruth()
        {
            if (ExProfileBehavior.Me.CurrentJob != ClassJobType.Fisher)
                return false;

            if (ExProfileBehavior.Me.ClassLevel < 66
                || ExProfileBehavior.Me.HasAura(
#if RB_CN
                    (int)
#else
                    (uint)
#endif
                    AbilityAura.TruthOfOceans))
                return false;

            return
                await
                    CastAura(
                        Ability.Truth,
                        AbilityAura.TruthOfOceans);
        }

        private async Task<bool> SelectGig()
        {
            if (ExProfileBehavior.Me.CurrentJob != ClassJobType.Fisher)
                return false;

            if (ExProfileBehavior.Me.ClassLevel < 61
                || gigSelected)
                return false;

            gigSelected = await _gigWindow.SelectGiG(GigId);

            return gigSelected;
        }

        private bool HandleReset()
        {
            if (Node == null ||
                Node.IsValid && (!FreeRange || !(Node.Location.Distance3D(ExProfileBehavior.Me.Location) > Radius)))
                return false;

            OnResetCachedDone();
            return true;
        }

        private async Task<bool> MoveToHotSpot()
        {
            if (HotSpots == null || HotSpots.CurrentOrDefault.WithinHotSpot2D(ExProfileBehavior.Me.Location)) return false;
            var name = !string.IsNullOrWhiteSpace(HotSpots.CurrentOrDefault.Name)
                ? "[" + HotSpots.CurrentOrDefault.Name + "] "
                : string.Empty;

            StatusText = string.Format(Localization.ExGather_MoveToHotSpot, name, HotSpots.CurrentOrDefault);

            await
                HotSpots.CurrentOrDefault.XYZ.MoveTo(
                    radius: HotSpots.CurrentOrDefault.Radius * 0.75f,
                    name: HotSpots.CurrentOrDefault.Name,
                    stopCallback: MovementStopCallback);

            startTime = DateTime.Now;
            return true;
        }

        private async Task<bool> FindNode(bool retryCenterHotspot = true)
        {
            if (Node != null)
                return false;

            StatusText = Localization.ExGather_SearchForNodes;

            while (Behaviors.ShouldContinue)
            {
                IEnumerable<GatheringPointObject> nodes =
                    GameObjectManager.GetObjectsOfType<GatheringPointObject>().Where(gpo => gpo.CanGather).ToArray();

                if (GatherStrategy == GatherStrategy.TouchAndGo && HotSpots != null)
                {
                    if (GatherObjects != null)
                        nodes = nodes.Where(gpo => GatherObjects.Contains(gpo.EnglishName, StringComparer.InvariantCultureIgnoreCase));

                    foreach (var node in
                        nodes.Where(gpo => HotSpots.CurrentOrDefault.WithinHotSpot2D(gpo.Location))
                            .OrderBy(gpo => gpo.Location.Distance2D(ExProfileBehavior.Me.Location))
                            .Skip(1))
                        if (!Blacklist.Contains(node.ObjectId, BlacklistFlags.Interact))
                            Blacklist.Add(
                                node,
                                BlacklistFlags.Interact,
                                TimeSpan.FromSeconds(18),
                                Localization.ExGather_SkipFurthestNodes);
                }

                nodes = nodes.Where(gpo => !Blacklist.Contains(gpo.ObjectId, BlacklistFlags.Interact));

                if (FreeRange)
                {
                    nodes = nodes.Where(gpo => gpo.Distance2D(ExProfileBehavior.Me.Location) < Radius);
                }
                else
                {
                    if (HotSpots != null)
                        nodes = nodes.Where(gpo => HotSpots.CurrentOrDefault.WithinHotSpot2D(gpo.Location));
                }

                // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression
                if (GatherObjects != null)
                    Node =
                        nodes.OrderBy(
                                gpo =>
                                    GatherObjects.FindIndex(i => string.Equals(gpo.EnglishName, i, StringComparison.InvariantCultureIgnoreCase)))
                            .ThenBy(gpo => gpo.Location.Distance2D(ExProfileBehavior.Me.Location))
                            .FirstOrDefault(gpo => GatherObjects.Contains(gpo.EnglishName, StringComparer.InvariantCultureIgnoreCase));
                else
                    Node = nodes.OrderBy(gpo => gpo.Location.Distance2D(ExProfileBehavior.Me.Location)).FirstOrDefault();

                if (Node == null)
                {
                    if (HotSpots != null)
                    {
                        var myLocation = ExProfileBehavior.Me.Location;

                        var distanceToFurthestVisibleGameObject =
                            GameObjectManager.GameObjects.Select(o => o.Location.Distance2D(myLocation))
                                .OrderByDescending(o => o)
                                .FirstOrDefault();

                        var distanceToFurthestVectorInHotspot = myLocation.Distance2D(HotSpots.CurrentOrDefault)
                                                                + HotSpots.CurrentOrDefault.Radius;

                        if (myLocation.Distance2D(HotSpots.CurrentOrDefault) > Radius && GatherStrategy == GatherStrategy.GatherOrCollect
                            && retryCenterHotspot && distanceToFurthestVisibleGameObject <= distanceToFurthestVectorInHotspot)
                        {
                            Logger.Verbose(Localization.ExGather_DistanceObject + distanceToFurthestVisibleGameObject);
                            Logger.Verbose(Localization.ExGather_DistanceHotSpot + distanceToFurthestVectorInHotspot);

                            Logger.Warn(
                                Localization.ExGather_NoAvailableNode);
                            await HotSpots.CurrentOrDefault.XYZ.MoveTo(radius: Radius, name: HotSpots.CurrentOrDefault.Name);

                            retryCenterHotspot = false;
                            await Coroutine.Yield();
                            continue;
                        }

                        if (!await ChangeHotSpot())
                        {
                            retryCenterHotspot = false;
                            await Coroutine.Yield();
                            continue;
                        }
                    }

                    if (FreeRange && !FreeRangeConditional())
                    {
                        await Coroutine.Yield();
                        isDone = true;
                        return true;
                    }

                    return true;
                }

                var entry = Blacklist.GetEntry(Node.ObjectId);
                if (entry != null && entry.Flags.HasFlag(BlacklistFlags.Interact))
                {
                    Logger.Warn(Localization.ExGather_NodeBlacklist);

                    if (await
                            Coroutine.Wait(entry.Length,
                                () => entry.IsFinished || Node.Location.Distance2D(ExProfileBehavior.Me.Location) > Radius)
                        || Core.Player.IsDead)
                        if (!entry.IsFinished)
                        {
                            Node = null;
                            Logger.Info(Localization.ExGather_NodeReset);
                            return false;
                        }

                    Logger.Info(Localization.ExGather_NodeBlacklistRemoved);
                }

                Logger.Info(Localization.ExGather_NodeSet + Node);

                if (HotSpots == null)
                    MovementManager.SetFacing2D(Node.Location);

                if (Poi.Current.Unit != Node)
                    Poi.Current = new Poi(Node, PoiType.Gather);

                return true;
            }

            return true;
        }

        private async Task<bool> ChangeHotSpot()
        {
            if (SpawnTimeout > 0 && DateTime.Now < startTime.AddSeconds(SpawnTimeout))
                return false;

            startTime = DateTime.Now;

            if (HotSpots != null)
                if (!HotSpots.Next())
                {
                    Logger.Info(Localization.ExGather_FinishedLoop, ++loopCount, Loops);

                    // If finished all loops, otherwise just incrementing loop count
                    if (loopCount == Loops)
                    {
                        isDone = true;
                        return true;
                    }

                    // If not cyclic and it is on the last index
                    if (!HotSpots.IsCyclic && HotSpots.Index == HotSpots.Count - 1)
                        HotSpots.Index = 0;
                }

            await Coroutine.Wait(2000, () => !Window<Gathering>.IsOpen && !GatheringMasterpiece.IsOpen);

            return true;
        }

        private bool FreeRangeConditional()
        {
            if (freeRangeConditionFunc == null)
                freeRangeConditionFunc = ScriptManager.GetCondition(FreeRangeCondition);

            return freeRangeConditionFunc();
        }

        private async Task<bool> ResetOrDone()
        {
            while (ExProfileBehavior.Me.InCombat && Behaviors.ShouldContinue)
                // TODO: Run away...far far away
                await Coroutine.Yield();

            if (!FreeRange && (HotSpots == null || HotSpots.Count == 0 || Node != null && Node.IsUnspoiled() && interactedWithNode))
                isDone = true;
            else
                ResetInternal();

            return true;
        }

        private async Task<bool> ExecutePoiLogic()
        {
            if (Poi.Current.Type != PoiType.Gather)
                return false;

            var result = FindGatherSpot() || await GatherSequence();

            if (!result)
                Poi.Clear(Localization.ExGather_ExecutePoiLogic);

            if (Poi.Current.Type == PoiType.Gather && (!Poi.Current.Unit.IsValid || !Poi.Current.Unit.IsVisible))
                Poi.Clear(Localization.ExGather_NodeIsGone);

            return result;
        }

        private bool FindGatherSpot()
        {
            if (GatherSpot != null)
                return false;

            if (GatherSpots != null && Node.Location.Distance3D(ExProfileBehavior.Me.Location) > Distance)
                GatherSpot =
                    GatherSpots.OrderBy(gs => gs.NodeLocation.Distance3D(Node.Location))
                        .FirstOrDefault(gs => gs.NodeLocation.Distance3D(Node.Location) <= Distance);

            // Either GatherSpots is null, the node is already in range, or there are no matches, use fallback
            if (GatherSpot == null)
            {
                if (Node == null || Node.Location == Vector3.Zero)
                {
                    ResetInternal();
                    return false;
                }

                SetFallbackGatherSpot(Node.Location, true);
            }

            Logger.Info(Localization.ExGather_GatherSpotSet + GatherSpot);

            return true;
        }

        private void SetFallbackGatherSpot(Vector3 location, bool useMesh)
        {
            switch (DefaultGatherSpotType)
            {
                // TODO: Smart stealth implementation (where any enemy within x distance and i'm not behind them, use stealth approach and set stealth location as current)
                // If flying, land in area closest to node not in sight of an enemy and stealth.
                case GatherSpotType.StealthGatherSpot:
                    GatherSpot = new StealthGatherSpot {NodeLocation = location, UseMesh = useMesh};
                    break;
                // ReSharper disable once RedundantCaseLabel
                case GatherSpotType.GatherSpot:
                default:
                    GatherSpot = new GatherSpot {NodeLocation = location, UseMesh = useMesh};
                    break;
            }
        }

        private async Task<bool> GatherSequence() { return await MoveToGatherSpot() /*&& await BeforeSpearFish()*/ && await SpearFish() /*&& await AfterSpearFish()*/; }

        private async Task<bool> MoveToGatherSpot()
        {
            var distance = Poi.Current.Location.Distance3D(ExProfileBehavior.Me.Location);
            if (FreeRange)
                while (distance > Distance && distance <= Radius && Behaviors.ShouldContinue)
                {
                    await Coroutine.Yield();
                    distance = Poi.Current.Location.Distance3D(ExProfileBehavior.Me.Location);
                }

            if (distance <= Distance)
                return true;
            await MoveToSpot(this);

            return false;
        }

        public async Task<bool> MoveToSpot(ExGatherTag tag)
        {
            tag.StatusText = "Moving to " + this;

            if (HotSpots == null || HotSpots.Count == 0)
                return false;

            var randomApproachLocation = tag.Node.Location.AddRandomDirection(3f);

            var result = await randomApproachLocation.MoveToPointWithin(1f);

            if (!result) return false;

            result =
                await
                    tag.Node.Location.MoveTo(
                        true,
                        radius: tag.Distance,
                        name: tag.Node.EnglishName,
                        stopCallback: tag.MovementStopCallback);

            return result;
        }

        private async Task<bool> BeforeSpearFish() { return true; }

        private async Task<bool> SpearFish()
        {
            return await InteractWithNode() && await PrepareRotation() && await ExecuteRotation()
                   && await Coroutine.Wait(2000, () => !Node.CanGather) && await WaitForGatherWindowToClose();
        }

        private async Task<bool> InteractWithNode()
        {
            StatusText = "Interacting with node";

            var attempts = 0;
            while (attempts++ < 5 && !CanDoAbility(Ability.Gig) && Behaviors.ShouldContinue && Poi.Current.Unit.IsVisible
                   && Poi.Current.Unit.IsValid)
            {
                var ticks = 0;
                while (MovementManager.IsFlying
#if !RB_CN
                       && !MovementManager.IsDiving
#endif
                       && ticks++ < 5 && Behaviors.ShouldContinue && Poi.Current.Unit.IsVisible
                       && Poi.Current.Unit.IsValid)
                {
                    var ground = ExProfileBehavior.Me.Location.GetFloor(5);
                    if (Math.Abs(ground.Y - ExProfileBehavior.Me.Location.Y) > float.Epsilon)
                        if (Navigator.PlayerMover is IFlightEnabledPlayerMover mover && !mover.IsLanding && !mover.IsTakingOff)
                            await CommonTasks.DescendTo(ground.Y);

                    await Coroutine.Sleep(200);
                }

                Poi.Current.Unit.Interact();

                if (await Coroutine.Wait(WindowDelay, () => CanDoAbility(Ability.Gig)))
                    break;

                if (attempts == 1 && WindowDelay <= 3000 && await Coroutine.Wait(WindowDelay, () => CanDoAbility(Ability.Gig)))
                    break;

                if (FreeRange)
                {
                    Logger.Warn(Localization.ExGather_GatherWindow, attempts);
                    continue;
                }

                Logger.Warn(Localization.ExGather_GatherWindow2, attempts);
                SetFallbackGatherSpot(Node.Location, true);

                await MoveToGatherSpot();
            }

            if (!CanDoAbility(Ability.Gig))
            {
                OnResetCachedDone();
                return false;
            }

            interactedWithNode = true;

            Logger.Verbose(
                Localization.ExGather_GatherStart,
                Node.EnglishName,
                ExProfileBehavior.Me.CurrentGP,
                ExProfileBehavior.Me.MaxGP,
                WorldManager.EorzaTime.ToShortTimeString());

            if (!Node.IsUnspoiled() && !Node.IsConcealed())
                BlacklistCurrentNode();

            return true;
        }

        private void BlacklistCurrentNode()
        {
            if (Poi.Current.Type != PoiType.Gather)
                return;

            if (Blacklist.Contains(Poi.Current.Unit, BlacklistFlags.Interact)) return;
            var timeToBlacklist = GatherStrategy == GatherStrategy.TouchAndGo
                ? TimeSpan.FromSeconds(12)
                : TimeSpan.FromSeconds(60);
            Blacklist.Add(
                Poi.Current.Unit,
                BlacklistFlags.Interact,
                timeToBlacklist,
                Localization.ExGather_BlackListNode + Poi.Current.Unit);
        }

        public async Task<bool> PrepareRotation()
        {
            if (Collect)
            {
                await CastAura(Ability.CollectorsGlove, AbilityAura.CollectorsGlove);
            }
            else
            {
                if (Core.Player.HasAura((int) AbilityAura.CollectorsGlove))
                    return await Cast(Ability.CollectorsGlove);
            }

            return true;
        }

        public async Task<bool> ExecuteRotation()
        {
            StatusText = "SpearFishing items";

            var hits = 0;
            while (await Coroutine.Wait(4500, () => ActionManager.CanCast(7632, Core.Player) || !Node.IsValid))
            {
                if (BountifulCatch && Core.Player.CurrentGP >= 200)
                    await Cast(Abilities.Map[Core.Player.CurrentJob][Ability.BountifulCatch]);

                await Coroutine.Sleep(4500);
                await Cast(Abilities.Map[Core.Player.CurrentJob][Ability.Gig]);

                while (SelectYesNoItem.IsOpen && Behaviors.ShouldContinue)
                {
                    SelectYesNoItem.Yes();
                    await Coroutine.Wait(2000, () => !SelectYesNoItem.IsOpen);
                }

                await Coroutine.Sleep(1000);

                Logger.Info(Localization.ExSpearFish_SpearFishing, SpearResult.FishName, SpearResult.IsHighQuality, SpearResult.Size, WorldManager.EorzaTime);
                if (hits == 0 && !Items.Any(SpearResult.ShouldKeep) && Core.Player.CurrentGP >= 200 && await Coroutine.Wait(4000, () => ActionManager.CanCast(7906, Core.Player)))
                {
                    Logger.Info(Localization.ExSpearFish_UsingVeteranTrade, SpearResult.FishName);
                    await Cast(Abilities.Map[Core.Player.CurrentJob][Ability.VeteranTrade]);
                }

                hits++;
            }

            StatusText = "SpearFishing items complete";

            return true;
        }

        private async Task<bool> AfterSpearFish() { return true; }

        private async Task<bool> WaitForGatherWindowToClose()
        {
            var ticks = 0;
            while (CanDoAbility(Ability.Gig) && ticks++ < 100 && Behaviors.ShouldContinue)
                await Coroutine.Yield();

            return true;
        }

        #region Ability Checks and Actions

        internal bool CanDoAbility(Ability ability) { return ActionManager.CanCast(Abilities.Map[Core.Player.CurrentJob][ability], ExProfileBehavior.Me); }

        internal bool DoAbility(Ability ability) { return ActionManager.DoAction(Abilities.Map[Core.Player.CurrentJob][ability], ExProfileBehavior.Me); }

        #endregion Ability Checks and Actions
    }
}